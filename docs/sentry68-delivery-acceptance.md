# Sentry 6.8 delivery and consent acceptance

These checks run the real **core Sentry .NET 6.8.0** serializer, HTTP transport and disk cache inside the existing xUnit test process. They neither launch the denied MAUI executable nor create a substitute UI/native-crash runner. No automatic WinUI exception, real process crash, device or current MultiShock runtime is exercised.

## HTTP rejection is different from transport failure

`HttpFailureSemanticsTests` uses a terminal `HttpMessageHandler` that cannot open sockets. The SDK's normal HTTP pipeline and cache remain active; results are not inferred from a custom `ITransport` alone.

| Stimulus | Verified result |
| --- | --- |
| HTTP500,503,429,403,413 or422 | Original event is removed from the SDK disk cache and is not replayed after SDK reinitialization. A new canary proves the reinitialized SDK can send. |
| HTTP503/429 with `Retry-After: 60` | The rejected original still is not durable/replayed. A retry header is not a promise that the client retains the event. |
| HTTP429 then a second capture in the same SDK lifetime | The second capture is suppressed before reaching the terminal handler during backoff, and no envelope remains in the disk cache. No client report describing these tested429 losses was observed. |
| HTTP500/503 then a successful subsequent send | SDK emits a `send_error` / `error` counter with quantity1. The collector parser accepts the actual serialized report. Reports can be separate metadata-only envelopes, not necessarily attached to the next error. |
| `HttpRequestException` before a response | Cache retains the event; SDK reinitialization replays the same ID and acknowledgment removes it. |

The pinned upstream [HTTP response handling](https://github.com/getsentry/sentry-dotnet/blob/6.8.0/src/Sentry/Http/HttpTransportBase.cs), [RetryAfterHandler](https://github.com/getsentry/sentry-dotnet/blob/6.8.0/src/Sentry/Internal/Http/RetryAfterHandler.cs) and [CachingTransport](https://github.com/getsentry/sentry-dotnet/blob/6.8.0/src/Sentry/Internal/Http/CachingTransport.cs) corroborate the observed distinction. No SDK source was copied or patched. These results apply to6.8.0 and the tested configuration, not every Sentry SDK/version.

**Operational consequence:** the collector's503 on metadata capacity exhaustion and500 on backend failure correctly indicate that the atomic transaction was not accepted. They do not guarantee that this SDK will retry. Never change rejection to200, silently drop required metadata, or partially acknowledge an envelope to hide this problem. Before cutover, review expected session/report volume against the documented per-app10,000row caps and1–30day retention, monitor rejections/storage, and explicitly accept the SDK's loss semantics or validate a bounded retry policy in an authorized application candidate. Client-side queues, SDK changes or a persistent server ingress queue would require their own privacy/capacity/recovery acceptance; none was invented here.

## Error filtering is not whole-SDK consent

`EnvelopeInventoryTests.BeforeSend_Error_OptOut_Does_Not_Suppress_Sessions_Or_Client_Reports` configures `BeforeSend => null`, session tracking and default client reports, then captures a synthetic error and explicitly ends the session. The error is absent, while actual SDK session updates and a `before_send` error counter are emitted.

The verified MultiShock source uses an error `BeforeSend` condition tied to `EnableAnalytics`; this test does **not** prove that its current runtime sends anything while opted out. It does prove that this callback alone is not sufficient to guarantee silence with metadata enabled. A future candidate must gate the SDK lifecycle/transport consistently with the application's consent policy and define what happens to already-cached reports when consent is revoked. Preserve meaningful session/report support when enabled; do not globally disable or silently discard it as a workaround. No MultiShock source or live configuration was changed.

## Mixed unhandled/crashed-session replay against the collector

`CollectorReplayTests` uses the existing disposable MonitoringHost at `http://127.0.0.1:55450`, PostgreSQL on55449 and normal project-scoped DSNs. The SDK transport refuses other origins, disables proxies/redirects, and only posts envelope routes. The test's owner requests are confined to that same loopback host. This fixture requires explicit `APTABASE_SDK_TEST_COLLECTOR`; without it, this one test skips rather than contacting another service.

The SDK constructs/serializes an event **explicitly marked** `synthetic.unhandled` / `Handled=false` and an individual session explicitly marked crashed. The method/basename/353:17 and SDK metadata are synthetic wire fixtures; they are not new Portable-PDB extraction or hook evidence. The tested sequence is:

1. Offline transport fails before opening a socket. Nothing reaches the collector; the mixed envelope remains on disk.
2. Reinitialization sends it to the real collector, then the test drops the acknowledgment. The database has one event and one crashed session, but the SDK retains the envelope for replay. Without an observed session start, eligible count is0 and crash-free rate is null.
3. Acknowledged replay leaves exactly one occurrence and one session. A late initial update fills start coverage; a subsequent exited update cannot downgrade the crash. Eligible count is1 and completed-session crash-free rate is0%.
4. The occurrence retains its original UUID, release/environment, `unhandled`, SDK6.8.0 and `at SyntheticManagedSite.ThrowSynthetic() (SyntheticManagedProbe.cs) line 353:17`. Private path, source text, email and session identity markers are absent from occurrence/health responses. Debug health remains separate.
5. Another SDK reinitialization sends nothing once acknowledged. Test-local cache is removed, and app settings are restored. The disposable host/database are stopped after acceptance.

This adds actual SDK6.8 serialization/cache-to-PostgreSQL evidence to the earlier hand-built mixed-envelope tests. It remains **same-process SDK reinitialization**, simulated transport failure and simulated acknowledgment loss. It does not close cross-process/abrupt-crash, real network, MAUI lifecycle or automatic WinUI gates. Client reports are disabled in this particular mixed replay test; their separate serialization/failure tests are identified above.

## Reproduce without any app launch

```powershell
# No sockets or services required for the new HTTP and consent tests:
dotnet test tests/SdkReliability --no-restore -m:1 --filter 'FullyQualifiedName~HttpFailureSemanticsTests|FullyQualifiedName~BeforeSend_Error_OptOut'
# Only with the separately started disposable MonitoringHost and synthetic database:
$env:APTABASE_SDK_TEST_COLLECTOR = 'http://127.0.0.1:55450/'
dotnet test tests/SdkReliability --no-restore -m:1
```

Do not substitute the prepared MAUI executable, another launcher or computer controls. Its approved single retry was denied before execution with only `blocked by policy`; no further launch was attempted. See the [retirement matrix](glitchtip-retirement-readiness.md) for owner/environment-dependent gates.
