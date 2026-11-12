import { BrowserContext, Page, Route, expect, test as base } from "@playwright/test";
import path from "node:path";
import type { Issue, IssueStatus } from "../webapp/features/analytics/errors/issues";
import { artifactsDir, waitForPageFadeIn } from "./shared-config";

const APP_ID = "fixture-app";

type BuildMode = "release" | "debug";
type Reply = { status: number; body?: unknown };
type ApiRequest = { method: string; url: URL; body: unknown; contentType?: string };
type SymbolArtifact = {
  id: string;
  buildId: string | null;
  status: "pending" | "ready" | "invalid";
  sizeBytes: number;
  createdAt: string;
  expiresAt: string;
};
type Interceptor = (request: ApiRequest) => Reply | undefined | Promise<Reply | undefined>;

function sampleIssue(overrides: Partial<Issue> & Pick<Issue, "id">): Issue {
  return {
    errorType: "Error",
    errorMessage: "",
    stackTrace: "",
    status: "unresolved",
    occurrenceCount: 1,
    firstSeen: "2026-11-01T08:00:00Z",
    lastSeen: "2026-11-01T08:00:00Z",
    resolvedAt: null,
    ...overrides,
  };
}

function fingerprint(seed: string) {
  return seed.repeat(64).slice(0, 64);
}

// Sample data for mocked-network UI checks only; it does not come from a real backend.
function sampleIssues(): Record<BuildMode, Issue[]> {
  return {
    release: [
      sampleIssue({
        id: fingerprint("a"),
        errorType: "NetworkError",
        errorMessage: "Request timed out after 30s while syncing settings",
        stackTrace: "NetworkError: Request timed out after 30s\n    at sync (settings.ts:88:13)\n    at main (app.ts:12:5)",
        occurrenceCount: 1284,
        firstSeen: "2026-10-02T09:15:00Z",
        lastSeen: "2026-11-11T14:02:00Z",
      }),
      sampleIssue({
        id: fingerprint("b"),
        errorType: "System.InvalidOperationException",
        errorMessage:
          "Sequence contains no matching element: /Users/sample/Library/Application Support/SampleApp/cache/thumbnails/2026/11/very-long-file-name-without-spaces.png",
        stackTrace: "at SampleApp.Thumbnails.Load()\n    at SampleApp.Gallery.Render()",
        occurrenceCount: 37,
        firstSeen: "2026-11-05T12:00:00Z",
        lastSeen: "2026-11-10T22:41:00Z",
      }),
      sampleIssue({
        id: fingerprint("c"),
        errorType: "TypeError",
        errorMessage: "Cannot read properties of undefined (reading 'theme')",
        stackTrace: "TypeError: Cannot read properties of undefined\n    at applyTheme (theme.ts:4:20)",
        status: "resolved",
        occurrenceCount: 9,
        firstSeen: "2026-09-20T07:00:00Z",
        lastSeen: "2026-10-01T10:00:00Z",
        resolvedAt: "2026-10-02T08:00:00Z",
      }),
    ],
    debug: [
      sampleIssue({
        id: fingerprint("d"),
        errorType: "AssertionError",
        errorMessage: "Expected window bounds to be positive",
        occurrenceCount: 3,
        firstSeen: "2026-11-11T09:00:00Z",
        lastSeen: "2026-11-11T09:30:00Z",
      }),
    ],
  };
}

const sampleReleases = [
  { release: "2.4.0", environment: "production", errorCount: 12, transactionCount: 840, firstSeen: "2026-11-03T08:00:00Z", lastSeen: "2026-11-11T14:00:00Z" },
  { release: "2.3.1", environment: "production", errorCount: 31, transactionCount: 2210, firstSeen: "2026-10-15T08:00:00Z", lastSeen: "2026-11-04T09:00:00Z" },
  { release: "2.4.0", environment: "beta", errorCount: 2, transactionCount: 95, firstSeen: "2026-11-01T08:00:00Z", lastSeen: "2026-11-10T19:00:00Z" },
];

