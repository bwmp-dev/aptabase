import { expect } from "@playwright/test";
import { APP_ID, login, reportError, reportTransaction, runToken, snapshot, test } from "./real-host";

// End-to-end checks against the loopback test host: real transaction ingestion, PostgreSQL aggregates,
// release catalog and read authorization. Each run labels its data with a unique release.

test.describe("Performance and releases against the real test host", () => {
  test("summarizes sampled operations and shows nested spans for a release", async ({ page, context, blockedExternal }) => {
    const token = runToken();
    const release = `r-${token}`;
    const name = `startup.${token}`;
    for (const [durationMs, status] of [
      [100, "ok"],
      [200, "ok"],
      [300, "error"],
    ] as const) {
      await reportTransaction(context.request, {
        name,
        operation: "ui.load",
        durationMs,
        status,
        release,
        environment: "production",
        spans: [
          { spanId: "a000000000000001", name: "load.preferences", operation: "file.read", startOffsetMs: 5, durationMs: 40 },
          {
            spanId: "a000000000000002",
            parentSpanId: "a000000000000001",
            name: "parse.preferences",
            operation: "serialize",
            startOffsetMs: 10,
            durationMs: 20,
          },
        ],
      });
    }

    await login(context, "owner");
    await page.goto(`/${APP_ID}/performance?release=${encodeURIComponent(release)}`);

    await expect(page.getByLabel("Release", { exact: true })).toHaveValue(release);
    const row = page.getByRole("table", { name: /Sampled operations/ }).getByRole("row").filter({ hasText: name });
    await expect(row.locator("td").nth(1)).toHaveText("3");
    await expect(row.locator("td").nth(2)).toHaveText("1");
    await expect(row.locator("td").nth(3)).toHaveText("200 ms");
    await snapshot(page, "performance");

    const transactions = page.getByRole("table", { name: /Sampled transactions/ });
    await expect(transactions.getByRole("row")).toHaveCount(4);
    const opener = transactions.getByRole("button", { name: new RegExp(name) }).first();
    await opener.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Spans (2)")).toBeVisible();
    await expect(dialog.getByText("Level 2:")).toBeAttached();
    await expect(dialog).toContainText(release);
    await snapshot(page, "transaction");
    await page.keyboard.press("Escape");
    await expect(opener).toBeFocused();
    expect(blockedExternal).toEqual([]);
  });

  test("release catalog combines errors and transactions and links to filtered pages", async ({ page, context }) => {
    const token = runToken();
    const release = `c-${token}`;
    await reportTransaction(context.request, { name: `save.${token}`, operation: "file.write", durationMs: 40, release, environment: "beta" });
    await reportTransaction(context.request, { name: `save.${token}`, operation: "file.write", durationMs: 60, release, environment: "beta" });
    await reportError(context.request, { errorType: "SaveError", errorMessage: `Disk full ${token}`, release, environment: "beta" });

    await login(context, "owner");
    await page.goto(`/${APP_ID}/releases`);
    const row = page.getByRole("table", { name: /Observed releases/ }).getByRole("row").filter({ hasText: release });
    await expect(row.locator("td").nth(1)).toHaveText("1");
    await expect(row.locator("td").nth(2)).toHaveText("2");
    await snapshot(page, "releases");

    await row.getByRole("link", { name: /^Issues for/ }).click();
    await expect(page).toHaveURL(new RegExp(`release=${release}&environment=beta`));
    await expect(page.getByRole("button", { name: /SaveError/ })).toBeVisible();
    await page.getByRole("button", { name: /SaveError/ }).click();
    const occurrences = page.getByRole("dialog").getByRole("region", { name: "Recent occurrences" });
    await expect(occurrences).toContainText(`${release} · beta · desktop`);
  });

  test("debug transactions stay out of release views", async ({ page, context }) => {
    const token = runToken();
    const name = `debug.${token}`;
    await reportTransaction(context.request, { name, operation: "ui.load", durationMs: 10, isDebug: true });

    await login(context, "owner");
    await page.goto(`/${APP_ID}/performance`);
    await expect(page.getByRole("heading", { name: "Operations" })).toBeVisible();
    await expect(page.getByRole("button", { name: new RegExp(`Filter transactions by ${name}`) })).toHaveCount(0);

    await page.getByRole("button", { name: "Build mode: release" }).click();
    await page.getByRole("button", { name: "Debug" }).click();
    await expect(page.getByRole("button", { name: new RegExp(`Filter transactions by ${name}`) })).toBeVisible();
  });

  test("shared reader can read performance; other apps and strangers cannot", async ({ page, context, browser }) => {
    const token = runToken();
    const name = `shared.${token}`;
    await reportTransaction(context.request, { name, operation: "ui.load", durationMs: 25 });

    await login(context, "reader");
    await page.goto(`/${APP_ID}/performance`);
    await expect(page.getByRole("button", { name: new RegExp(`Filter transactions by ${name}`) })).toBeVisible();
    expect((await context.request.get("/api/v0/apps/other-app/performance/summary")).status()).toBe(403);
    expect((await context.request.get("/api/v0/apps/other-app/releases")).status()).toBe(403);

    const stranger = await browser.newContext({ baseURL: test.info().project.use.baseURL });
    try {
      expect((await stranger.request.get(`/api/v0/apps/${APP_ID}/transactions`)).status()).toBe(401);
      expect((await stranger.request.post("/test/login/stranger")).status()).toBe(200);
      expect((await stranger.request.get(`/api/v0/apps/${APP_ID}/transactions`)).status()).toBe(403);
    } finally {
      await stranger.close();
    }
  });
});
