import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@features/apps", () => ({}));
vi.mock("../analytics/mode/BuildModeSelector", () => ({ BuildModeSelector: () => null }));

import { ReleaseCatalog } from "./ReleasesPage";

const fetchMock = vi.fn();
let reply: { status: number; body?: unknown };

function renderCatalog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <ReleaseCatalog appId="a1" buildMode="debug" />
      </QueryClientProvider>
    </MemoryRouter>
  );
}

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation(async () => ({ ok: reply.status < 300, status: reply.status, json: async () => reply.body }));
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("ReleaseCatalog", () => {
  it("lists observed releases with links that pre-filter issues and performance", async () => {
    reply = {
      status: 200,
      body: {
        releases: [
          {
            release: "2.0.0",
            environment: "production",
            errorCount: 4,
            transactionCount: 1500,
            firstSeen: "2026-11-01T00:00:00Z",
            lastSeen: "2026-11-11T00:00:00Z",
          },
          {
            release: "1.9.0",
            environment: "",
            errorCount: 0,
            transactionCount: 3,
            firstSeen: "2026-10-20T00:00:00Z",
            lastSeen: "2026-10-21T00:00:00Z",
          },
        ],
      },
    };
    renderCatalog();

    expect((await screen.findByRole("link", { name: "Issues for 2.0.0 production" })).getAttribute("href")).toBe(
      "/a1/errors?release=2.0.0&environment=production"
    );
    expect(screen.getByRole("link", { name: "Performance for 1.9.0" }).getAttribute("href")).toBe(
      "/a1/performance?release=1.9.0"
    );
    expect(screen.getByText("1,500")).toBeTruthy();
    expect(screen.getByText("No environment")).toBeTruthy();
    expect(screen.getByText(/not a deployment history/)).toBeTruthy();
    expect(new URL(fetchMock.mock.calls[0][0], "http://localhost").search).toBe("?buildMode=debug");
  });

  it("explains an empty catalog", async () => {
    reply = { status: 200, body: { releases: [] } };
    renderCatalog();
    expect(await screen.findByText("No releases reported yet")).toBeTruthy();
  });

  it("tells viewers without access", async () => {
    reply = { status: 403 };
    renderCatalog();
    expect((await screen.findByRole("alert")).textContent).toContain("You don't have access");
  });
});