const sampleEvents = [
  {
    eventId: "0b6f3a7c-1d2e-4f50-8a9b-0c1d2e3f4a01",
    timestamp: "2026-11-11T14:02:00Z",
    errorMessage: "Request timed out after 30s while syncing settings",
    stackTrace: "NetworkError: Request timed out after 30s\n    at sync (settings.ts:88:13)",
    release: "2.4.0",
    environment: "production",
    platform: "desktop",
    osName: "macOS",
    osVersion: "15.1",
    sdkVersion: "aptabase-electron@0.4.0",
    severity: "error",
    kind: "handled",
    hasNativeFrames: false,
  },
  {
    eventId: "0b6f3a7c-1d2e-4f50-8a9b-0c1d2e3f4a02",
    timestamp: "2026-11-10T08:30:00Z",
    errorMessage: "Request timed out after 30s while syncing settings",
    stackTrace: "",
    release: "2.3.1",
    environment: "production",
    platform: "desktop",
    osName: "Windows",
    osVersion: "11",
    sdkVersion: "aptabase-electron@0.4.0",
    severity: "error",
    kind: "handled",
    hasNativeFrames: false,
  },
];

// Sample synthetic values matching the NDK probe libraries used by the real-host checks.
export const PROBE_BUILD_ID = "bbe2c0d49e25325c5e51f939ec1eac37cc159800";
export const PROBE_FUNCTION = "aptabase_symbol_probe";
export const INLINE_BUILD_ID = "8c932eb449cfe39353e51fa362a4b63621a499e4";
export const INLINE_FUNCTION = "aptabase_inline_probe";
type SourceStatus = "available" | "unavailable" | "unconfigured" | "limit_exceeded";
const FIXTURE_NOW = Date.parse("2026-11-11T15:00:00Z");
const DAY_MS = 24 * 60 * 60 * 1000;

const sampleFunctions: Record<string, { name: string; start: number; size: number; locations: (address: number) => { function: string; file: string; line: number }[] }> = {
  [PROBE_BUILD_ID]: {
    name: PROBE_FUNCTION,
    start: 0x2dc,
    size: 24,
    locations: () => [{ function: PROBE_FUNCTION, file: "probe.c", line: 3 }],
  },
  [INLINE_BUILD_ID]: {
    name: INLINE_FUNCTION,
    start: 0x2d4,
    size: 16,
    locations: (address) =>
      address === 0x2d4
        ? [
            { function: "native_leaf", file: "inline-probe.c", line: 2 },
            { function: "native_middle", file: "inline-probe.c", line: 5 },
            { function: INLINE_FUNCTION, file: "inline-probe.c", line: 8 },
          ]
        : [{ function: INLINE_FUNCTION, file: "inline-probe.c", line: 8 }],
  },
};
const UNKNOWN_BUILD_ID = "0123456789abcdef0123456789abcdef01234567";
export const NATIVE_EVENT_ID = "0b6f3a7c-1d2e-4f50-8a9b-0c1d2e3f4a03";

const nativeEvent = {
  eventId: NATIVE_EVENT_ID,
  timestamp: "2026-11-09T07:45:00Z",
  errorMessage: "SIGSEGV in native module",
  stackTrace: `at native ${INLINE_BUILD_ID}+0x2d4\nat native ${PROBE_BUILD_ID}+0x2e0\nat native ${UNKNOWN_BUILD_ID}+0x1a40`,
  release: "2.4.0",
  environment: "production",
  platform: "desktop",
  osName: "Linux",
  osVersion: "6.8",
  sdkVersion: "aptabase-electron@0.4.0",
  severity: "fatal",
  kind: "crash",
  hasNativeFrames: true,
};

const nativeFrames = [
  { buildId: INLINE_BUILD_ID, address: "0x2d4" },
  { buildId: PROBE_BUILD_ID, address: "0x2e0" },
  { buildId: UNKNOWN_BUILD_ID, address: "0x1a40" },
];

export function readyProbeArtifact(): SymbolArtifact {
  return {
    id: "c7d1e2f3-0000-4000-8000-0000000000a1",
    buildId: PROBE_BUILD_ID,
    status: "ready",
    sizeBytes: 6144,
    createdAt: "2026-11-11T08:00:00Z",
    expiresAt: "2026-12-11T08:00:00Z",
  };
}

// Uploaded six days before the fixture's "now", so a shorter retention expires it.
export function readyInlineArtifact(): SymbolArtifact {
  return {
    id: "c7d1e2f3-0000-4000-8000-0000000000b2",
    buildId: INLINE_BUILD_ID,
    status: "ready",
    sizeBytes: 9216,
    createdAt: "2026-11-05T15:00:00Z",
    expiresAt: "2026-12-05T15:00:00Z",
  };
}

