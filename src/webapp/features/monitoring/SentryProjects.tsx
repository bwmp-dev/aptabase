import { Button } from "@components/Button";
import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { retryServerErrors } from "@fns/fetch-json";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useState } from "react";
import { toast } from "sonner";
import { SentryProject, createSentryProjects, fetchSentryProjects, monitoringKeys } from "./monitoring";

const buildModeLabels = { release: "Release builds", debug: "Debug builds" } as const;

export function SentryProjects({ appId }: { appId: string }) {
  const queryClient = useQueryClient();
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: monitoringKeys.sentry(appId),
    queryFn: () => fetchSentryProjects(appId),
    retry: retryServerErrors,
  });
  const create = useMutation({
    mutationFn: () => createSentryProjects(appId),
    onSuccess: (response) => queryClient.setQueryData(monitoringKeys.sentry(appId), response),
  });

  const renderBody = () => {
    if (isLoading) return <LoadingState size="sm" />;
    if (isError || !data) return <ErrorState refetch={refetch} />;
    if (data.projects.length === 0) {
      return (
        <div className="space-y-2">
          <p className="text-sm text-muted-foreground">No DSNs have been created for this app yet.</p>
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="outline" disabled={create.isPending} onClick={() => create.mutate()}>
              {create.isPending ? "Creating…" : "Create Sentry DSNs"}
            </Button>
            {create.isError && (
              <p role="alert" className="text-sm text-destructive">
                Couldn't create the DSNs. Please try again.
              </p>
            )}
          </div>
        </div>
      );
    }
    return (
      <ul className="space-y-3">
        {data.projects.map((project) => (
          <DsnRow key={project.id} project={project} />
        ))}
      </ul>
    );
  };

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground max-w-2xl">
        Point a supported Sentry SDK at these DSNs to send errors and transactions to this app. They only allow sending
        data and use the app's existing key, so they are only shown to owners. Only a subset of Sentry SDK events is
        supported: attachments, native crash dumps, sessions, profiling and replay are rejected. Set{" "}
        <code>SendDefaultPii = false</code> in the SDK.
      </p>
      {renderBody()}
    </div>
  );
}

function DsnRow({ project }: { project: SentryProject }) {
  const id = useId();
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const timeout = setTimeout(() => setCopied(false), 2000);
    return () => clearTimeout(timeout);
  }, [copied]);

  const copy = () => {
    navigator.clipboard.writeText(project.dsn).then(
      () => setCopied(true),
      () => toast.error("Couldn't copy the DSN.")
    );
  };

  return (
    <li className="space-y-1">
      <label htmlFor={id} className="text-sm font-medium block">
        {buildModeLabels[project.buildMode]}
      </label>
      <div className="flex items-center gap-2 max-w-2xl">
        <input
          id={id}
          readOnly
          value={project.dsn}
          onFocus={(event) => event.target.select()}
          className="h-9 min-w-0 flex-1 rounded-md border border-input bg-muted px-2 font-mono text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        />
        <Button type="button" variant="ghost" size="sm" onClick={copy} aria-label={`Copy ${buildModeLabels[project.buildMode]} DSN`}>
          {copied ? <IconCheck className="h-4 w-4" /> : <IconCopy className="h-4 w-4" />}
          <span className="ml-1 hidden sm:inline">{copied ? "Copied" : "Copy"}</span>
        </Button>
      </div>
    </li>
  );
}
