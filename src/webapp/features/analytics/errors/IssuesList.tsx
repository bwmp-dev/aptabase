import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { SegmentedControl } from "@components/SegmentedControl";
import { BuildMode, useApps } from "@features/apps";
import { ReleaseFilters } from "@features/releases/ReleaseFilters";
import { ReleaseFilter, emptyReleaseFilter } from "@features/releases/releases";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { IconAlertTriangle, IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { twJoin } from "tailwind-merge";
import { IssueDetailModal } from "./IssueDetailModal";
import { IssueSearch, useIssueSearch } from "./IssueSearch";
import { IssueStatusBadge } from "./IssueStatusBadge";
import {
  ISSUES_PAGE_SIZE,
  ISSUE_SEARCH_MAX_LENGTH,
  Issue,
  IssueStatusFilter,
  fetchIssues,
  issueKeys,
} from "./issues";

const statusOptions: { value: IssueStatusFilter; label: string }[] = [
  { value: "unresolved", label: "Unresolved" },
  { value: "resolved", label: "Resolved" },
  { value: "all", label: "All" },
];

const emptyMessages: Record<IssueStatusFilter, string> = {
  unresolved: "No unresolved issues",
  resolved: "No resolved issues",
  all: "No issues yet",
};

const noMatchMessages: Record<IssueStatusFilter, string> = {
  unresolved: "No unresolved issues match",
  resolved: "No resolved issues match",
  all: "No issues match",
};

type Props = {
  appId: string;
  initialFilter?: ReleaseFilter;
};

export function IssuesPanel({ appId, initialFilter = emptyReleaseFilter }: Props) {
  const { buildMode } = useApps();

  // Remounting on scope change drops filters, search, paging and the open issue,
  // so nothing from one app or build mode is ever shown or mutated in another.
  return (
    <IssuesList key={`${appId}:${buildMode}`} appId={appId} buildMode={buildMode} initialFilter={initialFilter} />
  );
}

type ListProps = {
  appId: string;
  buildMode: BuildMode;
  initialFilter: ReleaseFilter;
};

function IssuesList({ appId, buildMode, initialFilter }: ListProps) {
  const [status, setStatus] = useState<IssueStatusFilter>("unresolved");
  const [releaseFilter, setReleaseFilter] = useState<ReleaseFilter>(initialFilter);
  const [offset, setOffset] = useState(0);
  // The opener outlives the open state so focus can return to it after the dialog closes.
  const [selected, setSelected] = useState<{ issueId: string | null; opener: HTMLElement | null }>({
    issueId: null,
    opener: null,
  });
  const search = useIssueSearch(setOffset);

  const { data, error, isLoading, isError, refetch, isPlaceholderData } = useQuery({
    queryKey: issueKeys.list(appId, buildMode, status, search.search, releaseFilter, offset),
    queryFn: () => fetchIssues(appId, buildMode, status, search.search, releaseFilter, offset),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });

  const changeReleaseFilter = (changes: Partial<ReleaseFilter>) => {
    setReleaseFilter((current) => ({ ...current, ...changes }));
    setOffset(0);
  };

  const changeStatus = (value: IssueStatusFilter) => {
    setStatus(value);
    setOffset(0);
  };

  const renderContent = () => {
    if (isLoading) {
      return (
        <div className="py-12">
          <LoadingState size="lg" />
        </div>
      );
    }

    if (isError && search.search && httpStatus(error) === 400) {
      return <SearchRejected onClear={search.clear} />;
    }

    if (isError || !data) {
      return (
        <div className="py-12">
          <ErrorState refetch={refetch} />
        </div>
      );
    }

    if (data.issues.length === 0) {
      return (
        <IssuesEmpty
          status={status}
          search={search.search}
          buildMode={buildMode}
          isPastFirstPage={offset > 0}
          onFirstPage={() => setOffset(0)}
          onClearSearch={search.clear}
        />
      );
    }

    return (
      <>
        <IssuesTable
          issues={data.issues}
          isStale={isPlaceholderData}
          onSelect={(issueId, opener) => setSelected({ issueId, opener })}
        />
        <IssuesPagination
          offset={offset}
          total={data.pagination.total}
          disabled={isPlaceholderData}
          onChange={setOffset}
        />
      </>
    );
  };

  return (
    <div className="mt-6 space-y-4">
      <div className="flex flex-wrap items-end gap-4">
        <SegmentedControl label="Issue status" options={statusOptions} value={status} onChange={changeStatus} />
        <IssueSearch
          inputRef={search.inputRef}
          value={search.draft}
          onChange={search.setDraft}
          onSubmit={search.submit}
          onClear={search.clear}
        />
        <ReleaseFilters appId={appId} buildMode={buildMode} value={releaseFilter} onChange={changeReleaseFilter} />
      </div>

      <p className="text-sm text-muted-foreground max-w-3xl">
        Identical error reports from {buildMode} builds are grouped into issues. Counts are lifetime totals and ignore
        the date range. Only reports received since issue tracking was enabled are included.
        {(releaseFilter.release || releaseFilter.environment) &&
          " The release and environment filters match occurrences kept for up to 30 days, depending on app retention; resolving an issue still applies to every release and environment."}
      </p>

      {data && !data.canManage && (
        <p className="text-sm text-muted-foreground">
          You have read-only access to this app. Only app owners can resolve or reopen issues.
        </p>
      )}

      {renderContent()}

      <IssueDetailModal
        appId={appId}
        buildMode={buildMode}
        issueId={selected.issueId}
        releaseFilter={releaseFilter}
        onClose={() => setSelected((current) => ({ ...current, issueId: null }))}
        returnFocusTo={selected.opener}
      />
    </div>
  );
}

type EmptyProps = {
  status: IssueStatusFilter;
  search: string;
  buildMode: BuildMode;
  isPastFirstPage: boolean;
  onFirstPage: () => void;
  onClearSearch: () => void;
};

function IssuesEmpty({ status, search, buildMode, isPastFirstPage, onFirstPage, onClearSearch }: EmptyProps) {
  const renderBody = () => {
    if (isPastFirstPage) {
      return (
        <>
          <p className="text-lg">This page is empty</p>
          <Button variant="ghost" onClick={onFirstPage}>
            Go to first page
          </Button>
        </>
      );
    }

    if (search) {
      return (
        <>
          <p className="text-lg break-all">
            {noMatchMessages[status]} “{search}”
          </p>
          <p className="text-sm text-muted-foreground">
            Search matches error type or message text exactly as typed, ignoring case.
          </p>
          <Button variant="ghost" onClick={onClearSearch}>
            Clear search
          </Button>
        </>
      );
    }

    return (
      <>
        <p className="text-lg">{emptyMessages[status]}</p>
        <p className="text-sm text-muted-foreground">
          New error reports from {buildMode} builds will appear here as issues.
        </p>
      </>
    );
  };

  return (
    <div
      role="status"
      className="bg-muted rounded w-full py-12 px-4 flex flex-col space-y-1 items-center justify-center text-center"
    >
      {renderBody()}
    </div>
  );
}

function SearchRejected({ onClear }: { onClear: () => void }) {
  return (
    <div role="alert" className="py-12 flex flex-col space-y-1 items-center justify-center text-center">
      <p className="text-lg text-destructive flex items-center gap-2">
        <IconAlertTriangle className="h-5 w-5" />
        This search couldn't be run
      </p>
      <p className="text-sm text-muted-foreground">
        Searches can be up to {ISSUE_SEARCH_MAX_LENGTH} characters of plain text.
      </p>
      <Button variant="ghost" onClick={onClear}>
        Clear search
      </Button>
    </div>
  );
}

type TableProps = {
  issues: Issue[];
  isStale: boolean;
  onSelect: (issueId: string, opener: HTMLElement) => void;
};

function IssuesTable({ issues, isStale, onSelect }: TableProps) {
  return (
    <div className="overflow-x-auto" aria-busy={isStale}>
      <table className="min-w-full divide-y divide-gray-300">
        <caption className="sr-only">Issues, most recently seen first</caption>
        <thead>
          <tr>
            <th scope="col" className="py-3.5 pl-4 pr-3 text-left text-sm font-semibold">
              Issue
            </th>
            <th scope="col" className="hidden sm:table-cell px-3 py-3.5 text-left text-sm font-semibold">
              Status
            </th>
            <th scope="col" className="px-3 py-3.5 text-right text-sm font-semibold">
              Occurrences
            </th>
            <th scope="col" className="hidden lg:table-cell px-3 py-3.5 text-left text-sm font-semibold">
              First seen
            </th>
            <th scope="col" className="px-3 py-3.5 text-left text-sm font-semibold">
              Last seen
            </th>
          </tr>
        </thead>
        <tbody className={twJoin("divide-y divide-gray-600", isStale && "opacity-60")}>
          {issues.map((issue) => (
            <tr key={issue.id} className="hover:bg-accent">
              <td className="py-3 pl-4 pr-3 text-sm max-w-[11rem] sm:max-w-md">
                <button type="button" onClick={(event) => onSelect(issue.id, event.currentTarget)} className="w-full text-left rounded focus-ring">
                  <span className="block font-medium truncate">{issue.errorType}</span>
                  <span className="block text-muted-foreground truncate">{issue.errorMessage}</span>
                  <span className="mt-1 block sm:hidden">
                    <IssueStatusBadge status={issue.status} />
                  </span>
                </button>
              </td>
              <td className="hidden sm:table-cell whitespace-nowrap px-3 py-3 text-sm">
                <IssueStatusBadge status={issue.status} />
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {issue.occurrenceCount.toLocaleString()}
              </td>
              <td className="hidden lg:table-cell whitespace-nowrap px-3 py-3 text-sm">
                <time dateTime={issue.firstSeen}>{new Date(issue.firstSeen).toLocaleString()}</time>
              </td>
              <td className="px-3 py-3 text-sm sm:whitespace-nowrap">
                <time dateTime={issue.lastSeen}>{new Date(issue.lastSeen).toLocaleString()}</time>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

type PaginationProps = {
  offset: number;
  total: number;
  disabled: boolean;
  onChange: (offset: number) => void;
};

function IssuesPagination({ offset, total, disabled, onChange }: PaginationProps) {
  return (
    <div className="flex flex-wrap justify-between items-center gap-2">
      <p className="text-sm text-muted-foreground" aria-live="polite">
        Showing {offset + 1} to {Math.min(offset + ISSUES_PAGE_SIZE, total)} of {total.toLocaleString()} issues
      </p>
      <div className="flex gap-2">
        <Button
          disabled={offset === 0 || disabled}
          variant="ghost"
          onClick={() => onChange(Math.max(0, offset - ISSUES_PAGE_SIZE))}
        >
          <IconChevronLeft className="h-4 w-4" />
          Previous
        </Button>
        <Button
          disabled={offset + ISSUES_PAGE_SIZE >= total || disabled}
          variant="ghost"
          onClick={() => onChange(offset + ISSUES_PAGE_SIZE)}
        >
          Next
          <IconChevronRight className="h-4 w-4" />
        </Button>
      </div>
    </div>
  );
}
