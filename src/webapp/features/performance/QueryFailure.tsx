import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { httpStatus } from "@fns/fetch-json";
import { QueryObserverResult, RefetchOptions } from "@tanstack/react-query";

type Props = {
  error: unknown;
  refetch: (options?: RefetchOptions) => Promise<QueryObserverResult<unknown, Error>>;
  onResetFilters: () => void;
  subject?: string;
};

export function QueryFailure({ error, refetch, onResetFilters, subject = "performance data" }: Props) {
  const status = httpStatus(error);

  if (status === 400) {
    return (
      <div role="alert" className="py-8 flex flex-col items-center gap-1 text-center">
        <p className="text-destructive">These filters couldn't be applied.</p>
        <Button variant="ghost" onClick={onResetFilters}>
          Reset filters
        </Button>
      </div>
    );
  }

  if (status === 403) {
    return (
      <p role="alert" className="py-8 text-center text-muted-foreground">
        You don't have access to this app's {subject}.
      </p>
    );
  }

  return (
    <div className="py-8">
      <ErrorState refetch={refetch} />
    </div>
  );
}
