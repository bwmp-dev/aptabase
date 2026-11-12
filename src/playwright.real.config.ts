import { defineConfig } from "@playwright/test";
import path from "node:path";
import { artifactsDir, assertHeadless, sharedUse, viewportProjects } from "./e2e/shared-config";

assertHeadless();

// Runs against an already running loopback test host (Tests/MonitoringHost), never a real deployment.
const baseURL = process.env.E2E_REAL_BASE_URL;
if (!baseURL || new URL(baseURL).hostname !== "127.0.0.1") {
  throw new Error("Set E2E_REAL_BASE_URL to the loopback test host, e.g. http://127.0.0.1:55440");
}

export default defineConfig({
  testDir: "./e2e/real",
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,
  timeout: 60_000,
  reporter: [["list"]],
  outputDir: path.join(artifactsDir, "real-results"),
  use: { ...sharedUse, baseURL },
  projects: viewportProjects,
});
