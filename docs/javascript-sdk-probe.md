# Sentry Node SDK probe

`tests/SdkProbes/Node` runs the real Sentry Node SDK against the disposable loopback `tests/MonitoringHost`. It covers the Node runtime used by Node-based desktop stacks, such as a desktop app's Node main process. It is not Electron, browser/renderer, React Native or Hermes evidence. No server symbolication, source-map upload or binary intake was added.

## Dependencies and licenses

Test-only, pinned in `package.json` + `package-lock.json`, installed by `npm ci --ignore-scripts` into ignored `bin/deps`:

- `@sentry/node` **10.77.1** (released 2026-10-27, MIT). Its full tree is MIT/Apache-2.0 with no install scripts: `@sentry/*`, OpenTelemetry, `import-in-the-middle`, `require-in-the-middle`, `debug`, `ms` and the module lexers.
- `typescript` 5.9.3 (Apache-2.0), used only to compile the synthetic fixture.

The 11.x line was not selected. `@sentry/node` 11.9.0 is MIT, but it depends on `@sentry/bundler-plugins`, which pulls in the `sentry` CLI 0.45.0 under FSL-1.1-Apache-2.0. Review that license before adopting 11.x.

## DSN finding

The JavaScript SDK DSN parser (`@sentry/core`, the same regex in 10.77.1 and 11.12.0) only accepts a public key matching `\w+`. Raw Aptabase app keys (`A-{region}-{digits}`, and the synthetic `TEST-APP-KEY`) contain hyphens. The SDK logged `Invalid Sentry Dsn` and sent nothing. Every Sentry JavaScript SDK that shares `@sentry/core` behaves this way.

The backend now returns per-project 64-character lowercase hex public keys, derived as described in `sentry-compatibility.md`, in the normal DSN string. The probe requires that form and has no parser bypass. It passes the provisioned DSN string unchanged to `Sentry.init({ dsn })`. It then asserts that the SDK's own parsed DSN (`client.getDsn()`) round-trips through `dsnToString` to the identical string, with the same public key and project. The SDK builds the transport URL itself, and the probe delivers through the SDK's `makeNodeTransport`. If the parser rejects the DSN, the probe fails with a precise message. Legacy raw-key DSNs, which the collector still accepts, remain unusable from JavaScript SDKs because the parser rejects them.

## What the probe asserts

`run.ps1` provisions the release DSN and requires `http://<64 hex>@127.0.0.1:55440/<project>`. It then runs `npm ci` (240 s bound) and compiles the fixture (60 s bound). After that it runs two single Node processes in sequence (45 s bound each, 128 MB heap, internal 30 s watchdog):

1. **Handled:** a caught exception thrown from the compiled fixture, plus a manual `node.startup`/`app.startup` transaction with one `file.read` child span. The probe requires exactly one `event` and one `transaction` envelope, each answered with HTTP 200 through the SDK's own `makeNodeTransport`. The runner then reads the stored records through the owner API and checks:
   - SDK version and `handled` kind
   - the source-mapped top frame `at loadSyntheticSettings (thrower.ts) line 12:11`, with no `thrower.js` frame
   - `[EMAIL_REDACTED]` in the message
   - transaction name and operation, and the span stored as top-level
   - no retained `private-probe` extra or span description, no synthetic email, no `app:///` paths
2. **Uncaught:** an exception thrown from `setImmediate`. The SDK's `onUncaughtExceptionIntegration` must flush and then exit with code 1. The runner requires one stored `unhandled`/`fatal` occurrence and no transaction.

Probe exit codes: 2 means an assertion failed, 3 means the watchdog expired. Each envelope is saved unmodified to `bin/envelopes` before it is sent, for diagnosis.

## Privacy and isolation settings

```js
Sentry.init({
  dsn, release, environment,
  sendDefaultPii: false, includeServerName: false, sendClientReports: false,
  defaultIntegrations: false, maxBreadcrumbs: 0, spotlight: false,
  registerEsmLoaderHooks: false, shutdownTimeout: 5000,
  integrations: [onUncaughtExceptionIntegration(), onUnhandledRejectionIntegration({ mode: 'strict' }),
                 rewriteFramesIntegration({ iteratee /* absolute path -> app:///relative */ })],
  tracesSampleRate, transport,
});
```

Turning off default integrations removes these from the defaults:

- process sessions, which would send unsupported `session` items
- `contextLines`, which reads local source files
- `localVariables`
- `nodeContext`, which collects OS, device and culture data
- `modules`, `console`, `http`, `fetch` and `childProcess` breadcrumbs and spans

The probe checks every envelope before sending it:

- **Item type:** exactly one `event` or `transaction` item. Anything else is held back and fails the run.
- **Release metadata:** release, environment and SDK version match the probe's settings.
- **Public key:** the envelope's public key matches the DSN.
- **Absent fields:** no `server_name`, `user`, `request`, `modules` or breadcrumbs.
- **Stack frames:** no source context, locals or absolute paths.
- **Machine identifiers:** none of the local host name, user name, home directory or probe directory appears in any string. These values are compared in memory only and are never printed or stored.

Envelopes smaller than 32 KiB are sent uncompressed, as the SDK does by default.

