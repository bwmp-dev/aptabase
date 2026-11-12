import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MonitoringSettings } from "./monitoring";
import { MonitoringSettingsPanel } from "./MonitoringSettingsPanel";

type Reply = { status: number; body?: unknown };
type Route = (url: URL, init?: RequestInit) => Reply | undefined | Promise<Reply | undefined>;

const fetchMock = vi.fn();
let route: Route | undefined;
let settings: MonitoringSettings;
let projects: { id: string; buildMode: string; dsn: string }[];

const defaults: MonitoringSettings = {
  captureErrors: true,
  capturePerformance: true,
  forwardRawErrors: true,
  monitoringRetentionDays: 30,
  localAlertsEnabled: false,
  alertCooldownMinutes: 60,
};

function defaultReply(url: URL, init?: RequestInit): Reply {
  const method = init?.method ?? "GET";
  const path = url.pathname.replace("/api/v0/apps/a1", "");
  if (path === "/monitoring/settings" && method === "GET") return { status: 200, body: settings };
  if (path === "/monitoring/settings" && method === "PUT") {
    settings = JSON.parse(init!.body as string);
    return { status: 200, body: settings };
  }
  if (path === "/monitoring/data" && method === "GET") {
    return { status: 200, body: { counts: { issues: 4, events: 120, transactions: 900, queued: 2, alerts: 3 }, excludesDeliveredRawLogs: true } };
  }
  if (path === "/monitoring/data" && method === "DELETE") return { status: 204 };
  if (path === "/sentry-projects" && method === "GET") return { status: 200, body: { projects } };
  if (path === "/sentry-projects" && method === "POST") {
    projects = [
      { id: "11", buildMode: "release", dsn: "https://A-SH-1@monitor.example.test/11" },
      { id: "12", buildMode: "debug", dsn: "https://A-SH-1@monitor.example.test/12" },
    ];
    return { status: 200, body: { projects } };
  }
  return { status: 404 };
}

function calls(method: string, suffix: string) {
  return fetchMock.mock.calls
    .filter(([input, init]) => (init?.method ?? "GET") === method && new URL(input, "http://localhost").pathname.endsWith(suffix))
    .map(([, init]) => (init?.body ? JSON.parse(init.body as string) : null));
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MonitoringSettingsPanel appId="a1" />
    </QueryClientProvider>
  );
}

beforeEach(() => {
  settings = { ...defaults };
  projects = [];
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
  vi.unstubAllGlobals();
});

describe("MonitoringSettingsPanel settings", () => {
  it("saves the complete settings object", async () => {
    renderPanel();

    fireEvent.click(await screen.findByLabelText("Forward raw error logs"));
    fireEvent.click(screen.getByLabelText("Create local alerts"));
    fireEvent.change(screen.getByLabelText("Retention (days)"), { target: { value: "7" } });
    fireEvent.change(screen.getByLabelText("Regression cooldown (minutes)"), { target: { value: "15" } });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect((await screen.findByRole("status")).textContent).toBe("Settings saved.");
    expect(calls("PUT", "/monitoring/settings")).toEqual([
      {
        captureErrors: true,
        capturePerformance: true,
        forwardRawErrors: false,
        monitoringRetentionDays: 7,
        localAlertsEnabled: true,
        alertCooldownMinutes: 15,
      },
    ]);
    expect(screen.getByText(/raw logs already delivered are kept for 180 days/)).toBeTruthy();
  });

  it.each([
    ["0", "Retention (days)"],
    ["31", "Retention (days)"],
    ["2.5", "Retention (days)"],
    ["", "Regression cooldown (minutes)"],
    ["1441", "Regression cooldown (minutes)"],
  ])("rejects %j for %s before sending", async (value, label) => {
    renderPanel();
    const input = await screen.findByLabelText(label);
    fireEvent.change(input, { target: { value } });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect(input.getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByText(/Enter a whole number from 1 to/)).toBeTruthy();
    expect(calls("PUT", "/monitoring/settings")).toHaveLength(0);
  });

  it("explains a rejected save and keeps the edits", async () => {
    route = (url, init) => (init?.method === "PUT" ? { status: 400 } : undefined);
    renderPanel();
    fireEvent.change(await screen.findByLabelText("Retention (days)"), { target: { value: "5" } });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect((await screen.findByRole("alert")).textContent).toContain("These settings were rejected");
    expect((screen.getByLabelText("Retention (days)") as HTMLInputElement).value).toBe("5");
  });

  it("tells non-owners the settings are owner-only", async () => {
    route = () => ({ status: 403 });
    renderPanel();
    expect((await screen.findByRole("alert")).textContent).toContain("Only the app owner");
    expect(screen.queryByRole("button", { name: "Save settings" })).toBeNull();
  });
});

describe("MonitoringSettingsPanel Sentry DSNs", () => {
  it("creates release and debug DSNs on request", async () => {
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Create Sentry DSNs" }));

    expect(((await screen.findByLabelText("Release builds")) as HTMLInputElement).value).toBe(
      "https://A-SH-1@monitor.example.test/11"
    );
    expect((screen.getByLabelText("Debug builds") as HTMLInputElement).readOnly).toBe(true);
    expect(calls("POST", "/sentry-projects")).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Copy Release builds DSN" })).toBeTruthy();
  });
});

