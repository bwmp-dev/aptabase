import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FormEvent, useId, useState } from "react";
import { twJoin } from "tailwind-merge";
import { MonitoringSettings, clearMonitoringData, fetchDataPreview, monitoringKeys } from "./monitoring";

type Props = {
  appId: string;
  settings: MonitoringSettings;
};

const countLabels = [
  ["issues", "Issue summaries"],
  ["events", "Error occurrences"],
  ["transactions", "Transactions"],
  ["queued", "Raw logs waiting to be delivered"],
  ["alerts", "Local alerts"],
  ["symbols", "Native symbol files"],
] as const;

function clearErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 409:
      return "Turn off error capture, performance capture and raw log forwarding, save, then try again.";
    case 400:
      return "The app ID didn't match.";
    case 403:
      return "Only the app owner can clear monitoring data.";
    default:
      return "Couldn't clear monitoring data. Please try again.";
  }
}

export function ClearMonitoringData({ appId, settings }: Props) {
  const queryClient = useQueryClient();
  const inputId = useId();
  const [confirmation, setConfirmation] = useState("");
  const captureOff = !settings.captureErrors && !settings.capturePerformance && !settings.forwardRawErrors;

  const preview = useQuery({
    queryKey: monitoringKeys.data(appId),
    queryFn: () => fetchDataPreview(appId),
    retry: retryServerErrors,
  });

  const clear = useMutation({
    mutationFn: () => clearMonitoringData(appId, confirmation),
    onSuccess: () => {
      setConfirmation("");
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: monitoringKeys.app(appId) }),
        queryClient.invalidateQueries({ queryKey: ["issues", appId] }),
        queryClient.invalidateQueries({ queryKey: ["performance", appId] }),
        queryClient.invalidateQueries({ queryKey: ["releases", appId] }),
      ]);
    },
  });

  // Only clear against counts the owner has actually seen: never while the preview is loading, failed or refreshing.
  const previewCurrent = preview.isSuccess && !preview.isFetching;
  const canClear = captureOff && previewCurrent && confirmation === appId && !clear.isPending;

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (canClear) clear.mutate();
  };

  return (
    <form onSubmit={handleSubmit} className="space-y-4 max-w-2xl">
      <div className="text-sm text-muted-foreground space-y-2">
        <p>
          Permanently deletes this app's issues, error occurrences, transactions, undelivered raw logs, local alerts and
          native symbol files from this server's PostgreSQL database, for release and debug builds.
        </p>
        <p>
          Raw error logs that were already delivered are not deleted and are kept for 180 days. Usage analytics are not
          affected. This is not a full personal data deletion.
        </p>
      </div>

      <div aria-live="polite" aria-busy={preview.isFetching}>
        {preview.isLoading ? (
          <LoadingState size="sm" />
        ) : preview.isError || !preview.data ? (
          <ErrorState refetch={preview.refetch} />
        ) : (
          <dl
            className={twJoin(
              "grid grid-cols-[1fr_max-content] gap-x-6 gap-y-1 text-sm max-w-sm",
              preview.isFetching && "opacity-60"
            )}
          >
            {countLabels.map(([key, label]) => (
              <div key={key} className="contents">
                <dt className="text-muted-foreground">{label}</dt>
                <dd className="text-right tabular-nums">{preview.data.counts[key].toLocaleString()}</dd>
              </div>
            ))}
          </dl>
        )}
        {preview.isFetching && !preview.isLoading && (
          <p className="mt-1 text-xs text-muted-foreground">Updating counts…</p>
        )}
      </div>

      {!captureOff && (
        <p className="text-sm">
          To clear data, first turn off error capture, performance capture and raw log forwarding above and save.
        </p>
      )}

      <div className="space-y-1">
        <label htmlFor={inputId} className="text-sm font-medium block">
          Type the app ID <code className="font-mono">{appId}</code> to confirm
        </label>
        <input
          id={inputId}
          value={confirmation}
          onChange={(event) => {
            clear.reset();
            setConfirmation(event.target.value);
          }}
          disabled={!captureOff}
          autoComplete="off"
          spellCheck={false}
          className="h-9 w-full max-w-sm rounded-md border border-input bg-background px-2 font-mono text-sm ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
        />
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Button
          type="submit"
          variant="destructive"
          disabled={!canClear}
          aria-busy={clear.isPending}
        >
          {clear.isPending ? "Clearing…" : "Clear monitoring data"}
        </Button>
        {clear.isSuccess && (
          <p role="status" className="text-sm text-success">
            Monitoring data cleared.
          </p>
        )}
        {clear.isError && (
          <p role="alert" className="text-sm text-destructive">
            {clearErrorMessage(clear.error)}
          </p>
        )}
      </div>
    </form>
  );
}
