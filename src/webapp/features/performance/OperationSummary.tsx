import { LoadingState } from "@components/LoadingState";
import { twJoin } from "tailwind-merge";
import { OperationGroup, formatDuration } from "./performance";

type Props = {
  groups: OperationGroup[] | undefined;
  isLoading: boolean;
  isStale: boolean;
  selected: { name: string; operation: string } | null;
  onSelect: (group: OperationGroup) => void;
};

function isSelected(group: OperationGroup, selected: Props["selected"]) {
  return !!selected && selected.name === group.name && selected.operation === group.operation;
}

export function OperationSummary({ groups, isLoading, isStale, selected, onSelect }: Props) {
  if (isLoading) {
    return (
      <div className="py-8">
        <LoadingState size="md" />
      </div>
    );
  }

  if (!groups || groups.length === 0) {
    return (
      <div role="status" className="bg-muted rounded py-8 px-4 text-center space-y-1">
        <p>No sampled operations in this period</p>
        <p className="text-sm text-muted-foreground">
          Operations appear here once your app sends transactions to <code>/api/v0/transaction</code>.
        </p>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto" aria-busy={isStale}>
      <table className="min-w-full divide-y divide-gray-300">
        <caption className="sr-only">
          Sampled operations, most samples first. Select an operation to filter transactions.
        </caption>
        <thead>
          <tr>
            <th scope="col" className="py-3 pl-4 pr-3 text-left text-sm font-semibold">
              Operation
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              Samples
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              Errors
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              p50
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              p95
            </th>
            <th scope="col" className="hidden md:table-cell px-3 py-3 text-right text-sm font-semibold">
              Average
            </th>
          </tr>
        </thead>
        <tbody className={twJoin("divide-y divide-gray-600", isStale && "opacity-60")}>
          {groups.map((group) => (
            <tr
              key={`${group.operation}\n${group.name}`}
              className={isSelected(group, selected) ? "bg-accent" : "hover:bg-accent"}
            >
              <td className="py-3 pl-4 pr-3 text-sm max-w-[11rem] sm:max-w-md">
                <button
                  type="button"
                  aria-pressed={isSelected(group, selected)}
                  onClick={() => onSelect(group)}
                  className="w-full text-left rounded focus-ring"
                >
                  <span className="sr-only">Filter transactions by </span>
                  <span className="block font-medium truncate">{group.name}</span>
                  <span className="block text-muted-foreground truncate">{group.operation}</span>
                </button>
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {group.count.toLocaleString()}
              </td>
              <td
                className={twJoin(
                  "whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums",
                  group.errorCount > 0 && "text-destructive"
                )}
              >
                {group.errorCount.toLocaleString()}
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {formatDuration(group.p50Ms)}
              </td>
              <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {formatDuration(group.p95Ms)}
              </td>
              <td className="hidden md:table-cell whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                {formatDuration(group.avgMs)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