describe("MonitoringSettingsPanel clear data", () => {
  it("stays unavailable while any capture is on", async () => {
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });

    expect(await within(section).findByText("900")).toBeTruthy();
    expect((within(section).getByRole("textbox") as HTMLInputElement).disabled).toBe(true);
    expect((within(section).getByRole("button", { name: "Clear monitoring data" }) as HTMLButtonElement).disabled).toBe(true);
    expect(section.textContent).toContain("first turn off error capture, performance capture and raw log forwarding");
    expect(section.textContent).toContain("already delivered are not deleted and are kept for 180 days");
  });

  it("requires the exact app ID and clears once capture is off", async () => {
    settings = { ...defaults, captureErrors: false, capturePerformance: false, forwardRawErrors: false };
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });
    const input = within(section).getByLabelText(/Type the app ID/);
    const button = within(section).getByRole("button", { name: "Clear monitoring data" }) as HTMLButtonElement;

    fireEvent.change(input, { target: { value: "A1" } });
    expect(button.disabled).toBe(true);
    fireEvent.change(input, { target: { value: "a1" } });
    expect(button.disabled).toBe(false);
    fireEvent.click(button);

    expect((await within(section).findByRole("status")).textContent).toBe("Monitoring data cleared.");
    expect(calls("DELETE", "/monitoring/data")).toEqual([{ confirmAppId: "a1" }]);
    await waitFor(() => expect(calls("GET", "/monitoring/data").length).toBeGreaterThan(1));
  });

  function deferred() {
    let resolve: () => void = () => {};
    const promise = new Promise<void>((r) => (resolve = r));
    return { promise, resolve };
  }

  function isPreview(url: URL, init?: RequestInit) {
    return url.pathname.endsWith("/monitoring/data") && (init?.method ?? "GET") === "GET";
  }

  async function confirmAndTrySubmit(section: HTMLElement) {
    fireEvent.change(within(section).getByLabelText(/Type the app ID/), { target: { value: "a1" } });
    const button = within(section).getByRole("button", { name: "Clear monitoring data" }) as HTMLButtonElement;
    fireEvent.submit(button.closest("form")!);
    await new Promise((r) => setTimeout(r, 50));
    return button;
  }

  it("blocks clearing, including form submit, until the preview has loaded", async () => {
    settings = { ...defaults, captureErrors: false, capturePerformance: false, forwardRawErrors: false };
    const previewHeld = deferred();
    route = async (url, init) => {
      if (isPreview(url, init)) await previewHeld.promise;
      return undefined;
    };
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });

    const button = await confirmAndTrySubmit(section);
    expect(button.disabled).toBe(true);
    expect(calls("DELETE", "/monitoring/data")).toHaveLength(0);

    previewHeld.resolve();
    expect(await within(section).findByText("900")).toBeTruthy();
    await waitFor(() => expect(button.disabled).toBe(false));
  });

  it("blocks clearing while the preview has failed, and allows it after a successful retry", async () => {
    settings = { ...defaults, captureErrors: false, capturePerformance: false, forwardRawErrors: false };
    let failing = true;
    route = (url, init) => (failing && isPreview(url, init) ? { status: 500 } : undefined);
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });
    const retry = await within(section).findByRole("button", { name: "Refresh" }, { timeout: 6000 });

    const button = await confirmAndTrySubmit(section);
    expect(button.disabled).toBe(true);
    expect(calls("DELETE", "/monitoring/data")).toHaveLength(0);

    failing = false;
    fireEvent.click(retry);
    expect(await within(section).findByText("900")).toBeTruthy();
    await waitFor(() => expect(button.disabled).toBe(false));
    fireEvent.click(button);
    expect((await within(section).findByRole("status")).textContent).toBe("Monitoring data cleared.");
    expect(calls("DELETE", "/monitoring/data")).toEqual([{ confirmAppId: "a1" }]);
  }, 10_000);

  it("blocks clearing while a settings save refreshes the preview", async () => {
    settings = { ...defaults, captureErrors: false, capturePerformance: false, forwardRawErrors: false };
    let refreshHeld: ReturnType<typeof deferred> | null = null;
    route = async (url, init) => {
      if (refreshHeld && isPreview(url, init)) await refreshHeld.promise;
      return undefined;
    };
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });
    expect(await within(section).findByText("900")).toBeTruthy();
    fireEvent.change(within(section).getByLabelText(/Type the app ID/), { target: { value: "a1" } });
    const button = within(section).getByRole("button", { name: "Clear monitoring data" }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);

    refreshHeld = deferred();
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
    expect(await within(section).findByText("Updating counts…")).toBeTruthy();
    expect(button.disabled).toBe(true);
    fireEvent.submit(button.closest("form")!);
    await new Promise((r) => setTimeout(r, 50));
    expect(calls("DELETE", "/monitoring/data")).toHaveLength(0);

    refreshHeld.resolve();
    await waitFor(() => expect(button.disabled).toBe(false));
    expect(within(section).queryByText("Updating counts…")).toBeNull();
  });

  it("explains a conflict when capture was turned back on elsewhere", async () => {
    settings = { ...defaults, captureErrors: false, capturePerformance: false, forwardRawErrors: false };
    route = (url, init) => (init?.method === "DELETE" ? { status: 409 } : undefined);
    renderPanel();
    const section = await screen.findByRole("region", { name: "Clear monitoring data" });
    fireEvent.change(within(section).getByLabelText(/Type the app ID/), { target: { value: "a1" } });
    fireEvent.click(within(section).getByRole("button", { name: "Clear monitoring data" }));

    expect((await within(section).findByRole("alert")).textContent).toContain("Turn off error capture");
  });
});
