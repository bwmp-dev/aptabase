import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NativeSymbolArtifact, NativeSymbolsResponse } from "./monitoring";
import { MonitoringSettingsPanel } from "./MonitoringSettingsPanel";

// Mocked API only: these tests follow docs/native-symbols-api.md and do not exercise the real backend.

type Reply = { status: number; body?: unknown };
type Route = (url: URL, init?: RequestInit) => Reply | undefined | Promise<Reply | undefined>;

const BUILD_ID = "bbe2c0d49e25325c5e51f939ec1eac37cc159800";
const PENDING_ID = "6f1c2a9e-3b7d-4c55-9a1e-0d2f4b8c7e61";
const NEW_ID = "1d3e5f70-8a9b-4c2d-8e0f-123456789abc";

const fetchMock = vi.fn();
let route: Route | undefined;
let symbols: Record<"release" | "debug", NativeSymbolsResponse>;

function artifact(overrides: Partial<NativeSymbolArtifact> & Pick<NativeSymbolArtifact, "id">): NativeSymbolArtifact {
  return {
    buildId: null,
    status: "pending",
    sizeBytes: 1536,
    createdAt: "2026-11-10T08:00:00Z",
    expiresAt: "2026-12-10T08:00:00Z",
    ...overrides,
  };
}

const limits = { maxArtifactBytes: 8388608, maxAppBytes: 33554432, maxAppArtifacts: 8 };

function elfHeader({ elfClass = 2, data = 1, type = 3, machine = 183 } = {}) {
  const bytes = new Uint8Array(64);
  bytes.set([0x7f, 0x45, 0x4c, 0x46, elfClass, data, 1]);
  const view = new DataView(bytes.buffer);
  view.setUint16(16, type, true);
  view.setUint16(18, machine, true);
  return bytes;
}

function symbolFile(bytes: Uint8Array = elfHeader(), name = "libsample.so") {
  return new File([bytes], name, { type: "application/x-sharedlib" });
}

function defaultReply(url: URL, init?: RequestInit): Reply {
  const method = init?.method ?? "GET";
  const path = url.pathname.replace("/api/v0/apps/a1", "");
  const mode = url.searchParams.get("buildMode") as "release" | "debug";
  if (path === "/monitoring/settings") {
    return {
      status: 200,
      body: {
        captureErrors: true,
        capturePerformance: true,
        forwardRawErrors: false,
        monitoringRetentionDays: 14,
        localAlertsEnabled: false,
        alertCooldownMinutes: 60,
      },
    };
  }
  if (path === "/monitoring/data") {
    return { status: 200, body: { counts: { issues: 0, events: 0, transactions: 0, queued: 0, alerts: 0, symbols: 2 }, excludesDeliveredRawLogs: true } };
  }
  if (path === "/sentry-projects") return { status: 200, body: { projects: [] } };
  if (path === "/native-symbols" && method === "GET") return { status: 200, body: symbols[mode] };
  if (path === "/native-symbols" && method === "POST") {
    const created = artifact({ id: NEW_ID, sizeBytes: (init!.body as Blob).size, createdAt: "2026-11-11T09:00:00Z" });
    symbols[mode] = { ...symbols[mode], artifacts: [created, ...symbols[mode].artifacts] };
    return { status: 202, body: { artifact: created } };
  }
  const deleteMatch = path.match(/^\/native-symbols\/(.+)$/);
  if (deleteMatch && method === "DELETE") {
    const id = decodeURIComponent(deleteMatch[1]);
    if (!symbols[mode].artifacts.some((a) => a.id === id)) return { status: 404 };
    symbols[mode] = { ...symbols[mode], artifacts: symbols[mode].artifacts.filter((a) => a.id !== id) };
    return { status: 204 };
  }
  return { status: 404 };
}

function symbolCalls(method: string) {
  return fetchMock.mock.calls
    .filter(([input, init]) => (init?.method ?? "GET") === method && String(input).includes("/native-symbols"))
    .map(([input, init]) => ({ url: new URL(input, "http://localhost"), init: init as RequestInit | undefined }));
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MonitoringSettingsPanel appId="a1" />
    </QueryClientProvider>
  );
}

async function findSection() {
  return screen.findByRole("region", { name: "Native symbols" });
}

async function chooseFile(section: HTMLElement, file: File, mode = "release") {
  const input = await within(section).findByLabelText(`${mode} symbol file`);
  fireEvent.change(input, { target: { files: [file] } });
}

