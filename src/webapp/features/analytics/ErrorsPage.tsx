import { Page, PageHeading } from "@components/Page";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@components/Tabs";
import { useCurrentApp } from "@features/apps";
import { AlertsPanel } from "@features/monitoring/AlertsPanel";
import { releaseFilterFromSearch } from "@features/releases/releases";
import { Navigate, useSearchParams } from "react-router-dom";
import { BetaNotice } from "./errors/BetaNotice";
import { ErrorsList } from "./errors/ErrorsList";
import { IssuesPanel } from "./errors/IssuesList";
import { BuildModeSelector } from "./mode/BuildModeSelector";

const LOGS_VIEW = "logs";
const ALERTS_VIEW = "alerts";

Component.displayName = "ErrorsPage";
export function Component() {
  const app = useCurrentApp();
  const [searchParams, setSearchParams] = useSearchParams();

  if (!app) return <Navigate to="/" />;

  const requestedView = searchParams.get("view");
  const view =
    requestedView === LOGS_VIEW || (requestedView === ALERTS_VIEW && app.hasOwnership) ? requestedView : "issues";

  const changeView = (value: string) => {
    const params = new URLSearchParams(searchParams);
    if (value === LOGS_VIEW || value === ALERTS_VIEW) {
      params.set("view", value);
    } else {
      params.delete("view");
    }
    setSearchParams(params);
  };

  return (
    <Page title="Error Logs">
      <div className="flex justify-between items-center">
        <PageHeading
          title="Errors"
          subtitle="Track and debug crashes and errors in your app"
        />
        <BuildModeSelector />
      </div>
      <BetaNotice />
      <Tabs value={view} onValueChange={changeView}>
        <TabsList>
          <TabsTrigger value="issues">Issues</TabsTrigger>
          <TabsTrigger value={LOGS_VIEW}>Error logs</TabsTrigger>
          {app.hasOwnership && <TabsTrigger value={ALERTS_VIEW}>Alerts</TabsTrigger>}
        </TabsList>
        <TabsContent value="issues">
          <IssuesPanel appId={app.id} initialFilter={releaseFilterFromSearch(searchParams)} />
        </TabsContent>
        <TabsContent value={LOGS_VIEW}>
          <p className="mt-6 text-sm text-muted-foreground max-w-3xl">
            Raw error logs are kept for 180 days, separately from issue occurrence details.
          </p>
          <ErrorsList appId={app.id} />
        </TabsContent>
        {app.hasOwnership && (
          <TabsContent value={ALERTS_VIEW}>
            <AlertsPanel appId={app.id} />
          </TabsContent>
        )}
      </Tabs>
    </Page>
  );
}
