import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionCounts, SessionGroup, SessionHealthResponse } from "./health";

let buildMode = "release";

vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode }),
}));

import { formatCrashFreeRate } from "./health";
import { SessionHealthPanel } from "./SessionHealthPage";

type Reply = { status: number; body?: unknown };
type Handler = (url: URL) => Reply | undefined;

const fetchMock = vi.fn();
let override: Handler | undefined;

// The contract example: two completed sessions, one crashed, plus one still active.
function counts(overrides: Partial<SessionCounts> = {}): SessionCounts {
  return {
    observed: 3,
    withStart: 3,
    eligible: 2,
    crashed: 1,
    eligibleCrashed: 1,
    unhandled: 0,
    abnormal: 0,
    active: 1,
    errored: 1,
    incomplete: 1,
    crashFreeRate: 50,
    ...overrides,
  };
}

function group(overrides: Partial<SessionGroup> = {}): SessionGroup {
  return { release: "app@1", environment: "synthetic", ...counts(), ...overrides };
}

function health(overrides: Partial<SessionHealthResponse> = {}): SessionHealthResponse {
  return {
    sessions: counts(),
    groups: [group()],
    groupsTruncated: false,
    clientReports: {
      reports: 2,
      discardedEvents: 17,
      groups: [
        { reason: "queue_overflow", category: "error", quantity: 12 },
        { reason: "before_send", category: "error", quantity: 5 },
      ],
    },
    retentionDays: 30,
    ...overrides,
  };
}

function defaultReply(url: URL): Reply {
  if (url.pathname.endsWith("/releases")) {
    return {
      status: 200,
      body: {
        releases: [
          { release: "app@1", environment: "synthetic", errorCount: 1, transactionCount: 0, firstSeen: "", lastSeen: "" },
          { release: "app@2", environment: "production", errorCount: 0, transactionCount: 0, firstSeen: "", lastSeen: "" },
        ],
      },
    };
  }
  if (url.pathname.endsWith("/monitoring/health")) return { status: 200, body: health() };
  return { status: 404 };
}

function requestedPaths() {
  return fetchMock.mock.calls.map(([input]) => new URL(input as string, "http://localhost").pathname);
}

function requests(suffix: string) {
  return fetchMock.mock.calls
    .map(([input]) => new URL(input as string, "http://localhost"))
    .filter((url) => url.pathname.endsWith(suffix))
    .map((url) => Object.fromEntries(url.searchParams));
}

function rangeHours(params: Record<string, string>) {
  return (Date.parse(params.endDate) - Date.parse(params.startDate)) / 3_600_000;
}

function renderPanel(appId = "a1", initialFilter?: { release: string; environment: string }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = (id: string) => (
    <QueryClientProvider client={client}>
      <SessionHealthPanel appId={id} initialFilter={initialFilter} />
    </QueryClientProvider>
  );
  const view = render(ui(appId));
  return { rerender: (id = appId) => view.rerender(ui(id)) };
}

function coverageValue(label: string) {
  return screen.getByText(label, { selector: "dt" }).nextElementSibling?.textContent;
}

