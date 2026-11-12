import { Button } from "@components/Button";
import { LoadingState } from "@components/LoadingState";
import { IconChevronLeft, IconChevronRight } from "@tabler/icons-react";
import { twJoin } from "tailwind-merge";
import { TRANSACTIONS_PAGE_SIZE, Transaction, TransactionsResponse, formatDuration } from "./performance";
import { TransactionStatusBadge } from "./TransactionStatusBadge";

type Props = {
  data: TransactionsResponse | undefined;
  isLoading: boolean;
  isStale: boolean;
  offset: number;
  onPageChange: (offset: number) => void;
  onSelect: (transaction: Transaction, opener: HTMLElement) => void;
};

export function TransactionList({ data, isLoading, isStale, offset, onPageChange, onSelect }: Props) {
  if (isLoading) {
    return (
      <div className="py-8">
        <LoadingState size="md" />
      </div>
    );
  }

  if (!data || data.transactions.length === 0) {
    return (
      <div role="status" className="bg-muted rounded py-8 px-4 flex flex-col items-center gap-1 text-center">
        <p>{offset > 0 ? "This page is empty" : "No transactions match these filters"}</p>
        {offset > 0 && (
          <Button variant="ghost" onClick={() => onPageChange(0)}>
            Go to first page
          </Button>
        )}
      </div>
    );
  }

  const total = data.pagination.total;

  return (
    <>
      <div className="overflow-x-auto" aria-busy={isStale}>
        <table className="min-w-full divide-y divide-gray-300">
          <caption className="sr-only">Sampled transactions, newest first</caption>
          <thead>
            <tr>
              <th scope="col" className="py-3 pl-4 pr-3 text-left text-sm font-semibold">
                Transaction
              </th>
              <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
                Duration
              </th>
              <th scope="col" className="hidden sm:table-cell px-3 py-3 text-left text-sm font-semibold">
                Status
              </th>
              <th scope="col" className="hidden md:table-cell px-3 py-3 text-left text-sm font-semibold">
                Release
              </th>
              <th scope="col" className="hidden lg:table-cell px-3 py-3 text-left text-sm font-semibold">
                Environment
              </th>
              <th scope="col" className="hidden lg:table-cell px-3 py-3 text-left text-sm font-semibold">
                Platform
              </th>
              <th scope="col" className="px-3 py-3 text-left text-sm font-semibold">
                Started
              </th>
            </tr>
          </thead>
          <tbody className={twJoin("divide-y divide-gray-600", isStale && "opacity-60")}>
            {data.transactions.map((transaction) => (
              <tr key={transaction.eventId} className="hover:bg-accent">
                <td className="py-3 pl-4 pr-3 text-sm max-w-[11rem] sm:max-w-sm">
                  <button
                    type="button"
                    onClick={(event) => onSelect(transaction, event.currentTarget)}
                    className="w-full text-left rounded focus-ring"
                  >
                    <span className="block font-medium truncate">{transaction.name}</span>
                    <span className="block text-muted-foreground truncate">{transaction.operation}</span>
                    <span className="mt-1 block sm:hidden">
                      <TransactionStatusBadge status={transaction.status} />
                    </span>
                  </button>
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {formatDuration(transaction.durationMs)}
                </td>
                <td className="hidden sm:table-cell whitespace-nowrap px-3 py-3 text-sm">
                  <TransactionStatusBadge status={transaction.status} />
                </td>
                <td className="hidden md:table-cell px-3 py-3 text-sm">{transaction.release || "—"}</td>
                <td className="hidden lg:table-cell px-3 py-3 text-sm">{transaction.environment || "—"}</td>
                <td className="hidden lg:table-cell px-3 py-3 text-sm">{transaction.platform || "—"}</td>
                <td className="px-3 py-3 text-sm sm:whitespace-nowrap">
                  <time dateTime={transaction.timestamp}>{new Date(transaction.timestamp).toLocaleString()}</time>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex flex-wrap justify-between items-center gap-2">
        <p className="text-sm text-muted-foreground" aria-live="polite">
          Showing {offset + 1} to {Math.min(offset + TRANSACTIONS_PAGE_SIZE, total)} of {total.toLocaleString()}{" "}
          transactions
        </p>
        <div className="flex gap-2">
          <Button
            variant="ghost"
            disabled={offset === 0 || isStale}
            onClick={() => onPageChange(Math.max(0, offset - TRANSACTIONS_PAGE_SIZE))}
          >
            <IconChevronLeft className="h-4 w-4" />
            Previous
          </Button>
          <Button
            variant="ghost"
            disabled={offset + TRANSACTIONS_PAGE_SIZE >= total || isStale}
            onClick={() => onPageChange(offset + TRANSACTIONS_PAGE_SIZE)}
          >
            Next
            <IconChevronRight className="h-4 w-4" />
          </Button>
        </div>
      </div>
    </>
  );
}
