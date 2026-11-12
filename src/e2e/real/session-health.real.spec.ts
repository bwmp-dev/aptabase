import { APIRequestContext, expect } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { APP_ID, APP_KEY, login, runToken, snapshot, test, withSettings } from "./real-host";

// End-to-end checks against the loopback test host: real Sentry envelope ingestion of an error with session updates
// and a client report, the PostgreSQL session health summary, and read authorization. Each run uses a unique release.

const healthPath = `/api/v0/apps/${APP_ID}/monitoring/health`;

async function releaseProject(request: APIRequestContext) {
  let response = await request.get(`/api/v0/apps/${APP_ID}/sentry-projects`);
  expect(response.status()).toBe(200);
  let { projects } = (await response.json()) as { projects: { id: string; buildMode: string; dsn: string }[] };
  if (projects.length === 0) {
    response = await request.post(`/api/v0/apps/${APP_ID}/sentry-projects`);
    expect(response.status()).toBe(200);
    ({ projects } = await response.json());
  }
  const project = projects.find((p) => p.buildMode === "release")!;
  return { id: project.id, key: new URL(project.dsn).username };
}

function envelope(items: [string, unknown][]) {
  return (
    "{}\n" +
    items
      .map(([type, body]) => {
        const payload = JSON.stringify(body);
        return `${JSON.stringify({ type, length: Buffer.byteLength(payload) })}\n${payload}\n`;
      })
      .join("")
  );
}

function sid() {
  return randomUUID().replace(/-/g, "");
}

