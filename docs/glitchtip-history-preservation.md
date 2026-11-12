# Preserving GlitchTip history before retirement

Assessment date: 2026-11-12. Only public official documentation/source was inspected. The owner's deployed version, storage configuration and private data were **not accessed**. No export, importer, migration or service shutdown was executed. This plan complements [the retirement matrix](glitchtip-retirement-readiness.md); it does not certify a replacement.

## Verified interfaces and limits

Source observations below are pinned to upstream commit `a75aed68d84d515a0de14b06a56bab41db661f35`, not asserted to match the owner's deployment or the latest release. The upstream [LICENSE](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/LICENSE) is MIT; no GlitchTip implementation was copied.

| Option | Evidence | Appropriate use / limitation |
| --- | --- | --- |
| Version-matched database and storage archive | Current [installation documentation](https://glitchtip.com/documentation/install/) describes PostgreSQL hot storage, optional DuckDB/Parquet cold storage in S3 or a local directory, and separate file-upload storage. | Recommended authoritative preservation path. PostgreSQL alone may omit retained cold events and files. Inventory the installed version's actual configuration before choosing backup commands. |
| Issue/event API | Pinned [issue routes](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/apps/issue_events/api/issues.py) and [event routes](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/apps/issue_events/api/events.py) provide scoped lists, details and individual event JSON. | Useful supplementary archive or selected diagnostic export, subject to the deployed version and permissions. Not a demonstrated complete historical export. |
| GlitchTip importer | [Importer implementation](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/apps/importer/importer.py) imports organization/user/project/key/team configuration. Its run path does not import issue/event history. | Not a GlitchTip-to-Aptabase history migration. Do not infer history support from its name. |
| Official CLI | The [documented CLI commands](https://glitchtip.com/documentation/cli/) cover issue management, release/deploy operations, logs, test events and symbol uploads. | No complete historical-export command is documented on that page. The CLI was not installed or tested here. |
| Re-send archived events to Aptabase ingestion | No compatible historical importer is implemented in this fork. Normal ingestion is designed for current bounded events. | **Do not use as migration.** It can change grouping, counts, resolution/recurrence state, retention and local alerts; it cannot preserve original issue links or all unsupported fields. |

The pinned [API router](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/glitchtip/api/api.py) mounts issue routes under `/api/0/`. Source-verified examples, **not tested against a live instance**:

- `GET /api/0/organizations/{organization}/issues/`
- `GET /api/0/organizations/{organization}/issues/{issue}/events/`
- `GET /api/0/projects/{organization}/{project}/events/{event}/`
- `GET /api/0/organizations/{organization}/issues/{issue}/events/{event}/json/`

In the inspected event implementation, list queries use hot tables while some detail/latest/JSON routes can fall back to cold storage. Therefore enumerating list responses and fetching JSON does not establish complete cold-history coverage. Preserve both GlitchTip's internal event ID and the SDK event ID; they are not interchangeable. Existing lifetime issue totals may exceed retained event rows after retention.

[Pagination source](https://gitlab.com/glitchtip/glitchtip-backend/-/raw/a75aed68d84d515a0de14b06a56bab41db661f35/glitchtip/api/pagination.py) uses cursor `Link` headers with `rel="next"` and `results="true"`; hit-count headers can be capped. Any future authorized exporter must follow same-origin cursors, detect cycles/truncation, bound response sizes and retries, record missing rows, and never forward credentials to redirects or foreign links. It must verify those semantics against the deployed version. No exporter was added on assumptions about that version.

## Recommended preservation and rollback procedure

1. **Owner-approved read-only inventory:** record deployed image digest/version, PostgreSQL version/extensions/schema, hot/cold retention, upload and cold-storage locations, relevant configuration names and required secret-store references. Record required historic links, statuses, attachments, releases and any other actually used features. Never put private events or secrets in this repo.
2. **Agree archive access and lifetime:** the smallest reliable option is an encrypted, version-matched archive that can restore the old service in isolation when historical access is needed. This preserves history without promising that old issues appear in the new one-login dashboard. If historical search under Aptabase login is required, that is a separate importer/read-only archive-view acceptance decision; it is not silently satisfied by a backup.
3. **Take consistent backups under approval:** database dump or verified snapshot, matching uploads/cold objects and manifests, deployment definitions, pinned images and securely stored necessary secrets. Coordinate writes, archival moves and retention jobs across stores to avoid a torn snapshot; a PostgreSQL snapshot alone does not coordinate object storage. Record timestamps, versions, counts and SHA256 manifests. Encrypt and restrict access using the owner's approved storage policy.
4. **Restore before retiring:** use an isolated private environment at the matching version, with no app ingestion, external network delivery, email/webhooks or scheduled retention/archival mutations. Compare database tables and storage manifests; inspect agreed old links, oldest/newest retained events, resolved states and files. Explain discrepancies from retention rather than forcing lifetime totals to equal retained rows. Record recovery time and missing items. A successful dump exit code alone is insufficient.
5. **Preserve a rollback path:** retain the previous application configuration in the owner's secret store and the exact compatible application/collector artifacts. Redirect ingestion only after the runtime and production gates pass. Keep old history intact throughout the observation window. Agree how to retain any new collector events if a rollback occurs.
6. **Approve retirement and deletion separately:** stopping ingestion, changing a production DSN, draining/stopping the old service, changing retention, copying private history and deleting databases/buckets/archives each need appropriate explicit owner authorization. None was performed here. Deletion is irreversible once the final usable backup expires; merely stopping the service need not delete history.

## Local evidence versus remaining work

The fork's synthetic PostgreSQL recovery test dumps/restores all 22 public tables and checks authorization, identity, deduplication, retention and monitoring data after restoration. A native ClickHouse backup/restore test preserves synthetic analytics/raw-error rows, both views and TTL definitions. The production acceptance test separately restores cookie-encryption key files and verifies an existing login after restart. None is a GlitchTip backup or proof of a deployed restore. The owner's GlitchTip storage inventory and a private historical restore remain unverified.

No live credentials, private data, symbols or source files were obtained. No external archive service was created. The concrete next history action is an owner-authorized version/storage inventory, followed by an isolated restore rehearsal; implementing a blind importer first would not establish preservation.
