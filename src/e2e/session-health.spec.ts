import { expect, expectNoHorizontalOverflow, snapshot, test } from "./fixture-api";

// Mocked-network UI acceptance checks: the production bundle runs in headless Chromium and every
// /api request is answered by FixtureApi sample data. They do not exercise the real backend or database.
const healthPath = "/fixture-app/health";

function healthRequests(api: { requests: { url: URL }[] }) {
  return api.requests.filter((r) => r.url.pathname.endsWith("/monitoring/health")).map((r) => r.url.searchParams);
}

test.describe("Session health UI with fixture API", () => {
  test("shows crash-free completed sessions with coverage and filters by keyboard", async ({ page, api }) => {
    await page.goto(healthPath);

    const rate = page.getByTestId("crash-free-rate");
    await expect(rate).toContainText("97.8%");
    await expect(rate).toContainText("135 of 138 completed sessions ended without a crash.");
    await expect(page.getByText(/not crash-free usage overall/)).toBeVisible();
    await expect(page.getByRole("table", { name: /Sessions by release and environment/ }).getByRole("row")).toHaveCount(4);
    await expect(page.getByRole("table", { name: /Sessions by release and environment/ })).toContainText("None complete");
    const reports = page.getByRole("region", { name: "SDK discard reports" });
    await expect(reports).toContainText("3 reports listing 19 discarded events");
    await expect(reports).not.toContainText("%");
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "session-health");

    const day = page.getByRole("button", { name: "24 hours" });
    await day.focus();
    await page.keyboard.press("Enter");
    await expect(day).toHaveAttribute("aria-pressed", "true");
    await expect.poll(() => {
      const last = healthRequests(api).at(-1)!;
      return (Date.parse(last.get("endDate")!) - Date.parse(last.get("startDate")!)) / 3_600_000;
    }).toBe(24);

    const release = page.getByLabel("Release", { exact: true });
    await release.focus();
    await release.selectOption("2.3.1");
    await expect(rate).toContainText("37 of 38 completed sessions ended without a crash.");
    await expect(reports).toContainText("reports sent on their own are not included while filtering");
    await expect(reports).toContainText("No SDK discard reports in this period");
    expect(healthRequests(api).at(-1)!.get("release")).toBe("2.3.1");
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "session-health-filtered");

    await page.getByRole("button", { name: "Build mode: release" }).click();
    await page.getByRole("button", { name: "Debug" }).click();
    await expect(page.getByText("No sessions in this period")).toBeVisible();
    const debug = healthRequests(api).at(-1)!;
    expect(debug.get("buildMode")).toBe("debug");
    expect(debug.has("release")).toBe(false);
    await expect(page.getByLabel("Release", { exact: true })).toHaveValue("");
  });

  test("retries a failed summary and recovers by keyboard", async ({ page, api }) => {
    let failing = true;
    api.interceptor = (request) =>
      failing && request.url.pathname.endsWith("/monitoring/health") ? { status: 503 } : undefined;
    await page.goto(healthPath);

    const refresh = page.getByRole("button", { name: "Refresh" });
    await expect(refresh).toBeVisible({ timeout: 15_000 });
    expect(healthRequests(api)).toHaveLength(3);
    await expectNoHorizontalOverflow(page);

    failing = false;
    await refresh.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("crash-free-rate")).toContainText("97.8%");
  });

  test("shared readers view session health without owner-only requests", async ({ page, api }) => {
    api.canManage = false;
    await page.goto(healthPath);

    await expect(page.getByTestId("crash-free-rate")).toContainText("97.8%");
    await expectNoHorizontalOverflow(page);
    const ownerOnly = api.requests.filter(
      (r) =>
        (r.url.pathname.includes("/monitoring/") && !r.url.pathname.endsWith("/monitoring/health")) ||
        r.url.pathname.endsWith("/sentry-projects") ||
        r.url.pathname.includes("/native-symbols")
    );
    expect(ownerOnly).toEqual([]);
  });

  test("clear-data preview lists sessions and discard reports", async ({ page }) => {
    await page.goto("/fixture-app/settings");
    await page.getByRole("tab", { name: "Monitoring" }).click();
    const clearSection = page.getByRole("region", { name: "Clear monitoring data" });
    await expect(clearSection.getByText("Sessions", { exact: true }).locator("xpath=following-sibling::dd[1]")).toHaveText("163");
    await expect(clearSection.getByText("SDK discard reports", { exact: true }).locator("xpath=following-sibling::dd[1]")).toHaveText("3");
    await expect(clearSection).toContainText("private key for hashing session IDs is also removed");
    await expect(page.getByText(/session updates for Session health/)).toBeVisible();
  });
});
