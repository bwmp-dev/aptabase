import { TransactionStatus } from "./performance";

const styles: Record<TransactionStatus, string> = {
  ok: "bg-success/15 text-success",
  error: "bg-destructive/15 text-destructive",
  cancelled: "bg-muted text-muted-foreground",
};

const labels: Record<TransactionStatus, string> = {
  ok: "OK",
  error: "Error",
  cancelled: "Cancelled",
};

export function TransactionStatusBadge({ status }: { status: TransactionStatus }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${styles[status]}`}>
      {labels[status]}
    </span>
  );
}
