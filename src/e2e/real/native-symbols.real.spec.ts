import { APIRequestContext, Locator, Page, expect } from "@playwright/test";
import { createHash, randomUUID } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { APP_ID, PROBE_PATH, login, reportError, runToken, snapshot, test, withSettings } from "./real-host";

// End-to-end checks of owner symbol uploads and native-frame resolution against the loopback test host, using the
// synthetic NDK-built ARM64 probe libraries. They are only uploaded as data; nothing runs them on this machine.
test.describe.configure({ mode: "serial" });

const INLINE_PROBE_PATH = fileURLToPath(new URL("../../../tests/SdkProbes/Native/bin/inline-probe.so", import.meta.url));
const FIXTURES_PATH = fileURLToPath(new URL("../../../tests/SdkProbes/Native/bin/fixtures.json", import.meta.url));

// Functions, addresses and line chains are fixed by the probe sources and checked independently with GNU addr2line.
// Build IDs change whenever tests/SdkProbes/Native/run.ps1 rebuilds the libraries, so they come from its manifest.
const PROBE_EXPECTED = {
  path: PROBE_PATH,
  function: "aptabase_symbol_probe",
  address: "0x2e0",
  offset: 4,
  locations: [{ function: "aptabase_symbol_probe", file: "probe.c", line: 2 }],
};
// aptabase_inline_probe starts at 0x2d4 (size 16); its first instruction carries two inlined callees.
const INLINE_EXPECTED = {
  path: INLINE_PROBE_PATH,
  function: "aptabase_inline_probe",
  address: "0x2d4",
  offset: 0,
  locations: [
    { function: "native_leaf", file: "inline-probe.c", line: 2 },
    { function: "native_middle", file: "inline-probe.c", line: 5 },
    { function: "aptabase_inline_probe", file: "inline-probe.c", line: 8 },
  ],
};
type Probe = typeof PROBE_EXPECTED & { buildId: string };

// Reads the manifest written by run.ps1 and checks it against the library files actually being uploaded.
function loadProbes() {
  if (!existsSync(FIXTURES_PATH)) {
    throw new Error(`Missing ${FIXTURES_PATH}. Run tests/SdkProbes/Native/run.ps1 with the installed NDK first.`);
  }
  const manifest = JSON.parse(readFileSync(FIXTURES_PATH, "utf8")) as Record<string, { buildId?: unknown; sha256?: unknown }>;
  const verified = (key: "probe" | "inline", expected: typeof PROBE_EXPECTED): Probe => {
    const { buildId, sha256 } = manifest[key] ?? {};
    if (typeof buildId !== "string" || !/^[0-9a-f]{40}$/.test(buildId)) throw new Error(`${key}.buildId must be 40 lowercase hex characters`);
    if (typeof sha256 !== "string" || !/^[0-9a-f]{64}$/.test(sha256)) throw new Error(`${key}.sha256 must be 64 lowercase hex characters`);
    const bytes = readFileSync(expected.path);
    expect(createHash("sha256").update(bytes).digest("hex"), `${key} library matches the manifest hash`).toBe(sha256);
    expect(bytes.includes(Buffer.from(buildId, "hex")), `${key} library contains its manifest build ID`).toBe(true);
    return { ...expected, buildId };
  };
  return { probe: verified("probe", PROBE_EXPECTED), inline: verified("inline", INLINE_EXPECTED) };
}

let PROBE: Probe;
let INLINE: Probe;
let PROBES: Probe[];

test.beforeAll(() => {
  ({ probe: PROBE, inline: INLINE } = loadProbes());
  expect(PROBE.buildId).not.toBe(INLINE.buildId);
  PROBES = [INLINE, PROBE];
});

const UNKNOWN_BUILD_ID = "0123456789abcdef0123456789abcdef01234567";

const symbolsPath = `/api/v0/apps/${APP_ID}/native-symbols`;
const previewPath = `/api/v0/apps/${APP_ID}/monitoring/data`;

type Artifact = { id: string; buildId: string | null; status: string };

let report: { errorType: string; issueId: string; eventId: string } | undefined;

