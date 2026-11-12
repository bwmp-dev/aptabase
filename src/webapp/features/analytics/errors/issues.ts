import { BuildMode } from "@features/apps";
import { ReleaseFilter, appendReleaseFilter } from "@features/releases/releases";
import { fetchJson, jsonBody } from "@fns/fetch-json";

export type IssueStatus = "unresolved" | "resolved";
export type IssueStatusFilter = IssueStatus | "all";

export interface Issue {
  id: string;
  errorType: string;
  errorMessage: string;
  stackTrace: string;
  status: IssueStatus;
  occurrenceCount: number;
  firstSeen: string;
  lastSeen: string;
  resolvedAt: string | null;
}

export interface IssuesResponse {
  issues: Issue[];
  pagination: {
    offset: number;
    limit: number;
    total: number;
  };
  canManage: boolean;
}

export interface IssueEvent {
  eventId: string;
  timestamp: string;
  errorMessage: string;
  stackTrace: string;
  release: string;
  environment: string;
  platform: string;
  osName: string;
  osVersion: string;
  sdkVersion: string;
  severity: string;
  kind: string;
}

export interface IssueEventsResponse {
  events: IssueEvent[];
  pagination: { offset: number; limit: number; total: number };
}

export interface IssueResponse {
  issue: Issue;
  canManage: boolean;
}

export const ISSUES_PAGE_SIZE = 50;
export const ISSUE_SEARCH_MAX_LENGTH = 200;
export const ISSUE_EVENTS_PAGE_SIZE = 10;

export const issueKeys = {
  scope: (appId: string, buildMode: BuildMode) => ["issues", appId, buildMode] as const,
  list: (
    appId: string,
    buildMode: BuildMode,
    status: IssueStatusFilter,
    search: string,
    filter: ReleaseFilter,
    offset: number
  ) => [...issueKeys.scope(appId, buildMode), "list", status, search, filter, offset] as const,
  detail: (appId: string, buildMode: BuildMode, issueId: string) =>
    [...issueKeys.scope(appId, buildMode), "detail", issueId] as const,
  events: (appId: string, buildMode: BuildMode, issueId: string, filter: ReleaseFilter, offset: number) =>
    [...issueKeys.scope(appId, buildMode), "events", issueId, filter, offset] as const,
};

function issuesPath(appId: string, issueId?: string) {
  const base = `/api/v0/apps/${encodeURIComponent(appId)}/issues`;
  return issueId ? `${base}/${encodeURIComponent(issueId)}` : base;
}

export function fetchIssues(
  appId: string,
  buildMode: BuildMode,
  status: IssueStatusFilter,
  search: string,
  filter: ReleaseFilter,
  offset: number
): Promise<IssuesResponse> {
  const params = new URLSearchParams({
    buildMode,
    status,
    offset: offset.toString(),
    limit: ISSUES_PAGE_SIZE.toString(),
  });
  if (search) params.set("q", search);
  appendReleaseFilter(params, filter);
  return fetchJson(`${issuesPath(appId)}?${params}`);
}

export function fetchIssue(appId: string, buildMode: BuildMode, issueId: string): Promise<IssueResponse> {
  const params = new URLSearchParams({ buildMode });
  return fetchJson(`${issuesPath(appId, issueId)}?${params}`);
}

export function fetchIssueEvents(
  appId: string,
  buildMode: BuildMode,
  issueId: string,
  filter: ReleaseFilter,
  offset: number
): Promise<IssueEventsResponse> {
  const params = new URLSearchParams({
    buildMode,
    offset: offset.toString(),
    limit: ISSUE_EVENTS_PAGE_SIZE.toString(),
  });
  appendReleaseFilter(params, filter);
  return fetchJson(`${issuesPath(appId, issueId)}/events?${params}`);
}

export function updateIssueStatus(
  appId: string,
  buildMode: BuildMode,
  issueId: string,
  status: IssueStatus
): Promise<IssueResponse> {
  const params = new URLSearchParams({ buildMode });
  return fetchJson(`${issuesPath(appId, issueId)}?${params}`, jsonBody("PATCH", { status }));
}
