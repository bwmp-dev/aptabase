import { expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { APP_ID, APP_KEY, PROBE_PATH, login, reportError, runToken, snapshot, test, withSettings } from "./real-host";

// End-to-end checks of owner monitoring controls against the loopback test host. Settings are app-wide, so every
// test restores the settings it found, and the destructive clear runs last.
test.describe.configure({ mode: "serial" });

const settingsPath = `/api/v0/apps/${APP_ID}/monitoring/settings`;

test.describe("Monitoring controls against the real test host", () => {
  test("local alerts record new issues and regressions; owner reads them by keyboard", async ({ page, context }) => {
    await login(context, "owner");
    await withSettings(context.request, async (original) => {
      const token = runToken();
      const regressing = { errorType: "AlertRegression", errorMessage: `Regression ${token}` };
      const fresh = { errorType: "AlertNew", errorMessage: `New ${token}` };
      const issueIdFor = async (text: string) => {
        const list = await context.request.get(`/api/v0/apps/${APP_ID}/issues?status=all&q=${encodeURIComponent(text)}`);
        return (await list.json()).issues[0].id as string;
      };

      // The cooldown runs from an issue's last alert, so the regressing issue is created before alerts are enabled.
      expect((await context.request.put(settingsPath, { data: { ...original, localAlertsEnabled: false } })).status()).toBe(200);
      await reportError(context.request, regressing);
      const regressingId = await issueIdFor(regressing.errorMessage);
      expect(
        (await context.request.patch(`/api/v0/apps/${APP_ID}/issues/${regressingId}?buildMode=release`, { data: { status: "resolved" } })).status()
      ).toBe(200);

      expect((await context.request.put(settingsPath, { data: { ...original, localAlertsEnabled: true, alertCooldownMinutes: 1 } })).status()).toBe(200);
      await reportError(context.request, fresh);
      const freshId = await issueIdFor(fresh.errorMessage);
      await page.waitForTimeout(1_100);
      await reportError(context.request, regressing);

      await page.goto(`/${APP_ID}/errors?view=alerts`);
      const regression = page.getByRole("listitem").filter({ hasText: regressingId.slice(0, 12) });
      const created = page.getByRole("listitem").filter({ hasText: freshId.slice(0, 12) });
      await expect(regression).toHaveCount(1);
      await expect(regression).toContainText("Regression");
      await expect(created).toHaveCount(1);
      await expect(created).toContainText("New issue");
      await snapshot(page, "alerts");

      const markRead = regression.getByRole("button", { name: "Mark read" });
      await markRead.focus();
      await page.keyboard.press("Enter");
      const open = regression.getByRole("button", { name: /Open issue/ });
      await expect(open).toBeFocused();
      await expect(regression).not.toContainText("Unread");

      await page.keyboard.press("Enter");
      await expect(page.getByRole("dialog").getByRole("heading", { name: "AlertRegression" })).toBeVisible();
    });
  });

  test("shared reader cannot reach monitoring controls", async ({ page, context }) => {
    await login(context, "reader");
    await page.goto(`/${APP_ID}/errors?view=alerts`);
    await expect(page.getByRole("tab", { name: "Issues" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("tab", { name: "Alerts" })).toHaveCount(0);
    for (const path of ["monitoring/settings", "monitoring/data", "monitoring/alerts", "sentry-projects"]) {
      expect((await context.request.get(`/api/v0/apps/${APP_ID}/${path}`)).status(), path).toBe(403);
    }
  });

  test("owner provisions Sentry DSNs from settings", async ({ page, context, baseURL }) => {
    await login(context, "owner");
    await page.goto(`/${APP_ID}/settings`);
    await page.getByRole("tab", { name: "Monitoring" }).click();

    const create = page.getByRole("button", { name: "Create Sentry DSNs" });
    const release = page.getByLabel("Release builds", { exact: true });
    await expect(create.or(release)).toBeVisible();
    if (await create.isVisible()) await create.click();
    await expect(release).not.toHaveValue("");

    // DSN keys are project-scoped: a lowercase 64-hex key per app, project and build mode, never the raw app key.
    const response = await context.request.get(`/api/v0/apps/${APP_ID}/sentry-projects`);
    expect(response.status()).toBe(200);
    const { projects } = (await response.json()) as { projects: { id: string; buildMode: string; dsn: string }[] };
    expect(projects.map((p) => p.buildMode).sort()).toEqual(["debug", "release"]);
    const host = new URL(baseURL!);
    const keys = new Set<string>();
    for (const project of projects) {
      expect(project.id).toMatch(/^[1-9]\d*$/);
      const key = new URL(project.dsn).username;
      expect(key, `${project.buildMode} DSN key`).toMatch(/^[0-9a-f]{64}$/);
      expect(key).not.toBe(APP_KEY.toLowerCase());
      expect(project.dsn).toBe(`${host.protocol}//${key}@${host.host}/${project.id}`);
      keys.add(key);
      await expect(page.getByLabel(project.buildMode === "release" ? "Release builds" : "Debug builds", { exact: true })).toHaveValue(
        project.dsn
      );
    }
    expect(new Set(projects.map((p) => p.id)).size).toBe(2);
    expect(keys.size).toBe(2);
  });

  test("owner turns capture off in the form, then clears PostgreSQL monitoring data", async ({ page, context }) => {
    await login(context, "owner");
    await withSettings(context.request, async () => {
      await reportError(context.request, { errorType: "ClearError", errorMessage: `Clear ${runToken()}` });
      const upload = await context.request.post(`/api/v0/apps/${APP_ID}/native-symbols?buildMode=debug`, {
        headers: { "Content-Type": "application/octet-stream" },
        data: readFileSync(PROBE_PATH),
      });
      expect(upload.status(), "symbol files are part of the purge").toBe(202);

      await page.goto(`/${APP_ID}/settings`);
      await page.getByRole("tab", { name: "Monitoring" }).click();
      const clearSection = page.getByRole("region", { name: "Clear monitoring data" });
      await expect(clearSection.getByRole("button", { name: "Clear monitoring data" })).toBeDisabled();
      const symbolCount = clearSection.getByText("Native symbol files", { exact: true }).locator("xpath=following-sibling::dd[1]");
      await expect(symbolCount).not.toHaveText("0");

      for (const label of ["Capture errors", "Capture performance", "Forward raw error logs"]) {
        await page.getByLabel(label).uncheck();
      }
      await page.getByLabel("Retention (days)").fill("7");
      await page.getByRole("button", { name: "Save settings" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Settings saved." })).toBeVisible();
      expect(await (await context.request.get(settingsPath)).json()).toMatchObject({
        captureErrors: false,
        capturePerformance: false,
        forwardRawErrors: false,
        monitoringRetentionDays: 7,
      });

      const rejected = await context.request.post("/api/v0/error", {
        headers: { "App-Key": APP_KEY },
        data: { errorType: "Rejected", errorMessage: "capture is off", stackTrace: "at off (x.ts:1:1)" },
      });
      expect(rejected.status()).not.toBe(202);

      await clearSection.getByLabel(/Type the app ID/).fill(APP_ID);
      await clearSection.getByRole("button", { name: "Clear monitoring data" }).click();
      await expect(clearSection.getByRole("status")).toHaveText("Monitoring data cleared.");
      await snapshot(page, "monitoring-cleared");

      const preview = await (await context.request.get(`/api/v0/apps/${APP_ID}/monitoring/data`)).json();
      expect(preview).toEqual({
        counts: { issues: 0, events: 0, transactions: 0, queued: 0, alerts: 0, symbols: 0 },
        excludesDeliveredRawLogs: true,
      });
    });
  });
});
