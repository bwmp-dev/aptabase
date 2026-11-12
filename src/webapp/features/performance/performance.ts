import { BuildMode } from "@features/apps";
import { ReleaseFilter, appendReleaseFilter } from "@features/releases/releases";
import { fetchJson } from "@fns/fetch-json";

export type TransactionStatus = "ok" | "error" | "cancelled";

export interface Transaction {
  eventId: string;
  timestamp: string;
  name: string;
  operation: string;
  durationMs: number;
  status: TransactionStatus;
  release: string;
  environment: string;
  platform: string;
}

export interface Span {
  spanId: string;
  parentSpanId: string | null;
  name: string;
  operation: string;
  startOffsetMs: number;
  durationMs: number;
  status: TransactionStatus;
}

export interface OperationGroup {
  name: string;
  operation: string;
  count: number;
  errorCount: number;
  avgMs: number;
  p50Ms: number;
  p95Ms: number;
}

export interface TransactionsResponse {
  transactions: Transaction[];
  pagination: { offset: number; limit: number; total: number };
}

export interface TransactionDetail {
  transaction: Transaction;
  spans: Span[];
}

export type Period = "24h" | "7d" | "30d";

export const periods: readonly { value: Period; label: string }[] = [
  { value: "24h", label: "24 hours" },
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
];

const periodHours: Record<Period, number> = { "24h": 24, "7d": 24 * 7, "30d": 24 * 30 };

export type PeriodFilters = ReleaseFilter & { period: Period };

export type PerformanceFilters = PeriodFilters & {
  operation: { name: string; operation: string } | null;
};

export const TRANSACTIONS_PAGE_SIZE = 50;

export const performanceKeys = {
  scope: (appId: string, buildMode: BuildMode) => ["performance", appId, buildMode] as const,
  summary: (appId: string, buildMode: BuildMode, filters: PerformanceFilters) =>
    [...performanceKeys.scope(appId, buildMode), "summary", filters] as const,
  transactions: (appId: string, buildMode: BuildMode, filters: PerformanceFilters, offset: number) =>
    [...performanceKeys.scope(appId, buildMode), "transactions", filters, offset] as const,
  detail: (appId: string, buildMode: BuildMode, eventId: string) =>
    [...performanceKeys.scope(appId, buildMode), "detail", eventId] as const,
};

function appPath(appId: string) {
  return `/api/v0/apps/${encodeURIComponent(appId)}`;
}

// The range is computed when the request runs so refetches cover the latest samples.
export function periodParams(buildMode: BuildMode, filters: PeriodFilters) {
  const end = new Date();
  const start = new Date(end.getTime() - periodHours[filters.period] * 60 * 60 * 1000);
  const params = new URLSearchParams({ buildMode, startDate: start.toISOString(), endDate: end.toISOString() });
  appendReleaseFilter(params, filters);
  return params;
}

function filterParams(buildMode: BuildMode, filters: PerformanceFilters, includeOperation: boolean) {
  const params = periodParams(buildMode, filters);
  if (includeOperation && filters.operation) {
    params.set("name", filters.operation.name);
    params.set("operation", filters.operation.operation);
  }
  return params;
}

export function fetchSummary(appId: string, buildMode: BuildMode, filters: PerformanceFilters) {
  const params = filterParams(buildMode, filters, false);
  return fetchJson<{ groups: OperationGroup[] }>(`${appPath(appId)}/performance/summary?${params}`);
}

export function fetchTransactions(appId: string, buildMode: BuildMode, filters: PerformanceFilters, offset: number) {
  const params = filterParams(buildMode, filters, true);
  params.set("offset", offset.toString());
  params.set("limit", TRANSACTIONS_PAGE_SIZE.toString());
  return fetchJson<TransactionsResponse>(`${appPath(appId)}/transactions?${params}`);
}

export function fetchTransaction(appId: string, buildMode: BuildMode, eventId: string) {
  const params = new URLSearchParams({ buildMode });
  return fetchJson<TransactionDetail>(`${appPath(appId)}/transactions/${encodeURIComponent(eventId)}?${params}`);
}

export function formatDuration(ms: number) {
  if (ms >= 1000) return `${(ms / 1000).toLocaleString(undefined, { maximumFractionDigits: 2 })} s`;
  return `${ms.toLocaleString(undefined, { maximumFractionDigits: ms < 10 ? 1 : 0 })} ms`;
}

export type SpanNode = Span & { depth: number };

// Orders spans depth-first by start offset; spans whose parent is missing are treated as roots.
export function flattenSpanTree(spans: Span[]): SpanNode[] {
  const ids = new Set(spans.map((span) => span.spanId));
  const children = new Map<string | null, Span[]>();
  for (const span of spans) {
    const parent = span.parentSpanId && ids.has(span.parentSpanId) ? span.parentSpanId : null;
    children.set(parent, [...(children.get(parent) ?? []), span]);
  }

  const ordered: SpanNode[] = [];
  const visit = (parent: string | null, depth: number) => {
    const siblings = [...(children.get(parent) ?? [])].sort((a, b) => a.startOffsetMs - b.startOffsetMs);
    for (const span of siblings) {
      ordered.push({ ...span, depth });
      visit(span.spanId, depth + 1);
    }
  };
  visit(null, 0);
  return ordered;
}

// Deep trees (up to 100 nested spans) would otherwise push names out of view; the true level stays in the text.
export const MAX_VISUAL_SPAN_DEPTH = 6;

export function spanIndentRem(depth: number) {
  return Math.min(depth, MAX_VISUAL_SPAN_DEPTH) * 0.75;
}

const MIN_BAR_PERCENT = 0.5;

// Keeps zero-length and end-of-transaction spans visible inside the timeline track.
export function spanBar(startOffsetMs: number, durationMs: number, totalMs: number) {
  const total = Math.max(totalMs, 1);
  const width = Math.min(100, Math.max((durationMs / total) * 100, MIN_BAR_PERCENT));
  const left = Math.min(Math.max((startOffsetMs / total) * 100, 0), 100 - width);
  return { left, width };
}
