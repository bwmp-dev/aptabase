import { Button } from "@components/Button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@components/Dialog";
import { LoadingState } from "@components/LoadingState";
import { BuildMode } from "@features/apps";
import { ReleaseFilter } from "@features/releases/releases";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { IconAlertTriangle, IconCheck, IconCopy } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { toast } from "sonner";
import { IssueOccurrences } from "./IssueOccurrences";
import { IssueStatusBadge } from "./IssueStatusBadge";
import { Issue, IssueStatus, fetchIssue, issueKeys, updateIssueStatus } from "./issues";

type Props = {
  appId: string;
  buildMode: BuildMode;
  issueId: string | null;
  releaseFilter: ReleaseFilter;
  onClose: () => void;
  returnFocusTo: HTMLElement | null;
};

export function IssueDetailModal({ appId, buildMode, issueId, releaseFilter, onClose, returnFocusTo }: Props) {
  // Without a Radix trigger, closing would leave focus on the body; return it to the opener when it still exists.
  const restoreFocus = (event: Event) => {
    if (returnFocusTo?.isConnected) {
      event.preventDefault();
      returnFocusTo.focus();
    }
  };

  return (
    <Dialog open={issueId !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto" onCloseAutoFocus={restoreFocus}>
        {issueId && (
          <IssueDetail
            key={issueId}
            appId={appId}
            buildMode={buildMode}
            issueId={issueId}
            releaseFilter={releaseFilter}
          />
        )}
      </DialogContent>
    </Dialog>
  );
}

function loadErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 404:
      return "This issue no longer exists.";
    case 403:
      return "You don't have access to this issue.";
    default:
      return "Couldn't load this issue.";
  }
}

function updateErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 404:
      return "This issue no longer exists.";
    case 403:
      return "Only app owners can change the status of an issue.";
    default:
      return "Couldn't update the issue. Please try again.";
  }
}

type DetailProps = {
  appId: string;
  buildMode: BuildMode;
  issueId: string;
  releaseFilter: ReleaseFilter;
};

function IssueDetail({ appId, buildMode, issueId, releaseFilter }: DetailProps) {
  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: issueKeys.detail(appId, buildMode, issueId),
    queryFn: () => fetchIssue(appId, buildMode, issueId),
    retry: retryServerErrors,
  });

  if (isLoading) {
    return (
      <>
        <DialogHeader>
          <DialogTitle>Issue details</DialogTitle>
          <DialogDescription>Loading issue…</DialogDescription>
        </DialogHeader>
        <div className="py-8">
          <LoadingState size="md" />
        </div>
      </>
    );
  }

  if (isError || !data) {
    const canRetry = httpStatus(error) !== 404 && httpStatus(error) !== 403;
    return (
      <>
        <DialogHeader>
          <DialogTitle>Issue details</DialogTitle>
          <DialogDescription className="flex items-center gap-2 text-destructive" role="alert">
            <IconAlertTriangle className="h-4 w-4" />
            {loadErrorMessage(error)}
          </DialogDescription>
        </DialogHeader>
        {canRetry && (
          <div>
            <Button variant="outline" size="sm" onClick={() => refetch()}>
              Try again
            </Button>
          </div>
        )}
      </>
    );
  }

  const { issue, canManage } = data;

  return (
    <>
      <DialogHeader className="pr-6">
        <DialogTitle className="break-words leading-snug">{issue.errorType}</DialogTitle>
        <DialogDescription className="break-words">{issue.errorMessage}</DialogDescription>
      </DialogHeader>

      <IssueFacts issue={issue} />

      <section aria-labelledby="issue-status-heading" className="border-t pt-4 space-y-2">
        <h3 id="issue-status-heading" className="text-sm font-semibold">
          Status
        </h3>
        {canManage ? (
          <IssueStatusActions appId={appId} buildMode={buildMode} issue={issue} />
        ) : (
          <p className="text-sm text-muted-foreground">
            You have read-only access to this app. Only app owners can resolve or reopen issues.
          </p>
        )}
      </section>

      {issue.stackTrace && <StackTrace stackTrace={issue.stackTrace} />}

      <IssueOccurrences appId={appId} buildMode={buildMode} issueId={issue.id} filter={releaseFilter} />
    </>
  );
}

