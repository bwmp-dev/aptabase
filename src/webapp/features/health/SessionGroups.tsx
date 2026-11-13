import { MAX_HEALTH_GROUPS, SessionGroup, formatCrashFreeRate } from "./health";

type Props = {
  groups: SessionGroup[];
  truncated: boolean;
};

function Unset() {
  return <span className="italic text-muted-foreground">Not set</span>;
}

export function SessionGroups({ groups, truncated }: Props) {
  return (
    <section aria-labelledby="session-groups-heading" className="space-y-3">
      <h2 id="session-groups-heading" className="text-lg font-medium">
        By release and environment
      </h2>
      {truncated && (
        <p className="text-sm text-muted-foreground max-w-3xl">
          Showing the {MAX_HEALTH_GROUPS} release and environment pairs with the most sessions. The totals above include
          every matching session.
        </p>
      )}
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-300">
          <caption className="sr-only">Sessions by release and environment, most sessions first.</caption>
          <thead>
            <tr>
              <th scope="col" className="py-3 pl-4 pr-3 text-left text-sm font-semibold">
                Release and environment
              </th>
              <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
                Crash-free
              </th>
              <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
                Counted
              </th>
              <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
                Crashed
              </th>
              <th scope="col" className="hidden sm:table-cell px-3 py-3 text-right text-sm font-semibold">
                Unhandled
              </th>
              <th scope="col" className="hidden sm:table-cell px-3 py-3 text-right text-sm font-semibold">
                Not counted
              </th>
              <th scope="col" className="hidden md:table-cell px-3 py-3 text-right text-sm font-semibold">
                Observed
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-600">
            {groups.map((group) => (
              <tr key={`${group.release}\n${group.environment}`}>
                <td className="py-3 pl-4 pr-3 text-sm max-w-[9rem] sm:max-w-md">
                  <span className="block font-medium truncate">{group.release || <Unset />}</span>
                  <span className="block text-muted-foreground truncate">{group.environment || <Unset />}</span>
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.crashFreeRate === null ? (
                    <span className="text-muted-foreground">None complete</span>
                  ) : (
                    formatCrashFreeRate(group.crashFreeRate)
                  )}
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.eligible.toLocaleString()}
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.crashed.toLocaleString()}
                </td>
                <td className="hidden sm:table-cell whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.unhandled.toLocaleString()}
                </td>
                <td className="hidden sm:table-cell whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.incomplete.toLocaleString()}
                </td>
                <td className="hidden md:table-cell whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {group.observed.toLocaleString()}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
