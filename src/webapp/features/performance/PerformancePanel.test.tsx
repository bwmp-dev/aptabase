import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { OperationGroup, Span, Transaction } from "./performance";

let buildMode = "release";

vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode }),
}));

import { MAX_VISUAL_SPAN_DEPTH, flattenSpanTree, spanBar, spanIndentRem } from "./performance";
import { PerformancePanel } from "./PerformancePage";

type Reply = { status: number; body?: unknown };
type Handler = (url: URL) => Reply | undefined;

const fetchMock = vi.fn();
let override: Handler | undefined;

const transactionId = "a328eb5e-f1fc-4fa5-8222-1e24881369f0";

function group(overrides: Partial<OperationGroup> = {}): OperationGroup {
  return { name: "app.startup", operation: "ui.load", count: 120, errorCount: 3, avgMs: 140, p50Ms: 120, p95Ms: 1500, ...overrides };
}

function transaction(overrides: Partial<Transaction> = {}): Transaction {
  return {
    eventId: transactionId,
    timestamp: "2026-11-11T10:00:00Z",
    name: "app.startup",
    operation: "ui.load",
    durationMs: 250,
    status: "ok",
    release: "1.2.0",
    environment: "production",
    platform: "desktop",
    ...overrides,
  };
}

const spans: Span[] = [
  { spanId: "2222222222222222", parentSpanId: "1111111111111111", name: "read.file", operation: "file.read", startOffsetMs: 10, durationMs: 5, status: "ok" },
  { spanId: "1111111111111111", parentSpanId: null, name: "load.preferences", operation: "db.query", startOffsetMs: 5, durationMs: 40, status: "ok" },
  { spanId: "3333333333333333", parentSpanId: null, name: "render.window", operation: "ui.render", startOffsetMs: 60, durationMs: 100, status: "error" },
];

function defaultReply(url: URL): Reply {
  if (url.pathname.endsWith("/releases")) {
    return {
      status: 200,
      body: {
        releases: [
          { release: "1.2.0", environment: "production", errorCount: 1, transactionCount: 4, firstSeen: "", lastSeen: "" },
          { release: "1.1.0", environment: "staging", errorCount: 0, transactionCount: 2, firstSeen: "", lastSeen: "" },
        ],
      },
    };
  }
  if (url.pathname.endsWith("/performance/summary")) {
    return { status: 200, body: { groups: [group(), group({ name: "screen.open", operation: "ui.navigation", errorCount: 0 })] } };
  }
  if (url.pathname.endsWith("/transactions")) {
    return { status: 200, body: { transactions: [transaction()], pagination: { offset: 0, limit: 50, total: 120 } } };
  }
  if (url.pathname.endsWith(`/transactions/${transactionId}`)) {
    return { status: 200, body: { transaction: transaction(), spans } };
  }
  return { status: 404 };
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

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = () => (
    <QueryClientProvider client={client}>
      <PerformancePanel appId="a1" />
    </QueryClientProvider>
  );
  const view = render(ui());
  return { rerender: () => view.rerender(ui()) };
}

async function findTransactionButton() {
  const table = await screen.findByRole("table", { name: /Sampled transactions/ });
  return within(table).getByRole("button", { name: /app\.startup/ });
}

