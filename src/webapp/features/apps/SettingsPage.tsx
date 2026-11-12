import { Page, PageHeading } from "@components/Page";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@components/Tabs";
import { useCurrentApp } from "@features/apps";
import { MonitoringSettingsPanel } from "@features/monitoring/MonitoringSettingsPanel";
import { Navigate } from "react-router-dom";
import { AppSharing } from "./AppSharing";
import { DangerZone } from "./DangerZone";
import { GeneralSettings } from "./GeneralSettings";
import { OwnershipTransfer } from "./OwnershipTransfer";

Component.displayName = "SettingsPage";
export function Component() {
  const app = useCurrentApp();

  if (!app || !app.hasOwnership) return <Navigate to="/" />;

  return (
    <Page title={`${app.name} - Settings`}>
      <PageHeading title="Settings" subtitle="Manage your app settings" />

      <Tabs defaultValue="general" className="mt-8">
        {/* Five tabs don't fit a phone width; the row scrolls instead of widening the page. */}
        <TabsList className="overflow-x-auto overflow-y-hidden [&>button]:shrink-0 [&>button]:whitespace-nowrap">
          <TabsTrigger value="general">General</TabsTrigger>
          <TabsTrigger value="monitoring">Monitoring</TabsTrigger>
          <TabsTrigger value="sharing">Sharing</TabsTrigger>
          <TabsTrigger value="ownership">Ownership Transfer</TabsTrigger>
          <TabsTrigger value="danger">Danger Zone</TabsTrigger>
        </TabsList>
        <TabsContent value="general">
          <GeneralSettings app={app} />
        </TabsContent>
        <TabsContent value="monitoring">
          <MonitoringSettingsPanel key={app.id} appId={app.id} />
        </TabsContent>
        <TabsContent value="sharing">
          <AppSharing app={app} />
        </TabsContent>
        <TabsContent value="ownership">
          <OwnershipTransfer app={app} />
        </TabsContent>
        <TabsContent value="danger">
          <DangerZone app={app} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
