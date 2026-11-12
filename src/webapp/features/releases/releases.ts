import { BuildMode } from "@features/apps";
import { fetchJson } from "@fns/fetch-json";

export interface ObservedRelease {
  release: string;
  environment: string;
  errorCount: number;
  transactionCount: number;
  firstSeen: string;
  lastSeen: string;
}

export type ReleaseFilter = { release: string; environment: string };

export const emptyReleaseFilter: ReleaseFilter = { release: "", environment: "" };

export const releaseKeys = {
  list: (appId: string, buildMode: BuildMode) => ["releases", appId, buildMode] as const,
};

export function fetchReleases(appId: string, buildMode: BuildMode) {
  const params = new URLSearchParams({ buildMode });
  return fetchJson<{ releases: ObservedRelease[] }>(`/api/v0/apps/${encodeURIComponent(appId)}/releases?${params}`);
}

export function appendReleaseFilter(params: URLSearchParams, filter: ReleaseFilter) {
  if (filter.release) params.set("release", filter.release);
  if (filter.environment) params.set("environment", filter.environment);
}

export function releaseFilterFromSearch(search: URLSearchParams): ReleaseFilter {
  return { release: search.get("release") ?? "", environment: search.get("environment") ?? "" };
}
