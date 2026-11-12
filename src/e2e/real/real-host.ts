import { APIRequestContext, BrowserContext, Page, expect, test as base } from "@playwright/test";
import { randomUUID } from "node:crypto";
import path from "node:path";
import { artifactsDir, waitForPageFadeIn } from "../shared-config";

// Helpers for end-to-end checks against the loopback test host (real HTTP, auth cookies, ACLs, ingestion, PostgreSQL).

type Role = "owner" | "reader" | "stranger";
type BuildMode = "release" | "debug";

export const APP_ID = "test-app";
export const APP_KEY = "TEST-APP-KEY";

export const test = base.extend<{ blockedExternal: string[] }>({
  // Automatic so every test's browser traffic stays on the loopback host.
  blockedExternal: [
    async ({ context, baseURL }, use) => {
      const blocked: string[] = [];
      const origin = new URL(baseURL!).origin;
      await context.route("**/*", (route) => {
        const url = new URL(route.request().url());
        if (url.origin === origin) return route.continue();
        blocked.push(url.origin);
        return route.abort("blockedbyclient");
      });
      await use(blocked);
    },
    { auto: true },
  ],
});

export function runToken() {
  return `${test.info().project.name}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
}

export async function login(context: BrowserContext, role: Role, buildMode: BuildMode = "release") {
  const response = await context.request.post(`/test/login/${role}`);
  expect(response.status(), `login as ${role}`).toBe(200);
  await context.addInitScript((mode) => window.localStorage.setItem("buildmode", JSON.stringify(mode)), buildMode);
}

export async function reportError(
  request: APIRequestContext,
  error: {
    errorType: string;
    errorMessage: string;
    isDebug?: boolean;
    timestamp?: string;
    release?: string;
    environment?: string;
  }
) {
  const response = await request.post("/api/v0/error", {
    headers: { "App-Key": APP_KEY },
    data: {
      // Issues are fingerprinted by type and stack trace, so the stack carries the per-run message.
      stackTrace: `${error.errorType}: ${error.errorMessage}
    at run (sample.ts:1:1)`,
      platform: "desktop",
      osName: "Linux",
      osVersion: "6.8",
      appVersion: "1.0.0",
      sdkVersion: "e2e@1.0.0",
      severity: "error",
      kind: "handled",
      isDebug: false,
      ...error,
    },
  });
  expect(response.status(), await response.text()).toBe(202);
}

type SpanInput = {
  spanId: string;
  parentSpanId?: string;
  name: string;
  operation: string;
  startOffsetMs: number;
  durationMs: number;
  status?: "ok" | "error" | "cancelled";
};

export async function reportTransaction(
  request: APIRequestContext,
  transaction: {
    name: string;
    operation: string;
    durationMs: number;
    status?: "ok" | "error" | "cancelled";
    release?: string;
    environment?: string;
    isDebug?: boolean;
    spans?: SpanInput[];
  }
) {
  const response = await request.post("/api/v0/transaction", {
    headers: { "App-Key": APP_KEY },
    data: {
      eventId: randomUUID(),
      timestamp: new Date(Date.now() - 60_000).toISOString(),
      platform: "desktop",
      ...transaction,
    },
  });
  expect(response.status(), await response.text()).toBe(202);
}

export async function snapshot(page: Page, name: string) {
  await waitForPageFadeIn(page);
  const file = path.join(artifactsDir, "real-screenshots", `${test.info().project.name}-${name}.png`);
  await page.screenshot({ path: file, fullPage: true, animations: "disabled" });
}