const sampleGroups = [
  { name: "app.startup", operation: "ui.load", count: 640, errorCount: 4, avgMs: 412, p50Ms: 380, p95Ms: 910 },
  { name: "document.save", operation: "file.write", count: 210, errorCount: 0, avgMs: 38, p50Ms: 31, p95Ms: 84 },
  { name: "sync.settings", operation: "http.client", count: 95, errorCount: 11, avgMs: 2180, p50Ms: 1450, p95Ms: 6020 },
];

const sampleTransactions = [
  {
    eventId: "5c0e7a1b-2f3d-4e5a-9b6c-7d8e9f0a1b01",
    timestamp: "2026-11-11T13:58:00Z",
    name: "app.startup",
    operation: "ui.load",
    durationMs: 420,
    status: "ok",
    release: "2.4.0",
    environment: "production",
    platform: "desktop",
  },
  {
    eventId: "5c0e7a1b-2f3d-4e5a-9b6c-7d8e9f0a1b02",
    timestamp: "2026-11-11T13:40:00Z",
    name: "sync.settings",
    operation: "http.client",
    durationMs: 6120,
    status: "error",
    release: "2.3.1",
    environment: "production",
    platform: "desktop",
  },
  {
    eventId: "5c0e7a1b-2f3d-4e5a-9b6c-7d8e9f0a1b03",
    timestamp: "2026-11-11T13:20:00Z",
    name: "deep.tree",
    operation: "ui.load",
    durationMs: 100,
    status: "ok",
    release: "2.4.0",
    environment: "beta",
    platform: "desktop",
  },
];

// A chain of 100 nested spans, the maximum the backend accepts, ending at the transaction end.
const deepSpans = Array.from({ length: 100 }, (_, i) => ({
  spanId: (i + 1).toString(16).padStart(16, "0"),
  parentSpanId: i === 0 ? null : i.toString(16).padStart(16, "0"),
  name: `nested.step.${i + 1}`,
  operation: "work",
  startOffsetMs: i,
  durationMs: 100 - i,
  status: "ok",
}));

const sampleSpans = [
  { spanId: "1111111111111111", parentSpanId: null, name: "load.preferences", operation: "file.read", startOffsetMs: 5, durationMs: 60, status: "ok" },
  { spanId: "2222222222222222", parentSpanId: "1111111111111111", name: "parse.json", operation: "serialize", startOffsetMs: 40, durationMs: 18, status: "ok" },
  { spanId: "3333333333333333", parentSpanId: null, name: "render.main-window", operation: "ui.render", startOffsetMs: 80, durationMs: 310, status: "ok" },
];

type SessionCounts = {
  observed: number;
  withStart: number;
  eligible: number;
  crashed: number;
  eligibleCrashed: number;
  abnormal: number;
  active: number;
  errored: number;
};

// Sample session health per release and environment pair, release builds only.
const sampleSessionGroups: (SessionCounts & { release: string; environment: string })[] = [
  { release: "2.4.0", environment: "production", observed: 120, withStart: 118, eligible: 100, crashed: 3, eligibleCrashed: 2, abnormal: 4, active: 14, errored: 9 },
  { release: "2.3.1", environment: "production", observed: 40, withStart: 40, eligible: 38, crashed: 1, eligibleCrashed: 1, abnormal: 0, active: 2, errored: 3 },
  { release: "2.4.0", environment: "beta", observed: 3, withStart: 2, eligible: 0, crashed: 0, eligibleCrashed: 0, abnormal: 0, active: 2, errored: 0 },
];

// Discard counters with the release context of their accompanying event; "" marks a metadata-only report.
const sampleClientReports = [
  { release: "2.4.0", environment: "production", reason: "queue_overflow", category: "error", quantity: 12 },
  { release: "", environment: "", reason: "before_send", category: "error", quantity: 5 },
  { release: "", environment: "", reason: "ratelimit_backoff", category: "transaction", quantity: 2 },
];

function withDerived<T extends SessionCounts>(counts: T) {
  const crashFreeRate = counts.eligible === 0 ? null : ((counts.eligible - counts.eligibleCrashed) * 100) / counts.eligible;
  return { ...counts, incomplete: counts.observed - counts.eligible, crashFreeRate };
}

