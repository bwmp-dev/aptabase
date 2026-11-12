import { expect, expectNoHorizontalOverflow, snapshot, test } from "./fixture-api";

// Mocked-network UI acceptance checks: the production bundle runs in headless Chromium and every
// /api request is answered by FixtureApi sample data. They do not exercise the real backend or database.
test.describe("Monitoring controls and local alerts with fixture API", () => {
  test("owner edits settings, provisions DSNs and clears data after turning capture off", async ({ page, api }) => {
    await page.goto("/fixture-app/settings");
    await page.getByRole("tab", { name: "Monitoring" }).click();

    const retention = page.getByLabel("Retention (days)");
    await retention.fill("0");
    await page.getByRole("button", { name: "Save settings" }).click();
    await expect(page.getByText("Enter a whole number from 1 to 30.")).toBeVisible();
    await expect(retention).toHaveAttribute("aria-invalid", "true");

    await retention.fill("14");
    const forward = page.getByLabel("Forward raw error logs");
    await forward.focus();
    await page.keyboard.press("Space");
    await expect(forward).not.toBeChecked();
    await page.getByRole("button", { name: "Save settings" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Settings saved." })).toBeVisible();
    expect(api.monitoringSettings).toMatchObject({ monitoringRetentionDays: 14, forwardRawErrors: false });
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "monitoring-settings");

    await page.getByRole("button", { name: "Create Sentry DSNs" }).click();
    await expect(page.getByLabel("Release builds", { exact: true })).toHaveValue("http://A-SH-0000000000@127.0.0.1/41");
    await expect(page.getByLabel("Debug builds", { exact: true })).toHaveValue("http://A-SH-0000000000@127.0.0.1/42");

    const clearSection = page.getByRole("region", { name: "Clear monitoring data" });
    const clearButton = clearSection.getByRole("button", { name: "Clear monitoring data" });
    await expect(clearSection).toContainText("1,330");
    await expect(clearButton).toBeDisabled();
    await expect(clearSection.getByLabel(/Type the app ID/)).toBeDisabled();

    await page.getByLabel("Capture errors").uncheck();
    await page.getByLabel("Capture performance").uncheck();
    await page.getByRole("button", { name: "Save settings" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Settings saved." })).toBeVisible();

    const confirm = clearSection.getByLabel(/Type the app ID/);
    await confirm.fill("fixture-ap");
    await expect(clearButton).toBeDisabled();
    await confirm.fill("fixture-app");
    await clearButton.click();
    await expect(clearSection.getByRole("status")).toHaveText("Monitoring data cleared.");
    await expect(clearSection).not.toContainText("1,330");
    const deletion = api.requests.find((r) => r.method === "DELETE")!;
    expect(deletion.body).toEqual({ confirmAppId: "fixture-app" });
    await snapshot(page, "monitoring-cleared");
  });

  test("owner reviews local alerts by keyboard and opens the issue", async ({ page, api }) => {
    await page.goto("/fixture-app/errors?view=alerts");

    await expect(page.getByRole("tab", { name: "Alerts" })).toHaveAttribute("aria-selected", "true");
    const items = page.getByRole("listitem").filter({ hasText: /Regression|New issue/ });
    await expect(items).toHaveCount(2);
    await expect(items.first()).toContainText("Unread");
    await expectNoHorizontalOverflow(page);
    await snapshot(page, "alerts");

    const markRead = items.first().getByRole("button", { name: "Mark read" });
    await markRead.focus();
    await page.keyboard.press("Enter");
    const open = items.first().getByRole("button", { name: /Open issue/ });
    await expect(open).toBeFocused();
    await expect(items.first()).not.toContainText("Unread");
    expect(api.alerts[0].readAt).not.toBeNull();

    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "NetworkError" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(open).toBeFocused();
  });

  test("shared viewers get no monitoring controls or alerts", async ({ page, api }) => {
    api.canManage = false;
    await page.goto("/fixture-app/errors?view=alerts");

    await expect(page.getByRole("tab", { name: "Issues" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("tab", { name: "Alerts" })).toHaveCount(0);

    await page.goto("/fixture-app/performance");
    await expect(page.getByRole("table", { name: /Sampled operations/ })).toBeVisible();
    expect(api.requests.some((r) => r.url.pathname.includes("/monitoring/") || r.url.pathname.endsWith("/sentry-projects"))).toBe(false);
  });
});
