# Backend V2 correspondence live-certification harness

A standalone console tool that exercises the exact same REST contract Unity's typed
`Level5.BackendV2` clients use (same routes, same camelCase JSON DTOs, same auth flow) against a
**real, running** Backend V2 instance. Written for
[`docs/backend-v2-correspondence-certification.md`](../../../../level5/docs/backend-v2-correspondence-certification.md)
in the `level5` repo (issue #159 certification slice) after two Unity-native approaches proved
unworkable in this environment:

- Unity **EditMode** `[UnityTest]`s never observe real `UnityWebRequest` completion in batchmode
  (confirmed empirically: a manually-polled request stayed `InProgress` for 600 frames against a
  reachable server). `CoroutineTestRunner`'s synchronous busy-loop is fine for `FakeApiTransport`
  (which completes on the first `MoveNext()`) but cannot pump real async network I/O.
- Unity **PlayMode** tests run in a real player loop (so real `UnityWebRequest` calls would work),
  but `Assets/Tests/PlayMode`'s assembly definition cannot reference the default assembly where
  `Level5.BackendV2.*` lives - a hard Unity engine restriction (see the comment on
  `Level5BackendV2CorrespondenceScenePlayModeTests.cs`), not a project choice.

This tool sidesteps both: it is not Unity code at all, just a plain `HttpClient` replicating the
identical wire contract (confirmed against `Level5.Api`'s own controller/DTO definitions). Two
independent `HttpClient`s - one per account - give genuine session isolation (including true
concurrent dispatch for the simultaneous-completion race check), which Unity's own client stack
cannot give in a single process since `BackendV2SessionStore` is a deliberate process-global
singleton (a single-local-player game only ever needs one active session).

## What it proves, and what it doesn't

This is **black-box server certification**: it proves Backend V2's actual HTTP behavior (auth,
idempotency, sealed-result projection, restart survival, concurrent-completion resolution) against
a live database. It does **not** exercise Unity's own `UnityWebRequestTransport`/`AuthApiClient`/
`CorrespondenceScreenController`/`RemoteAttemptLauncher` code paths - those still only have
`FakeApiTransport`-based coverage plus the existing PlayMode scene smoke test. A future session with
GUI/device access to actually drive the Unity client is still needed to certify the client-side
wiring against a live backend, not just the server's own contract.

## Usage

```powershell
# 1. Trust the local ASP.NET Core HTTPS dev cert (one-time per machine - without this, every call
#    in step 3 fails with "The SSL connection could not be established"; plain curl -k or
#    Invoke-WebRequest bypass/ignore this, but this tool's HttpClient honors the OS cert store):
dotnet dev-certs https --trust

# 2. Start Backend V2 locally (from the v2/ directory):
./scripts/setup-local-dev.ps1
cd src/Level5.Api
dotnet run --launch-profile https
# Confirm: curl -k https://localhost:7029/health/live

# 3. In another terminal, from this directory:
cd v2/scripts/live-certification
dotnet run -- phase1
```

Phase 1 registers two fresh accounts (unique per run), forms a friendship, creates and accepts a
Best-of-3 sealed challenge, plays game 1 (including duplicate-request idempotency checks and the
sealed-disclosure-timing check), and writes handoff state to
`%TEMP%\level5_backendv2_live_cert_state.json`.

Between phase 1 and phase 2, restart the backend process (kill it, run `dotnet run` again) to
exercise Scenario 6 (server restart between turns) for real:

```powershell
# find and stop the running backend process, then start it again as in step 2
dotnet run -- phase2
```

Phase 2 reconnects both accounts via fresh login (not a reused in-memory token), verifies the
series survived the restart unchanged, plays the deciding game 2 with a genuinely concurrent
`Task.WhenAll` completion dispatch from both accounts (Scenario 8), and verifies the completed
series remains readable in both accounts' history (Scenario 10).

Each phase asserts as it goes (`Expect(...)` throws on any unexpected status/shape) and prints a
`[CERT]` log line with the HTTP status, correlation id, and server trace id/title for every call,
plus an evidence summary of every id captured. A non-zero exit code means something did not match
expectations - read the thrown assertion message, not just the log tail.

## Known limitations

- Not wired into CI - it mutates real server/database state (new accounts, a played-out series) and
  needs a specific two-phase manual restart in between, so it is a manual certification tool, not an
  automated regression test.
- Simultaneous-completion (Scenario 8) concurrency is real (`Task.WhenAll` on two independent
  `HttpClient`s) but still originates from one machine/process; it does not simulate network-level
  jitter or truly independent client hardware.
- Assumes the `score-only` ruleset (the only entry in `StaticRulesetCatalog` at the time this was
  written) and always has account A win every game, to keep the Bo3 outcome deterministic. If the
  catalog gains more rulesets or a different outcome needs certifying, adjust `BuildMetrics`/the
  win/lose assignment per game accordingly.