The probe ignores ambient configuration. It removes `SENTRY_*` and proxy variables before it loads the SDK, and the runner also strips `SENTRY_*` and `NODE_OPTIONS`/`NODE_PATH` from child processes. A manual run with a foreign `SENTRY_DSN`, `SENTRY_RELEASE`, `SENTRY_TRACES_SAMPLE_RATE` and an `http_proxy` still delivered only to the collector.

Network access is guarded in two places:

- **Socket guard:** `net.Socket.prototype.connect` refuses every TCP target except `127.0.0.1:55440`, and refuses every named-pipe or Unix-socket (IPC) path. `dgram.createSocket` is refused outright. The pipe test mirrors Node's own rules: a truthy `path` option, or a non-numeric string argument. Node's HTTP agent passes `path: null` for TCP, so HTTP connections are host/port checked. The probe needs no IPC exception. Inherited stdio pipes are wrapped from existing handles and never call `connect()`.
- **Transport guard:** the transport refuses any URL other than the DSN project's envelope endpoint carrying the DSN key.

Every run self-tests both guards against seven targets: a TEST-NET-1 TCP address, the wrong loopback port, a TEST-NET-1 `http.request`, a pipe path given as an option, a pipe path given as a string, a UDP socket, and a TEST-NET-1 transport URL. Each refusal throws before a socket is opened or a packet is sent.

The runner recreates only `bin/fixture` and `bin/envelopes`. Before it deletes anything, it checks three things, and refuses to delete if any check fails:

- the absolute path resolves strictly below the probe directory;
- no path component below the probe directory is a reparse point (junction or symlink);
- the tree to be deleted contains no reparse points. Windows PowerShell 5.1 `Remove-Item -Recurse` can traverse directory links.

Deletion then uses `Remove-Item -LiteralPath`. The `bin/deps` install target gets the same path check. A scratch self-test confirmed that all of these cases are refused, and that a file outside the probe directory survives:

- `../` escape
- absolute escape
- the probe root itself
- a junction as the target
- a path through a junction
- a nested junction

## Source-map boundary

`fixture/thrower.ts` is compiled by `tsc` into `bin/fixture` with an external map and no `sourcesContent`. The probe asserts that the map has no source content and that the compiled line differs from the source line (6 vs 12). It then calls `process.setSourceMapsEnabled(true)` before loading the compiled module. Node rewrites `Error.stack` from the local map, the SDK parses the original file, line and column, and `rewriteFrames` replaces the absolute path with `app:///fixture/thrower.ts`. The collector keeps only the basename and position.

This is client-side enrichment and needs the `.map` file on the client at runtime. It applies only to modules loaded after source maps are enabled, and it adds a cost to every `Error.stack` access. It does not prove Chromium renderer, bundled/minified, Hermes or ESM-loader-hook behaviour. Sentry debug IDs, artifact upload and server-side symbolication do not exist here.

## Run

Start the disposable host as described in `sdk-native-assessment.md`, then:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/SdkProbes/Node/run.ps1
```

## Evidence status (2026-11-11, Windows 11, Node 24.19.0, `@sentry/node` 10.77.1)

Strict mode **passed** against the disposable host built from backend commit `7f0685b`, with the derived-key DSN returned by the owner endpoint. The ordinary command above exited 0 with:

```
Sentry Node 10.77.1 on Node v24.19.0: event and transaction accepted (12:11 source-mapped from compiled line 6).
PASS: Sentry Node 10.77.1 on Node v24.19.0: handled source-mapped exception, actual uncaught process exception, transaction/span; loopback-only transport, privacy, scoped reads.
```

The run proved the following:

- **DSN:** the SDK's normal parser accepted the returned `http://<64 hex>@127.0.0.1:55440/<project>` string unchanged. The SDK's own transport delivered every envelope, each answered with HTTP 200. The envelope `trace.public_key` is the derived key.
- **Handled exception:** stored as `handled`, platform `node`, SDK `10.77.1`. The message has `[EMAIL_REDACTED]`. The stored top frame is `at loadSyntheticSettings (thrower.ts) line 12:11`, mapped locally from compiled line 6, with no `thrower.js` frame.
- **Transaction:** `node.startup`/`app.startup`, stored with one top-level `file.read` span.
- **Uncaught exception:** an actual exception thrown from `setImmediate`. The SDK flushed it and exited the process with code 1. It was stored as `unhandled`/`fatal`, with no transaction for that release.
- **Privacy:** no `private-probe` extra or span description, synthetic email, or `app:///` path was retained. The saved envelopes contain no local host name, user name, home directory or probe path. The fixture map has no `sourcesContent`.

Node's default uncaught-exception handler printed the probe's local stack trace to the runner's own stderr. That output stays local and is not part of any envelope.

An earlier strict run against the raw-key DSN failed: the SDK rejected the DSN and sent nothing. A removed diagnostic run, which handed the SDK pre-parsed DSN parts, is superseded and does not count as evidence.

Not verified: Electron main/renderer/preload, React Native, browser SDKs, other SDK versions, the 11.x line, gzip-encoded envelopes (over 32 KiB), offline queue/retry behaviour, unhandled-rejection capture, and macOS/Linux runtimes.
