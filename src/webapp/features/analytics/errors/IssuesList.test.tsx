import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Issue, IssueEvent } from "./issues";

let buildMode = "release";

vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode }),
}));

import { IssuesPanel } from "./IssuesList";

type Reply = { status: number; body?: unknown } | Promise<{ status: number; body?: unknown }>;
type Handler = (url: URL, init?: RequestInit) => Reply;

const fetchMock = vi.fn();
let handler: Handler;
let eventsHandler: Handler;

const releasesReply = {
  status: 200,
  body: {
    releases: [
      { release: "2.0.0", environment: "production", errorCount: 4, transactionCount: 0, firstSeen: "", lastSeen: "" },
      { release: "1.9.0", environment: "staging", errorCount: 1, transactionCount: 0, firstSeen: "", lastSeen: "" },
    ],
  },
};

function event(overrides: Partial<IssueEvent> = {}): IssueEvent {
  return {
    eventId: "7d0d1f5e-7c1e-4c31-9a39-62b3f6f0c001",
    timestamp: "2026-11-10T09:00:00Z",
    errorMessage: "Cannot read properties of undefined (reading 'x')",
    stackTrace: "at occurrence (app.js:9:9)",
    release: "2.0.0",
    environment: "production",
    platform: "desktop",
    osName: "Windows",
    osVersion: "11",
    sdkVersion: "aptabase-dotnet@1.0.0",
    severity: "fatal",
    kind: "crash",
    ...overrides,
  };
}

function eventsReply(events: IssueEvent[], total = events.length) {
  return { status: 200, body: { events, pagination: { offset: 0, limit: 10, total } } };
}

function issue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: "a".repeat(64),
    errorType: "TypeError",
    errorMessage: "Cannot read properties of undefined",
    stackTrace: "at main (app.js:1:1)",
    status: "unresolved",
    occurrenceCount: 1200,
    firstSeen: "2026-11-01T10:00:00Z",
    lastSeen: "2026-11-10T10:00:00Z",
    resolvedAt: null,
    ...overrides,
  };
}

function listReply(issues: Issue[], options: { offset?: number; total?: number; canManage?: boolean } = {}) {
  return {
    status: 200,
    body: {
      issues,
      pagination: { offset: options.offset ?? 0, limit: 50, total: options.total ?? issues.length },
      canManage: options.canManage ?? true,
    },
  };
}

function calls() {
  return fetchMock.mock.calls.map(([input, init]) => ({
    url: new URL(input as string, "http://localhost"),
    init: init as RequestInit | undefined,
  }));
}

function listRequests() {
  return calls()
    .filter((c) => c.url.pathname === "/api/v0/apps/a1/issues")
    .map((c) => Object.fromEntries(c.url.searchParams));
}

function searchBox() {
  return screen.getByRole("searchbox", { name: "Search issues" }) as HTMLInputElement;
}

function search(value: string) {
  fireEvent.change(searchBox(), { target: { value } });
}

async function clickNext() {
  const next = (await screen.findByRole("button", { name: /Next/ })) as HTMLButtonElement;
  await waitFor(() => expect(next.disabled).toBe(false));
  fireEvent.click(next);
}

function renderPanel(appId = "a1") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = () => (
    <QueryClientProvider client={client}>
      <IssuesPanel appId={appId} />
    </QueryClientProvider>
  );
  const view = render(ui());
  return { ...view, rerender: () => view.rerender(ui()) };
}

