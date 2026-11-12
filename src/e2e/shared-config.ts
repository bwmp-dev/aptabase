import { devices, type Page, type Project } from "@playwright/test";
import os from "node:os";
import path from "node:path";

export const artifactsDir = process.env.E2E_ARTIFACTS_DIR ?? path.join(os.tmpdir(), "aptabase-issues-e2e");

// These checks must never open a visible browser window or debugging UI.
export function assertHeadless() {
  const headedFlags = ["--headed", "--ui", "--debug"];
  if (process.env.PWDEBUG || process.argv.some((arg) => headedFlags.some((flag) => arg.startsWith(flag)))) {
    throw new Error("The acceptance tests only run headless.");
  }
}

export const sharedUse = {
  headless: true,
  locale: "en-US",
  timezoneId: "UTC",
  colorScheme: "light",
  serviceWorkers: "block",
  trace: "off",
  video: "off",
  screenshot: "off",
} as const;

export const viewportProjects: Project[] = [
  { name: "desktop", use: { ...devices["Desktop Chrome"], headless: true, viewport: { width: 1280, height: 800 } } },
  { name: "mobile", use: { ...devices["Pixel 7"], headless: true } },
];

// The page heading fades in with framer-motion; screenshots wait until its wrapper is fully opaque.
export async function waitForPageFadeIn(page: Page) {
  await page.waitForFunction(() => {
    let element = document.querySelector("h1")?.parentElement ?? null;
    while (element && !element.style.opacity) element = element.parentElement;
    return !element || Number(element.style.opacity) === 1;
  });
}