async function clickNext() {
  const next = (await screen.findByRole("button", { name: /Next/ })) as HTMLButtonElement;
  await waitFor(() => expect(next.disabled).toBe(false));
  fireEvent.click(next);
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

describe("PerformancePanel", () => {
  it("loads sampled operations and transactions for the last 7 release days", async () => {
    renderPanel();

    const operations = await screen.findByRole("table", { name: /Sampled operations/ });
    const rows = within(operations).getAllByRole("row");
    expect(rows[1].textContent).toContain("app.startup");
    expect(rows[1].textContent).toContain("120");
    expect(rows[1].textContent).toContain("1.5 s");
    expect(screen.getByText(/not total usage/)).toBeTruthy();
    expect(screen.getByText(/kept for up to 30 days, depending on app retention/)).toBeTruthy();

    const [summary] = requests("/performance/summary");
    expect(summary).toMatchObject({ buildMode: "release" });
    expect(rangeHours(summary)).toBe(168);
    expect(summary).not.toHaveProperty("release");
    expect(requests("/transactions")[0]).toMatchObject({ buildMode: "release", offset: "0", limit: "50" });
    expect(requests("/releases")[0]).toEqual({ buildMode: "release" });
  });

  it("applies period, release and environment filters and returns to the first page", async () => {
    renderPanel();
    await screen.findByText("Showing 1 to 50 of 120 transactions");

    await clickNext();
    await waitFor(() => expect(requests("/transactions").at(-1)?.offset).toBe("50"));

    const release = (await screen.findByRole("option", { name: "1.2.0" })).closest("select")!;
    fireEvent.change(release, { target: { value: "1.2.0" } });
    fireEvent.change(screen.getByLabelText("Environment"), { target: { value: "staging" } });
    fireEvent.click(screen.getByRole("button", { name: "24 hours" }));

    await waitFor(() =>
      expect(requests("/transactions").at(-1)).toMatchObject({ release: "1.2.0", environment: "staging", offset: "0" })
    );
    const summary = requests("/performance/summary").at(-1)!;
    expect(summary).toMatchObject({ release: "1.2.0", environment: "staging" });
    expect(rangeHours(summary)).toBe(24);
    expect(screen.getByRole("button", { name: "24 hours" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("filters transactions by a selected operation without refetching the summary", async () => {
    renderPanel();
    const operation = await screen.findByRole("button", { name: /Filter transactions by screen\.open/ });
    const summaryCalls = requests("/performance/summary").length;

    fireEvent.click(operation);
    await waitFor(() =>
      expect(requests("/transactions").at(-1)).toMatchObject({ name: "screen.open", operation: "ui.navigation" })
    );
    expect(operation.getAttribute("aria-pressed")).toBe("true");
    expect(requests("/performance/summary")).toHaveLength(summaryCalls);

    fireEvent.click(screen.getByRole("button", { name: /Show all operations/ }));
    await waitFor(() => expect(requests("/transactions").at(-1)).not.toHaveProperty("name"));
  });

  it("shows transaction details with nested spans and returns focus on close", async () => {
    renderPanel();
    const opener = await findTransactionButton();
    fireEvent.click(opener);

    const dialog = await screen.findByRole("dialog");
    const spanTable = await within(dialog).findByRole("table");
    const names = within(spanTable)
      .getAllByRole("row")
      .slice(1)
      .map((row) => row.querySelector("td")?.textContent);
    expect(names).toEqual([
      expect.stringContaining("load.preferences"),
      expect.stringContaining("Level 2: read.file"),
      expect.stringContaining("render.window"),
    ]);
    expect(within(dialog).getByText("Spans (3)")).toBeTruthy();
    expect(within(dialog).getByText("1.2.0")).toBeTruthy();
    expect(requests(`/transactions/${transactionId}`)[0]).toEqual({ buildMode: "release" });

    fireEvent.keyDown(dialog, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(document.activeElement).toBe(opener);
  });

  it("explains an expired transaction", async () => {
    override = (url) => (url.pathname.endsWith(`/transactions/${transactionId}`) ? { status: 404 } : undefined);
    renderPanel();
    fireEvent.click(await findTransactionButton());

    const dialog = await screen.findByRole("dialog");
    expect((await within(dialog).findByRole("alert")).textContent).toContain("kept for up to 30 days, depending on app retention");
    expect(within(dialog).queryByRole("button", { name: "Try again" })).toBeNull();
  });

  it("shows empty states without inventing metrics", async () => {
    override = (url) => {
      if (url.pathname.endsWith("/performance/summary")) return { status: 200, body: { groups: [] } };
      if (url.pathname.endsWith("/transactions")) {
        return { status: 200, body: { transactions: [], pagination: { offset: 0, limit: 50, total: 0 } } };
      }
      return undefined;
    };
    renderPanel();

    expect(await screen.findByText("No sampled operations in this period")).toBeTruthy();
    expect(await screen.findByText("No transactions match these filters")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("offers a filter reset when the query is rejected", async () => {
    override = (url) =>
      url.searchParams.get("release") && !url.pathname.endsWith("/releases") ? { status: 400 } : undefined;
    renderPanel();
    await screen.findByText("Showing 1 to 50 of 120 transactions");

    fireEvent.change(screen.getByLabelText("Release"), { target: { value: "1.1.0" } });
    const alerts = await screen.findAllByRole("alert");
    expect(alerts[0].textContent).toContain("These filters couldn't be applied.");

    fireEvent.click(within(alerts[0]).getByRole("button", { name: "Reset filters" }));
    expect(await screen.findByText("Showing 1 to 50 of 120 transactions")).toBeTruthy();
    expect((screen.getByLabelText("Release") as HTMLSelectElement).value).toBe("");
  });

  it("retries server failures twice, then offers a manual retry", async () => {
    let failing = true;
    override = (url) => (failing && url.pathname.endsWith("/performance/summary") ? { status: 503 } : undefined);
    renderPanel();

    const operations = await screen.findByRole("region", { name: "Operations" });
    const refresh = await within(operations).findByRole("button", { name: "Refresh" }, { timeout: 6000 });
    expect(requests("/performance/summary")).toHaveLength(3);
    expect(await screen.findByRole("table", { name: /Sampled transactions/ })).toBeTruthy();

    failing = false;
    fireEvent.click(refresh);
    expect(await within(operations).findByRole("table", { name: /Sampled operations/ })).toBeTruthy();
    expect(requests("/performance/summary")).toHaveLength(4);
  }, 10_000);

  it("tells viewers without access instead of retrying", async () => {
    override = (url) => (url.pathname.endsWith("/releases") ? undefined : { status: 403 });
    renderPanel();

    const alerts = await screen.findAllByRole("alert");
    expect(alerts[0].textContent).toContain("You don't have access to this app's performance data.");
    expect(requests("/performance/summary")).toHaveLength(1);
  });

  it("resets filters and the open transaction when the build mode changes", async () => {
    const view = renderPanel();
    fireEvent.change(await screen.findByLabelText("Environment"), { target: { value: "production" } });
    fireEvent.click(await findTransactionButton());
    expect(await screen.findByRole("dialog")).toBeTruthy();

    buildMode = "debug";
    view.rerender();

    await waitFor(() => expect(requests("/transactions").at(-1)).toMatchObject({ buildMode: "debug", offset: "0" }));
    expect(requests("/transactions").at(-1)).not.toHaveProperty("environment");
    expect(requests("/releases").at(-1)).toEqual({ buildMode: "debug" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect((screen.getByLabelText("Environment") as HTMLSelectElement).value).toBe("");
  });
});

describe("flattenSpanTree", () => {
  it("orders spans depth-first and treats spans with unknown parents as roots", () => {
    const nodes = flattenSpanTree([
      ...spans,
      { spanId: "4444444444444444", parentSpanId: "ffffffffffffffff", name: "orphan", operation: "x", startOffsetMs: 0, durationMs: 1, status: "ok" },
    ]);
    expect(nodes.map((n) => [n.name, n.depth])).toEqual([
      ["orphan", 0],
      ["load.preferences", 0],
      ["read.file", 1],
      ["render.window", 0],
    ]);
  });
});

describe("span layout bounds", () => {
  it("caps indentation for deeply nested spans", () => {
    expect(spanIndentRem(0)).toBe(0);
    expect(spanIndentRem(2)).toBe(1.5);
    expect(spanIndentRem(99)).toBe(spanIndentRem(MAX_VISUAL_SPAN_DEPTH));
  });

  it("keeps timeline bars inside the track", () => {
    expect(spanBar(0, 50, 100)).toEqual({ left: 0, width: 50 });
    expect(spanBar(100, 0, 100)).toEqual({ left: 99.5, width: 0.5 });
    expect(spanBar(40, 0, 0)).toEqual({ left: 99.5, width: 0.5 });
    expect(spanBar(10, 500, 100)).toEqual({ left: 0, width: 100 });
  });

  it("shows the real level of spans nested beyond the visual cap", async () => {
    const chain: Span[] = Array.from({ length: 100 }, (_, i) => ({
      spanId: (i + 1).toString(16).padStart(16, "0"),
      parentSpanId: i === 0 ? null : i.toString(16).padStart(16, "0"),
      name: `step.${i + 1}`,
      operation: "work",
      startOffsetMs: i,
      durationMs: 100 - i,
      status: "ok",
    }));
    override = (url) =>
      url.pathname.endsWith(`/transactions/${transactionId}`)
        ? { status: 200, body: { transaction: transaction({ durationMs: 100 }), spans: chain } }
        : undefined;
    renderPanel();
    fireEvent.click(await findTransactionButton());

    const dialog = await screen.findByRole("dialog");
    const rows = within(await within(dialog).findByRole("table")).getAllByRole("row");
    const deepest = rows.at(-1)!.querySelector("td")!;
    expect(deepest.textContent).toContain("Level 100: step.100");
    expect((deepest.firstElementChild as HTMLElement).style.paddingLeft).toBe(`${spanIndentRem(MAX_VISUAL_SPAN_DEPTH)}rem`);
  });
});