export class FixtureApi {
  readonly requests: ApiRequest[] = [];
  readonly unexpected: string[] = [];
  readonly blockedExternal: string[] = [];
  canManage = true;
  patchDelayMs = 0;
  monitoringSettings = {
    captureErrors: true,
    capturePerformance: true,
    forwardRawErrors: true,
    monitoringRetentionDays: 30,
    localAlertsEnabled: true,
    alertCooldownMinutes: 60,
  };
  sentryProjects: { id: string; buildMode: string; dsn: string }[] = [];
  alerts = [
    { eventId: "8a7b6c5d-4e3f-4a1b-9c2d-000000000001", issueId: fingerprint("a"), kind: "regression", createdAt: "2026-11-11T14:02:00Z", readAt: null as string | null },
    { eventId: "8a7b6c5d-4e3f-4a1b-9c2d-000000000002", issueId: fingerprint("b"), kind: "new_issue", createdAt: "2026-11-05T12:00:00Z", readAt: "2026-11-06T08:00:00Z" as string | null },
  ];
  cleared = false;
  symbolsEnabled = true;
  sourceStatus: SourceStatus = "available";
  nativeSymbols: Record<BuildMode, SymbolArtifact[]> = { release: [], debug: [] };
  private uploadCount = 0;
  interceptor: Interceptor | undefined;
  private issues = sampleIssues();

  // Stands in for the isolated processor finishing validation of every pending upload.
  validatePendingSymbols() {
    for (const artifact of [...this.nativeSymbols.release, ...this.nativeSymbols.debug]) {
      if (artifact.status !== "pending") continue;
      artifact.status = "ready";
      artifact.buildId = PROBE_BUILD_ID;
    }
  }

  listRequests() {
    return this.requests
      .filter((r) => r.method === "GET" && r.url.pathname === `/api/v0/apps/${APP_ID}/issues`)
      .map((r) => Object.fromEntries(r.url.searchParams));
  }

  async handle(request: ApiRequest): Promise<Reply> {
    this.requests.push(request);
    const intercepted = await this.interceptor?.(request);
    if (intercepted) return intercepted;

    const { method, url } = request;
    const route = `${method} ${url.pathname}`;

    if (route === "GET /api/_auth/me") {
      return { status: 200, body: { id: "user-1", name: "Sample Owner", email: "owner@example.test", avatarUrl: "" } };
    }
    if (route === "GET /api/_apps") {
      return {
        status: 200,
        body: [
          {
            id: APP_ID,
            name: "Sample App",
            appKey: "A-SH-0000000000",
            iconPath: "",
            hasEvents: true,
            hasOwnership: this.canManage,
          },
        ],
      };
    }
    if (route === "GET /api/_app-requests") {
      return { status: 200, body: [] };
    }
    if (route === "GET /api/_billing") {
      return {
        status: 200,
        body: { usage: 0, month: 11, year: 2026, state: "OK", plan: { name: "Sample", monthlyPrice: 0, monthlyEvents: 0 } },
      };
    }

    const appBase = `/api/v0/apps/${APP_ID}`;
    if (method === "GET" && url.pathname === `${appBase}/releases`) return { status: 200, body: { releases: sampleReleases } };
    if (method === "GET" && url.pathname === `${appBase}/performance/summary`) return this.performanceSummary(url);
    if (method === "GET" && url.pathname === `${appBase}/transactions`) return this.transactionList(url);
    if (method === "GET" && url.pathname.startsWith(`${appBase}/transactions/`)) {
      const eventId = url.pathname.slice(`${appBase}/transactions/`.length);
      const transaction = sampleTransactions.find((t) => t.eventId === eventId);
      const spans = transaction?.name === "deep.tree" ? deepSpans : sampleSpans;
      return transaction ? { status: 200, body: { transaction, spans } } : { status: 404 };
    }
    if (url.pathname === `${appBase}/native-symbols` || url.pathname.startsWith(`${appBase}/native-symbols/`)) {
      return this.symbols(request, url.pathname.slice(`${appBase}/native-symbols`.length));
    }
    if (method === "GET" && url.pathname === `${appBase}/monitoring/health`) return this.sessionHealth(url);
    if (url.pathname.startsWith(`${appBase}/monitoring/`) || url.pathname === `${appBase}/sentry-projects`) {
      return this.monitoring(method, url.pathname.slice(appBase.length), request.body);
    }

    const issuesBase = `${appBase}/issues`;
    if (url.pathname === issuesBase && method === "GET") return this.list(url);
    if (method === "GET" && url.pathname.startsWith(`${issuesBase}/`) && url.pathname.endsWith("/native-frames")) {
      return this.frames(url);
    }
    if (method === "GET" && url.pathname.startsWith(`${issuesBase}/`) && url.pathname.endsWith("/events")) {
      const events = [...sampleEvents, nativeEvent];
      return { status: 200, body: { events, pagination: { offset: 0, limit: 10, total: events.length } } };
    }
    if (url.pathname.startsWith(`${issuesBase}/`)) {
      const issueId = url.pathname.slice(issuesBase.length + 1);
      if (method === "GET") return this.detail(url, issueId);
      if (method === "PATCH") return this.updateStatus(url, issueId, request.body);
    }

    this.unexpected.push(route);
    return { status: 404, body: {} };
  }

