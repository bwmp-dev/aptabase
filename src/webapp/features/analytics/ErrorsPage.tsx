import { Page, PageHeading } from "@components/Page";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@components/Tabs";
import { useCurrentApp } from "@features/apps";
import { Navigate, useSearchParams } from "react-router-dom";
import { BetaNotice } from "./errors/BetaNotice";
import { ErrorsList } from "./errors/ErrorsList";
import { IssuesPanel } from "./errors/IssuesList";
import { BuildModeSelector } from "./mode/BuildModeSelector";

const LOGS_VIEW = "logs";

Component.displayName = "ErrorsPage";
export function Component() {
  const app = useCurrentApp();
  const [searchParams, setSearchParams] = useSearchParams();

  if (!app) return <Navigate to="/" />;

  const view = searchParams.get("view") === LOGS_VIEW ? LOGS_VIEW : "issues";

  const changeView = (value: string) => {
    const params = new URLSearchParams(searchParams);
    if (value === LOGS_VIEW) {
      params.set("view", LOGS_VIEW);
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
        </TabsList>
        <TabsContent value="issues">
          <IssuesPanel appId={app.id} />
        </TabsContent>
        <TabsContent value={LOGS_VIEW}>
          <ErrorsList appId={app.id} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
