import { BuildMode } from "@features/apps";
import { fetchJson, fetchNoContent, jsonBody } from "@fns/fetch-json";

export interface MonitoringSettings {
  captureErrors: boolean;
  capturePerformance: boolean;
  forwardRawErrors: boolean;
  monitoringRetentionDays: number;
  localAlertsEnabled: boolean;
  alertCooldownMinutes: number;
}

export interface MonitoringDataPreview {
  counts: { issues: number; events: number; transactions: number; queued: number; alerts: number; symbols: number };
  excludesDeliveredRawLogs: boolean;
}

export type AlertKind = "new_issue" | "regression";

export interface MonitoringAlert {
  eventId: string;
  issueId: string;
  kind: AlertKind;
  createdAt: string;
  readAt: string | null;
}

export interface MonitoringAlertsResponse {
  alerts: MonitoringAlert[];
  pagination: { offset: number; limit: number; total: number };
}

export interface SentryProject {
  id: string;
  buildMode: BuildMode;
  dsn: string;
}

export type NativeSymbolStatus = "pending" | "ready" | "invalid";

export interface NativeSymbolArtifact {
  id: string;
  buildId: string | null;
  status: NativeSymbolStatus;
  sizeBytes: number;
  createdAt: string;
  expiresAt: string;
}

export interface NativeSymbolLimits {
  maxArtifactBytes: number;
  maxAppBytes: number;
  maxAppArtifacts: number;
}

export interface NativeSymbolsResponse {
  enabled: boolean;
  artifacts: NativeSymbolArtifact[];
  limits: NativeSymbolLimits;
}

export const RETENTION_DAYS = { min: 1, max: 30 } as const;
export const COOLDOWN_MINUTES = { min: 1, max: 1440 } as const;
export const ALERTS_PAGE_SIZE = 50;

export const monitoringKeys = {
  app: (appId: string) => ["monitoring", appId] as const,
  settings: (appId: string) => [...monitoringKeys.app(appId), "settings"] as const,
  data: (appId: string) => [...monitoringKeys.app(appId), "data"] as const,
  sentry: (appId: string) => [...monitoringKeys.app(appId), "sentry"] as const,
  allNativeSymbols: (appId: string) => [...monitoringKeys.app(appId), "native-symbols"] as const,
  nativeSymbols: (appId: string, buildMode: BuildMode) => [...monitoringKeys.allNativeSymbols(appId), buildMode] as const,
  alerts: (appId: string, buildMode: BuildMode) => [...monitoringKeys.app(appId), "alerts", buildMode] as const,
  alertsPage: (appId: string, buildMode: BuildMode, offset: number) =>
    [...monitoringKeys.alerts(appId, buildMode), offset] as const,
};

function appPath(appId: string) {
  return `/api/v0/apps/${encodeURIComponent(appId)}`;
}

export function fetchSettings(appId: string) {
  return fetchJson<MonitoringSettings>(`${appPath(appId)}/monitoring/settings`);
}

export function saveSettings(appId: string, settings: MonitoringSettings) {
  return fetchJson<MonitoringSettings>(`${appPath(appId)}/monitoring/settings`, jsonBody("PUT", settings));
}

export function fetchDataPreview(appId: string) {
  return fetchJson<MonitoringDataPreview>(`${appPath(appId)}/monitoring/data`);
}

export function clearMonitoringData(appId: string, confirmAppId: string) {
  return fetchNoContent(`${appPath(appId)}/monitoring/data`, jsonBody("DELETE", { confirmAppId }));
}

export function fetchAlerts(appId: string, buildMode: BuildMode, offset: number) {
  const params = new URLSearchParams({ buildMode, offset: offset.toString(), limit: ALERTS_PAGE_SIZE.toString() });
  return fetchJson<MonitoringAlertsResponse>(`${appPath(appId)}/monitoring/alerts?${params}`);
}

export function markAlertRead(appId: string, buildMode: BuildMode, eventId: string) {
  const params = new URLSearchParams({ buildMode });
  return fetchNoContent(`${appPath(appId)}/monitoring/alerts/${encodeURIComponent(eventId)}/read?${params}`, {
    method: "PATCH",
  });
}

export function fetchSentryProjects(appId: string) {
  return fetchJson<{ projects: SentryProject[] }>(`${appPath(appId)}/sentry-projects`);
}

export function createSentryProjects(appId: string) {
  return fetchJson<{ projects: SentryProject[] }>(`${appPath(appId)}/sentry-projects`, { method: "POST" });
}

export function fetchNativeSymbols(appId: string, buildMode: BuildMode) {
  const params = new URLSearchParams({ buildMode });
  return fetchJson<NativeSymbolsResponse>(`${appPath(appId)}/native-symbols?${params}`);
}

// The raw bytes are the whole body: no multipart wrapper, so no client filename or path is sent.
export function uploadNativeSymbols(appId: string, buildMode: BuildMode, file: Blob) {
  const params = new URLSearchParams({ buildMode });
  return fetchJson<{ artifact: NativeSymbolArtifact }>(`${appPath(appId)}/native-symbols?${params}`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/octet-stream" },
    body: file,
  });
}

export function deleteNativeSymbols(appId: string, buildMode: BuildMode, id: string) {
  const params = new URLSearchParams({ buildMode });
  return fetchNoContent(`${appPath(appId)}/native-symbols/${encodeURIComponent(id)}?${params}`, {
    method: "DELETE",
  });
}
