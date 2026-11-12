import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@features/apps", () => ({
  useCurrentApp: () => ({ id: "a1" }),
}));
vi.mock("@components/Page", () => ({
  Page: ({ children }: { children: unknown }) => <>{children}</>,
  PageHeading: () => null,
}));
vi.mock("./mode/BuildModeSelector", () => ({ BuildModeSelector: () => null }));
vi.mock("./errors/BetaNotice", () => ({ BetaNotice: () => null }));
vi.mock("./errors/IssuesList", () => ({ IssuesPanel: () => <p>issues panel</p> }));
vi.mock("./errors/ErrorsList", () => ({ ErrorsList: () => <p>raw error logs</p> }));

import { Component as ErrorsPage } from "./ErrorsPage";

function Search() {
  return <output data-testid="search">{useLocation().search}</output>;
}

function renderPage(path = "/a1/errors") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <ErrorsPage />
      <Search />
    </MemoryRouter>
  );
}

afterEach(cleanup);

describe("ErrorsPage", () => {
  it("shows issues by default and keeps raw error logs one tab away", () => {
    renderPage("/a1/errors?osName=iOS");

    expect(screen.getByText("issues panel")).toBeTruthy();
    expect(screen.queryByText("raw error logs")).toBeNull();

    fireEvent.mouseDown(screen.getByRole("tab", { name: "Error logs" }));

    expect(screen.getByText("raw error logs")).toBeTruthy();
    expect(screen.getByTestId("search").textContent).toBe("?osName=iOS&view=logs");
  });

  it("opens raw error logs from a shared link", () => {
    renderPage("/a1/errors?view=logs");

    expect(screen.getByText("raw error logs")).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Issues" }));
    expect(screen.getByText("issues panel")).toBeTruthy();
    expect(screen.getByTestId("search").textContent).toBe("");
  });
});
