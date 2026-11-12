'use strict';
// Test-only acceptance probe for the real Sentry Node SDK against the disposable
// loopback MonitoringHost. Exit codes: 0 pass, 1 SDK uncaught-exception exit
// (expected only in "unhandled" mode), 2 probe assertion failure, 3 watchdog.
const fs = require('node:fs');
const os = require('node:os');
const net = require('node:net');
const http = require('node:http');
const dgram = require('node:dgram');
const path = require('node:path');
const { createRequire } = require('node:module');

const COLLECTOR_HOST = '127.0.0.1';
const COLLECTOR_PORT = 55440;
const SDK_VERSION = '10.77.1';
const probeDir = __dirname;
const binDir = path.join(probeDir, 'bin');
const envelopeDir = path.join(binDir, 'envelopes');
const mode = process.argv[2] === 'unhandled' ? 'unhandled' : 'handled';
const failures = [];
const deliveries = [];
const refusals = [];
let envelopeCount = 0;

setTimeout(() => {
  console.error('FAIL: probe watchdog expired');
  process.exit(3);
}, 30000).unref();

process.on('exit', () => {
  for (const refusal of refusals) failures.push(`refused ${refusal}`);
  for (const failure of failures) console.error(`FAIL: ${failure}`);
  if (failures.length > 0) process.exitCode = 2;
});

const dsn = process.env.APTABASE_PROBE_DSN ?? '';
const release = process.env.APTABASE_PROBE_RELEASE ?? '';
const dsnUrl = URL.canParse(dsn) ? new URL(dsn) : undefined;
if (!dsnUrl || dsnUrl.protocol !== 'http:' || dsnUrl.hostname !== COLLECTOR_HOST || dsnUrl.port !== String(COLLECTOR_PORT)
    || !/^[0-9a-f]{64}$/.test(dsnUrl.username) || dsnUrl.password || dsnUrl.search || dsnUrl.hash || !/^\/[0-9]+$/.test(dsnUrl.pathname)) {
  failures.push('only the disposable MonitoringHost DSN is allowed');
  process.exit(2);
}
if (!/^node-probe-[a-f0-9]{32}$/.test(release)) {
  failures.push('missing probe release');
  process.exit(2);
}

// The SDK reads SENTRY_* variables and proxy settings when options are omitted;
// ambient configuration must never redirect or alter probe telemetry.
for (const name of Object.keys(process.env)) {
  if (/^(SENTRY_|APTABASE_PROBE_)/i.test(name) || /^(https?|all|no)_proxy$/i.test(name)) delete process.env[name];
}

// Any connection other than the exact loopback collector is refused before it
// is attempted, including connections the SDK or OpenTelemetry might open.
// Named-pipe/Unix-socket (IPC) paths are refused too: the probe needs none, and
// inherited stdio pipes are wrapped from existing handles without connect().
const originalConnect = net.Socket.prototype.connect;
net.Socket.prototype.connect = function guardedConnect(...args) {
  let target = args[0];
  if (Array.isArray(target)) target = target[0];
  let host;
  let port;
  if (target !== null && typeof target === 'object') {
    // net treats any truthy path as a pipe; http's agent passes path: null for TCP.
    if (target.path) {
      refusals.push('IPC connection');
      throw new Error('Refused IPC connection');
    }
    host = target.host ?? 'localhost';
    port = Number(target.port);
  } else {
    // Mirrors Node's normalizeArgs: a non-numeric string is a pipe path.
    if (typeof target === 'string' && !/^[0-9]+$/.test(target)) {
      refusals.push('IPC connection');
      throw new Error('Refused IPC connection');
    }
    port = Number(target);
    host = typeof args[1] === 'string' ? args[1] : 'localhost';
  }
  if (host !== COLLECTOR_HOST || port !== COLLECTOR_PORT) {
    refusals.push(`network connection to ${host}:${port}`);
    throw new Error('Refused non-collector network connection');
  }
  return originalConnect.apply(this, args);
};
dgram.createSocket = () => {
  refusals.push('UDP socket');
  throw new Error('Refused UDP socket');
};

// Self-tests: each guard throws before any socket is opened or packet is sent.
// 192.0.2.1 is a TEST-NET-1 documentation address; the pipe name is unused.
const guardSelfTests = [
  ['non-loopback TCP', () => net.connect({ host: '192.0.2.1', port: 443 })],
  ['wrong loopback port', () => net.connect(COLLECTOR_PORT + 1, COLLECTOR_HOST)],
  ['non-collector HTTP request', () => http.request({ host: '192.0.2.1', port: 80, path: '/', agent: new http.Agent() })],
  ['IPC path option', () => net.connect({ path: '\\\\.\\pipe\\aptabase-probe-refused' })],
  ['IPC path string', () => net.createConnection('\\\\.\\pipe\\aptabase-probe-refused')],
  ['UDP socket', () => dgram.createSocket('udp4')],
];
for (const [label, attempt] of guardSelfTests) {
  const before = refusals.length;
  try {
    attempt();
    failures.push(`network guard allowed ${label}`);
  } catch {
    if (refusals.length !== before + 1) failures.push(`network guard did not record its ${label} refusal`);
  }
}
refusals.length = 0;

