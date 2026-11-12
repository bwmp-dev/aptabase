import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { BuildMode } from "@features/apps";
import { ReleaseFilter } from "@features/releases/releases";
import { retryServerErrors } from "@fns/fetch-json";
import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ISSUE_EVENTS_PAGE_SIZE, IssueEvent, fetchIssueEvents, issueKeys } from "./issues";
import { NativeFrames } from "./NativeFrames";

type Props = {
  appId: string;
  buildMode: BuildMode;
  issueId: string;
  filter: ReleaseFilter;
};

function describeFilter(filter: ReleaseFilter) {
  return [filter.release && `release ${filter.release}`, filter.environment && `environment ${filter.environment}`]
    .filter(Boolean)
    .join(" and ");
}

export function IssueOccurrences({ appId, buildMode, issueId, filter }: Props) {
  const [offset, setOffset] = useState(0);
  const { data, isLoading, isError, refetch, isPlaceholderData } = useQuery({
    queryKey: issueKeys.events(appId, buildMode, issueId, filter, offset),
    queryFn: () => fetchIssueEvents(appId, buildMode, issueId, filter, offset),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });
  const filterText = describeFilter(filter);

  const renderBody = () => {
    if (isLoading) return <LoadingState size="sm" />;
    if (isError || !data) return <ErrorState refetch={refetch} />;
    if (data.events.length === 0) {
      return (
        <p className="text-sm text-muted-foreground">
          No retained occurrences{filterText && ` in ${filterText}`}. Occurrence details are kept for up to 30 days, depending on app retention; raw error logs are kept separately for 180 days.
        </p>
      );
    }

    const total = data.pagination.total;
    return (
      <>
        <ul className="divide-y border-y" aria-busy={isPlaceholderData}>
          {data.events.map((event) => (
            <OccurrenceItem key={event.eventId} appId={appId} buildMode={buildMode} issueId={issueId} event={event} />
          ))}
        </ul>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-xs text-muted-foreground" aria-live="polite">
            {offset + 1}–{Math.min(offset + ISSUE_EVENTS_PAGE_SIZE, total)} of {total.toLocaleString()} retained
          </p>
          <div className="flex gap-1">
            <Button
              variant="ghost"
              size="xs"
              disabled={offset === 0 || isPlaceholderData}
              onClick={() => setOffset(Math.max(0, offset - ISSUE_EVENTS_PAGE_SIZE))}
              aria-label="Previous occurrences"
            >
              <IconChevronLeft className="h-4 w-4" />
            </Button>
            <Button
              variant="ghost"
              size="xs"
              disabled={offset + ISSUE_EVENTS_PAGE_SIZE >= total || isPlaceholderData}
              onClick={() => setOffset(offset + ISSUE_EVENTS_PAGE_SIZE)}
              aria-label="Next occurrences"
            >
              <IconChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      </>
    );
  };

  return (
    <section aria-labelledby="issue-occurrences-heading" className="border-t pt-4 space-y-3">
      <div>
        <h3 id="issue-occurrences-heading" className="text-sm font-semibold">
          Recent occurrences
        </h3>
        <p className="text-xs text-muted-foreground">
          Newest first, kept for up to 30 days, depending on app retention{filterText && `, limited to ${filterText}`}.
        </p>
      </div>
      {renderBody()}
    </section>
  );
}

type OccurrenceItemProps = {
  appId: string;
  buildMode: BuildMode;
  issueId: string;
  event: IssueEvent;
};

function OccurrenceItem({ appId, buildMode, issueId, event }: OccurrenceItemProps) {
  // Native frames are resolved when read, so they are only requested once the occurrence is expanded.
  const [expanded, setExpanded] = useState(false);
  const context = [event.release, event.environment, event.platform, [event.osName, event.osVersion].join(" ").trim()]
    .filter(Boolean)
    .join(" · ");

  return (
    <li>
      <details className="py-2" onToggle={(e) => setExpanded(e.currentTarget.open)}>
        <summary className="cursor-pointer rounded text-sm focus-ring marker:text-muted-foreground">
          <time dateTime={event.timestamp}>{new Date(event.timestamp).toLocaleString()}</time>
          {context && <span className="text-muted-foreground"> · {context}</span>}
        </summary>
        <div className="mt-2 space-y-2 pl-4 text-sm">
          <p className="break-words">{event.errorMessage}</p>
          <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-xs">
            <dt className="text-muted-foreground">Severity</dt>
            <dd className="capitalize">{event.severity || "Not reported"}</dd>
            <dt className="text-muted-foreground">Kind</dt>
            <dd>{event.kind || "Not reported"}</dd>
            <dt className="text-muted-foreground">SDK</dt>
            <dd>{event.sdkVersion || "Not reported"}</dd>
            <dt className="text-muted-foreground">Event ID</dt>
            <dd className="font-mono break-all">{event.eventId}</dd>
          </dl>
          {event.stackTrace && (
            <pre className="bg-muted p-3 rounded-md text-xs font-mono overflow-x-auto whitespace-pre-wrap break-words">
              {event.stackTrace}
            </pre>
          )}
          {expanded && event.hasNativeFrames && (
            <NativeFrames appId={appId} buildMode={buildMode} issueId={issueId} eventId={event.eventId} />
          )}
        </div>
      </details>
    </li>
  );
}