beforeEach(() => {
  buildMode = "release";
  override = undefined;
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (input: string) => {
    const url = new URL(input, "http://localhost");
    const reply = override?.(url) ?? defaultReply(url);
    return { ok: reply.status < 300, status: reply.status, json: async () => reply.body };
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("SessionHealthPanel", () => {
  it("shows 50% for two completed sessions with one crash and leaves the active session out", async () => {
    renderPanel();

    const rate = await screen.findByTestId("crash-free-rate");
    expect(rate.textContent).toContain("50%");
    expect(rate.textContent).toContain("1 of 2 completed sessions ended without a crash.");
    expect(screen.getByRole("heading", { name: "Crash-free completed sessions" })).toBeTruthy();
    expect(screen.getByText(/not crash-free usage overall, and there is no per-user rate/)).toBeTruthy();
    expect(screen.getByText(/kept for up to 30 days/)).toBeTruthy();
    expect(coverageValue("Counted in the rate")).toBe("2");
    expect(coverageValue("Not counted")).toBe("1");
    expect(coverageValue("Still active")).toBe("1");

    const [query] = requests("/monitoring/health");
    expect(query).toMatchObject({ buildMode: "release" });
    expect(rangeHours(query)).toBe(168);
    expect(query).not.toHaveProperty("release");
    expect(query).not.toHaveProperty("environment");
  });

  it("shows every coverage count from the response", async () => {
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? {
            status: 200,
            body: health({
              sessions: counts({
                observed: 40,
                withStart: 35,
                eligible: 25,
                crashed: 6,
                eligibleCrashed: 4,
                unhandled: 2,
                abnormal: 3,
                active: 9,
                errored: 11,
                incomplete: 15,
                crashFreeRate: 84,
              }),
            }),
          }
        : undefined;
    renderPanel();
    await screen.findByText("84%");

    expect(coverageValue("Sessions observed")).toBe("40");
    expect(coverageValue("Counted in the rate")).toBe("25");
    expect(coverageValue("Not counted")).toBe("15");
    expect(coverageValue("Still active")).toBe("9");
    expect(coverageValue("Ended abnormally")).toBe("3");
    expect(coverageValue("Unhandled, no crash recorded")).toBe("2");
    expect(coverageValue("Start not received")).toBe("5");
    expect(coverageValue("Crashed")).toBe("6");
    expect(coverageValue("Crashed and counted")).toBe("4");
    expect(coverageValue("With errors")).toBe("11");
    expect(screen.getByText("21 of 25 completed sessions ended without a crash.")).toBeTruthy();
  });

  it("shows no rate when no session is complete", async () => {
    const none = counts({ observed: 2, withStart: 1, eligible: 0, crashed: 1, eligibleCrashed: 0, active: 1, incomplete: 2, crashFreeRate: null });
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? { status: 200, body: health({ sessions: none, groups: [group(none)] }) }
        : undefined;
    renderPanel();

    const rate = await screen.findByTestId("crash-free-rate");
    expect(rate.textContent).toContain("No completed sessions");
    expect(rate.textContent).toContain("2 observed, none complete.");
    expect(rate.textContent).not.toMatch(/\d%/);
    const groups = screen.getByRole("table", { name: /Sessions by release and environment/ });
    expect(within(groups).getByText("None complete")).toBeTruthy();
    expect(groups.textContent).not.toMatch(/\d%/);
  });

  it("leaves unhandled-only sessions out of the rate without calling them crashes", async () => {
    const unhandledOnly = counts({
      observed: 2,
      withStart: 2,
      eligible: 0,
      crashed: 0,
      eligibleCrashed: 0,
      unhandled: 2,
      active: 0,
      errored: 2,
      incomplete: 2,
      crashFreeRate: null,
    });
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? { status: 200, body: health({ sessions: unhandledOnly, groups: [group(unhandledOnly)] }) }
        : undefined;
    renderPanel();

    const rate = await screen.findByTestId("crash-free-rate");
    expect(rate.textContent).toContain("No completed sessions");
    expect(rate.textContent).toContain("2 observed, none complete.");
    expect(coverageValue("Unhandled, no crash recorded")).toBe("2");
    expect(coverageValue("Counted in the rate")).toBe("0");
    expect(coverageValue("Not counted")).toBe("2");
    expect(coverageValue("Crashed")).toBe("0");
    expect(coverageValue("Crashed and counted")).toBe("0");
    expect(screen.getByText(/not confirmed crashes and are not counted as completed/)).toBeTruthy();
    const table = screen.getByRole("table", { name: /Sessions by release and environment/ });
    const cells = within(within(table).getAllByRole("row")[1]).getAllByRole("cell");
    expect(cells.map((cell) => cell.textContent)).toEqual(["app@1synthetic", "None complete", "0", "0", "2", "2", "2"]);
    expect(document.body.textContent).not.toMatch(/\d%/);
  });

  it("keeps the rate denominator to completed sessions when unhandled sessions are mixed in", async () => {
    const mixed = counts({
      observed: 10,
      withStart: 10,
      eligible: 4,
      crashed: 1,
      eligibleCrashed: 1,
      unhandled: 3,
      abnormal: 0,
      active: 3,
      errored: 5,
      incomplete: 6,
      crashFreeRate: 75,
    });
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? { status: 200, body: health({ sessions: mixed, groups: [group(mixed)] }) }
        : undefined;
    renderPanel();

    const rate = await screen.findByTestId("crash-free-rate");
    expect(rate.textContent).toContain("75%");
    expect(rate.textContent).toContain("3 of 4 completed sessions ended without a crash.");
    expect(coverageValue("Unhandled, no crash recorded")).toBe("3");
    expect(coverageValue("Crashed")).toBe("1");
    expect(coverageValue("Not counted")).toBe("6");
    const table = screen.getByRole("table", { name: /Sessions by release and environment/ });
    const cells = within(within(table).getAllByRole("row")[1]).getAllByRole("cell");
    expect(cells.map((cell) => cell.textContent)).toEqual(["app@1synthetic", "75%", "4", "1", "3", "6", "10"]);
  });

  it("explains unhandled sessions only when there are some", async () => {
    renderPanel();
    await screen.findByTestId("crash-free-rate");

    expect(coverageValue("Unhandled, no crash recorded")).toBe("0");
    expect(screen.queryByText(/not confirmed crashes and are not counted as completed/)).toBeNull();
  });

  it("lists release groups, marks missing context and explains truncation", async () => {
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? {
            status: 200,
            body: health({
              sessions: counts({ observed: 500, eligible: 400, eligibleCrashed: 4, crashFreeRate: 99 }),
              groups: [group(), group({ release: "", environment: "", crashFreeRate: 97.36842105 })],
              groupsTruncated: true,
            }),
          }
        : undefined;
    renderPanel();

    const table = await screen.findByRole("table", { name: /Sessions by release and environment/ });
    const rows = within(table).getAllByRole("row");
    expect(rows).toHaveLength(3);
    expect(rows[1].textContent).toContain("app@1");
    expect(rows[1].textContent).toContain("50%");
    expect(within(rows[2]).getAllByText("Not set")).toHaveLength(2);
    expect(rows[2].textContent).toContain("97.3%");
    expect(screen.getByText(/Showing the 100 release and environment pairs with the most sessions/)).toBeTruthy();
    expect(screen.getByText("396 of 400 completed sessions ended without a crash.")).toBeTruthy();
  });

  it("reports discarded quantities without percentages", async () => {
    renderPanel();

    const section = await screen.findByRole("region", { name: "SDK discard reports" });
    expect(section.textContent).toContain("2 reports listing 17 discarded events");
    expect(section.textContent).toContain("not events this server rejected and not a share of total traffic");
    expect(section.textContent).not.toContain("%");
    const rows = within(within(section).getByRole("table")).getAllByRole("row");
    expect(rows[1].textContent).toBe("queue_overflowerror12");
    expect(rows[2].textContent).toBe("before_senderror5");
    expect(section.textContent).not.toContain("reports sent on their own are not included");
  });

  it("applies period, release and environment filters and explains report context", async () => {
    renderPanel();
    await screen.findByTestId("crash-free-rate");

    const release = (await screen.findByRole("option", { name: "app@2" })).closest("select")!;
    fireEvent.change(release, { target: { value: "app@2" } });
    fireEvent.change(screen.getByLabelText("Environment"), { target: { value: "production" } });
    fireEvent.click(screen.getByRole("button", { name: "30 days" }));

    await waitFor(() =>
      expect(requests("/monitoring/health").at(-1)).toMatchObject({ release: "app@2", environment: "production" })
    );
    expect(rangeHours(requests("/monitoring/health").at(-1)!)).toBe(720);
    const section = await screen.findByRole("region", { name: "SDK discard reports" });
    expect(section.textContent).toContain("reports sent on their own are not included while filtering");

    fireEvent.click(screen.getByRole("button", { name: "24 hours" }));
    await waitFor(() => expect(rangeHours(requests("/monitoring/health").at(-1)!)).toBe(24));
  });

  it("offers release and environment labels seen only in session data", async () => {
    override = (url) => {
      if (url.pathname.endsWith("/releases")) return { status: 200, body: { releases: [] } };
      if (url.pathname.endsWith("/monitoring/health") && !url.searchParams.get("release")) {
        return {
          status: 200,
          body: health({ groups: [group({ release: "session-only@1", environment: "qa" }), group({ release: "", environment: "" })] }),
        };
      }
      if (url.pathname.endsWith("/monitoring/health")) {
        return { status: 200, body: health({ groups: [group({ release: "session-only@1", environment: "qa" })] }) };
      }
      return undefined;
    };
    renderPanel();

    const release = (await screen.findByRole("option", { name: "session-only@1" })).closest("select")!;
    expect(within(screen.getByLabelText("Environment")).getByRole("option", { name: "qa" })).toBeTruthy();
    fireEvent.change(release, { target: { value: "session-only@1" } });
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ release: "session-only@1" }));
    fireEvent.change(screen.getByLabelText("Environment"), { target: { value: "qa" } });
    await waitFor(() =>
      expect(requests("/monitoring/health").at(-1)).toMatchObject({ release: "session-only@1", environment: "qa" })
    );
    expect((screen.getByLabelText("Release") as HTMLSelectElement).value).toBe("session-only@1");
    expect(within(release).getAllByRole("option").map((o) => o.textContent)).toEqual(["All releases", "session-only@1"]);
  });

  it("shows empty states without inventing metrics", async () => {
    override = (url) =>
      url.pathname.endsWith("/monitoring/health")
        ? {
            status: 200,
            body: health({
              sessions: counts({ observed: 0, withStart: 0, eligible: 0, crashed: 0, eligibleCrashed: 0, active: 0, errored: 0, incomplete: 0, crashFreeRate: null }),
              groups: [],
              clientReports: { reports: 0, discardedEvents: 0, groups: [] },
            }),
          }
        : undefined;
    renderPanel();

    expect(await screen.findByText("No sessions in this period")).toBeTruthy();
    expect(screen.getByText("No SDK discard reports in this period")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.queryByTestId("crash-free-rate")).toBeNull();
    expect(document.body.textContent).not.toMatch(/\d%/);
  });

  it("offers a filter reset when the query is rejected", async () => {
    override = (url) =>
      url.pathname.endsWith("/monitoring/health") && url.searchParams.get("release") ? { status: 400 } : undefined;
    renderPanel();
    await screen.findByTestId("crash-free-rate");

    fireEvent.change(screen.getByLabelText("Release"), { target: { value: "app@2" } });
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("These filters couldn't be applied.");

    fireEvent.click(within(alert).getByRole("button", { name: "Reset filters" }));
    expect(await screen.findByTestId("crash-free-rate")).toBeTruthy();
    expect((screen.getByLabelText("Release") as HTMLSelectElement).value).toBe("");
  });

  it("retries server failures twice, then offers a manual retry", async () => {
    let failing = true;
    override = (url) => (failing && url.pathname.endsWith("/monitoring/health") ? { status: 503 } : undefined);
    renderPanel();

    const refresh = await screen.findByRole("button", { name: "Refresh" }, { timeout: 6000 });
    expect(requests("/monitoring/health")).toHaveLength(3);

    failing = false;
    fireEvent.click(refresh);
    expect(await screen.findByTestId("crash-free-rate")).toBeTruthy();
    expect(requests("/monitoring/health")).toHaveLength(4);
  }, 10_000);

  it("tells viewers without access instead of retrying", async () => {
    override = (url) => (url.pathname.endsWith("/monitoring/health") ? { status: 403 } : undefined);
    renderPanel();

    expect((await screen.findByRole("alert")).textContent).toContain(
      "You don't have access to this app's session health data."
    );
    expect(requests("/monitoring/health")).toHaveLength(1);
  });

  it("reads only the health summary and release catalog, never owner-only monitoring routes", async () => {
    renderPanel();
    await screen.findByTestId("crash-free-rate");
    await waitFor(() => expect(requests("/releases")).toHaveLength(1));

    expect(new Set(requestedPaths())).toEqual(new Set(["/api/v0/apps/a1/monitoring/health", "/api/v0/apps/a1/releases"]));
  });

  it("resets filters when the build mode or app changes", async () => {
    const view = renderPanel();
    const chooseEnvironment = async () => {
      const option = await screen.findByRole("option", { name: "synthetic" });
      fireEvent.change(option.closest("select")!, { target: { value: "synthetic" } });
    };
    await chooseEnvironment();
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ environment: "synthetic" }));

    buildMode = "debug";
    view.rerender();
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ buildMode: "debug" }));
    expect(requests("/monitoring/health").at(-1)).not.toHaveProperty("environment");
    expect((screen.getByLabelText("Environment") as HTMLSelectElement).value).toBe("");

    await chooseEnvironment();
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ environment: "synthetic" }));
    view.rerender("a2");
    await waitFor(() => expect(requestedPaths().at(-1)).toMatch(/^\/api\/v0\/apps\/a2\//));
    await waitFor(() => expect(requestedPaths()).toContain("/api/v0/apps/a2/monitoring/health"));
    const lastA2 = fetchMock.mock.calls
      .map(([input]) => new URL(input as string, "http://localhost"))
      .filter((url) => url.pathname === "/api/v0/apps/a2/monitoring/health")
      .at(-1)!;
    expect(lastA2.searchParams.has("environment")).toBe(false);
    expect((screen.getByLabelText("Environment") as HTMLSelectElement).value).toBe("");
  });
});

describe("SessionHealthPanel link filters", () => {
  it("applies a linked filter only to the scope it was opened in", async () => {
    const view = renderPanel("a1", { release: "app@1", environment: "" });
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ release: "app@1" }));

    buildMode = "debug";
    view.rerender();
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ buildMode: "debug" }));
    expect(requests("/monitoring/health").at(-1)).not.toHaveProperty("release");

    buildMode = "release";
    view.rerender();
    await waitFor(() => expect(requests("/monitoring/health").at(-1)).toMatchObject({ buildMode: "release" }));
    expect(requests("/monitoring/health").at(-1)).not.toHaveProperty("release");
  });
});

describe("formatCrashFreeRate", () => {
  it("rounds down so any crash stays visible", () => {
    expect(formatCrashFreeRate(50)).toBe("50%");
    expect(formatCrashFreeRate(100)).toBe("100%");
    expect(formatCrashFreeRate(0)).toBe("0%");
    expect(formatCrashFreeRate(99.96)).toBe("99.9%");
    expect(formatCrashFreeRate(97.36842105)).toBe("97.3%");
    expect(formatCrashFreeRate(4.35)).toBe("4.3%");
  });
});
