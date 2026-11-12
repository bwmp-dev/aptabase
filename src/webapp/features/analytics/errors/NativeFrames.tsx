import { Button } from "@components/Button";
import { LoadingState } from "@components/LoadingState";
import { BuildMode } from "@features/apps";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { useQuery } from "@tanstack/react-query";
import { NativeFrame, NativeFramesResponse, NativeSourceStatus, fetchNativeFrames, issueKeys } from "./issues";

type Props = {
  appId: string;
  buildMode: BuildMode;
  issueId: string;
  eventId: string;
};

const unresolvedLabels = {
  unresolved: "Unresolved",
  ambiguous: "Ambiguous: more than one symbol file matches this build ID",
} as const;

const capabilityLabels: Record<NativeFramesResponse["capability"], string> = {
  "function-only": "Function names only: no file names, line numbers or inlined frames.",
  "function-and-source":
    "Function names, plus file names, line numbers and inlined calls when the symbol file has line information. No source code or columns.",
};

const missingSourceLabels: Record<Exclude<NativeSourceStatus, "available">, string> = {
  unavailable: "No source location: the symbol file has no line information for this address.",
  unconfigured: "No source location: source locations aren't enabled on this server.",
  limit_exceeded: "No source location: the symbol file exceeded the source-location processing limits.",
};

export function NativeFrames({ appId, buildMode, issueId, eventId }: Props) {
  const { data, error, isLoading, isError, isFetching, refetch } = useQuery({
    queryKey: issueKeys.nativeFrames(appId, buildMode, issueId, eventId),
    queryFn: () => fetchNativeFrames(appId, buildMode, issueId, eventId),
    retry: retryServerErrors,
  });

  const renderBody = () => {
    if (isLoading) return <LoadingState size="sm" />;
    if (isError || !data) {
      switch (httpStatus(error)) {
        case 404:
          return (
            <p className="text-xs text-muted-foreground">
              The native frames for this occurrence are no longer retained.
            </p>
          );
        case 403:
          return (
            <p role="alert" className="text-xs text-muted-foreground">
              You don't have access to this app's native frames.
            </p>
          );
        default:
          return (
            <div className="flex flex-wrap items-center gap-2">
              <p role="alert" className="text-xs text-destructive">
                Couldn't load the native frames.
              </p>
              <Button type="button" variant="ghost" size="xs" disabled={isFetching} onClick={() => refetch()}>
                {isFetching ? "Retrying…" : "Retry"}
              </Button>
            </div>
          );
      }
    }

    const hasUnresolved = data.frames.some((frame) => frame.status !== "resolved");
    return (
      <>
        <ol aria-label="Stack frames" className="divide-y rounded-md border text-xs">
          {data.frames.map((frame, index) => (
            <FrameRow key={index} index={index} frame={frame} />
          ))}
        </ol>
        {hasUnresolved && (
          <p className="text-xs text-muted-foreground">
            Unresolved frames have no ready symbol file for their build ID, or their address isn't inside a known
            function. App owners can upload symbol files in Settings.
          </p>
        )}
      </>
    );
  };

  return (
    <section aria-label="Native frames" className="space-y-2">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3">
        <h4 className="text-xs font-semibold">Native frames</h4>
        {data && <p className="text-xs text-muted-foreground">{capabilityLabels[data.capability]}</p>}
      </div>
      {renderBody()}
    </section>
  );
}

function FrameRow({ index, frame }: { index: number; frame: NativeFrame }) {
  const resolved = frame.status === "resolved" && frame.function !== null;
  return (
    <li className="flex gap-3 px-3 py-2">
      <span className="tabular-nums text-muted-foreground">{index}</span>
      <div className="min-w-0 flex-1 space-y-0.5">
        <p className="break-all">
          {resolved ? (
            <>
              <span className="font-mono">{frame.function}</span>
              {frame.offset !== null && (
                <span className="font-mono text-muted-foreground"> +0x{frame.offset.toString(16)}</span>
              )}
            </>
          ) : (
            <span className="text-muted-foreground">
              {unresolvedLabels[frame.status === "ambiguous" ? "ambiguous" : "unresolved"]}
            </span>
          )}
        </p>
        {resolved && <SourceLocations frame={frame} />}
        <p className="font-mono text-muted-foreground break-all">
          {frame.address} in build {frame.buildId}
        </p>
      </div>
    </li>
  );
}

function SourceLocations({ frame }: { frame: NativeFrame }) {
  if (frame.sourceStatus !== "available" || frame.locations.length === 0) {
    const status = frame.sourceStatus === "available" ? "unavailable" : frame.sourceStatus;
    return <p className="text-muted-foreground">{missingSourceLabels[status]}</p>;
  }

  const [only] = frame.locations;
  if (frame.locations.length === 1) {
    return (
      <p className="font-mono break-all">
        {only.file}:{only.line}
      </p>
    );
  }

  const outermost = frame.locations.length - 1;
  return (
    <div className="space-y-0.5">
      <p className="text-muted-foreground">Inlined calls, innermost first:</p>
      <ol aria-label="Inlined calls" className="space-y-0.5 border-l pl-2">
        {frame.locations.map((location, index) => (
          <li key={index} className="break-all">
            <span className="font-mono">{location.function}</span>{" "}
            <span className="font-mono text-muted-foreground">
              {location.file}:{location.line}
            </span>
            {index < outermost && <span className="text-muted-foreground"> (inlined)</span>}
          </li>
        ))}
      </ol>
    </div>
  );
}
