// Serves the production build (wwwroot) on loopback only, with SPA fallback.
// API requests never reach this server: the Playwright specs answer them from fixtures.
import { createReadStream, existsSync, statSync } from "node:fs";
import { createServer } from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../wwwroot");
const indexHtml = path.join(root, "index.html");
const port = Number(process.env.E2E_PORT);

if (!Number.isInteger(port) || port <= 0) {
  console.error("E2E_PORT must be set to a port number.");
  process.exit(1);
}

if (!existsSync(indexHtml)) {
  console.error(`Missing ${indexHtml}. Run "npm run build" before the acceptance tests.`);
  process.exit(1);
}

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".json": "application/json",
  ".txt": "text/plain; charset=utf-8",
};

function resolveFile(urlPath) {
  const decoded = decodeURIComponent(urlPath);
  const candidate = path.resolve(root, `.${decoded}`);
  if (!candidate.startsWith(root + path.sep)) return null;
  if (existsSync(candidate) && statSync(candidate).isFile()) return candidate;
  return null;
}

const server = createServer((req, res) => {
  if (req.method !== "GET" && req.method !== "HEAD") {
    res.writeHead(405).end();
    return;
  }

  const url = new URL(req.url ?? "/", "http://127.0.0.1");
  if (url.pathname.startsWith("/api/")) {
    res.writeHead(502, { "Content-Type": "text/plain" }).end("API requests must be served by test fixtures.");
    return;
  }

  let file;
  try {
    file = resolveFile(url.pathname);
  } catch {
    res.writeHead(400).end();
    return;
  }
  const isAsset = path.extname(url.pathname) !== "";
  if (!file && isAsset) {
    res.writeHead(404).end();
    return;
  }

  const target = file ?? indexHtml;
  res.writeHead(200, {
    "Content-Type": contentTypes[path.extname(target)] ?? "application/octet-stream",
    "Cache-Control": "no-store",
  });
  if (req.method === "HEAD") {
    res.end();
    return;
  }
  createReadStream(target).pipe(res);
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Serving ${root} at http://127.0.0.1:${port}`);
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