const statusActions: Record<IssueStatus, { hint: string; next: IssueStatus; label: string; pendingLabel: string }> = {
  unresolved: {
    hint: "Resolve this issue once it is fixed. It reopens automatically if the error occurs again afterwards.",
    next: "resolved",
    label: "Resolve issue",
    pendingLabel: "Resolving…",
  },
  resolved: {
    hint: "This issue reopens automatically if the error occurs again after it was resolved. Delayed reports of earlier occurrences and retried deliveries don't reopen it. You can also reopen it yourself.",
    next: "unresolved",
    label: "Reopen issue",
    pendingLabel: "Reopening…",
  },
};

type StatusActionsProps = {
  appId: string;
  buildMode: BuildMode;
  issue: Issue;
};

function IssueStatusActions({ appId, buildMode, issue }: StatusActionsProps) {
  const queryClient = useQueryClient();
  const action = statusActions[issue.status];

  const mutation = useMutation({
    mutationFn: (status: IssueStatus) => updateIssueStatus(appId, buildMode, issue.id, status),
    onSuccess: (response) => {
      queryClient.setQueryData(issueKeys.detail(appId, buildMode, issue.id), response);
      return queryClient.invalidateQueries({ queryKey: [...issueKeys.scope(appId, buildMode), "list"] });
    },
  });

  return (
    <>
      <p className="text-sm text-muted-foreground">{action.hint}</p>
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant={action.next === "resolved" ? "default" : "outline"}
          size="sm"
          disabled={mutation.isPending}
          aria-busy={mutation.isPending}
          onClick={() => mutation.mutate(action.next)}
        >
          {mutation.isPending ? action.pendingLabel : action.label}
        </Button>
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive flex items-center gap-1">
            <IconAlertTriangle className="h-4 w-4" />
            {updateErrorMessage(mutation.error)}
          </p>
        )}
      </div>
    </>
  );
}

function formatDate(iso: string) {
  return new Date(iso).toLocaleString();
}

function IssueFacts({ issue }: { issue: Issue }) {
  return (
    <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
      <dt className="text-muted-foreground">Status</dt>
      <dd>
        <IssueStatusBadge status={issue.status} />
      </dd>
      <dt className="text-muted-foreground">Occurrences</dt>
      <dd className="tabular-nums">{issue.occurrenceCount.toLocaleString()} (lifetime)</dd>
      <dt className="text-muted-foreground">First seen</dt>
      <dd>
        <time dateTime={issue.firstSeen}>{formatDate(issue.firstSeen)}</time>
      </dd>
      <dt className="text-muted-foreground">Last seen</dt>
      <dd>
        <time dateTime={issue.lastSeen}>{formatDate(issue.lastSeen)}</time>
      </dd>
      {issue.resolvedAt && (
        <>
          <dt className="text-muted-foreground">Resolved</dt>
          <dd>
            <time dateTime={issue.resolvedAt}>{formatDate(issue.resolvedAt)}</time>
          </dd>
        </>
      )}
      <dt className="text-muted-foreground">Fingerprint</dt>
      <dd className="font-mono text-xs break-all self-center">{issue.id}</dd>
    </dl>
  );
}

function StackTrace({ stackTrace }: { stackTrace: string }) {
  const [justCopied, setJustCopied] = useState(false);

  useEffect(() => {
    if (!justCopied) return;
    const timeout = setTimeout(() => setJustCopied(false), 2000);
    return () => clearTimeout(timeout);
  }, [justCopied]);

  const copy = () => {
    navigator.clipboard.writeText(stackTrace).then(
      () => setJustCopied(true),
      () => toast.error("Couldn't copy the stack trace.")
    );
  };

  return (
    <section aria-labelledby="issue-stack-heading">
      <div className="flex items-center justify-between mb-3">
        <h3 id="issue-stack-heading" className="text-sm font-semibold">
          Stack trace
        </h3>
        <button
          type="button"
          onClick={copy}
          className="flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
        >
          {justCopied ? <IconCheck className="h-4 w-4" /> : <IconCopy className="h-4 w-4" />}
          <span>{justCopied ? "Copied!" : "Copy"}</span>
        </button>
      </div>
      <pre className="bg-muted p-4 rounded-md text-xs font-mono overflow-x-auto whitespace-pre-wrap break-words">
        {stackTrace}
      </pre>
    </section>
  );
}
