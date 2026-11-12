import type { Locator, Page } from "@playwright/test";
import {
  INLINE_BUILD_ID,
  INLINE_FUNCTION,
  NATIVE_EVENT_ID,
  PROBE_BUILD_ID,
  PROBE_FUNCTION,
  expect,
  expectNoHorizontalOverflow,
  readyInlineArtifact,
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

// Frame rows only: resolved frames can contain a nested list of inlined calls.
function frameRows(frames: Locator) {
  return frames.getByRole("list", { name: "Stack frames" }).locator(":scope > li");
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
    await expect(section).toContainText("Source code and columns are never read or stored");
    await expect(section.getByText("No symbol files uploaded for release builds.")).toBeVisible();
    await expect(purgeCount(page)).toHaveText("0");

    const bytes = elfHeader();
    await section.getByLabel("release symbol file", { exact: true }).setInputFiles({ name: "libprobe.so", mimeType: "application/x-sharedlib", buffer: bytes });
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

  test("occurrence shows frames with source locations, then unresolved after the owner deletes symbols", async ({ page, api }) => {
    api.nativeSymbols.release = [readyProbeArtifact()];
    const frames = await expandNativeOccurrence(page);

    await expect(frames).toContainText("plus file names, line numbers and inlined calls when the symbol file has line information");
    const rows = frameRows(frames);
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0)).toContainText("Unresolved");
    await expect(rows.nth(0)).toContainText(`0x2d4 in build ${INLINE_BUILD_ID}`);
    await expect(rows.nth(1)).toContainText(`${PROBE_FUNCTION} +0x4`);
    await expect(rows.nth(1)).toContainText("probe.c:3");
    await expect(rows.nth(1)).toContainText(`0x2e0 in build ${PROBE_BUILD_ID}`);
    await expect(rows.nth(2)).toContainText("Unresolved");
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
    await expect(frameRows(unresolved)).toHaveCount(3);
    await expect(frameRows(unresolved).nth(1)).toContainText("Unresolved");
    await expect(frameRows(unresolved).nth(1)).toContainText(`0x2e0 in build ${PROBE_BUILD_ID}`);
    await expect(unresolved).not.toContainText(PROBE_FUNCTION);
    await expect(unresolved).not.toContainText("probe.c");
  });

  test("inline chains read innermost first, wrap at phone width, and fall back when source is missing", async ({ page, api }) => {
    api.nativeSymbols.release = [readyProbeArtifact(), readyInlineArtifact()];
    const frames = await expandNativeOccurrence(page);

    const inline = frameRows(frames).nth(0);
    await expect(inline).toContainText(`${INLINE_FUNCTION} +0x0`);
    await expect(inline).toContainText("Inlined calls, innermost first:");
    await expect(inline.getByRole("list", { name: "Inlined calls" }).getByRole("listitem")).toHaveText([
      "native_leaf inline-probe.c:2 (inlined)",
      "native_middle inline-probe.c:5 (inlined)",
      `${INLINE_FUNCTION} inline-probe.c:8`,
    ]);
    await expect(inline).toContainText(`0x2d4 in build ${INLINE_BUILD_ID}`);
    await expectNoHorizontalOverflow(page);
    const box = await inline.boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(page.viewportSize()!.width);
    await inline.scrollIntoViewIfNeeded();
    await snapshot(page, "native-inline");

    // Collapsing and expanding re-reads the frames, so a server without source locations shows the fallback.
    api.sourceStatus = "unconfigured";
    const summary = page.getByRole("dialog").locator("details").filter({ hasText: "Linux 6.8" }).locator("summary");
    await summary.click();
    await summary.click();
    await expect(frameRows(frames).nth(0)).toContainText("No source location: source locations aren't enabled on this server.");
    await expect(frameRows(frames).nth(0)).toContainText(`${INLINE_FUNCTION} +0x0`);
    await expect(frames.getByRole("list", { name: "Inlined calls" })).toHaveCount(0);

    api.sourceStatus = "unavailable";
    await summary.click();
    await summary.click();
    await expect(frameRows(frames).nth(1)).toContainText("No source location: the symbol file has no line information for this address.");
    await expect(frames).not.toContainText("inline-probe.c");
  });

  test("a shorter retention expires older symbols and their frames fall back to unresolved", async ({ page, api }) => {
    api.nativeSymbols.release = [readyProbeArtifact(), readyInlineArtifact()];
    const section = await openSymbolsSettings(page);
    await expect(section.getByRole("button", { name: `Delete build ID ${INLINE_BUILD_ID}` })).toBeVisible();
    await expect(purgeCount(page)).toHaveText("2");

    await page.getByLabel("Retention (days)").fill("3");
    await page.getByRole("button", { name: "Save settings" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Settings saved." })).toBeVisible();
    await expect(section.getByRole("button", { name: `Delete build ID ${INLINE_BUILD_ID}` })).toHaveCount(0);
    await expect(section.getByRole("button", { name: `Delete build ID ${PROBE_BUILD_ID}` })).toBeVisible();
    await expect(section).toContainText("kept for 3 days after upload");
    await expect(purgeCount(page)).toHaveText("1");

    const frames = await expandNativeOccurrence(page);
    await expect(frameRows(frames).nth(0)).toContainText("Unresolved");
    await expect(frameRows(frames).nth(0)).not.toContainText("inline-probe.c");
    await expect(frameRows(frames).nth(1)).toContainText("probe.c:3");
  });

  test("shared readers see resolved frames but never request symbol controls", async ({ page, api }) => {
    api.canManage = false;
    api.nativeSymbols.release = [readyProbeArtifact(), readyInlineArtifact()];

    const frames = await expandNativeOccurrence(page);
    await expect(frameRows(frames).nth(1)).toContainText(`${PROBE_FUNCTION} +0x4`);
    await expect(frameRows(frames).nth(0).getByRole("list", { name: "Inlined calls" }).getByRole("listitem")).toHaveCount(3);

    expect(api.requests.some((r) => r.url.pathname.includes("/native-symbols") || r.url.pathname.includes("/monitoring/"))).toBe(false);
  });
});
