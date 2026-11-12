import type { Locator, Page } from "@playwright/test";
import {
  NATIVE_EVENT_ID,
  PROBE_BUILD_ID,
  PROBE_FUNCTION,
  expect,
  expectNoHorizontalOverflow,
  readyProbeArtifact,
  snapshot,
  test,
} from "./fixture-api";

// Mocked-network UI acceptance checks: every /api request is answered by FixtureApi sample data, including the
// stand-in for asynchronous symbol validation. They do not exercise the real backend, processor or database.

function elfHeader() {
  const bytes = Buffer.alloc(64);
  bytes.set([0x7f, 0x45, 0x4c, 0x46, 2, 1, 1]);
  bytes.writeUInt16LE(3, 16);
  bytes.writeUInt16LE(183, 18);
  return bytes;
}

async function openSymbolsSettings(page: Page) {
  await page.goto("/fixture-app/settings");
  await page.getByRole("tab", { name: "Monitoring" }).click();
  return page.getByRole("region", { name: "Native symbols" });
}

async function expandNativeOccurrence(page: Page): Promise<Locator> {
  await page.goto("/fixture-app/errors");
  await page.getByRole("button", { name: /NetworkError/ }).click();
  const occurrences = page.getByRole("dialog").getByRole("region", { name: "Recent occurrences" });
  const native = occurrences.locator("details").filter({ hasText: "Linux 6.8" });
  const summary = native.locator("summary");
  await summary.focus();
  await page.keyboard.press("Enter");
  return native.getByRole("region", { name: "Native frames" });
}

function purgeCount(page: Page) {
  return page
    .getByRole("region", { name: "Clear monitoring data" })
    .getByText("Native symbol files", { exact: true })
    .locator("xpath=following-sibling::dd[1]");
}

test.describe("Native symbols and native frames with fixture API", () => {
  test("owner uploads symbols, sees validation finish, and deletes them", async ({ page, api }) => {
    const section = await openSymbolsSettings(page);
    await expect(section).toContainText("Only function names and address ranges are read");
    await expect(section.getByText("No symbol files uploaded for release builds.")).toBeVisible();
    await expect(purgeCount(page)).toHaveText("0");

    const bytes = elfHeader();
    await section.getByLabel("release symbol file").setInputFiles({ name: "libprobe.so", mimeType: "application/x-sharedlib", buffer: bytes });
    await expect(section.getByRole("status")).toHaveText("Upload received. It will be validated before it can be used.");
    const row = section.getByRole("listitem").first();
    await expect(row).toContainText("Pending validation");
    await expect(row).not.toContainText("Ready");

    const upload = api.requests.find((r) => r.method === "POST" && r.url.pathname.endsWith("/native-symbols"))!;
    expect(upload.contentType).toBe("application/octet-stream");
    expect(Buffer.compare(upload.body as Buffer, bytes)).toBe(0);
    expect(upload.url.search).toBe("?buildMode=release");
    await expect(purgeCount(page)).toHaveText("1");

    api.validatePendingSymbols();
    await expect(row).toContainText("Ready", { timeout: 10_000 });
    await expect(row).toContainText(PROBE_BUILD_ID);
    await expectNoHorizontalOverflow(page);
    await row.scrollIntoViewIfNeeded();
    await snapshot(page, "native-symbols");

    const deleteButton = section.getByRole("button", { name: `Delete build ID ${PROBE_BUILD_ID}` });
    await deleteButton.click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(`Build ID ${PROBE_BUILD_ID} will be removed from release builds`);
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
    expect(api.nativeSymbols.release).toHaveLength(1);

    await deleteButton.click();
    await dialog.getByRole("button", { name: "Delete" }).click();
    await expect(section.getByRole("status")).toHaveText(`Deleted build ID ${PROBE_BUILD_ID}.`);
    await expect(section.getByText("No symbol files uploaded for release builds.")).toBeVisible();
    await expect(purgeCount(page)).toHaveText("0");
    const deletion = api.requests.find((r) => r.method === "DELETE" && r.url.pathname.includes("/native-symbols/"))!;
    expect(deletion.url.searchParams.get("buildMode")).toBe("release");
  });

  test("uploads stay off while the processor is unconfigured", async ({ page, api }) => {
    api.symbolsEnabled = false;
    api.nativeSymbols.release = [readyProbeArtifact()];
    const section = await openSymbolsSettings(page);

    await expect(section).toContainText("Symbol processing isn't configured on this server");
    await expect(section.getByRole("button", { name: "Upload release symbols" })).toBeDisabled();
    await expect(section.getByRole("button", { name: `Delete build ID ${PROBE_BUILD_ID}` })).toBeEnabled();
    expect(api.requests.some((r) => r.method === "POST")).toBe(false);
  });

  test("occurrence shows function-only frames, then unresolved after the owner deletes symbols", async ({ page, api }) => {
    api.nativeSymbols.release = [readyProbeArtifact()];
    const frames = await expandNativeOccurrence(page);

    await expect(frames).toContainText("Function names only: no file names, line numbers or inlined frames.");
    const rows = frames.getByRole("listitem");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText(`${PROBE_FUNCTION} +0x4`);
    await expect(rows.nth(0)).toContainText(`0x2e0 in build ${PROBE_BUILD_ID}`);
    await expect(rows.nth(1)).toContainText("Unresolved");
    await expectNoHorizontalOverflow(page);
    await frames.scrollIntoViewIfNeeded();
    await snapshot(page, "native-frames");
    const read = api.requests.find((r) => r.url.pathname.endsWith("/native-frames"))!;
    expect(read.url.pathname).toContain(`/events/${NATIVE_EVENT_ID}/native-frames`);
    expect(read.url.searchParams.get("buildMode")).toBe("release");

    const section = await openSymbolsSettings(page);
    await section.getByRole("button", { name: `Delete build ID ${PROBE_BUILD_ID}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete" }).click();
    await expect(section.getByRole("status")).toHaveText(`Deleted build ID ${PROBE_BUILD_ID}.`);

    const unresolved = await expandNativeOccurrence(page);
    await expect(unresolved.getByRole("listitem")).toHaveCount(2);
    await expect(unresolved.getByRole("listitem").nth(0)).toContainText("Unresolved");
    await expect(unresolved.getByRole("listitem").nth(0)).toContainText(`0x2e0 in build ${PROBE_BUILD_ID}`);
    await expect(unresolved).not.toContainText(PROBE_FUNCTION);
  });

  test("shared readers see resolved frames but never request symbol controls", async ({ page, api }) => {
    api.canManage = false;
    api.nativeSymbols.release = [readyProbeArtifact()];

    const frames = await expandNativeOccurrence(page);
    await expect(frames.getByRole("listitem").nth(0)).toContainText(`${PROBE_FUNCTION} +0x4`);

    expect(api.requests.some((r) => r.url.pathname.includes("/native-symbols") || r.url.pathname.includes("/monitoring/"))).toBe(false);
  });
});
