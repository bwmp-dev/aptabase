import { Button } from "@components/Button";
import { Page, PageHeading } from "@components/Page";
import { BuildMode, useApps, useCurrentApp } from "@features/apps";
import { retryServerErrors } from "@fns/fetch-json";
import { IconX } from "@tabler/icons-react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ReleaseFilter, emptyReleaseFilter, releaseFilterFromSearch } from "@features/releases/releases";
import { Navigate, useSearchParams } from "react-router-dom";
import { BuildModeSelector } from "../analytics/mode/BuildModeSelector";
import { OperationSummary } from "./OperationSummary";
import { PerformanceFilterBar } from "./PerformanceFilterBar";
import {
  OperationGroup,
  PerformanceFilters,
  Transaction,
  fetchSummary,
  fetchTransactions,
  performanceKeys,
} from "./performance";
import { QueryFailure } from "./QueryFailure";
import { TransactionDetailModal } from "./TransactionDetailModal";
import { TransactionList } from "./TransactionList";

const defaultFilters: PerformanceFilters = { period: "7d", ...emptyReleaseFilter, operation: null };

Component.displayName = "PerformancePage";
export function Component() {
  const app = useCurrentApp();
  const [searchParams] = useSearchParams();

  if (!app) return <Navigate to="/" />;

  return (
    <Page title="Performance">
      <div className="flex justify-between items-center">
        <PageHeading title="Performance" subtitle="Timings of sampled operations reported by your app" />
        <BuildModeSelector />
      </div>
      <PerformancePanel appId={app.id} initialFilter={releaseFilterFromSearch(searchParams)} />
    </Page>
  );
}

type PanelProps = {
  appId: string;
  initialFilter?: ReleaseFilter;
};

export function PerformancePanel({ appId, initialFilter = emptyReleaseFilter }: PanelProps) {
  const { buildMode } = useApps();

  // Remounting on scope change drops filters, paging and the open transaction, since releases,
  // environments and transactions differ between apps and build modes.
  return (
    <PerformanceView key={`${appId}:${buildMode}`} appId={appId} buildMode={buildMode} initialFilter={initialFilter} />
  );
}

type ViewProps = {
  appId: string;
  buildMode: BuildMode;
  initialFilter: ReleaseFilter;
};

function PerformanceView({ appId, buildMode, initialFilter }: ViewProps) {
  const [filters, setFilters] = useState<PerformanceFilters>(() => ({ ...defaultFilters, ...initialFilter }));
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<{ eventId: string | null; opener: HTMLElement | null }>({
    eventId: null,
    opener: null,
  });

  const summary = useQuery({
    queryKey: performanceKeys.summary(appId, buildMode, { ...filters, operation: null }),
    queryFn: () => fetchSummary(appId, buildMode, filters),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });

  const transactions = useQuery({
    queryKey: performanceKeys.transactions(appId, buildMode, filters, offset),
    queryFn: () => fetchTransactions(appId, buildMode, filters, offset),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });

  const changeFilters = (changes: Partial<PerformanceFilters>) => {
    setFilters((current) => ({ ...current, ...changes }));
    setOffset(0);
  };

  const resetFilters = () => {
    setFilters(defaultFilters);
    setOffset(0);
  };

  const toggleOperation = (group: OperationGroup) => {
    const isCurrent = filters.operation?.name === group.name && filters.operation.operation === group.operation;
    changeFilters({ operation: isCurrent ? null : { name: group.name, operation: group.operation } });
  };

  return (
    <div className="mt-6 space-y-6">
      <div className="space-y-3">
        <PerformanceFilterBar appId={appId} buildMode={buildMode} filters={filters} onChange={changeFilters} />
        <p className="text-sm text-muted-foreground max-w-3xl">
          Counts are the operations your app sampled and sent from {buildMode} builds, not total usage. Transactions
          are kept for up to 30 days, depending on app retention.
        </p>
      </div>

      <section aria-labelledby="operations-heading" className="space-y-3">
        <h2 id="operations-heading" className="text-lg font-medium">
          Operations
        </h2>
        {summary.isError ? (
          <QueryFailure error={summary.error} refetch={summary.refetch} onResetFilters={resetFilters} />
        ) : (
          <OperationSummary
            groups={summary.data?.groups}
            isLoading={summary.isLoading}
            isStale={summary.isPlaceholderData}
            selected={filters.operation}
            onSelect={toggleOperation}
          />
        )}
      </section>

      <section aria-labelledby="transactions-heading" className="space-y-3">
        <div className="flex flex-wrap items-center gap-3">
          <h2 id="transactions-heading" className="text-lg font-medium">
            Transactions
          </h2>
          {filters.operation && (
            <Button
              variant="outline"
              size="xs"
              onClick={() => changeFilters({ operation: null })}
              aria-label={`Show all operations (currently ${filters.operation.name})`}
            >
              <span className="max-w-[14rem] truncate">{filters.operation.name}</span>
              <IconX className="ml-1 h-3.5 w-3.5" />
            </Button>
          )}
        </div>
        {transactions.isError ? (
          <QueryFailure error={transactions.error} refetch={transactions.refetch} onResetFilters={resetFilters} />
        ) : (
          <TransactionList
            data={transactions.data}
            isLoading={transactions.isLoading}
            isStale={transactions.isPlaceholderData}
            offset={offset}
            onPageChange={setOffset}
            onSelect={(transaction: Transaction, opener) => setSelected({ eventId: transaction.eventId, opener })}
          />
        )}
      </section>

      <TransactionDetailModal
        appId={appId}
        buildMode={buildMode}
        eventId={selected.eventId}
        onClose={() => setSelected((current) => ({ ...current, eventId: null }))}
        returnFocusTo={selected.opener}
      />
    </div>
  );
}