beforeEach(() => {
  buildMode = "release";
  eventsHandler = () => eventsReply([event()]);
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (input: string, init?: RequestInit) => {
    const url = new URL(input, "http://localhost");
    const reply = url.pathname.endsWith("/releases")
      ? releasesReply
      : url.pathname.endsWith("/events")
      ? await eventsHandler(url, init)
      : await handler(url, init);
    return { ok: reply.status >= 200 && reply.status < 300, status: reply.status, json: async () => reply.body };
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("IssuesPanel list", () => {
  it("requests unresolved release issues by default and explains lifetime counts", async () => {
    handler = () =>
      listReply([
        issue({ id: "b".repeat(64), errorType: "RangeError", lastSeen: "2026-11-10T12:00:00Z" }),
        issue({ errorType: "TypeError" }),
      ]);
    renderPanel();

    const rows = await screen.findAllByRole("row");
    expect(rows[1].textContent).toContain("RangeError");
    expect(rows[2].textContent).toContain("TypeError");
    expect(listRequests()).toEqual([{ buildMode: "release", status: "unresolved", offset: "0", limit: "50" }]);
    expect(calls()[0].init?.credentials).toBe("include");
    expect(screen.getByText(/Counts are lifetime totals and ignore the date range/)).toBeTruthy();
    expect(screen.getByText(/Only reports received since issue tracking was enabled/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Unresolved" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("refetches from the first page when the status filter changes", async () => {
    handler = () => listReply([issue()], { total: 120 });
    renderPanel();

    await clickNext();
    await waitFor(() => expect(listRequests().at(-1)?.offset).toBe("50"));

    fireEvent.click(screen.getByRole("button", { name: "Resolved" }));
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ status: "resolved", offset: "0" }));

    fireEvent.click(screen.getByRole("button", { name: "All" }));
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ status: "all", offset: "0" }));
    expect(screen.getByRole("button", { name: "All" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("shows a filter-specific empty state", async () => {
    handler = () => listReply([]);
    renderPanel();

    expect(await screen.findByText("No unresolved issues")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("retries server failures, then offers a manual retry", async () => {
    let fail = true;
    handler = () => (fail ? { status: 500 } : listReply([issue()]));
    renderPanel();

    const retry = await screen.findByRole("button", { name: "Refresh" }, { timeout: 6000 });
    expect(listRequests()).toHaveLength(3);
    fail = false;
    fireEvent.click(retry);

    expect(await screen.findByText("TypeError")).toBeTruthy();
    expect(listRequests()).toHaveLength(4);
  }, 10_000);

  it("resets filters, search, paging and the open issue when the build mode changes", async () => {
    handler = (url) =>
      url.pathname.endsWith("/issues")
        ? listReply([issue()], { total: 120 })
        : { status: 200, body: { issue: issue(), canManage: true } };
    const view = renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: "Resolved" }));
    search("TypeError");
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ q: "TypeError" }));
    await clickNext();
    await waitFor(() =>
      expect(listRequests().at(-1)).toMatchObject({ status: "resolved", q: "TypeError", offset: "50" })
    );
    fireEvent.click(screen.getByRole("button", { name: /TypeError/ }));
    expect(await screen.findByRole("dialog")).toBeTruthy();

    buildMode = "debug";
    view.rerender();

    await waitFor(() =>
      expect(listRequests().at(-1)).toEqual({ buildMode: "debug", status: "unresolved", offset: "0", limit: "50" })
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("button", { name: "Unresolved" }).getAttribute("aria-pressed")).toBe("true");
    expect(searchBox().value).toBe("");
  });
});

describe("IssuesPanel search", () => {
  it("debounces a trimmed literal query and returns to the first page", async () => {
    handler = () => listReply([issue()], { total: 120 });
    renderPanel();

    await clickNext();
    await waitFor(() => expect(listRequests().at(-1)?.offset).toBe("50"));

    search("  50% off_[x]*  ");
    expect(listRequests().some((r) => "q" in r)).toBe(false);

    await waitFor(() =>
      expect(listRequests().at(-1)).toEqual({
        buildMode: "release",
        status: "unresolved",
        offset: "0",
        limit: "50",
        q: "50% off_[x]*",
      })
    );
  });

  it("applies the query immediately on submit and keeps it when the status filter changes", async () => {
    handler = () => listReply([issue()], { total: 120 });
    renderPanel();
    await screen.findByText("TypeError");

    search("timeout");
    fireEvent.submit(screen.getByRole("search"));
    await waitFor(() => expect(listRequests().at(-1)?.q).toBe("timeout"), { timeout: 200 });

    await clickNext();
    await waitFor(() => expect(listRequests().at(-1)?.offset).toBe("50"));
    fireEvent.click(screen.getByRole("button", { name: "All" }));
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ status: "all", q: "timeout", offset: "0" }));
  });

  it("does not send whitespace-only searches and limits input to 200 characters", async () => {
    handler = () => listReply([issue()]);
    renderPanel();
    await screen.findByText("TypeError");

    expect(searchBox().maxLength).toBe(200);
    search("   ");
    fireEvent.submit(screen.getByRole("search"));
    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(listRequests()).toHaveLength(1);
    expect(listRequests().some((r) => "q" in r)).toBe(false);
  });

  it("explains when nothing matches and clears back to the full list", async () => {
    handler = (url) => (url.searchParams.has("q") ? listReply([]) : listReply([issue()]));
    renderPanel();
    await screen.findByText("TypeError");

    search("nothing here");
    const status = await screen.findByRole("status");
    expect(status.textContent).toContain("No unresolved issues match “nothing here”");

    fireEvent.click(within(status).getByRole("button", { name: "Clear search" }));

    expect(await screen.findByText("TypeError")).toBeTruthy();
    expect(searchBox().value).toBe("");
    expect(document.activeElement).toBe(searchBox());
    expect(listRequests().at(-1)).not.toHaveProperty("q");
  });

  it("explains a rejected search instead of a generic failure", async () => {
    handler = (url) => (url.searchParams.has("q") ? { status: 400 } : listReply([issue()]));
    renderPanel();
    await screen.findByText("TypeError");

    search("bad");
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("This search couldn't be run");
    expect(screen.queryByRole("button", { name: "Refresh" })).toBeNull();

    fireEvent.click(within(alert).getByRole("button", { name: "Clear search" }));
    expect(await screen.findByText("TypeError")).toBeTruthy();
  });
});

