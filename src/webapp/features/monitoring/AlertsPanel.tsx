import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { BuildMode, useApps } from "@features/apps";
import { emptyReleaseFilter } from "@features/releases/releases";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { twJoin } from "tailwind-merge";
import { IssueDetailModal } from "../analytics/errors/IssueDetailModal";
import {
  ALERTS_PAGE_SIZE,
  AlertKind,
  MonitoringAlert,
  fetchAlerts,
  fetchSettings,
  markAlertRead,
  monitoringKeys,
} from "./monitoring";

const kindLabels: Record<AlertKind, string> = {
  new_issue: "New issue",
  regression: "Regression",
};

const kindDescriptions: Record<AlertKind, string> = {
  new_issue: "A new issue was grouped.",
  regression: "A resolved issue occurred again and was reopened.",
};

export function AlertsPanel({ appId }: { appId: string }) {
  const { buildMode } = useApps();

  // Alerts are build-scoped, so paging and the open issue reset with the app or build mode.
  return <AlertsList key={`${appId}:${buildMode}`} appId={appId} buildMode={buildMode} />;
}

function markReadErrorMessage(error: unknown) {
  return httpStatus(error) === 404
    ? "That alert no longer exists. Alerts are kept for 30 days."
    : "Couldn't mark the alert as read. Please try again.";
}

function AlertsList({ appId, buildMode }: { appId: string; buildMode: BuildMode }) {
  const queryClient = useQueryClient();
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<{ issueId: string | null; opener: HTMLElement | null }>({
    issueId: null,
    opener: null,
  });

  const settings = useQuery({
    queryKey: monitoringKeys.settings(appId),
    queryFn: () => fetchSettings(appId),
    retry: retryServerErrors,
  });
  const alerts = useQuery({
    queryKey: monitoringKeys.alertsPage(appId, buildMode, offset),
    queryFn: () => fetchAlerts(appId, buildMode, offset),
    placeholderData: keepPreviousData,
    retry: retryServerErrors,
  });
  const markRead = useMutation({
    mutationFn: (eventId: string) => markAlertRead(appId, buildMode, eventId),
    onSettled: () => queryClient.invalidateQueries({ queryKey: monitoringKeys.alerts(appId, buildMode) }),
  });

  const renderBody = () => {
    if (alerts.isLoading) {
      return (
        <div className="py-12">
          <LoadingState size="lg" />
        </div>
      );
    }
    if (alerts.isError || !alerts.data) {
      return httpStatus(alerts.error) === 403 ? (
        <p role="alert" className="py-12 text-center text-muted-foreground">
          Only the app owner can see alerts.
        </p>
      ) : (
        <div className="py-12">
          <ErrorState refetch={alerts.refetch} />
        </div>
      );
    }
    if (alerts.data.alerts.length === 0) {
      return (
        <div role="status" className="bg-muted rounded py-12 px-4 text-center space-y-1">
          <p className="text-lg">{offset > 0 ? "This page is empty" : "No alerts"}</p>
          {offset > 0 ? (
            <Button variant="ghost" onClick={() => setOffset(0)}>
              Go to first page
            </Button>
          ) : (
            <p className="text-sm text-muted-foreground">
              New issues and regressions from {buildMode} builds will appear here while local alerts are on.
            </p>
          )}
        </div>
      );
    }

    const total = alerts.data.pagination.total;
    return (
      <>
        <ul className={twJoin("divide-y border-y", alerts.isPlaceholderData && "opacity-60")} aria-busy={alerts.isPlaceholderData}>
          {alerts.data.alerts.map((alert) => (
            <AlertItem
              key={alert.eventId}
              alert={alert}
              isMarking={markRead.isPending && markRead.variables === alert.eventId}
              onOpen={(opener) => setSelected({ issueId: alert.issueId, opener })}
              onMarkRead={() => markRead.mutate(alert.eventId)}
            />
          ))}
        </ul>
        <div className="flex flex-wrap justify-between items-center gap-2">
          <p className="text-sm text-muted-foreground" aria-live="polite">
            Showing {offset + 1} to {Math.min(offset + ALERTS_PAGE_SIZE, total)} of {total.toLocaleString()} alerts
          </p>
          <div className="flex gap-2">
            <Button
              variant="ghost"
              disabled={offset === 0 || alerts.isPlaceholderData}
              onClick={() => setOffset(Math.max(0, offset - ALERTS_PAGE_SIZE))}
            >
              <IconChevronLeft className="h-4 w-4" />
              Previous
            </Button>
            <Button
              variant="ghost"
              disabled={offset + ALERTS_PAGE_SIZE >= total || alerts.isPlaceholderData}
              onClick={() => setOffset(offset + ALERTS_PAGE_SIZE)}
            >
              Next
              <IconChevronRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      </>
    );
  };

  return (
    <div className="mt-6 space-y-4">
      <p className="text-sm text-muted-foreground max-w-3xl">
        A local inbox of new issues and regressions from {buildMode} builds. Nothing is emailed or sent outside this
        server. Alerts are kept for 30 days, and marking one as read doesn't resolve the issue.
      </p>
      {settings.data && !settings.data.localAlertsEnabled && (
        <p className="text-sm">
          Local alerts are turned off, so no new alerts are being created.{" "}
          <Link to={`/${appId}/settings`} className="underline underline-offset-2">
            Change this in Settings
          </Link>
          .
        </p>
      )}
      {markRead.isError && (
        <p role="alert" className="text-sm text-destructive">
          {markReadErrorMessage(markRead.error)}
        </p>
      )}
      {renderBody()}
      <IssueDetailModal
        appId={appId}
        buildMode={buildMode}
        issueId={selected.issueId}
        releaseFilter={emptyReleaseFilter}
        onClose={() => setSelected((current) => ({ ...current, issueId: null }))}
        returnFocusTo={selected.opener}
      />
    </div>
  );
}

