import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Markdown } from "./Markdown";

function renderMarkdown(content: string) {
  return render(<Markdown baseURL="https://raw.example/docs" content={content} />).container;
}

describe("Markdown", () => {
  it("renders GFM tables and highlights fenced code", () => {
    const container = renderMarkdown(
      ["| Key | Value |", "| --- | ----- |", "| a | b |", "", "```swift", "let x = 1", "```"].join("\n"),
    );

    expect(container.querySelectorAll("table td")).toHaveLength(2);
    const code = container.querySelector("pre code");
    expect(code?.classList.contains("hljs")).toBe(true);
    expect(code?.classList.contains("language-swift")).toBe(true);
  });

  it("keeps only the first language of an info string as the code class", () => {
    const container = renderMarkdown(["```js&#x20;hidden", "run()", "```"].join("\n"));

    const code = container.querySelector("pre code");
    expect(code?.classList.contains("language-js")).toBe(true);
    expect(code?.classList.contains("hidden")).toBe(false);
  });

  it("resolves relative images against the base URL, skips og.png and opens links in a new tab", () => {
    const container = renderMarkdown(
      "![logo](img/logo.png) ![abs](https://cdn.example/a.png) ![og](img/og.png)\n\n[Docs](https://aptabase.com)",
    );

    const sources = Array.from(container.querySelectorAll("img")).map((img) => img.getAttribute("src"));
    expect(sources).toEqual(["https://raw.example/docs/img/logo.png", "https://cdn.example/a.png"]);
    expect(container.querySelector("a")?.getAttribute("target")).toBe("_blank");
  });

  it("passes through raw HTML from the instructions source", () => {
    const container = renderMarkdown("<details><summary>More</summary>\n\nInside\n\n</details>");

    expect(container.querySelector("details summary")?.textContent).toBe("More");
  });
});