async function probeArtifacts(request: APIRequestContext) {
  const response = await request.get(`${symbolsPath}?buildMode=release`);
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { enabled: boolean; artifacts: Artifact[] };
  const buildIds = PROBES.map((p) => p.buildId);
  return { enabled: body.enabled, probes: body.artifacts.filter((a) => a.buildId !== null && buildIds.includes(a.buildId)) };
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

// Frame rows only: resolved frames can contain a nested list of inlined calls.
function frameRows(frames: Locator) {
  return frames.getByRole("list", { name: "Stack frames" }).locator(":scope > li");
}

function inlineChain(row: Locator) {
  return row.getByRole("list", { name: "Inlined calls" }).getByRole("listitem");
}

function requireReport() {
  if (!report) throw new Error("The native report from the earlier serial test is missing.");
  return report;
}

test.describe("Native symbols against the real test host", () => {
  test("owner uploads both probe libraries and sees validation finish", async ({ page, context }) => {
    await login(context, "owner");

    // Start from no probe artifacts so the uploads are new and must go through validation and source enrichment.
    const existing = await probeArtifacts(context.request);
    expect(existing.enabled, "the test host must configure the isolated symbol processor").toBe(true);
    for (const artifact of existing.probes) {
      expect((await context.request.delete(`${symbolsPath}/${artifact.id}?buildMode=release`)).status()).toBe(204);
    }
    const countBefore = await symbolCount(context.request);

    const section = await openSymbolsSettings(page);
    for (const probe of PROBES) {
      await section.getByLabel("release symbol file", { exact: true }).setInputFiles(probe.path);
      await expect(section.getByRole("status")).toHaveText("Upload received. It will be validated before it can be used.");
      await expect(section.getByRole("listitem").filter({ hasText: probe.buildId })).toContainText("Ready", { timeout: 30_000 });
    }
    expect((await probeArtifacts(context.request)).probes.map((a) => a.status)).toEqual(["ready", "ready"]);

    const count = await symbolCount(context.request);
    expect(count).toBe(countBefore + 2);
    const purge = page
      .getByRole("region", { name: "Clear monitoring data" })
      .getByText("Native symbol files", { exact: true })
      .locator("xpath=following-sibling::dd[1]");
    await expect(purge).toHaveText(count.toLocaleString("en-US"));
    await section.getByRole("listitem").filter({ hasText: PROBE.buildId }).scrollIntoViewIfNeeded();
    await snapshot(page, "native-symbols");
  });

  test("owner sees functions, source locations and the inline chain on the expanded occurrence", async ({ page, context }) => {
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
          { buildId: INLINE.buildId, address: INLINE.address },
          { buildId: PROBE.buildId, address: PROBE.address },
          { buildId: UNKNOWN_BUILD_ID, address: "0x1a40" },
        ],
      });
    });
    const list = await context.request.get(`/api/v0/apps/${APP_ID}/issues?status=all&q=${encodeURIComponent(errorType)}`);
    const issueId = (await list.json()).issues[0].id as string;
    report = { errorType, issueId, eventId };

    const api = await context.request.get(framesPath(issueId, eventId));
    expect(api.status()).toBe(200);
    const body = await api.json();
    expect(body.capability).toBe("function-and-source");
    for (const [index, probe] of PROBES.entries()) {
      expect(body.frames[index]).toEqual({
        buildId: probe.buildId,
        address: probe.address,
        status: "resolved",
        function: probe.function,
        offset: probe.offset,
        sourceStatus: "available",
        locations: probe.locations,
      });
    }
    expect(body.frames[2]).toMatchObject({ buildId: UNKNOWN_BUILD_ID, address: "0x1a40", status: "unresolved", function: null, offset: null, locations: [] });
    expect(body.frames).toHaveLength(3);

    const frames = await expandOccurrenceFrames(page, errorType);
    await expect(frames).toContainText("plus file names, line numbers and inlined calls when the symbol file has line information");
    const rows = frameRows(frames);
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0)).toContainText(`${INLINE.function} +0x0`);
    await expect(inlineChain(rows.nth(0))).toHaveText([
      "native_leaf inline-probe.c:2 (inlined)",
      "native_middle inline-probe.c:5 (inlined)",
      "aptabase_inline_probe inline-probe.c:8",
    ]);
    await expect(rows.nth(0)).toContainText(`${INLINE.address} in build ${INLINE.buildId}`);
    await expect(rows.nth(1)).toContainText(`${PROBE.function} +0x${PROBE.offset.toString(16)}`);
    await expect(rows.nth(1)).toContainText(`probe.c:${PROBE.locations[0].line}`);
    await expect(inlineChain(rows.nth(1))).toHaveCount(0);
    await expect(rows.nth(2)).toContainText("Unresolved");
    await rows.nth(0).scrollIntoViewIfNeeded();
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
    await expect(inlineChain(frameRows(frames).nth(0))).toHaveCount(3);
    await expect(frameRows(frames).nth(1)).toContainText(`${PROBE.function} +0x4`);
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

  test("owner deletes the probe symbols and the occurrence falls back to unresolved", async ({ page, context }) => {
    const { errorType, issueId, eventId } = requireReport();
    await login(context, "owner");
    const countBefore = await symbolCount(context.request);

    const section = await openSymbolsSettings(page);
    for (const probe of PROBES) {
      await section.getByRole("button", { name: `Delete build ID ${probe.buildId}` }).click();
      const dialog = page.getByRole("dialog");
      await expect(dialog).toContainText(`Build ID ${probe.buildId} will be removed from release builds`);
      await dialog.getByRole("button", { name: "Delete" }).click();
      await expect(section.getByRole("status")).toHaveText(`Deleted build ID ${probe.buildId}.`);
    }
    expect((await probeArtifacts(context.request)).probes).toEqual([]);
    expect(await symbolCount(context.request)).toBe(countBefore - 2);

    const api = await (await context.request.get(framesPath(issueId, eventId))).json();
    for (const [index, probe] of PROBES.entries()) {
      expect(api.frames[index]).toMatchObject({
        buildId: probe.buildId,
        address: probe.address,
        status: "unresolved",
        function: null,
        offset: null,
        locations: [],
      });
    }

    const frames = await expandOccurrenceFrames(page, errorType);
    await expect(frameRows(frames).nth(0)).toContainText("Unresolved");
    await expect(frameRows(frames).nth(0)).toContainText(`${INLINE.address} in build ${INLINE.buildId}`);
    await expect(frameRows(frames).nth(1)).toContainText(`${PROBE.address} in build ${PROBE.buildId}`);
    await expect(frames).not.toContainText(PROBE.function);
    await expect(frames).not.toContainText("inline-probe.c");
  });
});
