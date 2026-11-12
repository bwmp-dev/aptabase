import { SessionHealthResponse } from "./health";

type Props = {
  reports: SessionHealthResponse["clientReports"];
  filtered: boolean;
};

export function ClientReports({ reports, filtered }: Props) {
  return (
    <section aria-labelledby="client-reports-heading" className="space-y-3">
      <div className="space-y-1">
        <h2 id="client-reports-heading" className="text-lg font-medium">
          SDK discard reports
        </h2>
        <p className="text-sm text-muted-foreground max-w-3xl">
          Counts of events your app's SDK dropped before sending, as the SDK reported them in Sentry client reports.
          They are not events this server rejected and not a share of total traffic. Identical reports from different
          devices may be counted once.
        </p>
        {filtered && (
          <p className="text-sm text-muted-foreground max-w-3xl">
            A report only has a release and environment when an error or transaction was sent with it, so reports sent
            on their own are not included while filtering.
          </p>
        )}
      </div>

      {reports.reports === 0 ? (
        <div role="status" className="bg-muted rounded py-8 px-4 text-center">
          <p>No SDK discard reports in this period</p>
        </div>
      ) : (
        <>
          <p className="text-sm">
            <span className="tabular-nums">{reports.reports.toLocaleString()}</span>{" "}
            {reports.reports === 1 ? "report" : "reports"} listing{" "}
            <span className="tabular-nums">{reports.discardedEvents.toLocaleString()}</span> discarded{" "}
            {reports.discardedEvents === 1 ? "event" : "events"}
          </p>
          {reports.groups.length > 0 && (
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-gray-300">
                <caption className="sr-only">Discarded events by reason and category, largest first.</caption>
                <thead>
                  <tr>
                    <th scope="col" className="py-3 pl-4 pr-3 text-left text-sm font-semibold">
                      Reason
                    </th>
                    <th scope="col" className="px-3 py-3 text-left text-sm font-semibold">
                      Category
                    </th>
                    <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
                      Discarded
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-600">
                  {reports.groups.map((group) => (
                    <tr key={`${group.reason}\n${group.category}`}>
                      <td className="py-3 pl-4 pr-3 text-sm font-mono break-all">{group.reason}</td>
                      <td className="px-3 py-3 text-sm font-mono break-all">{group.category}</td>
                      <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                        {group.quantity.toLocaleString()}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}
