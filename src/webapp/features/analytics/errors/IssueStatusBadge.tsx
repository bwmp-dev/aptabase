import { IssueStatus } from "./issues";

const styles: Record<IssueStatus, string> = {
  unresolved: "bg-amber-500/15 text-amber-600 dark:text-amber-400",
  resolved: "bg-success/15 text-success",
};

export function IssueStatusBadge({ status }: { status: IssueStatus }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium capitalize ${styles[status]}`}>
      {status}
    </span>
  );
}