// Node applies source maps to Error.stack only for modules loaded after this.
process.setSourceMapsEnabled(true);
const fixtureSource = fs.readFileSync(path.join(probeDir, 'fixture', 'thrower.ts'), 'utf8').split(/\r?\n/);
const compiledSource = fs.readFileSync(path.join(binDir, 'fixture', 'thrower.js'), 'utf8').split(/\r?\n/);
const sourceMap = JSON.parse(fs.readFileSync(path.join(binDir, 'fixture', 'thrower.js.map'), 'utf8'));
const marker = 'SOURCE-MAP-PROBE-THROW';
const expectedLine = fixtureSource.findIndex(line => line.includes(marker)) + 1;
const expectedColumn = fixtureSource[expectedLine - 1].indexOf('new Error') + 1;
const compiledLine = compiledSource.findIndex(line => line.includes(marker)) + 1;
if (expectedLine < 1 || compiledLine < 1 || compiledLine === expectedLine) failures.push('fixture lines do not distinguish source-mapped positions');
if ('sourcesContent' in sourceMap) failures.push('fixture source map embeds source content');
const { loadSyntheticSettings } = require(path.join(binDir, 'fixture', 'thrower.js'));

const dependencies = createRequire(path.join(binDir, 'deps', 'package.json'));
const Sentry = dependencies('@sentry/node');
const { serializeEnvelope, dsnToString } = dependencies('@sentry/core');
if (Sentry.SDK_VERSION !== SDK_VERSION) failures.push(`unexpected SDK version ${Sentry.SDK_VERSION}`);

const forbiddenValues = [
  ['host name', os.hostname()],
  ['user name', os.userInfo().username],
  ['home directory', os.homedir()],
  ['probe directory', probeDir],
].filter(([, value]) => value && value.length >= 4).map(([label, value]) => [label, value.toLowerCase(), value.replace(/\\/g, '/').toLowerCase()]);

function visitStrings(value, visit) {
  if (typeof value === 'string') visit(value);
  else if (Array.isArray(value)) value.forEach(item => visitStrings(item, visit));
  else if (value !== null && typeof value === 'object') {
    for (const [key, item] of Object.entries(value)) {
      visit(key);
      visitStrings(item, visit);
    }
  }
}

function inspectEnvelope(header, items) {
  const types = items.map(([itemHeader]) => itemHeader.type);
  if (items.length !== 1 || !['event', 'transaction'].includes(types[0])) {
    failures.push(`unsupported envelope items [${types.join(',')}] were suppressed`);
    return false;
  }
  const body = items[0][1];
  for (const [label, lower, normalized] of forbiddenValues) {
    visitStrings([header, body], text => {
      const candidate = text.toLowerCase();
      if (candidate.includes(lower) || candidate.replace(/\\/g, '/').includes(normalized)) failures.push(`${types[0]} contains the local ${label}`);
    });
  }
  for (const field of ['server_name', 'user', 'request', 'modules']) {
    if (body[field] !== undefined) failures.push(`${types[0]} unexpectedly contains ${field}`);
  }
  if (Array.isArray(body.breadcrumbs) && body.breadcrumbs.length > 0) failures.push(`${types[0]} contains breadcrumbs`);
  if (body.release !== release || body.environment !== 'sdk-acceptance') failures.push(`${types[0]} release/environment mismatch`);
  if (header.trace?.public_key !== dsnUrl.username) failures.push(`${types[0]} envelope public key does not match the DSN`);
  if (body.sdk?.version !== SDK_VERSION) failures.push(`${types[0]} SDK metadata mismatch`);
  if (types[0] === 'event') inspectError(body);
  return true;
}

function inspectError(body) {
  const exception = body.exception?.values?.at(-1);
  const frames = exception?.stacktrace?.frames ?? [];
  for (const frame of frames) {
    for (const field of ['pre_context', 'context_line', 'post_context', 'vars', 'abs_path']) {
      if (frame[field] !== undefined) failures.push(`stack frame contains ${field}`);
    }
    if (/^([a-z]:[\\/]|[\\/]|file:)/i.test(frame.filename ?? '')) failures.push('stack frame contains an absolute path');
  }
  const top = frames.at(-1);
  if (mode === 'handled') {
    if (exception?.mechanism?.handled !== true) failures.push('handled exception mechanism mismatch');
    if (top?.function !== 'loadSyntheticSettings' || top?.filename !== 'app:///fixture/thrower.ts'
        || top?.lineno !== expectedLine || top?.colno !== expectedColumn) {
      failures.push(`source-mapped throw frame mismatch: ${JSON.stringify(top)}`);
    }
    if (frames.some(frame => (frame.filename ?? '').endsWith('thrower.js'))) failures.push('compiled fixture frame was not source-mapped');
  } else if (exception?.mechanism?.handled !== false || body.level !== 'fatal') {
    failures.push('uncaught exception was not marked fatal/unhandled by the SDK');
  }
}

