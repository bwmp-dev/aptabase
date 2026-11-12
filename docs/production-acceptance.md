# Self-hosted production acceptance

This increment tests the real Aptabase `Program` in **Production**, using an in-process HTTP test server, disposable PostgreSQL/ClickHouse and a local mail catcher. It does not use MonitoringHost's test-login route. No browser, MAUI process, public listener or external notification is involved.

## Configuration changes

- `BASE_URL` must now be an HTTPS origin outside Development, without credentials, path, query or fragment. Bare hostnames still normalize to HTTPS. Existing production HTTP or subpath configurations now fail startup with a generic message; migrate the public origin before updating. Development still permits HTTP. This validates configuration, not certificates or proxy trust.
- Self-hosted deployments can set `DATA_PROTECTION_KEYS_DIRECTORY` to an **absolute private persistent directory** for ASP.NET cookie-encryption keys. Existing default behavior remains when unset; managed-cloud AWS key persistence is unchanged. Use the same private key store across replicas of this application, not across unrelated applications. Back it up independently from PostgreSQL and keep application identity/content-root conventions consistent on restore.
- Explicit filesystem key persistence does **not** automatically encrypt key XML at rest. The operator must provide appropriate access restrictions, encrypted storage/backups and secret handling. No permissions were changed by this work. Keep `AUTH_SECRET` and other deployment secrets stable in the owner's secret store; do not commit keys or secret values. Losing cookie keys invalidates existing logins.
- Keep the backend private behind the intended TLS reverse proxy. The existing production middleware treats incoming requests as HTTPS; `BASE_URL` validation does not make a directly exposed HTTP listener safe. Validate the actual proxy, certificate, host routing and redirect behavior in staging.

## Liveness and readiness

`GET /healthz` remains process liveness. `GET /readyz` performs PostgreSQL `SELECT 1` and, when `CLICKHOUSE_URL` is configured, a ClickHouse `SELECT 1`. It returns plain `Healthy`/200 or `Unhealthy`/503 with no-store caching and no provider exception, endpoint or credential details. PostgreSQL connection/authentication has a separate two-second timeout; the health registration has a five-second cancellation deadline. Tests verify both refused and accepted-but-stalled connections return within eight seconds locally. Cancellation is not a hard real-time guarantee under process starvation.

Use readiness to remove unavailable instances from traffic, not to restart every process during a shared database outage. Avoid high-frequency probes: each check opens a bounded PostgreSQL connection without pooling. The anonymous route has a fixed generic response; restrict probe reachability/frequency at the hosting boundary as appropriate.

Readiness does **not** certify schema completeness, writable disk capacity, native-worker availability, backlog/retention progress, Tinybird, SMTP, external delivery, TLS or end-to-end SDK capture. Startup migrations and those operational checks remain separate. No external alert sender is enabled.

## Automated evidence

`ProductionConfigurationTests` covers origin validation and absolute key-directory configuration without creating directories. `ProductionAcceptanceTests` exercises:

- Real registration, mail-catcher magic link and login; Secure/HttpOnly/SameSite=Strict cookies; private response no-store.
- HSTS, frame denial, MIME-sniffing protection and CSP on the built application shell; no credentialed CORS response to a foreign origin on the account route.
- PostgreSQL/ClickHouse healthy, refused and stalled states; generic readiness failure while liveness stays healthy; recovery after restoring the connection.
- Copying generated key files to a fresh private test directory, restarting the production host and accepting the prior cookie; single-use magic-link replay refused.
- Logout sends an expired cookie; missing/tampered cookies return 401. This does **not** claim server-side revocation of every previously copied cookie.

All generated databases and key directories are task-owned and removed. Existing `MonitoringRecoveryTests` separately rehearses PostgreSQL dump/restore across all 22 public tables, including retention and authorization. CI now builds the web bundle before production-shell assertions and runs integration tests sequentially; remote CI has not been executed here.

An additional opt-in ClickHouse 26.8.36.3 rehearsal uses native `BACKUP DATABASE` / `RESTORE DATABASE ... AS` on synthetic data. All six repository migrations are applied to a fresh database. Analytics and raw-error rows match after restore; both aggregate views work, including after dropping the source database, and both TTL definitions remain. The archive is SHA256-hashed and removed along with both databases. This verifies schema/row recovery, not a 180-day wall-clock expiry or distributed/remote-storage recovery. It uses the cached container's existing allowed backup path; no server permissions/configuration were changed. See the official [ClickHouse backup/restore syntax](https://clickhouse.com/docs/concepts/features/backup-restore/overview).

To enable that test, set `APTABASE_ANALYTICS_RECOVERY_CONTAINER` to the full ID of a disposable ClickHouse container labelled `aptabase.test=production-acceptance`, with port8123 published only on127.0.0.1 and matching `APTABASE_TEST_CLICKHOUSE_URL`. It is deliberately skipped when no test container is selected. The current fixture expects the stock image's `/var/lib/clickhouse/backups` base directory. PostgreSQL recovery has its separate `APTABASE_RECOVERY_CONTAINER` guard. These tests never select a production database for backup.

Reproduction uses dedicated synthetic local services and existing test options:

```powershell
dotnet test tests/UnitTests --no-restore -m:1
# Set APTABASE_ISSUE_TEST_DATABASE_URL, APTABASE_TEST_CLICKHOUSE_URL,
# APTABASE_TEST_SMTP_PORT and APTABASE_TEST_MAILCATCHER_URL to disposable services.
dotnet test tests/IntegrationTests --no-restore -m:1 --settings tests/IntegrationTests/sequential.runsettings --filter FullyQualifiedName~ProductionAcceptanceTests
```

Remaining production gates: actual HTTPS/proxy/mail deployment; operational recovery including ClickHouse and persistent cookie keys at the deployed identity; capacity/load and worker/storage topology; owner-selected retention; real application SDK behavior. See the [retirement matrix](glitchtip-retirement-readiness.md) and [history preservation assessment](glitchtip-history-preservation.md). Passing this test does not authorize deployment or retirement.
