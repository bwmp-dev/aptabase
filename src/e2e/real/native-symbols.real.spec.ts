import { APIRequestContext, Locator, Page, expect } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { APP_ID, PROBE_PATH, login, reportError, runToken, snapshot, test, withSettings } from "./real-host";

// End-to-end checks of owner symbol uploads and native-frame resolution against the loopback test host, using the
// synthetic NDK-built ARM64 probe library. The library is only uploaded as data; nothing runs it on this machine.
test.describe.configure({ mode: "serial" });

// Values reported by LLVM for the probe build (docs/native-symbols-api.md): function start 0x2dc, size 24.
const PROBE_BUILD_ID = "bbe2c0d49e25325c5e51f939ec1eac37cc159800";
const PROBE_FUNCTION = "aptabase_symbol_probe";
const PROBE_ADDRESS = "0x2e0";
const PROBE_OFFSET = 4;
const UNKNOWN_BUILD_ID = "0123456789abcdef0123456789abcdef01234567";

const symbolsPath = `/api/v0/apps/${APP_ID}/native-symbols`;
const previewPath = `/api/v0/apps/${APP_ID}/monitoring/data`;

type Artifact = { id: string; buildId: string | null; status: string };

let report: { errorType: string; issueId: string; eventId: string } | undefined;

async function probeArtifacts(request: APIRequestContext) {
  const response = await request.get(`${symbolsPath}?buildMode=release`);
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { enabled: boolean; artifacts: Artifact[] };
  return { enabled: body.enabled, probes: body.artifacts.filter((a) => a.buildId === PROBE_BUILD_ID) };
}

async function symbolCount(request: APIRequestContext) {
  const response = await request.get(previewPath);
  expect(response.status()).toBe(200);
  return (await response.json()).counts.symbols as number;
}

function framesPath(issueId: string, eventId: string) {
  return `/api/v0/apps/${APP_ID}/issues/${issueId}/events/${eventId}/native-frames?buildMode=release`;
}

async function openSymbolsSettings(page: Page) {
  await page.goto(`/${APP_ID}/settings`);
  await page.getByRole("tab", { name: "Monitoring" }).click();
  return page.getByRole("region", { name: "Native symbols" });
}

async function expandOccurrenceFrames(page: Page, errorType: string): Promise<Locator> {
  await page.goto(`/${APP_ID}/errors?status=all`);
  await page.getByRole("button", { name: new RegExp(errorType) }).click();
  const occurrences = page.getByRole("dialog").getByRole("region", { name: "Recent occurrences" });
  const summary = occurrences.locator("summary").first();
  await summary.focus();
  await page.keyboard.press("Enter");
  return occurrences.getByRole("region", { name: "Native frames" });
}

function requireReport() {
  if (!report) throw new Error("The native report from the earlier serial test is missing.");
  return report;
}