describe("IssuesPanel detail", () => {
  const issueId = "a".repeat(64);
  const detailPath = `/api/v0/apps/a1/issues/${issueId}`;

  it("lets owners resolve an issue, showing pending state and refreshing the list", async () => {
    let current = issue();
    let releasePatch: () => void = () => {};
    handler = (url, init) => {
      if (url.pathname.endsWith("/issues")) return listReply([current]);
      if (init?.method === "PATCH") {
        return new Promise((resolve) => {
          releasePatch = () => {
            current = issue({ status: "resolved", resolvedAt: "2026-11-11T09:00:00Z" });
            resolve({ status: 200, body: { issue: current, canManage: true } });
          };
        });
      }
      return { status: 200, body: { issue: current, canManage: true } };
    };
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText("at main (app.js:1:1)")).toBeTruthy();
    expect(within(dialog).getByText(/1,200 \(lifetime\)/)).toBeTruthy();
    expect(calls().find((c) => c.url.pathname === detailPath)?.url.searchParams.get("buildMode")).toBe("release");

    const listCountBefore = listRequests().length;
    fireEvent.click(within(dialog).getByRole("button", { name: "Resolve issue" }));

    const pending = await within(dialog).findByRole("button", { name: "Resolving…" });
    expect((pending as HTMLButtonElement).disabled).toBe(true);

    const patch = calls().find((c) => c.init?.method === "PATCH")!;
    expect(patch.url.pathname).toBe(detailPath);
    expect(patch.url.searchParams.get("buildMode")).toBe("release");
    expect(JSON.parse(patch.init!.body as string)).toEqual({ status: "resolved" });

    releasePatch();

    expect(await within(dialog).findByRole("button", { name: "Reopen issue" })).toBeTruthy();
    expect(within(dialog).getByText(/reopens automatically if the error occurs again after it was resolved/)).toBeTruthy();
    expect(within(dialog).getByText(/Delayed reports of earlier occurrences and retried deliveries/)).toBeTruthy();
    await waitFor(() => expect(listRequests().length).toBeGreaterThan(listCountBefore));
  });

  it("lets owners reopen a resolved issue", async () => {
    let current = issue({ status: "resolved", resolvedAt: "2026-11-11T09:00:00Z" });
    handler = (url, init) => {
      if (url.pathname.endsWith("/issues")) return listReply([current]);
      if (init?.method === "PATCH") current = issue({ status: "unresolved", resolvedAt: null });
      return { status: 200, body: { issue: current, canManage: true } };
    };
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(await within(dialog).findByRole("button", { name: "Reopen issue" }));

    expect(await within(dialog).findByRole("button", { name: "Resolve issue" })).toBeTruthy();
    const patch = calls().find((c) => c.init?.method === "PATCH")!;
    expect(JSON.parse(patch.init!.body as string)).toEqual({ status: "unresolved" });
  });

  it("keeps the issue unchanged and explains a failed status change", async () => {
    handler = (url, init) => {
      if (url.pathname.endsWith("/issues")) return listReply([issue()]);
      if (init?.method === "PATCH") return { status: 403 };
      return { status: 200, body: { issue: issue(), canManage: true } };
    };
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(await within(dialog).findByRole("button", { name: "Resolve issue" }));

    expect((await within(dialog).findByRole("alert")).textContent).toContain("Only app owners can change");
    const button = within(dialog).getByRole("button", { name: "Resolve issue" }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
  });

  it("offers no status changes to read-only viewers", async () => {
    handler = (url) =>
      url.pathname.endsWith("/issues")
        ? listReply([issue()], { canManage: false })
        : { status: 200, body: { issue: issue(), canManage: false } };
    renderPanel();

    expect(await screen.findByText(/You have read-only access/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText(/Only app owners can resolve or reopen issues/)).toBeTruthy();
    expect(within(dialog).queryByRole("button", { name: /Resolve issue|Reopen issue/ })).toBeNull();
    expect(calls().some((c) => c.init?.method === "PATCH")).toBe(false);

    fireEvent.keyDown(dialog, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(document.activeElement).toBe(screen.getByRole("button", { name: /TypeError/ }));
  });

  it("explains when the issue no longer exists", async () => {
    handler = (url) => (url.pathname.endsWith("/issues") ? listReply([issue()]) : { status: 404 });
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    expect((await within(dialog).findByRole("alert")).textContent).toContain("This issue no longer exists.");
    expect(within(dialog).queryByRole("button", { name: "Try again" })).toBeNull();
  });
});

describe("IssuesPanel releases and occurrences", () => {
  function eventRequests() {
    return calls()
      .filter((c) => c.url.pathname.endsWith("/events"))
      .map((c) => Object.fromEntries(c.url.searchParams));
  }

  it("filters issues by observed release and environment and explains the scope", async () => {
    handler = () => listReply([issue()], { total: 120 });
    renderPanel();
    await clickNext();
    await waitFor(() => expect(listRequests().at(-1)?.offset).toBe("50"));

    const release = (await screen.findByRole("option", { name: "2.0.0" })).closest("select")!;
    fireEvent.change(release, { target: { value: "2.0.0" } });
    fireEvent.change(screen.getByLabelText("Environment"), { target: { value: "staging" } });

    await waitFor(() =>
      expect(listRequests().at(-1)).toMatchObject({ release: "2.0.0", environment: "staging", offset: "0" })
    );
    expect(screen.getByText(/filters match occurrences kept for up to 30 days, depending on app retention/)).toBeTruthy();
    expect(screen.getByText(/resolving an issue still applies to every release and environment/)).toBeTruthy();
  });

  it("starts from filters passed in the link", async () => {
    handler = () => listReply([issue()]);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <IssuesPanel appId="a1" initialFilter={{ release: "1.9.0", environment: "" }} />
      </QueryClientProvider>
    );

    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ release: "1.9.0" }));
    expect(listRequests().at(-1)).not.toHaveProperty("environment");
    expect((screen.getByLabelText("Release") as HTMLSelectElement).value).toBe("1.9.0");
  });

  it("lists recent occurrences with their context, limited to the active filters", async () => {
    handler = (url) =>
      url.pathname.endsWith("/issues") ? listReply([issue()]) : { status: 200, body: { issue: issue(), canManage: true } };
    eventsHandler = () => eventsReply([event(), event({ eventId: "7d0d1f5e-7c1e-4c31-9a39-62b3f6f0c002", release: "" })], 12);
    renderPanel();

    await screen.findByRole("option", { name: "production" });
    fireEvent.change(screen.getByLabelText("Environment"), { target: { value: "production" } });
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ environment: "production" }));
    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");

    const occurrences = await within(dialog).findByRole("region", { name: "Recent occurrences" });
    expect(occurrences.textContent).toContain("limited to environment production");
    expect(await within(occurrences).findByText(/2\.0\.0 · production · desktop · Windows 11/)).toBeTruthy();
    expect(occurrences.textContent).toContain("1–10 of 12 retained");
    expect(eventRequests().at(-1)).toEqual({
      buildMode: "release",
      offset: "0",
      limit: "10",
      environment: "production",
    });

    fireEvent.click(within(occurrences).getByRole("button", { name: "Next occurrences" }));
    await waitFor(() => expect(eventRequests().at(-1)?.offset).toBe("10"));
  });

  it("explains when no occurrence details are retained", async () => {
    handler = (url) =>
      url.pathname.endsWith("/issues") ? listReply([issue()]) : { status: 200, body: { issue: issue(), canManage: false } };
    eventsHandler = () => eventsReply([]);
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: /TypeError/ }));
    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText(/No retained occurrences\. Occurrence details are kept for up to 30 days, depending on app retention; raw error logs are kept separately for 180 days\./)).toBeTruthy();
  });
});
