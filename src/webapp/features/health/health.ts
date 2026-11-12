import { BuildMode } from "@features/apps";
import { PeriodFilters, periodParams } from "@features/performance/performance";
import { ReleaseFilter } from "@features/releases/releases";
import { fetchJson } from "@fns/fetch-json";

export interface SessionCounts {
  observed: number;
  withStart: number;
  eligible: number;
  crashed: number;
  eligibleCrashed: number;
  abnormal: number;
  active: number;
  errored: number;
  incomplete: number;
  crashFreeRate: number | null;
}

export interface SessionGroup extends SessionCounts {
  release: string;
  environment: string;
}

export interface DiscardCounter {
  reason: string;
  category: string;
  quantity: number;
}

export interface SessionHealthResponse {
  sessions: SessionCounts;
  groups: SessionGroup[];
  groupsTruncated: boolean;
  clientReports: { reports: number; discardedEvents: number; groups: DiscardCounter[] };
  retentionDays: number;
}

export const MAX_HEALTH_GROUPS = 100;

export const healthKeys = {
  app: (appId: string) => ["health", appId] as const,
  summary: (appId: string, buildMode: BuildMode, filters: PeriodFilters) =>
    [...healthKeys.app(appId), buildMode, filters] as const,
};

export function fetchSessionHealth(appId: string, buildMode: BuildMode, filters: PeriodFilters) {
  const params = periodParams(buildMode, filters);
  return fetchJson<SessionHealthResponse>(`/api/v0/apps/${encodeURIComponent(appId)}/monitoring/health?${params}`);
}

export function mergeObservedPairs(current: ReleaseFilter[], groups: SessionGroup[]) {
  const known = new Set(current.map((pair) => `${pair.release}
${pair.environment}`));
  const added = groups
    .filter((group) => !known.has(`${group.release}
${group.environment}`))
    .map(({ release, environment }) => ({ release, environment }));
  return added.length > 0 ? [...current, ...added] : current;
}

// Rounds down so a rate with any crash never reads as 100%.
export function formatCrashFreeRate(rate: number) {
  const floored = Math.floor(rate * 10 + 1e-9) / 10;
  return `${floored.toLocaleString(undefined, { maximumFractionDigits: 1 })}%`;
}
