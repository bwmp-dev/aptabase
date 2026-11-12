import { Button } from "@components/Button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@components/Dialog";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { SegmentedControl } from "@components/SegmentedControl";
import { issueKeys } from "@features/analytics/errors/issues";
import { BuildMode } from "@features/apps";
import { formatDate } from "@fns/format-date";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { IconTrash, IconUpload } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChangeEvent, useRef, useState } from "react";
import {
  NativeSymbolArtifact,
  NativeSymbolLimits,
  NativeSymbolStatus,
  NativeSymbolsResponse,
  deleteNativeSymbols,
  fetchNativeSymbols,
  monitoringKeys,
  uploadNativeSymbols,
} from "./monitoring";

const buildModeOptions = [
  { value: "release", label: "Release" },
  { value: "debug", label: "Debug" },
] as const;

const statusLabels: Record<NativeSymbolStatus, string> = {
  pending: "Pending validation",
  ready: "Ready",
  invalid: "Rejected",
};

const statusClasses: Record<NativeSymbolStatus, string> = {
  pending: "text-muted-foreground",
  ready: "text-success",
  invalid: "text-destructive",
};

// Validation runs asynchronously on the server; keep checking until no artifact is pending.
const PENDING_POLL_MS = 5000;

// The `accept` filter only narrows the file picker; the client header check and the server's validation decide.
const FILE_ACCEPT = ".so,.debug";