  private monitoring(method: string, path: string, body: unknown): Reply {
    if (!this.canManage) return { status: 403 };
    if (path === "/monitoring/settings" && method === "GET") return { status: 200, body: this.monitoringSettings };
    if (path === "/monitoring/settings" && method === "PUT") {
      const next = body as typeof this.monitoringSettings;
      if (next.monitoringRetentionDays < 1 || next.monitoringRetentionDays > 30) return { status: 400 };
      this.monitoringSettings = next;
      this.applySymbolRetention(next.monitoringRetentionDays);
      return { status: 200, body: next };
    }
    if (path === "/monitoring/data" && method === "GET") {
      const counts = this.cleared
        ? { issues: 0, events: 0, transactions: 0, sessions: 0, clientReports: 0, queued: 0, alerts: 0, symbols: 0 }
        : {
            issues: 3,
            events: 1330,
            transactions: 3,
            sessions: 163,
            clientReports: 3,
            queued: 0,
            alerts: this.alerts.length,
            symbols: this.symbolCount(),
          };
      return { status: 200, body: { counts, excludesDeliveredRawLogs: true } };
    }
    if (path === "/monitoring/data" && method === "DELETE") {
      const { captureErrors, capturePerformance, forwardRawErrors } = this.monitoringSettings;
      if (captureErrors || capturePerformance || forwardRawErrors) return { status: 409 };
      if ((body as { confirmAppId?: string })?.confirmAppId !== APP_ID) return { status: 400 };
      this.cleared = true;
      this.nativeSymbols = { release: [], debug: [] };
      return { status: 204 };
    }
    if (path === "/monitoring/alerts" && method === "GET") {
      return { status: 200, body: { alerts: this.alerts, pagination: { offset: 0, limit: 50, total: this.alerts.length } } };
    }
    if (path.startsWith("/monitoring/alerts/") && path.endsWith("/read") && method === "PATCH") {
      const eventId = path.split("/")[3];
      const alert = this.alerts.find((a) => a.eventId === eventId);
      if (!alert) return { status: 404 };
      alert.readAt ??= "2026-11-11T15:00:00Z";
      return { status: 204 };
    }
    if (path === "/sentry-projects" && method === "GET") return { status: 200, body: { projects: this.sentryProjects } };
    if (path === "/sentry-projects" && method === "POST") {
      this.sentryProjects = [
        { id: "41", buildMode: "release", dsn: `http://A-SH-0000000000@127.0.0.1/41` },
        { id: "42", buildMode: "debug", dsn: `http://A-SH-0000000000@127.0.0.1/42` },
      ];
      return { status: 200, body: { projects: this.sentryProjects } };
    }
    this.unexpected.push(`${method} ${path}`);
    return { status: 404 };
  }

  private symbolCount() {
    return this.nativeSymbols.release.length + this.nativeSymbols.debug.length;
  }

