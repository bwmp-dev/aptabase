import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@aptabase/web", () => ({ trackEvent: () => {} }));
vi.mock("@features/apps", () => ({
  useApps: () => ({ buildMode: "release" }),
  useCurrentApp: () => ({ id: "a1", name: "App" }),
}));
vi.mock("@features/analytics/query", () => ({ liveSessionDetails: () => Promise.resolve(undefined) }));
vi.mock("@tanstack/react-query", () => ({ useQuery: () => ({ isLoading: false, data: undefined }) }));
vi.mock("@components/Page", () => ({
  Page: ({ children }: { children: unknown }) => <>{children}</>,
  PageHeading: () => null,
}));

import { Component as LiveSessionDetailsPage } from "./LiveSessionDetailsPage";

function CurrentLocation() {
  const location = useLocation();
  return (
    <output data-testid="location" data-state={JSON.stringify(location.state)}>
      {location.pathname + location.search}
    </output>
  );
}

function renderAt(state: unknown) {
  render(
    <MemoryRouter initialEntries={[{ pathname: "/a1/live/s1", state }]}>
      <Routes>
        <Route path="/:id/live/:sessionId" element={<LiveSessionDetailsPage />} />
        <Route path="*" element={<CurrentLocation />} />
      </Routes>
    </MemoryRouter>,
  );
  fireEvent.click(screen.getByRole("button", { name: /back/i }));
  return screen.getByTestId("location");
}

afterEach(cleanup);

describe("LiveSessionDetailsPage back navigation", () => {
  it("returns to the originating page with its search params and session filters", () => {
    const location = renderAt({
      returnTo: { pathname: "/a1/sessions", search: "?osName=iOS&period=7d" },
      sessionFilters: { country: "DE" },
    });

    expect(location.textContent).toBe("/a1/sessions?osName=iOS&period=7d");
    expect(JSON.parse(location.dataset.state ?? "null")).toEqual({ sessionFilters: { country: "DE" } });
  });

  it("falls back to the live view when no return location was recorded", () => {
    expect(renderAt(null).textContent).toBe("/a1/live/");
  });

  it.each([
    ["protocol-relative", { pathname: "//evil.example/phish", search: "" }],
    ["backslash after the leading slash", { pathname: "/\\evil.example/phish", search: "" }],
    ["leading backslashes", { pathname: "\\\\evil.example/phish", search: "" }],
    ["embedded backslash", { pathname: "/a1/\\/evil.example", search: "" }],
    ["absolute URL", { pathname: "https://evil.example/phish", search: "" }],
    ["tab-separated slashes", { pathname: "/\t/evil.example", search: "" }],
    ["search that is not a query string", { pathname: "/a1/sessions", search: "//evil.example" }],
    ["non-string fields", { pathname: ["/a1/sessions"], search: undefined }],
  ])("falls back to the live view for a %s return path", (_, returnTo) => {
    const location = renderAt({ returnTo, sessionFilters: { country: "DE" } });

    expect(location.textContent).toBe("/a1/live/");
    expect(location.dataset.state).toBe("null");
  });

  it("keeps a return path without search params", () => {
    expect(renderAt({ returnTo: { pathname: "/a1/errors", search: "" } }).textContent).toBe("/a1/errors");
  });
});
