import { Page, expect } from "@playwright/test";
import { APP_ID, login, reportError, runToken, snapshot, test } from "./real-host";

// End-to-end checks against the loopback test host: real HTTP, auth cookies, ACLs, ingestion and PostgreSQL.
// Raw error analytics (ClickHouse) reads are not supported by the host and are not covered here.

async function searchIssues(page: Page, value: string) {
  const searchBox = page.getByRole("searchbox", { name: "Search issues" });
  await searchBox.focus();
  await page.keyboard.type(value);
  await page.keyboard.press("Enter");
}

async function issueRows(page: Page) {
  return page.getByRole("table").getByRole("row");
}

test.describe("Issues against the real test host", () => {
  test("owner groups, searches, resolves; later reports reopen and delayed ones don't", async ({
    page,
    context,
    blockedExternal,
  }) => {
    const token = runToken();
    const crash = { errorType: "SyncError", errorMessage: `Sync failed ${token}` };
    const delayed = { errorType: "CacheError", errorMessage: `Cache miss ${token}` };
    await reportError(context.request, crash);
    await reportError(context.request, crash);
    await reportError(context.request, delayed);

    await login(context, "owner");
    await page.goto(`/${APP_ID}/errors`);
    await searchIssues(page, token.toUpperCase());

    const rows = await issueRows(page);
    await expect(rows).toHaveCount(3);
    const crashRow = rows.filter({ hasText: "SyncError" });
    await expect(crashRow.locator("td").nth(2)).toHaveText("2");
    await snapshot(page, "owner-search");

    for (const issue of [crash, delayed]) {
      const opener = page.getByRole("button", { name: new RegExp(issue.errorType) });
      await opener.focus();
      await page.keyboard.press("Enter");
      const dialog = page.getByRole("dialog");
      await dialog.getByRole("button", { name: "Resolve issue" }).focus();
      await page.keyboard.press("Enter");
      await expect(dialog.getByRole("button", { name: "Reopen issue" })).toBeVisible();
      if (issue === crash) await snapshot(page, "owner-resolved");
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
    }
    await expect(page.getByRole("status")).toContainText("No unresolved issues match");

    // Event timestamps are normalized server-side; wait so the new report is strictly after resolvedAt.
    await page.waitForTimeout(1_100);
    await reportError(context.request, crash);
    await reportError(context.request, { ...delayed, timestamp: "2020-01-01T00:00:00Z" });

    await page.reload();
    await searchIssues(page, token);
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(1)).toContainText("SyncError");
    await expect(rows.nth(1).locator("td").nth(2)).toHaveText("3");

    await page.getByRole("button", { name: "Resolved", exact: true }).click();
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(1)).toContainText("CacheError");
    await expect(rows.nth(1).locator("td").nth(2)).toHaveText("2");

    await page.getByRole("button", { name: /CacheError/ }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Reopen issue" }).click();
    await expect(page.getByRole("dialog").getByRole("button", { name: "Resolve issue" })).toBeVisible();
    expect(blockedExternal).toEqual([]);
  });

  test("search pages through more than one page of results", async ({ page, context }) => {
    const token = runToken();
    for (let i = 0; i < 51; i++) {
      await reportError(context.request, { errorType: "PageError", errorMessage: `Page ${token} #${i}` });
      // The ingestion rate limit is 20 requests per second per IP.
      await page.waitForTimeout(60);
    }

    await login(context, "owner");
    await page.goto(`/${APP_ID}/errors`);
    await searchIssues(page, token);

    const rows = await issueRows(page);
    await expect(rows).toHaveCount(51);
    await expect(page.getByText("Showing 1 to 50 of 51 issues")).toBeVisible();
    await page.getByRole("button", { name: /Next/ }).click();
    await expect(page.getByText("Showing 51 to 51 of 51 issues")).toBeVisible();
    await expect(rows).toHaveCount(2);
    await snapshot(page, "pagination");
  });

  test("release and debug issues stay separate", async ({ page, context }) => {
    const token = runToken();
    await reportError(context.request, { errorType: "DebugOnly", errorMessage: `Debug ${token}`, isDebug: true });

    await login(context, "owner");
    await page.goto(`/${APP_ID}/errors`);
    await searchIssues(page, token);
    await expect(page.getByRole("status")).toContainText("No unresolved issues match");

    await page.getByRole("button", { name: "Build mode: release" }).click();
    await page.getByRole("button", { name: "Debug" }).click();
    await searchIssues(page, token);
    await expect(page.getByRole("button", { name: new RegExp(`DebugOnly Debug ${token}`) })).toBeVisible();
    await expect(await issueRows(page)).toHaveCount(2);
    await snapshot(page, "debug-mode");
  });

  test("reader can view but not change issues; other apps stay private", async ({ page, context, browser }) => {
    const token = runToken();
    await reportError(context.request, { errorType: "ReaderError", errorMessage: `Shared ${token}` });

    await login(context, "reader");
    await page.goto(`/${APP_ID}/errors`);
    await expect(page.getByText("You have read-only access to this app.")).toBeVisible();
    await searchIssues(page, token);

    const opener = page.getByRole("button", { name: /ReaderError/ });
    await opener.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText(/Only app owners can resolve or reopen issues/)).toBeVisible();
    await expect(dialog.getByRole("button", { name: /Resolve issue|Reopen issue/ })).toHaveCount(0);
    await snapshot(page, "reader-detail");
    await page.keyboard.press("Escape");
    await expect(opener).toBeFocused();

    const list = await context.request.get(`/api/v0/apps/${APP_ID}/issues?status=all&q=${encodeURIComponent(token)}`);
    const issueId = (await list.json()).issues[0].id as string;
    const patch = await context.request.patch(`/api/v0/apps/${APP_ID}/issues/${issueId}?buildMode=release`, {
      data: { status: "resolved" },
    });
    expect(patch.status()).toBe(403);
    expect((await context.request.get("/api/v0/apps/other-app/issues")).status()).toBe(403);

    const strangerContext = await browser.newContext({ baseURL: test.info().project.use.baseURL });
    try {
      expect((await strangerContext.request.get(`/api/v0/apps/${APP_ID}/issues`)).status()).toBe(401);
      expect((await strangerContext.request.post("/test/login/stranger")).status()).toBe(200);
      expect((await strangerContext.request.get(`/api/v0/apps/${APP_ID}/issues`)).status()).toBe(403);
    } finally {
      await strangerContext.close();
    }
  });
});
