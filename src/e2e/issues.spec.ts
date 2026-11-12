import { appErrorsPath, expect, expectNoHorizontalOverflow, snapshot, test } from "./fixture-api";

// Mocked-network UI acceptance checks: the production bundle runs in headless Chromium and every
// /api request is answered by FixtureApi. They do not exercise the real backend or database.
test.describe("Issues UI with fixture API", () => {
  test("owner searches, resolves and reopens an issue with the keyboard", async ({ page, api, pageErrors }) => {
    api.patchDelayMs = 500;
    await page.goto(appErrorsPath);

    await expect(page.getByRole("tab", { name: "Issues" })).toHaveAttribute("aria-selected", "true");
    const rows = page.getByRole("table").getByRole("row");
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(1)).toContainText("NetworkError");
    await expect(rows.nth(2)).toContainText("System.InvalidOperationException");
    await expect(page.getByText(/Counts are lifetime totals and ignore the date range/)).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await expect(page.getByRole("columnheader", { name: "Occurrences" })).toBeInViewport();
    await expect(page.getByRole("columnheader", { name: "Last seen" })).toBeInViewport();
    await snapshot(page, "list");

    const searchBox = page.getByRole("searchbox", { name: "Search issues" });
    await searchBox.focus();
    await page.keyboard.type("TIMED out");
    await page.keyboard.press("Enter");
    await expect(rows).toHaveCount(2);
    expect(api.listRequests().at(-1)).toMatchObject({ q: "TIMED out", offset: "0", status: "unresolved" });
    await snapshot(page, "search");

    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "Clear search" })).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(page.getByLabel("Release", { exact: true })).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(page.getByLabel("Environment", { exact: true })).toBeFocused();
    await page.keyboard.press("Tab");
    const issueButton = page.getByRole("button", { name: /NetworkError/ });
    await expect(issueButton).toBeFocused();
    await page.keyboard.press("Enter");

    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "NetworkError" })).toBeVisible();
    await expect(dialog).toContainText("1,284 (lifetime)");
    await expect(dialog).toContainText("It reopens automatically if the error occurs again afterwards.");
    await snapshot(page, "detail");

    const resolve = dialog.getByRole("button", { name: "Resolve issue" });
    await resolve.focus();
    await page.keyboard.press("Enter");
    await expect(dialog.getByRole("button", { name: "Resolving…" })).toBeDisabled();
    await expect(dialog.getByRole("button", { name: "Reopen issue" })).toBeVisible();
    await expect(dialog).toContainText("Delayed reports of earlier occurrences and retried deliveries don't reopen it.");
    await snapshot(page, "resolved");

    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("status")).toContainText("No unresolved issues match “TIMED out”");

    await page.getByRole("button", { name: "Resolved", exact: true }).click();
    await expect(rows).toHaveCount(2);
    await issueButton.click();
    await dialog.getByRole("button", { name: "Reopen issue" }).click();
    await expect(dialog.getByRole("button", { name: "Resolve issue" })).toBeVisible();

    const patches = api.requests.filter((r) => r.method === "PATCH");
    expect(patches.map((r) => r.body)).toEqual([{ status: "resolved" }, { status: "unresolved" }]);
    expect(patches.every((r) => r.url.searchParams.get("buildMode") === "release")).toBe(true);
    expect(pageErrors).toEqual([]);
  });

  test("read-only viewer gets no lifecycle controls and focus returns after closing", async ({ page, api }) => {
    api.canManage = false;
    await page.goto(appErrorsPath);

    await expect(page.getByText("You have read-only access to this app.")).toBeVisible();
    const issueButton = page.getByRole("button", { name: /NetworkError/ });
    await issueButton.focus();
    await page.keyboard.press("Enter");

    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText(/Only app owners can resolve or reopen issues/)).toBeVisible();
    await expect(dialog.getByRole("button", { name: /Resolve issue|Reopen issue/ })).toHaveCount(0);
    await snapshot(page, "read-only");

    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(issueButton).toBeFocused();
    expect(api.requests.some((r) => r.method === "PATCH")).toBe(false);
  });

  test("shows loading, then a server failure that can be retried", async ({ page, api }) => {
    let releaseFirst: () => void = () => {};
    const firstHeld = new Promise<void>((resolve) => (releaseFirst = resolve));
    let failing = true;
    let listCalls = 0;
    api.interceptor = async ({ method, url }) => {
      if (method !== "GET" || !url.pathname.endsWith("/issues") || !failing) return undefined;
      listCalls += 1;
      if (listCalls === 1) await firstHeld;
      return { status: 500 };
    };
    await page.goto(appErrorsPath);

    await expect(page.locator("svg.animate-spin")).toBeVisible();
    await snapshot(page, "loading");
    releaseFirst();

    // Server errors are retried twice with backoff before the error state appears.
    await expect(page.getByText("Oops... Something went wrong.")).toBeVisible({ timeout: 10_000 });
    expect(listCalls).toBe(3);
    await snapshot(page, "error");

    failing = false;
    const refresh = page.getByRole("button", { name: "Refresh" });
    await refresh.focus();
    await page.keyboard.press("Enter");

    await expect(page.getByRole("table").getByRole("row")).toHaveCount(3);
  });

  test("explains a rejected search and clears back to the list", async ({ page, api }) => {
    api.interceptor = ({ url }) => (url.searchParams.has("q") ? { status: 400 } : undefined);
    await page.goto(appErrorsPath);
    await expect(page.getByRole("table")).toBeVisible();

    const searchBox = page.getByRole("searchbox", { name: "Search issues" });
    await searchBox.fill("anything");
    const alert = page.getByRole("alert").filter({ hasText: "This search couldn't be run" });
    await expect(alert).toBeVisible({ timeout: 1_500 });
    expect(api.listRequests().filter((r) => "q" in r)).toHaveLength(1);
    await snapshot(page, "search-rejected");

    await alert.getByRole("button", { name: "Clear search" }).click();
    await expect(page.getByRole("table")).toBeVisible();
    await expect(searchBox).toHaveValue("");
    await expect(searchBox).toBeFocused();
  });

  test("switching build mode resets search and loads debug issues", async ({ page, api }) => {
    await page.goto(appErrorsPath);
    const searchBox = page.getByRole("searchbox", { name: "Search issues" });
    await searchBox.fill("network");
    await expect(page.getByRole("table").getByRole("row")).toHaveCount(2);

    await page.getByRole("button", { name: "Build mode: release" }).click();
    await page.getByRole("button", { name: "Debug" }).click();

    await expect(page.getByRole("button", { name: /AssertionError/ })).toBeVisible();
    await expect(searchBox).toHaveValue("");
    expect(api.listRequests().at(-1)).toEqual({ buildMode: "debug", status: "unresolved", offset: "0", limit: "50" });
  });

  test("raw error logs stay reachable from the tabs", async ({ page, api }) => {
    api.interceptor = ({ url }) => {
      if (url.pathname.endsWith("/errors")) {
        return { status: 200, body: { errors: [], pagination: { offset: 0, limit: 50, total: 0 } } };
      }
      if (url.pathname.endsWith("/errors/types") || url.pathname === "/api/_stats/top-operatingsystems") {
        return { status: 200, body: [] };
      }
      return undefined;
    };
    await page.goto(appErrorsPath);

    const logsTab = page.getByRole("tab", { name: "Error logs" });
    await logsTab.focus();
    await page.keyboard.press("Enter");
    await expect(logsTab).toHaveAttribute("aria-selected", "true");
    await expect(page).toHaveURL(/view=logs/);
    await expect(page.getByText("Date Range:")).toBeVisible();
  });
});