beforeEach(() => {
  symbols = {
    release: {
      enabled: true,
      limits,
      artifacts: [
        artifact({ id: "a0e4c1b2-0000-4000-8000-000000000001", buildId: BUILD_ID, status: "ready", sizeBytes: 3 * 1024 * 1024 }),
        artifact({ id: PENDING_ID }),
      ],
    },
    debug: { enabled: true, limits, artifacts: [] },
  };
  route = undefined;
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (input: string, init?: RequestInit) => {
    const url = new URL(input, "http://localhost");
    const reply = (await route?.(url, init)) ?? defaultReply(url, init);
    return { ok: reply.status < 300, status: reply.status, json: async () => reply.body };
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("Native symbols listing", () => {
  it("lists release artifacts with their status, size and expiry, and states the limits", async () => {
    renderPanel();
    const section = await findSection();
    const list = await within(section).findByRole("list", { name: "release symbol files" });

    const rows = within(list).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain(BUILD_ID);
    expect(rows[0].textContent).toContain("Ready");
    expect(rows[0].textContent).toContain("3 MiB");
    expect(rows[0].querySelector("time[datetime='2026-12-10T08:00:00Z']")).not.toBeNull();
    expect(rows[1].textContent).toContain(PENDING_ID);
    expect(rows[1].textContent).toContain("Pending validation");
    expect(rows[1].textContent).not.toContain("Ready");

    expect(section.textContent).toContain("Each file can be up to 8 MiB. An app can keep 8 files and 32 MiB in total");
    expect(section.textContent).toContain("kept for 14 days");
    expect(section.textContent).toContain("Only function names and address ranges are read");
    expect(section.textContent).toContain("Expanded occurrences that include native frames show the resolved function names");
    expect(section.textContent).toContain("leaves those frames unresolved; the error reports themselves are kept");
    expect(within(section).queryByRole("link")).toBeNull();

    const gets = symbolCalls("GET");
    expect(gets.length).toBeGreaterThan(0);
    expect(gets.every(({ url }) => url.pathname === "/api/v0/apps/a1/native-symbols" && url.searchParams.get("buildMode") === "release")).toBe(true);
  });

  it("loads debug artifacts separately and resets upload messages when the build mode changes", async () => {
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile());
    expect((await within(section).findByRole("status")).textContent).toContain("Upload received");

    fireEvent.click(within(section).getByRole("button", { name: "Debug" }));
    expect(await within(section).findByText("No symbol files uploaded for debug builds.")).toBeTruthy();
    expect(within(section).queryByRole("status")).toBeNull();
    expect(symbolCalls("GET").some(({ url }) => url.searchParams.get("buildMode") === "debug")).toBe(true);
    expect(within(section).getByRole("button", { name: "Upload debug symbols" })).toBeTruthy();
  });

  it("polls while an artifact is pending and shows the validated result", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderPanel();
    const section = await findSection();
    await within(section).findByText("Pending validation");

    symbols.release = {
      ...symbols.release,
      artifacts: symbols.release.artifacts.map((a) => (a.id === PENDING_ID ? { ...a, status: "invalid" as const } : a)),
    };
    await act(() => vi.advanceTimersByTimeAsync(5000));

    expect(await within(section).findByText("Rejected")).toBeTruthy();
    expect(within(section).queryByText("Pending validation")).toBeNull();
    const getsAfterSettled = symbolCalls("GET").length;
    await act(() => vi.advanceTimersByTimeAsync(15000));
    expect(symbolCalls("GET").length).toBe(getsAfterSettled);
  });

  it("explains a forbidden listing without offering uploads", async () => {
    route = (url) => (url.pathname.endsWith("/native-symbols") ? { status: 403 } : undefined);
    renderPanel();
    const section = await findSection();
    expect((await within(section).findByRole("alert")).textContent).toContain("Only the app owner can manage symbol files");
    expect(within(section).queryByRole("button", { name: /Upload/ })).toBeNull();
  });

  it("offers a retry when the listing fails", async () => {
    route = (url) => (url.pathname.endsWith("/native-symbols") ? { status: 500 } : undefined);
    renderPanel();
    const section = await findSection();
    expect(await within(section).findByRole("button", { name: "Refresh" }, { timeout: 6000 })).toBeTruthy();
    expect(within(section).queryByRole("button", { name: /Upload/ })).toBeNull();
  }, 10_000);

  it("never requests artifacts when the viewer can't manage monitoring", async () => {
    route = () => ({ status: 403 });
    renderPanel();
    expect((await screen.findByRole("alert")).textContent).toContain("Only the app owner");
    expect(screen.queryByRole("region", { name: "Native symbols" })).toBeNull();
    expect(symbolCalls("GET")).toHaveLength(0);
  });

  it("refreshes ready artifacts after a shorter retention is saved", async () => {
    const OLD_ID = "a0e4c1b2-0000-4000-8000-000000000002";
    const OLD_BUILD_ID = "aa".repeat(20);
    symbols.release = {
      ...symbols.release,
      artifacts: [
        artifact({ id: "a0e4c1b2-0000-4000-8000-000000000001", buildId: BUILD_ID, status: "ready" }),
        artifact({ id: OLD_ID, buildId: OLD_BUILD_ID, status: "ready", createdAt: "2026-11-01T08:00:00Z" }),
      ],
    };
    // The server applies the new retention immediately: the old artifact expires, the newer one expires sooner.
    route = (url, init) => {
      if (!url.pathname.endsWith("/monitoring/settings") || init?.method !== "PUT") return undefined;
      symbols.release = {
        ...symbols.release,
        artifacts: symbols.release.artifacts
          .filter((a) => a.id !== OLD_ID)
          .map((a) => ({ ...a, expiresAt: "2026-11-11T08:00:00Z" })),
      };
      return { status: 200, body: JSON.parse(init.body as string) };
    };
    renderPanel();
    const section = await findSection();
    expect(await within(section).findByText(OLD_BUILD_ID)).toBeTruthy();
    const readsBeforeSave = symbolCalls("GET").length;

    fireEvent.change(screen.getByLabelText("Retention (days)"), { target: { value: "1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect(await screen.findByText("Settings saved.")).toBeTruthy();
    await waitFor(() => expect(within(section).queryByText(OLD_BUILD_ID)).toBeNull());
    const [row] = within(within(section).getByRole("list", { name: "release symbol files" })).getAllByRole("listitem");
    expect(row.querySelector("time[datetime='2026-11-11T08:00:00Z']")).not.toBeNull();
    expect(symbolCalls("GET").length).toBeGreaterThan(readsBeforeSave);
    expect(section.textContent).toContain("kept for 1 day after upload");
  });
});

describe("Native symbols upload", () => {
  it("posts the raw file bytes as octet-stream without its name and shows it as pending", async () => {
    renderPanel();
    const section = await findSection();
    const file = symbolFile(elfHeader(), "libsecret-name.so");
    await chooseFile(section, file);

    expect((await within(section).findByRole("status")).textContent).toBe(
      "Upload received. It will be validated before it can be used."
    );
    const [post] = symbolCalls("POST");
    expect(post.url.pathname).toBe("/api/v0/apps/a1/native-symbols");
    expect(post.url.searchParams.get("buildMode")).toBe("release");
    expect(post.init?.headers).toEqual({ Accept: "application/json", "Content-Type": "application/octet-stream" });
    expect(post.init?.body).toBe(file);
    expect(JSON.stringify(post.init?.headers) + post.url.href).not.toContain("libsecret-name");

    const rows = within(within(section).getByRole("list", { name: "release symbol files" })).getAllByRole("listitem");
    expect(rows[0].textContent).toContain(NEW_ID);
    expect(rows[0].textContent).toContain("Pending validation");
  });

  it.each([
    ["a minidump", new Uint8Array([0x4d, 0x44, 0x4d, 0x50, ...new Array(60).fill(0)])],
    ["a zip archive", new Uint8Array([0x50, 0x4b, 0x03, 0x04, ...new Array(60).fill(0)])],
    ["an x86-64 ELF", elfHeader({ machine: 62 })],
    ["a 32-bit ELF", elfHeader({ elfClass: 1 })],
    ["a big-endian ELF", elfHeader({ data: 2 })],
    ["an ELF executable", elfHeader({ type: 2 })],
    ["a truncated header", elfHeader().slice(0, 12)],
  ])("rejects %s before uploading", async (_, bytes) => {
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile(bytes));

    expect((await within(section).findByRole("alert")).textContent).toContain(
      "This isn't a 64-bit little-endian ARM64 ELF shared library"
    );
    expect(symbolCalls("POST")).toHaveLength(0);
  });

  it("rejects files over the server's size limit and empty files before uploading", async () => {
    symbols.release = { ...symbols.release, limits: { ...limits, maxArtifactBytes: 32 } };
    renderPanel();
    const section = await findSection();

    await chooseFile(section, symbolFile());
    expect((await within(section).findByRole("alert")).textContent).toBe("This file is larger than the 32 B limit.");

    await chooseFile(section, symbolFile(new Uint8Array(0)));
    await waitFor(() => expect(within(section).getByRole("alert").textContent).toBe("This file is empty."));
    expect(symbolCalls("POST")).toHaveLength(0);
  });

  it.each([
    [400, "The server couldn't read this upload"],
    [409, "reached its symbol storage limit of 8 files or 32 MiB across release and debug builds"],
    [413, "larger than the 8 MiB limit"],
    [415, "didn't accept this upload's content type"],
    [503, "Symbol processing is unavailable right now"],
    [500, "Couldn't upload the symbol file"],
  ])("explains a %i upload response", async (status, message) => {
    route = (url, init) => (init?.method === "POST" ? { status } : undefined);
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile());

    expect((await within(section).findByRole("alert")).textContent).toContain(message);
    expect(within(section).queryByRole("status")).toBeNull();
    expect((within(section).getByRole("button", { name: "Upload release symbols" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("refreshes the processor state after a 503 and disables uploads when it was turned off", async () => {
    route = (url, init) => {
      if (init?.method !== "POST") return undefined;
      symbols.release = { ...symbols.release, enabled: false };
      return { status: 503 };
    };
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile());

    expect(await within(section).findByText(/Symbol processing isn't configured on this server/)).toBeTruthy();
    expect((within(section).getByRole("button", { name: "Upload release symbols" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("disables uploads while processing is unconfigured but keeps existing artifacts deletable", async () => {
    symbols.release = { ...symbols.release, enabled: false };
    renderPanel();
    const section = await findSection();

    expect(await within(section).findByText(/uploads are turned off/)).toBeTruthy();
    expect((within(section).getByRole("button", { name: "Upload release symbols" }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(section).getByLabelText("release symbol file") as HTMLInputElement).disabled).toBe(true);
    expect((within(section).getByRole("button", { name: `Delete build ID ${BUILD_ID}` }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("disables the upload control while an upload is in flight", async () => {
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    route = async (url, init) => {
      if (init?.method === "POST") await held;
      return undefined;
    };
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile());

    const button = await within(section).findByRole("button", { name: "Uploading…" });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    release();
    expect(await within(section).findByRole("button", { name: "Upload release symbols" })).toBeTruthy();
    expect(symbolCalls("POST")).toHaveLength(1);
  });
});

describe("Native symbols delete", () => {
  it("confirms with the build ID before deleting and leaves the list unchanged on cancel", async () => {
    renderPanel();
    const section = await findSection();
    fireEvent.click(await within(section).findByRole("button", { name: `Delete build ID ${BUILD_ID}` }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog.textContent).toContain(`Build ID ${BUILD_ID} will be removed from release builds`);
    expect(dialog.textContent).toContain(
      "Error reports already received are kept; native frames that used this file will show as unresolved"
    );
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(symbolCalls("DELETE")).toHaveLength(0);

    const previewReads = () =>
      fetchMock.mock.calls.filter(([input, init]) => String(input).endsWith("/monitoring/data") && !init?.method).length;
    await waitFor(() => expect(previewReads()).toBeGreaterThan(0));
    const previewReadsBefore = previewReads();
    fireEvent.click(within(section).getByRole("button", { name: `Delete build ID ${BUILD_ID}` }));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Delete" }));

    expect((await within(section).findByRole("status")).textContent).toBe(`Deleted build ID ${BUILD_ID}.`);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const [del] = symbolCalls("DELETE");
    expect(del.url.pathname).toBe("/api/v0/apps/a1/native-symbols/a0e4c1b2-0000-4000-8000-000000000001");
    expect(del.url.searchParams.get("buildMode")).toBe("release");
    expect(within(section).queryByText(BUILD_ID)).toBeNull();
    await waitFor(() => expect(previewReads()).toBeGreaterThan(previewReadsBefore));
  });

  it("replaces an earlier upload message with the delete result", async () => {
    renderPanel();
    const section = await findSection();
    await chooseFile(section, symbolFile());
    expect((await within(section).findByRole("status")).textContent).toContain("Upload received");

    fireEvent.click(within(section).getByRole("button", { name: `Delete build ID ${BUILD_ID}` }));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Delete" }));

    await waitFor(() =>
      expect(within(section).getAllByRole("status").map((s) => s.textContent)).toEqual([`Deleted build ID ${BUILD_ID}.`])
    );
  });

  it("names a pending artifact by its upload ID", async () => {
    renderPanel();
    const section = await findSection();
    fireEvent.click(await within(section).findByRole("button", { name: `Delete upload ${PENDING_ID}` }));

    expect((await screen.findByRole("dialog")).textContent).toContain(`Upload ID ${PENDING_ID} will be removed`);
  });

  it("explains an artifact that is already gone and refreshes the list", async () => {
    renderPanel();
    const section = await findSection();
    fireEvent.click(await within(section).findByRole("button", { name: `Delete build ID ${BUILD_ID}` }));
    symbols.release = { ...symbols.release, artifacts: symbols.release.artifacts.filter((a) => a.buildId !== BUILD_ID) };
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Delete" }));

    expect((await screen.findByRole("alert")).textContent).toContain("This symbol file no longer exists");
    await waitFor(() => expect(within(section).queryByText(BUILD_ID)).toBeNull());
  });
});