const ELF_MACHINE_AARCH64 = 183;
const ELF_TYPE_DYN = 3;

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${trimDecimal(bytes / 1024)} KiB`;
  return `${trimDecimal(bytes / (1024 * 1024))} MiB`;
}

function trimDecimal(value: number) {
  return Number.isInteger(value) ? value.toString() : value.toFixed(1);
}

function readHeader(file: Blob, length: number) {
  return new Promise<Uint8Array>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(new Uint8Array(reader.result as ArrayBuffer));
    reader.onerror = () => reject(reader.error);
    reader.readAsArrayBuffer(file.slice(0, length));
  });
}

// Catches obvious mistakes (dumps, archives, dSYMs, other architectures) before uploading. It is a convenience,
// not a security check: the server validates every artifact in isolation.
async function looksLikeArm64SharedObject(file: Blob) {
  const header = await readHeader(file, 20);
  if (header.length < 20) return false;
  const view = new DataView(header.buffer, header.byteOffset, header.byteLength);
  const magic = header[0] === 0x7f && header[1] === 0x45 && header[2] === 0x4c && header[3] === 0x46;
  const elf64 = header[4] === 2;
  const littleEndian = header[5] === 1;
  return (
    magic &&
    elf64 &&
    littleEndian &&
    view.getUint16(16, true) === ELF_TYPE_DYN &&
    view.getUint16(18, true) === ELF_MACHINE_AARCH64
  );
}

class UploadRejected extends Error {}

function uploadErrorMessage(error: unknown, limits: NativeSymbolLimits) {
  if (error instanceof UploadRejected) return error.message;
  switch (httpStatus(error)) {
    case 400:
      return "The server couldn't read this upload. Choose an ARM64 ELF symbol file and try again.";
    case 401:
      return "Your session has expired. Sign in again to upload symbols.";
    case 403:
      return "Only the app owner can upload symbol files.";
    case 409:
      return `This app has reached its symbol storage limit of ${limits.maxAppArtifacts} files or ${formatBytes(
        limits.maxAppBytes
      )} across release and debug builds. Delete a file and try again.`;
    case 413:
      return `This file is larger than the ${formatBytes(limits.maxArtifactBytes)} limit.`;
    case 415:
      return "The server didn't accept this upload's content type. Please try again.";
    case 503:
      return "Symbol processing is unavailable right now. Please try again later.";
    default:
      return "Couldn't upload the symbol file. Please try again.";
  }
}

function deleteErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 403:
      return "Only the app owner can delete symbol files.";
    case 404:
      return "This symbol file no longer exists. It may have expired or been deleted already.";
    default:
      return "Couldn't delete the symbol file. Please try again.";
  }
}

export function NativeSymbols({ appId, retentionDays }: { appId: string; retentionDays: number }) {
  const [buildMode, setBuildMode] = useState<BuildMode>("release");

  return (
    <div className="space-y-4">
      <div className="text-sm text-muted-foreground space-y-2 max-w-2xl">
        <p>
          Upload unstripped ARM64 native libraries so native text crash reports can be matched to function names. Only
          64-bit little-endian ARM64 ELF shared libraries (<code>.so</code>) with a GNU build ID are supported. Only
          function names and address ranges are read: no file names, line numbers, inlined frames or source code.
        </p>
        <p>
          Apple dSYMs, Windows PDBs, Breakpad symbols, source maps, archives and crash dumps are not accepted. Expanded
          occurrences that include native frames show the resolved function names. Deleting a file, or letting it
          expire, leaves those frames unresolved; the error reports themselves are kept.
        </p>
        <p>
          Files are kept for {retentionDays} {retentionDays === 1 ? "day" : "days"} after upload, following the
          monitoring retention setting, and can't be downloaded. Clearing monitoring data also deletes them.
        </p>
      </div>
      <SegmentedControl label="Build mode" options={buildModeOptions} value={buildMode} onChange={setBuildMode} />
      {/* Upload, delete and status messages belong to one app and build mode, so they reset when either changes. */}
      <NativeSymbolsForMode key={`${appId}:${buildMode}`} appId={appId} buildMode={buildMode} />
    </div>
  );
}

function NativeSymbolsForMode({ appId, buildMode }: { appId: string; buildMode: BuildMode }) {
  const queryClient = useQueryClient();
  const queryKey = monitoringKeys.nativeSymbols(appId, buildMode);
  const fileInput = useRef<HTMLInputElement>(null);
  const [pendingDelete, setPendingDelete] = useState<NativeSymbolArtifact | null>(null);
  const [deletedLabel, setDeletedLabel] = useState<string | null>(null);

  const symbols = useQuery({
    queryKey,
    queryFn: () => fetchNativeSymbols(appId, buildMode),
    retry: retryServerErrors,
    refetchInterval: (query) =>
      query.state.data?.artifacts.some((artifact) => artifact.status === "pending") ? PENDING_POLL_MS : false,
  });

  const upload = useMutation({
    mutationFn: async ({ file, limits }: { file: File; limits: NativeSymbolLimits }) => {
      if (file.size === 0) throw new UploadRejected("This file is empty.");
      if (file.size > limits.maxArtifactBytes) {
        throw new UploadRejected(`This file is larger than the ${formatBytes(limits.maxArtifactBytes)} limit.`);
      }
      if (!(await looksLikeArm64SharedObject(file))) {
        throw new UploadRejected(
          "This isn't a 64-bit little-endian ARM64 ELF shared library. Crash dumps, archives and other symbol formats aren't accepted."
        );
      }
      return uploadNativeSymbols(appId, buildMode, file);
    },
    onSuccess: ({ artifact }) => {
      queryClient.setQueryData<NativeSymbolsResponse>(queryKey, (current) =>
        current
          ? { ...current, artifacts: [artifact, ...current.artifacts.filter((existing) => existing.id !== artifact.id)] }
          : current
      );
      return Promise.all([
        queryClient.invalidateQueries({ queryKey }),
        queryClient.invalidateQueries({ queryKey: monitoringKeys.data(appId) }),
      ]);
    },
    onError: (error) => {
      // A 503 may mean the operator turned processing off; refresh so `enabled` is current.
      if (httpStatus(error) === 503) return queryClient.invalidateQueries({ queryKey });
    },
  });

  const remove = useMutation({
    mutationFn: (artifact: NativeSymbolArtifact) => deleteNativeSymbols(appId, buildMode, artifact.id),
    onSuccess: (_, artifact) => {
      queryClient.setQueryData<NativeSymbolsResponse>(queryKey, (current) =>
        current ? { ...current, artifacts: current.artifacts.filter((existing) => existing.id !== artifact.id) } : current
      );
      upload.reset();
      setDeletedLabel(artifactLabel(artifact));
      setPendingDelete(null);
      return Promise.all([
        queryClient.invalidateQueries({ queryKey }),
        queryClient.invalidateQueries({ queryKey: issueKeys.allNativeFrames(appId, buildMode) }),
        queryClient.invalidateQueries({ queryKey: monitoringKeys.data(appId) }),
      ]);
    },
    onError: (error) => {
      if (httpStatus(error) === 404) return queryClient.invalidateQueries({ queryKey });
    },
  });

  if (symbols.isLoading) return <LoadingState size="sm" />;
  if (symbols.isError || !symbols.data) {
    return httpStatus(symbols.error) === 403 ? (
      <p role="alert" className="text-sm text-muted-foreground">
        Only the app owner can manage symbol files.
      </p>
    ) : (
      <ErrorState refetch={symbols.refetch} />
    );
  }

  const { enabled, artifacts, limits } = symbols.data;
  const totalBytes = artifacts.reduce((sum, artifact) => sum + artifact.sizeBytes, 0);

  const handleFileChange = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Clear the input so choosing the same file again still triggers a change.
    event.target.value = "";
    if (!file || upload.isPending) return;
    setDeletedLabel(null);
    upload.mutate({ file, limits });
  };

  const openDelete = (artifact: NativeSymbolArtifact) => {
    remove.reset();
    setPendingDelete(artifact);
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Each file can be up to {formatBytes(limits.maxArtifactBytes)}. An app can keep {limits.maxAppArtifacts} files
        and {formatBytes(limits.maxAppBytes)} in total across release and debug builds.
      </p>

      {!enabled && (
        <p role="note" className="text-sm max-w-2xl">
          Symbol processing isn't configured on this server, so uploads are turned off. The server operator needs to set
          up the isolated symbol processor first. Existing files can still be reviewed and deleted.
        </p>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <input
          ref={fileInput}
          type="file"
          accept={FILE_ACCEPT}
          aria-label={`${buildMode} symbol file`}
          tabIndex={-1}
          className="sr-only"
          disabled={!enabled || upload.isPending}
          onChange={handleFileChange}
        />
        <Button
          type="button"
          variant="outline"
          disabled={!enabled || upload.isPending}
          aria-busy={upload.isPending}
          onClick={() => fileInput.current?.click()}
        >
          <IconUpload className="h-4 w-4 mr-2" />
          {upload.isPending ? "Uploading…" : `Upload ${buildMode} symbols`}
        </Button>
        <div aria-live="polite" className="text-sm">
          {upload.isSuccess && (
            <p role="status">
              {upload.data.artifact.status === "ready"
                ? "Upload received. These symbols are ready."
                : upload.data.artifact.status === "invalid"
                ? "Upload received, but the file was rejected."
                : "Upload received. It will be validated before it can be used."}
            </p>
          )}
          {deletedLabel && <p role="status">Deleted {deletedLabel}.</p>}
        </div>
      </div>
      {upload.isError && (
        <p role="alert" className="text-sm text-destructive max-w-2xl">
          {uploadErrorMessage(upload.error, limits)}
        </p>
      )}

      {artifacts.length === 0 ? (
        <p className="text-sm text-muted-foreground">No symbol files uploaded for {buildMode} builds.</p>
      ) : (
        <div className="space-y-2">
          <ul aria-label={`${buildMode} symbol files`} className="divide-y border-y">
            {artifacts.map((artifact) => (
              <ArtifactRow key={artifact.id} artifact={artifact} onDelete={() => openDelete(artifact)} />
            ))}
          </ul>
          <p className="text-xs text-muted-foreground tabular-nums">
            {artifacts.length} {artifacts.length === 1 ? "file" : "files"}, {formatBytes(totalBytes)} for {buildMode}{" "}
            builds
          </p>
        </div>
      )}

      <Dialog
        open={pendingDelete !== null}
        onOpenChange={(open) => {
          if (!open && !remove.isPending) setPendingDelete(null);
        }}
      >
        {pendingDelete && (
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Delete symbol file?</DialogTitle>
              <DialogDescription>
                {pendingDelete.buildId ? "Build ID " : "Upload ID "}
                <code className="font-mono break-all text-foreground">{pendingDelete.buildId ?? pendingDelete.id}</code>{" "}
                will be removed from {buildMode} builds. Error reports already received are kept; native frames that used this
                file will show as unresolved.
              </DialogDescription>
            </DialogHeader>
            {remove.isError && (
              <p role="alert" className="text-sm text-destructive">
                {deleteErrorMessage(remove.error)}
              </p>
            )}
            <DialogFooter className="gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={remove.isPending}
                onClick={() => setPendingDelete(null)}
              >
                Cancel
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={remove.isPending}
                aria-busy={remove.isPending}
                onClick={() => remove.mutate(pendingDelete)}
              >
                {remove.isPending ? "Deleting…" : "Delete"}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </div>
  );
}

function artifactLabel(artifact: NativeSymbolArtifact) {
  return artifact.buildId ? `build ID ${artifact.buildId}` : `upload ${artifact.id}`;
}

function ArtifactRow({ artifact, onDelete }: { artifact: NativeSymbolArtifact; onDelete: () => void }) {
  return (
    <li className="flex items-start gap-3 py-3">
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span className="text-sm font-medium">{artifact.buildId ? "Build ID" : "Upload ID"}</span>
          <span className={`text-sm ${statusClasses[artifact.status]}`}>{statusLabels[artifact.status]}</span>
        </div>
        <p className="font-mono text-xs break-all">{artifact.buildId ?? artifact.id}</p>
        <p className="text-xs text-muted-foreground">
          {formatBytes(artifact.sizeBytes)} · Uploaded <time dateTime={artifact.createdAt}>{formatDate(artifact.createdAt)}</time>{" "}
          · Expires <time dateTime={artifact.expiresAt}>{formatDate(artifact.expiresAt)}</time>
        </p>
        {artifact.status === "invalid" && (
          <p className="text-xs text-destructive">
            Not a supported ARM64 ELF shared library with a GNU build ID and function symbols. Delete it to free space.
          </p>
        )}
      </div>
      <Button type="button" variant="ghost" size="sm" onClick={onDelete} aria-label={`Delete ${artifactLabel(artifact)}`}>
        <IconTrash className="h-4 w-4" />
        <span className="ml-1 hidden sm:inline">Delete</span>
      </Button>
    </li>
  );
}
