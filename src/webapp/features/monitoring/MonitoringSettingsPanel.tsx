import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { useQuery } from "@tanstack/react-query";
import { ClearMonitoringData } from "./ClearMonitoringData";
import { fetchSettings, monitoringKeys } from "./monitoring";
import { MonitoringSettingsForm } from "./MonitoringSettingsForm";
import { SentryProjects } from "./SentryProjects";

export function MonitoringSettingsPanel({ appId }: { appId: string }) {
  const { data, error, isLoading, isError, refetch } = useQuery({
    queryKey: monitoringKeys.settings(appId),
    queryFn: () => fetchSettings(appId),
    retry: retryServerErrors,
  });

  if (isLoading) return <LoadingState size="md" />;
  if (isError || !data) {
    return httpStatus(error) === 403 ? (
      <p role="alert" className="text-muted-foreground">
        Only the app owner can manage monitoring settings.
      </p>
    ) : (
      <ErrorState refetch={refetch} />
    );
  }

  return (
    <div className="max-w-3xl space-y-10">
      <section aria-labelledby="monitoring-settings-heading" className="space-y-4">
        <h2 id="monitoring-settings-heading" className="text-lg font-medium">
          Error and performance monitoring
        </h2>
        <MonitoringSettingsForm appId={appId} settings={data} />
      </section>

      <section aria-labelledby="sentry-heading" className="space-y-4 border-t pt-8">
        <h2 id="sentry-heading" className="text-lg font-medium">
          Sentry SDK DSNs
        </h2>
        <SentryProjects appId={appId} />
      </section>

      <section aria-labelledby="clear-monitoring-heading" className="space-y-4 border-t pt-8">
        <h2 id="clear-monitoring-heading" className="text-lg font-medium">
          Clear monitoring data
        </h2>
        <ClearMonitoringData appId={appId} settings={data} />
      </section>
    </div>
  );
}