  private symbols(request: ApiRequest, path: string): Reply {
    if (!this.canManage) return { status: 403 };
    const mode = this.scope(request.url);
    const limits = { maxArtifactBytes: 8388608, maxAppBytes: 33554432, maxAppArtifacts: 8 };
    if (path === "" && request.method === "GET") {
      return { status: 200, body: { enabled: this.symbolsEnabled, artifacts: this.nativeSymbols[mode], limits } };
    }
    if (path === "" && request.method === "POST") {
      if (!this.symbolsEnabled) return { status: 503 };
      if (request.contentType !== "application/octet-stream") return { status: 415 };
      if (!Buffer.isBuffer(request.body) || request.body.length === 0) return { status: 400 };
      if (this.symbolCount() >= limits.maxAppArtifacts) return { status: 409 };
      this.uploadCount += 1;
      const artifact: SymbolArtifact = {
        id: `d8e9f0a1-0000-4000-8000-${this.uploadCount.toString().padStart(12, "0")}`,
        buildId: null,
        status: "pending",
        sizeBytes: request.body.length,
        createdAt: "2026-11-11T15:00:00Z",
        expiresAt: "2026-12-11T15:00:00Z",
      };
      this.nativeSymbols[mode] = [artifact, ...this.nativeSymbols[mode]];
      return { status: 202, body: { artifact } };
    }
    if (path.startsWith("/") && request.method === "DELETE") {
      const id = decodeURIComponent(path.slice(1));
      if (!this.nativeSymbols[mode].some((a) => a.id === id)) return { status: 404 };
      this.nativeSymbols[mode] = this.nativeSymbols[mode].filter((a) => a.id !== id);
      return { status: 204 };
    }
    this.unexpected.push(`${request.method} ${request.url.pathname}`);
    return { status: 404 };
  }

  // Readers may see frames; resolution is computed at read time from ready artifacts, like the backend contract.
  private frames(url: URL): Reply {
    if (!url.pathname.includes(`/events/${NATIVE_EVENT_ID}/`)) return { status: 404 };
    const ready = this.nativeSymbols[this.scope(url)].filter((a) => a.status === "ready");
    const unresolved = { function: null, offset: null, sourceStatus: "unavailable", locations: [] };
    const frames = nativeFrames.map((frame) => {
      const address = parseInt(frame.address, 16);
      const matches = ready.filter((a) => a.buildId === frame.buildId);
      if (matches.length > 1) return { ...frame, status: "ambiguous", ...unresolved };
      const fn = sampleFunctions[frame.buildId];
      if (matches.length === 0 || !fn || address < fn.start || address >= fn.start + fn.size) {
        return { ...frame, status: "unresolved", ...unresolved };
      }
      const available = this.sourceStatus === "available";
      return {
        ...frame,
        status: "resolved",
        function: fn.name,
        offset: address - fn.start,
        sourceStatus: this.sourceStatus,
        locations: available ? fn.locations(address) : [],
      };
    });
    return { status: 200, body: { capability: "function-and-source", frames } };
  }

  // Like the backend, symbol data expires by the current retention counted from upload.
  private applySymbolRetention(days: number) {
    for (const mode of ["release", "debug"] as const) {
      this.nativeSymbols[mode] = this.nativeSymbols[mode]
        .map((a) => ({ ...a, expiresAt: new Date(Date.parse(a.createdAt) + days * DAY_MS).toISOString() }))
        .filter((a) => Date.parse(a.expiresAt) > FIXTURE_NOW);
    }
  }

  private performanceSummary(url: URL): Reply {
    const release = url.searchParams.get("release");
    const groups = release && release !== "2.4.0" ? [] : sampleGroups;
    return { status: 200, body: { groups } };
  }

  // Readable by owners and shared readers alike; filters behave like the backend summary query.
  private sessionHealth(url: URL): Reply {
    const release = url.searchParams.get("release");
    const environment = url.searchParams.get("environment");
    const matches = (row: { release: string; environment: string }) =>
      (!release || row.release === release) && (!environment || row.environment === environment);
    const live = this.scope(url) === "release" && !this.cleared;
    const groups = live ? sampleSessionGroups.filter(matches) : [];
    const keys = ["observed", "withStart", "eligible", "crashed", "eligibleCrashed", "abnormal", "active", "errored"] as const;
    const totals = Object.fromEntries(keys.map((key) => [key, groups.reduce((sum, g) => sum + g[key], 0)])) as SessionCounts;
    const reports = live ? sampleClientReports.filter(matches) : [];
    return {
      status: 200,
      body: {
        sessions: withDerived(totals),
        groups: groups.map(withDerived),
        groupsTruncated: false,
        clientReports: {
          reports: reports.length,
          discardedEvents: reports.reduce((sum, r) => sum + r.quantity, 0),
          groups: reports.map(({ reason, category, quantity }) => ({ reason, category, quantity })),
        },
        retentionDays: this.monitoringSettings.monitoringRetentionDays,
      },
    };
  }

