import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MonitoringAlert } from "./monitoring";

let buildMode = "release";

vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode }),
}));

import { AlertsPanel } from "./AlertsPanel";

type Reply = { status: number; body?: unknown };

const fetchMock = vi.fn();
let alerts: MonitoringAlert[];
let override: ((url: URL, init?: RequestInit) => Reply | undefined) | undefined;
let alertsEnabled = true;

const issueId = "c".repeat(64);

function alert(overrides: Partial<MonitoringAlert> = {}): MonitoringAlert {
  return {
    eventId: "9f1c2d3e-4b5a-4c6d-8e7f-000000000001",
    issueId,
    kind: "regression",
    createdAt: "2026-11-11T12:00:00Z",
    readAt: null,
    ...overrides,
  };
}

function defaultReply(url: URL, init?: RequestInit): Reply {
  const path = url.pathname.replace("/api/v0/apps/a1", "");
  if (path === "/monitoring/settings") {
    return {
      status: 200,
      body: {
        captureErrors: true,
        capturePerformance: true,
        forwardRawErrors: true,
        monitoringRetentionDays: 30,
        localAlertsEnabled: alertsEnabled,
        alertCooldownMinutes: 60,
      },
    };
  }
  if (path === "/monitoring/alerts") {
    return { status: 200, body: { alerts, pagination: { offset: 0, limit: 50, total: alerts.length } } };
  }
  if (path.endsWith("/read") && init?.method === "PATCH") {
    const eventId = path.split("/")[3];
    alerts = alerts.map((a) => (a.eventId === eventId ? { ...a, readAt: "2026-11-11T13:00:00Z" } : a));
    return { status: 204 };
  }
  if (path === `/issues/${issueId}`) {
    return {
      status: 200,
      body: {
        issue: {
          id: issueId,
          errorType: "SyncError",
          errorMessage: "Sync failed",
          stackTrace: "",
          status: "unresolved",
          occurrenceCount: 4,
          firstSeen: "2026-11-01T00:00:00Z",
          lastSeen: "2026-11-11T12:00:00Z",
          resolvedAt: null,
        },
        canManage: true,
      },
    };
  }
  if (path === `/issues/${issueId}/events`) {
    return { status: 200, body: { events: [], pagination: { offset: 0, limit: 10, total: 0 } } };
  }
  return { status: 404 };
}

function requests(suffix: string, method = "GET") {
  return fetchMock.mock.calls
    .filter(([input, init]) => (init?.method ?? "GET") === method && new URL(input, "http://localhost").pathname.endsWith(suffix))
    .map(([input]) => new URL(input, "http://localhost"));
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = () => (
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <AlertsPanel appId="a1" />
      </QueryClientProvider>
    </MemoryRouter>
  );
  const view = render(ui());
  return { rerender: () => view.rerender(ui()) };
}

beforeEach(() => {
  buildMode = "release";
  alertsEnabled = true;
  override = undefined;
  alerts = [alert(), alert({ eventId: "9f1c2d3e-4b5a-4c6d-8e7f-000000000002", kind: "new_issue", readAt: "2026-11-10T00:00:00Z" })];
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (input: string, init?: RequestInit) => {
    const url = new URL(input, "http://localhost");
    const reply = override?.(url, init) ?? defaultReply(url, init);
    return { ok: reply.status < 300, status: reply.status, json: async () => reply.body };
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("AlertsPanel", () => {
  it("lists build-scoped alerts with kind and unread state", async () => {
    renderPanel();

    const items = await screen.findAllByRole("listitem");
    expect(items[0].textContent).toContain("Regression");
    expect(items[0].textContent).toContain("Unread");
    expect(items[1].textContent).toContain("New issue");
    expect(items[1].textContent).not.toContain("Unread");
    expect(within(items[1]).queryByRole("button", { name: "Mark read" })).toBeNull();
    expect(Object.fromEntries(requests("/monitoring/alerts")[0].searchParams)).toEqual({
      buildMode: "release",
      offset: "0",
      limit: "50",
    });
    expect(screen.getByText(/Nothing is emailed or sent outside this server/)).toBeTruthy();
  });

  it("marks an alert read and keeps keyboard focus in the item", async () => {
    renderPanel();
    const [first] = await screen.findAllByRole("listitem");
    const markRead = within(first).getByRole("button", { name: "Mark read" });
    markRead.focus();
    fireEvent.click(markRead);

    await waitFor(() => expect(within(first).queryByText("Unread")).toBeNull());
    expect(requests("/read", "PATCH")[0].pathname).toBe(
      "/api/v0/apps/a1/monitoring/alerts/9f1c2d3e-4b5a-4c6d-8e7f-000000000001/read"
    );
    expect(requests("/read", "PATCH")[0].searchParams.get("buildMode")).toBe("release");
    expect(document.activeElement).toBe(within(first).getByRole("button", { name: /Open issue/ }));
  });

  it("opens the issue detail for an alert", async () => {
    renderPanel();
    const [first] = await screen.findAllByRole("listitem");
    fireEvent.click(within(first).getByRole("button", { name: /Open issue/ }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByRole("heading", { name: "SyncError" })).toBeTruthy();
    expect(requests(`/issues/${issueId}`)[0].searchParams.get("buildMode")).toBe("release");
  });

  it("explains a missing alert after mark-read fails", async () => {
    override = (url, init) => (init?.method === "PATCH" ? { status: 404 } : undefined);
    renderPanel();
    const [first] = await screen.findAllByRole("listitem");
    fireEvent.click(within(first).getByRole("button", { name: "Mark read" }));

    expect((await screen.findByRole("alert")).textContent).toContain("That alert no longer exists");
  });

  it("shows an empty inbox and that alerts are off", async () => {
    alerts = [];
    alertsEnabled = false;
    renderPanel();

    expect(await screen.findByText("No alerts")).toBeTruthy();
    expect(await screen.findByText(/Local alerts are turned off/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Change this in Settings" }).getAttribute("href")).toBe("/a1/settings");
  });

  it("tells non-owners alerts are owner-only", async () => {
    override = () => ({ status: 403 });
    renderPanel();
    expect((await screen.findByRole("alert")).textContent).toContain("Only the app owner can see alerts.");
  });

  it("reloads alerts for the new build mode", async () => {
    const view = renderPanel();
    await screen.findAllByRole("listitem");

    buildMode = "debug";
    view.rerender();

    await waitFor(() => expect(requests("/monitoring/alerts").at(-1)?.searchParams.get("buildMode")).toBe("debug"));
    expect(requests("/monitoring/alerts").at(-1)?.searchParams.get("offset")).toBe("0");
  });
});
