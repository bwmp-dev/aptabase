import { defineConfig } from "@playwright/test";
import path from "node:path";
import { artifactsDir, assertHeadless, sharedUse, viewportProjects } from "./e2e/shared-config";

assertHeadless();

const port = Number(process.env.E2E_PORT ?? 4317);

// Mocked-network UI checks against the production bundle; see playwright.real.config.ts for the real backend.
export default defineConfig({
  testDir: "./e2e",
  testIgnore: "real/**",
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,
  timeout: 30_000,
  reporter: [["list"]],
  outputDir: path.join(artifactsDir, "results"),
  use: { ...sharedUse, baseURL: `http://127.0.0.1:${port}` },
  projects: viewportProjects,
  webServer: {
    command: "node e2e/static-server.mjs",
    url: `http://127.0.0.1:${port}/`,
    env: { E2E_PORT: String(port) },
    reuseExistingServer: false,
    timeout: 20_000,
  },
});
