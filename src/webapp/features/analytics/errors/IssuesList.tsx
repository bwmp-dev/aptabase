import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { BuildMode, useApps } from "@features/apps";
import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { twJoin } from "tailwind-merge";
import { IssueDetailModal } from "./IssueDetailModal";
import { IssueStatusBadge } from "./IssueStatusBadge";
import { ISSUES_PAGE_SIZE, Issue, IssueStatusFilter, fetchIssues, issueKeys } from "./issues";

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

type Props = {
  appId: string;
};

export function IssuesPanel({ appId }: Props) {
  const { buildMode } = useApps();

  // Remounting on scope change drops filters, paging and the open issue,
  // so nothing from one app or build mode is ever shown or mutated in another.
  return <IssuesList key={`${appId}:${buildMode}`} appId={appId} buildMode={buildMode} />;
}

type ListProps = {
  appId: string;
  buildMode: BuildMode;
};

function IssuesList({ appId, buildMode }: ListProps) {
  const [status, setStatus] = useState<IssueStatusFilter>("unresolved");
  const [offset, setOffset] = useState(0);
  const [selectedIssueId, setSelectedIssueId] = useState<string | null>(null);

  const { data, isLoading, isError, refetch, isPlaceholderData } = useQuery({
    queryKey: issueKeys.list(appId, buildMode, status, offset),
    queryFn: () => fetchIssues(appId, buildMode, status, offset),
    placeholderData: keepPreviousData,
  });

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

    if (isError || !data) {
      return (
        <div className="py-12">
          <ErrorState refetch={refetch} />
        </div>
      );
    }

    if (data.issues.length === 0) {
      return (
        <div className="bg-muted rounded w-full py-12 px-4 flex flex-col space-y-1 items-center justify-center text-center">
          {offset > 0 ? (
            <>
              <p className="text-lg">This page is empty</p>
              <Button variant="ghost" onClick={() => setOffset(0)}>
                Go to first page
              </Button>
            </>
          ) : (
            <>
              <p className="text-lg">{emptyMessages[status]}</p>
              <p className="text-sm text-muted-foreground">
                New error reports from {buildMode} builds will appear here as issues.
              </p>
            </>
          )}
        </div>
      );
    }

    return (
      <>
        <IssuesTable issues={data.issues} isStale={isPlaceholderData} onSelect={setSelectedIssueId} />
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
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div
          role="group"
          aria-label="Issue status"
          className="inline-flex items-center rounded-md bg-muted p-1 text-muted-foreground"
        >
          {statusOptions.map((option) => (
            <button
              key={option.value}
              type="button"
              aria-pressed={status === option.value}
              onClick={() => changeStatus(option.value)}
              className={twJoin(
                "rounded-sm px-3 py-1.5 text-sm font-medium transition-colors focus-ring",
                status === option.value ? "bg-background text-foreground shadow-sm" : "hover:text-foreground"
              )}
            >
              {option.label}
            </button>
          ))}
        </div>
        <p className="text-sm text-muted-foreground max-w-xl">
          Identical error reports from {buildMode} builds are grouped into issues. Counts are lifetime totals and ignore
          the date range. Only reports received since issue tracking was enabled are included.
        </p>
      </div>

      {data && !data.canManage && (
        <p className="text-sm text-muted-foreground">
          You have read-only access to this app. Only app owners can resolve or reopen issues.
        </p>
      )}

      {renderContent()}

      <IssueDetailModal
        appId={appId}
        buildMode={buildMode}
        issueId={selectedIssueId}
        onClose={() => setSelectedIssueId(null)}
      />
    </div>
  );
}

type TableProps = {
  issues: Issue[];
  isStale: boolean;
  onSelect: (issueId: string) => void;
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
            <th scope="col" className="px-3 py-3.5 text-left text-sm font-semibold">
              Status
            </th>
            <th scope="col" className="px-3 py-3.5 text-right text-sm font-semibold">
              Occurrences
            </th>
            <th scope="col" className="px-3 py-3.5 text-left text-sm font-semibold">
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
              <td className="py-3 pl-4 pr-3 text-sm max-w-md">
                <button type="button" onClick={() => onSelect(issue.id)} className="w-full text-left rounded focus-ring">
                  <span className="block font-medium truncate">{issue.errorType}</span>
                  <span className="block text-muted-foreground truncate">{issue.errorMessage}</span>
                </button>
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm">
                <IssueStatusBadge status={issue.status} />
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {issue.occurrenceCount.toLocaleString()}
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm">
                <time dateTime={issue.firstSeen}>{new Date(issue.firstSeen).toLocaleString()}</time>
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm">
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
      <p className="text-sm text-muted-foreground">
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
