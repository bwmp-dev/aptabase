import { expect, expectNoHorizontalOverflow, snapshot, test } from "./fixture-api";

// Mocked-network UI acceptance checks: the production bundle runs in headless Chromium and every
// /api request is answered by FixtureApi sample data. They do not exercise the real backend or database.
const performancePath = "/fixture-app/performance";

test.describe("Performance, releases and occurrences UI with fixture API", () => {
  test("inspects sampled operations, filters transactions and opens nested spans by keyboard", async ({
    page,
    api,
  }) => {
    await page.goto(performancePath);

    const operations = page.getByRole("table", { name: /Sampled operations/ });
    await expect(operations.getByRole("row")).toHaveCount(4);
    await expect(page.getByText(/not total usage/)).toBeVisible();
    await expect(page.getByRole("columnheader", { name: "p95" })).toBeInViewport();
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "performance");

    const filterButton = page.getByRole("button", { name: "Filter transactions by sync.settings http.client" });
    await filterButton.focus();
    await page.keyboard.press("Enter");
    await expect(filterButton).toHaveAttribute("aria-pressed", "true");
    const transactions = page.getByRole("table", { name: /Sampled transactions/ });
    await expect(transactions.getByRole("row")).toHaveCount(2);
    const lastList = api.requests.filter((r) => r.url.pathname.endsWith("/transactions")).at(-1)!;
    expect(Object.fromEntries(lastList.url.searchParams)).toMatchObject({ name: "sync.settings", operation: "http.client" });

    const opener = transactions.getByRole("button", { name: /sync\.settings/ });
    await opener.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "sync.settings" })).toBeVisible();
    await expect(dialog.getByText("Spans (3)")).toBeVisible();
    await expect(dialog.getByText("Level 2:")).toBeAttached();
    await snapshot(page, "transaction");

    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();

    await page.getByLabel("Release", { exact: true }).selectOption("2.3.1");
    await expect(page.getByText("No sampled operations in this period")).toBeVisible();
    const summary = api.requests.filter((r) => r.url.pathname.endsWith("/performance/summary")).at(-1)!;
    expect(summary.url.searchParams.get("release")).toBe("2.3.1");
    expect(summary.url.searchParams.has("name")).toBe(false);
  });

  test("keeps a 100-level span tree readable at narrow widths", async ({ page }) => {
    await page.goto(performancePath);
    await page.getByRole("table", { name: /Sampled transactions/ }).getByRole("button", { name: /deep\.tree/ }).click();

    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Spans (100)")).toBeVisible();
    const deepest = dialog.getByText("Level 100: nested.step.100", { exact: true });
    await deepest.scrollIntoViewIfNeeded();
    await expect(deepest).toBeVisible();

    const viewport = page.viewportSize()!;
    const box = (await deepest.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + Math.min(box.width, 40)).toBeLessThanOrEqual(viewport.width);
    const dialogBox = (await dialog.boundingBox())!;
    expect(box.x).toBeLessThan(dialogBox.x + dialogBox.width / 2);
    await snapshot(page, "deep-spans");
  });

  test("release catalog links open performance already filtered", async ({ page, api }) => {
    await page.goto("/fixture-app/releases");

    await expect(page.getByRole("table", { name: /Observed releases/ }).getByRole("row")).toHaveCount(4);
    await expect(page.getByText(/not a deployment history/)).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "releases");

    await page.getByRole("link", { name: "Performance for 2.3.1 production" }).click();
    await expect(page).toHaveURL(/\/fixture-app\/performance\?release=2\.3\.1&environment=production/);
    await expect(page.getByLabel("Release", { exact: true })).toHaveValue("2.3.1");
    await expect(page.getByLabel("Environment", { exact: true })).toHaveValue("production");
    await expect(page.getByRole("table", { name: /Sampled transactions/ }).getByRole("row")).toHaveCount(2);
    const list = api.requests.filter((r) => r.url.pathname.endsWith("/transactions")).at(-1)!;
    expect(Object.fromEntries(list.url.searchParams)).toMatchObject({ release: "2.3.1", environment: "production" });
  });

  test("issue detail shows retained occurrences with expandable context", async ({ page }) => {
    await page.goto("/fixture-app/errors?release=2.4.0");

    await expect(page.getByLabel("Release", { exact: true })).toHaveValue("2.4.0");
    await page.getByRole("button", { name: /NetworkError/ }).click();
    const occurrences = page.getByRole("dialog").getByRole("region", { name: "Recent occurrences" });
    await expect(occurrences).toContainText("limited to release 2.4.0");

    const first = occurrences.locator("summary").first();
    await first.focus();
    await page.keyboard.press("Enter");
    await expect(occurrences.getByText("aptabase-electron@0.4.0").first()).toBeVisible();
    await expect(occurrences.getByText("at sync (settings.ts:88:13)")).toBeVisible();
    await occurrences.scrollIntoViewIfNeeded();
    await snapshot(page, "occurrences");
  });
});