type ItemProps = {
  alert: MonitoringAlert;
  isMarking: boolean;
  onOpen: (opener: HTMLElement) => void;
  onMarkRead: () => void;
};

function AlertItem({ alert, isMarking, onOpen, onMarkRead }: ItemProps) {
  const unread = alert.readAt === null;
  const openRef = useRef<HTMLButtonElement>(null);
  const markRef = useRef<HTMLButtonElement>(null);
  const markHadFocus = useRef(false);

  // The mark-read button disappears once the alert is read, so keyboard focus moves to the item's other action.
  useEffect(() => {
    if (!unread && markHadFocus.current) {
      markHadFocus.current = false;
      openRef.current?.focus();
    }
  }, [unread]);

  const handleMarkRead = () => {
    markHadFocus.current = document.activeElement === markRef.current;
    onMarkRead();
  };

  return (
    <li className="flex flex-wrap items-center justify-between gap-3 py-3">
      <div className="min-w-0 space-y-0.5">
        <p className="flex flex-wrap items-center gap-2 text-sm">
          <span className={unread ? "font-semibold" : "font-medium text-muted-foreground"}>{kindLabels[alert.kind]}</span>
          {unread && (
            <span className="inline-flex items-center rounded-full bg-primary/15 px-2 py-0.5 text-xs font-medium text-primary">
              Unread
            </span>
          )}
          <time dateTime={alert.createdAt} className="text-muted-foreground">
            {new Date(alert.createdAt).toLocaleString()}
          </time>
        </p>
        <p className="text-sm text-muted-foreground">
          {kindDescriptions[alert.kind]} Issue <span className="font-mono text-xs">{alert.issueId.slice(0, 12)}</span>
        </p>
      </div>
      <div className="flex gap-2">
        <Button ref={openRef} variant="outline" size="sm" onClick={(event) => onOpen(event.currentTarget)}>
          Open issue<span className="sr-only"> for this {kindLabels[alert.kind].toLowerCase()} alert</span>
        </Button>
        {unread && (
          <Button
            ref={markRef}
            variant="ghost"
            size="sm"
            disabled={isMarking}
            aria-busy={isMarking}
            onClick={handleMarkRead}
          >
            {isMarking ? "Marking…" : "Mark read"}
          </Button>
        )}
      </div>
    </li>
  );
}