function toAppFrame(frame) {
  const filename = frame.filename;
  if (!filename || !path.isAbsolute(filename)) return frame;
  const relative = path.relative(probeDir, filename);
  const inProbe = relative && !relative.startsWith('..') && !path.isAbsolute(relative);
  return { ...frame, filename: `app:///${(inProbe ? relative : path.basename(filename)).replace(/\\/g, '/')}` };
}

function collectorTransport(options) {
  const url = new URL(options.url);
  if (url.protocol !== 'http:' || url.hostname !== COLLECTOR_HOST || url.port !== String(COLLECTOR_PORT)
      || url.pathname !== `/api${dsnUrl.pathname}/envelope/` || url.searchParams.get('sentry_key') !== dsnUrl.username) {
    throw new Error('Refusing non-collector Sentry transport URL');
  }
  const transport = Sentry.makeNodeTransport(options);
  return {
    async send(envelope) {
      const [header, items] = envelope;
      const serialized = serializeEnvelope(envelope);
      fs.mkdirSync(envelopeDir, { recursive: true });
      const types = items.map(([itemHeader]) => itemHeader.type).join('+') || 'empty';
      fs.writeFileSync(path.join(envelopeDir, `${release}-${mode}-${envelopeCount++}-${types}.envelope`), serialized);
      if (!inspectEnvelope(header, items)) return {};
      try {
        const response = await transport.send(envelope);
        deliveries.push({ type: types, statusCode: response.statusCode });
        if (response.statusCode !== 200) failures.push(`collector returned ${response.statusCode ?? 'no status'} for ${types}`);
        return response;
      } catch (error) {
        deliveries.push({ type: types, statusCode: undefined });
        failures.push(`transport error for ${types}: ${error.message}`);
        throw error;
      }
    },
    flush: timeout => transport.flush(timeout),
  };
}

Sentry.init({
  dsn,
  release,
  environment: 'sdk-acceptance',
  debug: false,
  spotlight: false,
  sendDefaultPii: false,
  includeServerName: false,
  sendClientReports: false,
  registerEsmLoaderHooks: false,
  defaultIntegrations: false,
  integrations: [
    Sentry.onUncaughtExceptionIntegration(),
    Sentry.onUnhandledRejectionIntegration({ mode: 'strict' }),
    Sentry.rewriteFramesIntegration({ iteratee: toAppFrame }),
  ],
  maxBreadcrumbs: 0,
  tracesSampleRate: 1.0,
  shutdownTimeout: 5000,
  transport: collectorTransport,
});

try {
  collectorTransport({ url: 'http://192.0.2.1:55440/api/1/envelope/' });
  failures.push('transport guard allowed a non-collector URL');
} catch {}

const parsedDsn = Sentry.getClient()?.getDsn();
if (!parsedDsn) {
  failures.push(`Sentry ${Sentry.SDK_VERSION} rejected the provisioned DSN; its parser requires the public key to match /^\\w+$/ (letters, digits, underscore)`);
  process.exit(2);
}
if (dsnToString(parsedDsn) !== dsn || parsedDsn.publicKey !== dsnUrl.username || parsedDsn.projectId !== dsnUrl.pathname.slice(1)) {
  failures.push('SDK-parsed DSN does not round-trip to the provisioned DSN string');
  process.exit(2);
}

async function captureHandled() {
  Sentry.setExtra('discard-probe', 'private-probe-context');
  try {
    loadSyntheticSettings({ label: 'owner@example.test', retries: 0 });
  } catch (error) {
    Sentry.captureException(error);
  }
  await Sentry.startSpan({ name: 'node.startup', op: 'app.startup' }, () =>
    Sentry.startSpan({ name: 'private-probe-description', op: 'file.read' }, () => new Promise(resolve => setTimeout(resolve, 5))));
  if (!(await Sentry.flush(10000))) failures.push('SDK flush timed out');
  await Sentry.close(5000);
  const delivered = deliveries.filter(delivery => delivery.statusCode === 200).map(delivery => delivery.type).sort();
  if (delivered.join(',') !== 'event,transaction') failures.push(`expected one event and one transaction delivery, got [${delivered.join(',')}]`);
  if (failures.length === 0) console.log(`Sentry Node ${Sentry.SDK_VERSION} on Node ${process.version}: event and transaction accepted (${expectedLine}:${expectedColumn} source-mapped from compiled line ${compiledLine}).`);
}

if (mode === 'unhandled') {
  setImmediate(() => {
    throw new TypeError('Node uncaught probe owner@example.test');
  });
} else {
  captureHandled().catch(error => {
    failures.push(`probe error: ${error.message}`);
  });
}
