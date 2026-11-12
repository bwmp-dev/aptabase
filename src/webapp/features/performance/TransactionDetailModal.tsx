import { Button } from "@components/Button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@components/Dialog";
import { LoadingState } from "@components/LoadingState";
import { BuildMode } from "@features/apps";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { twJoin } from "tailwind-merge";
import {
  MAX_VISUAL_SPAN_DEPTH,
  Span,
  Transaction,
  fetchTransaction,
  flattenSpanTree,
  formatDuration,
  performanceKeys,
  spanBar,
  spanIndentRem,
} from "./performance";
import { TransactionStatusBadge } from "./TransactionStatusBadge";

type Props = {
  appId: string;
  buildMode: BuildMode;
  eventId: string | null;
  onClose: () => void;
  returnFocusTo: HTMLElement | null;
};

export function TransactionDetailModal({ appId, buildMode, eventId, onClose, returnFocusTo }: Props) {
  // Without a Radix trigger, closing would leave focus on the body; return it to the opener when it still exists.
  const restoreFocus = (event: Event) => {
    if (returnFocusTo?.isConnected) {
      event.preventDefault();
      returnFocusTo.focus();
    }
  };

  return (
    <Dialog open={eventId !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto" onCloseAutoFocus={restoreFocus}>
        {eventId && <TransactionDetail key={eventId} appId={appId} buildMode={buildMode} eventId={eventId} />}
      </DialogContent>
    </Dialog>
  );
}

function loadErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 404:
      return "This transaction is no longer available. Transactions are kept for up to 30 days, depending on app retention.";
    case 403:
      return "You don't have access to this app's performance data.";
    default:
      return "Couldn't load this transaction.";
  }
}

function TransactionDetail({ appId, buildMode, eventId }: { appId: string; buildMode: BuildMode; eventId: string }) {
  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: performanceKeys.detail(appId, buildMode, eventId),
    queryFn: () => fetchTransaction(appId, buildMode, eventId),
    retry: retryServerErrors,
  });

  if (isLoading) {
    return (
      <>
        <DialogHeader>
          <DialogTitle>Transaction details</DialogTitle>
          <DialogDescription>Loading transaction…</DialogDescription>
        </DialogHeader>
        <div className="py-8">
          <LoadingState size="md" />
        </div>
      </>
    );
  }

  if (isError || !data) {
    const status = httpStatus(error);
    return (
      <>
        <DialogHeader>
          <DialogTitle>Transaction details</DialogTitle>
          <DialogDescription className="flex items-center gap-2 text-destructive" role="alert">
            <IconAlertTriangle className="h-4 w-4 shrink-0" />
            {loadErrorMessage(error)}
          </DialogDescription>
        </DialogHeader>
        {status !== 404 && status !== 403 && (
          <div>
            <Button variant="outline" size="sm" onClick={() => refetch()}>
              Try again
            </Button>
          </div>
        )}
      </>
    );
  }

  const { transaction, spans } = data;

  return (
    <>
      <DialogHeader className="pr-6">
        <DialogTitle className="break-words leading-snug">{transaction.name}</DialogTitle>
        <DialogDescription className="break-words">{transaction.operation}</DialogDescription>
      </DialogHeader>
      <TransactionFacts transaction={transaction} />
      <SpanTree spans={spans} totalMs={transaction.durationMs} />
    </>
  );
}

function TransactionFacts({ transaction }: { transaction: Transaction }) {
  return (
    <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
      <dt className="text-muted-foreground">Status</dt>
      <dd>
        <TransactionStatusBadge status={transaction.status} />
      </dd>
      <dt className="text-muted-foreground">Duration</dt>
      <dd className="tabular-nums">{formatDuration(transaction.durationMs)}</dd>
      <dt className="text-muted-foreground">Started</dt>
      <dd>
        <time dateTime={transaction.timestamp}>{new Date(transaction.timestamp).toLocaleString()}</time>
      </dd>
      <dt className="text-muted-foreground">Release</dt>
      <dd>{transaction.release || "Not reported"}</dd>
      <dt className="text-muted-foreground">Environment</dt>
      <dd>{transaction.environment || "Not reported"}</dd>
      <dt className="text-muted-foreground">Platform</dt>
      <dd>{transaction.platform || "Not reported"}</dd>
      <dt className="text-muted-foreground">Event ID</dt>
      <dd className="font-mono text-xs break-all self-center">{transaction.eventId}</dd>
    </dl>
  );
}

function barStyle(span: Span, totalMs: number) {
  const { left, width } = spanBar(span.startOffsetMs, span.durationMs, totalMs);
  return { left: `${left}%`, width: `${width}%` };
}

function SpanTree({ spans, totalMs }: { spans: Span[]; totalMs: number }) {
  const nodes = flattenSpanTree(spans);

  return (
    <section aria-labelledby="spans-heading" className="border-t pt-4 space-y-3">
      <h3 id="spans-heading" className="text-sm font-semibold">
        Spans ({spans.length})
      </h3>
      {nodes.length === 0 ? (
        <p className="text-sm text-muted-foreground">This transaction was reported without spans.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <caption className="sr-only">Spans in start order; nested spans are indented under their parent</caption>
            <thead>
              <tr className="text-left text-muted-foreground">
                <th scope="col" className="py-2 pr-3 font-medium">
                  Span
                </th>
                <th scope="col" className="py-2 px-3 font-medium text-right">
                  Start
                </th>
                <th scope="col" className="py-2 px-3 font-medium text-right">
                  Duration
                </th>
                <th scope="col" className="hidden sm:table-cell py-2 px-3 font-medium">
                  Status
                </th>
                <th scope="col" className="hidden md:table-cell py-2 pl-3 font-medium w-1/3">
                  <span className="sr-only">Timeline</span>
                </th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {nodes.map((span) => (
                <tr key={span.spanId}>
                  <td className="py-2 pr-3 max-w-[12rem] sm:max-w-xs">
                    <div style={{ paddingLeft: `${spanIndentRem(span.depth)}rem` }}>
                      <span className="block truncate font-medium">
                        {span.depth > 0 && (
                          <span
                            className={
                              span.depth > MAX_VISUAL_SPAN_DEPTH ? "text-xs font-normal text-muted-foreground" : "sr-only"
                            }
                          >
                            Level {span.depth + 1}:{" "}
                          </span>
                        )}
                        {span.name}
                      </span>
                      <span className="block truncate text-muted-foreground">{span.operation}</span>
                      <span className="mt-1 block sm:hidden">
                        <TransactionStatusBadge status={span.status} />
                      </span>
                    </div>
                  </td>
                  <td className="py-2 px-3 text-right tabular-nums whitespace-nowrap">
                    +{formatDuration(span.startOffsetMs)}
                  </td>
                  <td className="py-2 px-3 text-right tabular-nums whitespace-nowrap">
                    {formatDuration(span.durationMs)}
                  </td>
                  <td className="hidden sm:table-cell py-2 px-3">
                    <TransactionStatusBadge status={span.status} />
                  </td>
                  <td className="hidden md:table-cell py-2 pl-3" aria-hidden="true">
                    <div className="relative h-2 overflow-hidden rounded-sm bg-muted">
                      <div
                        className={twJoin(
                          "absolute inset-y-0 rounded-sm",
                          span.status === "error" ? "bg-destructive" : "bg-primary"
                        )}
                        style={barStyle(span, totalMs)}
                      />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
