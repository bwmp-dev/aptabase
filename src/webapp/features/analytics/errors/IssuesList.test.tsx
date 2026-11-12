import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Issue } from "./issues";

let buildMode = "release";

vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode }),
}));

import { IssuesPanel } from "./IssuesList";

type Reply = { status: number; body?: unknown } | Promise<{ status: number; body?: unknown }>;
type Handler = (url: URL, init?: RequestInit) => Reply;

const fetchMock = vi.fn();
let handler: Handler;

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
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (input: string, init?: RequestInit) => {
    const reply = await handler(new URL(input, "http://localhost"), init);
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

    fireEvent.click(await screen.findByRole("button", { name: /Next/ }));
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

  it("shows an error with retry when the list fails", async () => {
    let fail = true;
    handler = () => (fail ? { status: 500 } : listReply([issue()]));
    renderPanel();

    const retry = await screen.findByRole("button", { name: "Refresh" });
    fail = false;
    fireEvent.click(retry);

    expect(await screen.findByText("TypeError")).toBeTruthy();
    expect(listRequests()).toHaveLength(2);
  });

  it("resets filters, paging and the open issue when the build mode changes", async () => {
    handler = (url) =>
      url.pathname.endsWith("/issues")
        ? listReply([issue()], { total: 120 })
        : { status: 200, body: { issue: issue(), canManage: true } };
    const view = renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: "Resolved" }));
    fireEvent.click(await screen.findByRole("button", { name: /Next/ }));
    await waitFor(() => expect(listRequests().at(-1)).toMatchObject({ status: "resolved", offset: "50" }));
    fireEvent.click(screen.getByRole("button", { name: /TypeError/ }));
    expect(await screen.findByRole("dialog")).toBeTruthy();

    buildMode = "debug";
    view.rerender();

    await waitFor(() =>
      expect(listRequests().at(-1)).toEqual({ buildMode: "debug", status: "unresolved", offset: "0", limit: "50" })
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("button", { name: "Unresolved" }).getAttribute("aria-pressed")).toBe("true");
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