test.describe("Native symbols against the real test host", () => {
  test("owner uploads the probe symbols and sees validation finish", async ({ page, context }) => {
    expect(readFileSync(PROBE_PATH).includes(Buffer.from(PROBE_BUILD_ID, "hex")), "probe contains the expected build ID").toBe(true);
    await login(context, "owner");

    // Start from no probe artifact so the upload is new and must go through validation.
    const existing = await probeArtifacts(context.request);
    expect(existing.enabled, "the test host must configure the isolated symbol processor").toBe(true);
    for (const artifact of existing.probes) {
      expect((await context.request.delete(`${symbolsPath}/${artifact.id}?buildMode=release`)).status()).toBe(204);
    }
    const countBefore = await symbolCount(context.request);

    const section = await openSymbolsSettings(page);
    await section.getByLabel("release symbol file").setInputFiles(PROBE_PATH);
    await expect(section.getByRole("status")).toHaveText("Upload received. It will be validated before it can be used.");

    const row = section.getByRole("listitem").filter({ hasText: PROBE_BUILD_ID });
    await expect(row).toContainText("Ready", { timeout: 30_000 });
    expect((await probeArtifacts(context.request)).probes.map((a) => a.status)).toEqual(["ready"]);

    const count = await symbolCount(context.request);
    expect(count).toBe(countBefore + 1);
    const purge = page
      .getByRole("region", { name: "Clear monitoring data" })
      .getByText("Native symbol files", { exact: true })
      .locator("xpath=following-sibling::dd[1]");
    await expect(purge).toHaveText(count.toLocaleString("en-US"));
    await row.scrollIntoViewIfNeeded();
    await snapshot(page, "native-symbols");
  });

  test("owner sees function-only resolution on the expanded native occurrence", async ({ page, context }) => {
    await login(context, "owner");
    const errorType = `NativeProbe_${runToken().replace(/-/g, "_")}`;
    const eventId = randomUUID();
    await withSettings(context.request, async (original) => {
      if (!original.captureErrors) {
        const enabled = await context.request.put(`/api/v0/apps/${APP_ID}/monitoring/settings`, { data: { ...original, captureErrors: true } });
        expect(enabled.status()).toBe(200);
      }
      await reportError(context.request, {
        errorType,
        errorMessage: `Native probe ${eventId}`,
        eventId,
        nativeFrames: [
          { buildId: PROBE_BUILD_ID, address: PROBE_ADDRESS },
          { buildId: UNKNOWN_BUILD_ID, address: "0x1a40" },
        ],
      });
    });
    const list = await context.request.get(`/api/v0/apps/${APP_ID}/issues?status=all&q=${encodeURIComponent(errorType)}`);
    const issueId = (await list.json()).issues[0].id as string;
    report = { errorType, issueId, eventId };

    const api = await context.request.get(framesPath(issueId, eventId));
    expect(api.status()).toBe(200);
    expect(await api.json()).toEqual({
      capability: "function-only",
      frames: [
        { buildId: PROBE_BUILD_ID, address: PROBE_ADDRESS, status: "resolved", function: PROBE_FUNCTION, offset: PROBE_OFFSET },
        { buildId: UNKNOWN_BUILD_ID, address: "0x1a40", status: "unresolved", function: null, offset: null },
      ],
    });

    const frames = await expandOccurrenceFrames(page, errorType);
    await expect(frames).toContainText("Function names only: no file names, line numbers or inlined frames.");
    const rows = frames.getByRole("listitem");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText(`${PROBE_FUNCTION} +0x${PROBE_OFFSET.toString(16)}`);
    await expect(rows.nth(0)).toContainText(`${PROBE_ADDRESS} in build ${PROBE_BUILD_ID}`);
    await expect(rows.nth(1)).toContainText("Unresolved");
    await frames.scrollIntoViewIfNeeded();
    await snapshot(page, "native-frames");
  });

  test("shared reader sees resolved frames but cannot reach symbol artifacts", async ({ page, context }) => {
    const { errorType, issueId, eventId } = requireReport();
    await login(context, "reader");
    const symbolRequests: string[] = [];
    page.on("request", (request) => {
      if (new URL(request.url()).pathname.includes("/native-symbols")) symbolRequests.push(request.url());
    });

    const frames = await expandOccurrenceFrames(page, errorType);
    await expect(frames.getByRole("listitem").nth(0)).toContainText(`${PROBE_FUNCTION} +0x4`);
    await page.goto(`/${APP_ID}/settings`);
    await expect(page).not.toHaveURL(/\/settings$/);
    expect(symbolRequests).toEqual([]);

    expect((await context.request.get(framesPath(issueId, eventId))).status()).toBe(200);
    expect((await context.request.get(`${symbolsPath}?buildMode=release`)).status()).toBe(403);
    const upload = await context.request.post(`${symbolsPath}?buildMode=release`, {
      headers: { "Content-Type": "application/octet-stream" },
      data: readFileSync(PROBE_PATH),
    });
    expect(upload.status()).toBe(403);
    expect((await context.request.delete(`${symbolsPath}/${randomUUID()}?buildMode=release`)).status()).toBe(403);
    expect((await context.request.get(previewPath)).status()).toBe(403);
  });

  test("a user without app access cannot read native frames", async ({ context }) => {
    const { issueId, eventId } = requireReport();
    await login(context, "stranger");
    expect((await context.request.get(framesPath(issueId, eventId))).status()).toBe(403);
    expect((await context.request.get(`${symbolsPath}?buildMode=release`)).status()).toBe(403);
  });

  test("owner deletes the probe symbols and the occurrence becomes unresolved", async ({ page, context }) => {
    const { errorType, issueId, eventId } = requireReport();
    await login(context, "owner");
    const countBefore = await symbolCount(context.request);

    const section = await openSymbolsSettings(page);
    await section.getByRole("button", { name: `Delete build ID ${PROBE_BUILD_ID}` }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(`Build ID ${PROBE_BUILD_ID} will be removed from release builds`);
    await dialog.getByRole("button", { name: "Delete" }).click();
    await expect(section.getByRole("status")).toHaveText(`Deleted build ID ${PROBE_BUILD_ID}.`);
    expect((await probeArtifacts(context.request)).probes).toEqual([]);
    expect(await symbolCount(context.request)).toBe(countBefore - 1);

    const api = await (await context.request.get(framesPath(issueId, eventId))).json();
    expect(api.frames[0]).toEqual({ buildId: PROBE_BUILD_ID, address: PROBE_ADDRESS, status: "unresolved", function: null, offset: null });

    const frames = await expandOccurrenceFrames(page, errorType);
    const first = frames.getByRole("listitem").nth(0);
    await expect(first).toContainText("Unresolved");
    await expect(first).toContainText(`${PROBE_ADDRESS} in build ${PROBE_BUILD_ID}`);
    await expect(frames).not.toContainText(PROBE_FUNCTION);
  });
});
