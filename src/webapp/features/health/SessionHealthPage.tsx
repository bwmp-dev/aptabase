import { LoadingState } from "@components/LoadingState";
import { Page, PageHeading } from "@components/Page";
import { BuildMode, useApps, useCurrentApp } from "@features/apps";
import { PerformanceFilterBar } from "@features/performance/PerformanceFilterBar";
import { PeriodFilters } from "@features/performance/performance";
import { QueryFailure } from "@features/performance/QueryFailure";
import { ReleaseFilter, emptyReleaseFilter, releaseFilterFromSearch } from "@features/releases/releases";
import { retryServerErrors } from "@fns/fetch-json";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { twJoin } from "tailwind-merge";
import { BuildModeSelector } from "../analytics/mode/BuildModeSelector";
import { ClientReports } from "./ClientReports";
import { SessionGroups } from "./SessionGroups";
import { SessionSummary } from "./SessionSummary";
import { SessionHealthResponse, fetchSessionHealth, healthKeys, mergeObservedPairs } from "./health";

const defaultFilters: PeriodFilters = { period: "7d", ...emptyReleaseFilter };

Component.displayName = "SessionHealthPage";
export function Component() {
  const app = useCurrentApp();
  const [searchParams] = useSearchParams();

  if (!app) return <Navigate to="/" />;

  return (
    <Page title="Session health">
      <div className="flex justify-between items-center">
        <PageHeading title="Session health" subtitle="Crash-free completed sessions and SDK discard reports" />
        <BuildModeSelector />
      </div>
      <SessionHealthPanel appId={app.id} initialFilter={releaseFilterFromSearch(searchParams)} />
    </Page>
  );
}

type PanelProps = {
  appId: string;
  initialFilter?: ReleaseFilter;
};

export function SessionHealthPanel({ appId, initialFilter = emptyReleaseFilter }: PanelProps) {
  const { buildMode } = useApps();
  const scope = `${appId}:${buildMode}`;
  const [firstScope] = useState(scope);
  const [leftFirstScope, setLeftFirstScope] = useState(false);
  if (scope !== firstScope && !leftFirstScope) setLeftFirstScope(true);

  // Releases and environments differ between apps and build modes, so a scope change starts from default filters,
  // and a filter from the link only applies to the scope it was opened in.
  return (
    <SessionHealthView
      key={scope}
      appId={appId}
      buildMode={buildMode}
      initialFilter={leftFirstScope ? emptyReleaseFilter : initialFilter}
    />
  );
}

type ViewProps = {
  appId: string;
  buildMode: BuildMode;
  initialFilter: ReleaseFilter;
};

function SessionHealthView({ appId, buildMode, initialFilter }: ViewProps) {
  const [filters, setFilters] = useState<PeriodFilters>(() => ({ ...defaultFilters, ...initialFilter }));

  const health = useQuery({
    queryKey: healthKeys.summary(appId, buildMode, filters),
    queryFn: () => fetchSessionHealth(appId, buildMode, filters),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });

  // The release catalog only lists releases with errors or transactions, so labels seen in session groups are kept as
  // filter options too, including ones that drop out of the response once a filter is applied.
  const [observed, setObserved] = useState<ReleaseFilter[]>([]);
  const [observedFrom, setObservedFrom] = useState<SessionHealthResponse>();
  if (health.data && health.data !== observedFrom) {
    const groups = health.data.groups;
    setObservedFrom(health.data);
    setObserved((current) => mergeObservedPairs(current, groups));
  }

  const changeFilters = (changes: Partial<PeriodFilters>) => setFilters((current) => ({ ...current, ...changes }));
  const filtered = !!filters.release || !!filters.environment;

  return (
    <div className="mt-6 space-y-8">
      <div className="space-y-3">
        <PerformanceFilterBar
          appId={appId}
          buildMode={buildMode}
          filters={filters}
          onChange={changeFilters}
          observed={observed}
        />
        <p className="text-sm text-muted-foreground max-w-3xl">
          Reported by Sentry SDKs from {buildMode} builds. Sessions are selected by when they started; discard reports
          by when the SDK sent them.
        </p>
      </div>

      {health.isLoading ? (
        <div className="py-8">
          <LoadingState size="md" />
        </div>
      ) : health.isError || !health.data ? (
        <QueryFailure
          error={health.error}
          refetch={health.refetch}
          onResetFilters={() => setFilters(defaultFilters)}
          subject="session health data"
        />
      ) : (
        <div aria-busy={health.isPlaceholderData} className={twJoin("space-y-8", health.isPlaceholderData && "opacity-60")}>
          <SessionSummary sessions={health.data.sessions} retentionDays={health.data.retentionDays} />
          {health.data.sessions.observed > 0 && (
            <SessionGroups groups={health.data.groups} truncated={health.data.groupsTruncated} />
          )}
          <ClientReports reports={health.data.clientReports} filtered={filtered} />
        </div>
      )}
    </div>
  );
}