test.describe("Session health against the real test host", () => {
  test("ingests an error with sessions and a client report, and shows crash-free completed sessions", async ({
    page,
    context,
    browser,
    blockedExternal,
  }) => {
    await login(context, "owner");
    const token = runToken();
    const release = `s-${token}`;
    const started = new Date(Date.now() - 5 * 60_000);
    const session = (status: string, seq: number, errors = 0) => ({
      sid: sid(),
      started: started.toISOString(),
      timestamp: new Date(started.getTime() + seq * 1000).toISOString(),
      seq,
      init: true,
      status,
      errors,
      duration: seq,
      did: "synthetic-device",
      attrs: { release, environment: "synthetic", ip_address: "192.0.2.1", user_agent: "synthetic-agent" },
    });

    await withSettings(context.request, async (original) => {
      const settings = await context.request.put(`/api/v0/apps/${APP_ID}/monitoring/settings`, {
        data: { ...original, captureErrors: true },
      });
      expect(settings.status()).toBe(200);
      const project = await releaseProject(context.request);

      const response = await context.request.post(`/api/${project.id}/envelope/?sentry_key=${project.key}`, {
        headers: { "Content-Type": "application/x-sentry-envelope" },
        data: envelope([
          ["event", { event_id: sid(), message: `Synthetic crash ${token}`, release, environment: "synthetic" }],
          ["session", session("crashed", 2, 1)],
          ["session", session("exited", 3)],
          ["session", session("ok", 0)],
          [
            "client_report",
            {
              timestamp: new Date().toISOString(),
              discarded_events: [
                { reason: "queue_overflow", category: "error", quantity: 3 },
                { reason: "before_send", category: "error", quantity: 2 },
              ],
            },
          ],
        ]),
      });
      expect(response.status(), await response.text()).toBe(200);

      const summary = await (await context.request.get(`${healthPath}?buildMode=release&release=${encodeURIComponent(release)}`)).json();
      expect(summary.sessions).toEqual({
        observed: 3,
        withStart: 3,
        eligible: 2,
        crashed: 1,
        eligibleCrashed: 1,
        abnormal: 0,
        active: 1,
        errored: 1,
        incomplete: 1,
        crashFreeRate: 50,
      });
      expect(summary.clientReports).toMatchObject({ reports: 1, discardedEvents: 5 });
      expect(JSON.stringify(summary)).not.toContain("synthetic-device");
      expect(JSON.stringify(summary)).not.toMatch(/"(sid|did|sessionId|sessionKey)"/);

      await page.goto(`/${APP_ID}/health?release=${encodeURIComponent(release)}`);
      await expect(page.getByLabel("Release", { exact: true })).toHaveValue(release);
      const rate = page.getByTestId("crash-free-rate");
      await expect(rate).toContainText("50%");
      await expect(rate).toContainText("1 of 2 completed sessions ended without a crash.");
      const reports = page.getByRole("region", { name: "SDK discard reports" });
      await expect(reports).toContainText("1 report listing 5 discarded events");
      await expect(reports).toContainText("reports sent on their own are not included while filtering");
      await snapshot(page, "session-health");

      await page.getByRole("button", { name: "Build mode: release" }).click();
      await page.getByRole("button", { name: "Debug" }).click();
      await expect(page.getByLabel("Release", { exact: true })).toHaveValue("");
      const debug = await (await context.request.get(`${healthPath}?buildMode=debug&release=${encodeURIComponent(release)}`)).json();
      expect(debug.sessions.observed).toBe(0);
    });

    const reader = await browser.newContext({ baseURL: test.info().project.use.baseURL });
    try {
      await login(reader, "reader");
      const readerPage = await reader.newPage();
      const ownerOnly: string[] = [];
      readerPage.on("request", (request) => {
        const path = new URL(request.url()).pathname;
        if (/\/monitoring\/(settings|data|alerts)|\/sentry-projects|\/native-symbols/.test(path)) ownerOnly.push(path);
      });
      await readerPage.goto(`/${APP_ID}/health?release=${encodeURIComponent(release)}`);
      await expect(readerPage.getByTestId("crash-free-rate")).toContainText("50%");
      expect(ownerOnly).toEqual([]);
      expect((await reader.request.get("/api/v0/apps/other-app/monitoring/health")).status()).toBe(403);
    } finally {
      await reader.close();
    }

    const stranger = await browser.newContext({ baseURL: test.info().project.use.baseURL });
    try {
      expect((await stranger.request.get(healthPath)).status()).toBe(401);
      expect((await stranger.request.get(healthPath, { headers: { "App-Key": APP_KEY } })).status()).toBe(401);
      expect((await stranger.request.post("/test/login/stranger")).status()).toBe(200);
      expect((await stranger.request.get(healthPath)).status()).toBe(403);
    } finally {
      await stranger.close();
    }
    expect(blockedExternal).toEqual([]);
  });

  test("filters by a release seen only in session updates", async ({ page, context }) => {
    await login(context, "owner");
    const release = `so-${runToken()}`;
    const started = new Date(Date.now() - 2 * 60_000).toISOString();
    await withSettings(context.request, async (original) => {
      expect((await context.request.put(`/api/v0/apps/${APP_ID}/monitoring/settings`, { data: { ...original, captureErrors: true } })).status()).toBe(200);
      const project = await releaseProject(context.request);
      const response = await context.request.post(`/api/${project.id}/envelope/?sentry_key=${project.key}`, {
        headers: { "Content-Type": "application/x-sentry-envelope" },
        data: envelope([
          ["session", { sid: sid(), started, timestamp: started, seq: 1, init: true, status: "exited", errors: 0, duration: 1, attrs: { release, environment: "session-only" } }],
        ]),
      });
      expect(response.status(), await response.text()).toBe(200);

      const catalog = await (await context.request.get(`/api/v0/apps/${APP_ID}/releases?buildMode=release`)).json();
      expect(catalog.releases.some((r: { release: string }) => r.release === release)).toBe(false);

      await page.goto(`/${APP_ID}/health`);
      const select = page.getByLabel("Release", { exact: true });
      await expect(select.locator("option", { hasText: release })).toHaveCount(1);
      await select.selectOption(release);
      const rate = page.getByTestId("crash-free-rate");
      await expect(rate).toContainText("100%");
      await expect(rate).toContainText("1 of 1 completed sessions ended without a crash.");
      await page.getByLabel("Environment", { exact: true }).selectOption("session-only");
      await expect(rate).toContainText("1 of 1 completed sessions");
    });
  });

  test("rejects invalid filters", async ({ context }) => {
    await login(context, "owner");
    for (const query of ["buildMode=invalid", `release=${"r".repeat(101)}`, `environment=${"e".repeat(51)}`]) {
      expect((await context.request.get(`${healthPath}?${query}`)).status(), query).toBe(400);
    }
    const start = new Date(Date.now() - 40 * 24 * 3_600_000).toISOString();
    expect((await context.request.get(`${healthPath}?startDate=${start}&endDate=${new Date().toISOString()}`)).status()).toBe(400);
  });
});