  private transactionList(url: URL): Reply {
    const name = url.searchParams.get("name");
    const release = url.searchParams.get("release");
    const transactions = sampleTransactions
      .filter((t) => !name || t.name === name)
      .filter((t) => !release || t.release === release);
    return { status: 200, body: { transactions, pagination: { offset: 0, limit: 50, total: transactions.length } } };
  }

  private scope(url: URL) {
    return (url.searchParams.get("buildMode") ?? "release") as BuildMode;
  }

  private list(url: URL): Reply {
    const status = url.searchParams.get("status") ?? "unresolved";
    const q = (url.searchParams.get("q") ?? "").trim().toLowerCase();
    const offset = Number(url.searchParams.get("offset") ?? 0);
    const limit = Number(url.searchParams.get("limit") ?? 50);
    const matching = this.issues[this.scope(url)]
      .filter((issue) => status === "all" || issue.status === status)
      .filter((issue) => !q || `${issue.errorType}\n${issue.errorMessage}`.toLowerCase().includes(q))
      .sort((a, b) => b.lastSeen.localeCompare(a.lastSeen));
    return {
      status: 200,
      body: {
        issues: matching.slice(offset, offset + limit),
        pagination: { offset, limit, total: matching.length },
        canManage: this.canManage,
      },
    };
  }

  private detail(url: URL, issueId: string): Reply {
    const issue = this.issues[this.scope(url)].find((i) => i.id === issueId);
    return issue ? { status: 200, body: { issue, canManage: this.canManage } } : { status: 404 };
  }

  private async updateStatus(url: URL, issueId: string, body: unknown): Promise<Reply> {
    if (!this.canManage) return { status: 403 };
    const status = (body as { status?: IssueStatus } | null)?.status;
    if (status !== "resolved" && status !== "unresolved") return { status: 400 };
    const issue = this.issues[this.scope(url)].find((i) => i.id === issueId);
    if (!issue) return { status: 404 };
    if (this.patchDelayMs) await new Promise((resolve) => setTimeout(resolve, this.patchDelayMs));
    issue.status = status;
    issue.resolvedAt = status === "resolved" ? "2026-11-11T15:00:00Z" : null;
    return { status: 200, body: { issue, canManage: true } };
  }
}

async function routeAll(context: BrowserContext, api: FixtureApi, baseURL: string) {
  const origin = new URL(baseURL).origin;
  await context.route("**/*", async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin) {
      api.blockedExternal.push(url.origin);
      return route.abort("blockedbyclient");
    }
    if (!url.pathname.startsWith("/api/")) return route.continue();

    const contentType = request.headers()["content-type"];
    const binary = contentType === "application/octet-stream";
    const postData = binary ? null : request.postData();
    const reply = await api.handle({
      method: request.method(),
      url,
      contentType,
      body: binary ? request.postDataBuffer() : postData ? JSON.parse(postData) : null,
    });
    return route.fulfill({
      status: reply.status,
      contentType: "application/json",
      body: JSON.stringify(reply.body ?? {}),
    });
  });
}

export const test = base.extend<{ api: FixtureApi; pageErrors: string[] }>({
  // Automatic so no test can reach the network or the static server's API path without fixtures.
  api: [
    async ({ context, baseURL }, use) => {
      const api = new FixtureApi();
      await routeAll(context, api, baseURL!);
      await context.addInitScript(() => window.localStorage.setItem("buildmode", JSON.stringify("release")));
      await use(api);
      expect(api.unexpected, "unmocked API routes").toEqual([]);
    },
    { auto: true },
  ],
  pageErrors: async ({ page }, use) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await use(errors);
    expect(errors, "uncaught page errors").toEqual([]);
  },
});

export { expect };

export const appErrorsPath = `/${APP_ID}/errors`;

export async function snapshot(page: Page, name: string) {
  await waitForPageFadeIn(page);
  const project = test.info().project.name;
  await page.screenshot({ path: path.join(artifactsDir, "screenshots", `${project}-${name}.png`), fullPage: true, animations: "disabled" });
}

// Compares with the emulated device width: mobile Chrome zooms out to fit overflowing pages, which also grows innerWidth.
export async function expectNoHorizontalOverflow(page: Page) {
  const contentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
  expect(contentWidth, "page must not scroll horizontally").toBeLessThanOrEqual(page.viewportSize()!.width);
}
