import { SessionCounts, formatCrashFreeRate } from "./health";

type Props = {
  sessions: SessionCounts;
  retentionDays: number;
};

export function SessionSummary({ sessions, retentionDays }: Props) {
  const coverage = [
    ["Sessions observed", sessions.observed],
    ["Counted in the rate", sessions.eligible],
    ["Not counted", sessions.incomplete],
    ["Still active", sessions.active],
    ["Ended abnormally", sessions.abnormal],
    ["Start not received", sessions.observed - sessions.withStart],
    ["Crashed", sessions.crashed],
    ["Crashed and counted", sessions.eligibleCrashed],
    ["With errors", sessions.errored],
  ] as const;

  return (
    <section aria-labelledby="crash-free-heading" className="space-y-4">
      <div className="space-y-1">
        <h2 id="crash-free-heading" className="text-lg font-medium">
          Crash-free completed sessions
        </h2>
        <p className="text-sm text-muted-foreground max-w-3xl">
          Only sessions whose start was received and that ended as exited or crashed are counted. Active, abnormal and
          partially received sessions are left out, so this is not crash-free usage overall, and there is no per-user
          rate. Sessions are kept for up to {retentionDays} days.
        </p>
      </div>

      {sessions.observed === 0 ? (
        <div role="status" className="bg-muted rounded py-8 px-4 text-center space-y-1">
          <p>No sessions in this period</p>
          <p className="text-sm text-muted-foreground">
            Sessions appear once a Sentry SDK with session tracking sends updates to this app's DSN while error capture
            is on.
          </p>
        </div>
      ) : (
        <div className="flex flex-col gap-6 md:flex-row md:items-start md:gap-12">
          <div className="md:w-64 shrink-0" data-testid="crash-free-rate">
            {sessions.crashFreeRate === null ? (
              <>
                <p className="text-xl font-medium">No completed sessions</p>
                <p className="text-sm text-muted-foreground">
                  A rate needs at least one session with a received start and an exited or crashed end.{" "}
                  {sessions.observed.toLocaleString()} observed, none complete.
                </p>
              </>
            ) : (
              <>
                <p className="text-4xl font-semibold tabular-nums">{formatCrashFreeRate(sessions.crashFreeRate)}</p>
                <p className="text-sm text-muted-foreground">
                  {(sessions.eligible - sessions.eligibleCrashed).toLocaleString()} of{" "}
                  {sessions.eligible.toLocaleString()} completed sessions ended without a crash.
                </p>
              </>
            )}
          </div>

          <div className="space-y-2 min-w-0">
            <h3 className="text-sm font-semibold">Coverage</h3>
            <dl className="grid grid-cols-[minmax(0,1fr)_max-content] gap-x-6 gap-y-1 text-sm max-w-sm">
              {coverage.map(([label, value]) => (
                <div key={label} className="contents">
                  <dt className="text-muted-foreground">{label}</dt>
                  <dd className="text-right tabular-nums">{value.toLocaleString()}</dd>
                </div>
              ))}
            </dl>
            <p className="text-xs text-muted-foreground max-w-sm">
              A session can be both active and missing its start, so the reasons for not counting can overlap.
            </p>
          </div>
        </div>
      )}
    </section>
  );
}
