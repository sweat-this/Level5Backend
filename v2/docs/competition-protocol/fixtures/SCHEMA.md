# Canonical compatibility fixture schema

These fixtures are the single source of truth for "what a completed exchange between Unity and
the Backend must mean." They describe **protocol-level semantics** — participants, commands, and
participant-visible outcomes — not any one side's persistence shape. Neither `Level5.Domain`'s
`VersusSeries`/`Score`/`AttemptStatus` types nor Unity's `VersusSeriesDocument`/`AttemptResult`
types appear here, and neither side's tests may assert against this file's shape directly; they
translate it into their own types first (see the two "Backend-executable" test files this
directory is paired with, and the Unity mirror described in
[`../README.md`](../README.md#16-compatibility-tests)).

A fixture is a JSON object:

```jsonc
{
  "fixtureId": "kebab-case-unique-id",
  "protocolVersion": 1,               // CompetitionProtocolVersion this fixture targets
  "description": "One sentence: what semantic this proves.",
  "backendExecutable": true,          // can dev-branch Backend (this issue's audit target) run this today?
  "requiresDownstream": [],           // issue refs that must land before this is backend-executable, e.g. ["#9", "#11"]
  "seriesFormat": { "totalGames": 3 },
  "ruleset": {
    "rulesetId": "opaque-string",
    "rulesetVersion": 1,
    "informationPolicy": "SealedAttempt | OpenTarget",
    "alternatesFirstAttempt": false,
    "comparisonKeys": [
      { "metric": "Score", "direction": "HigherWins | LowerWins" }
    ]
  },
  "commands": [
    // Applied strictly in order. "by" is a role, not a protocol id — each runner binds
    // "challenger"/"opponent" to its own two participant identities.
    { "op": "createChallenge", "by": "challenger" },
    { "op": "accept", "by": "opponent" },
    { "op": "decline", "by": "opponent" },
    { "op": "cancel", "by": "challenger" },
    { "op": "startAttempt", "by": "challenger", "game": 1 },
    { "op": "completeAttempt", "by": "challenger", "game": 1, "result": { "Score": 100 } },
    { "op": "completeAttemptExpectRejected", "by": "challenger", "game": 1, "result": { "Score": 1 } }
  ],
  "expected": {
    // Free-form per fixture: viewer-scoped visibility assertions, final series status/winner,
    // rejection expectations. See each fixture's own shape; the point is participant-visible
    // outcome, never an internal field name from either codebase.
  },
  "notes": "Why this fixture exists / what it would catch if broken."
}
```

`result` objects use the **named protocol metrics** from
[Competition Protocol V1 §10](../README.md#10-attempt-result-semantics) (`Score`,
`CompletionTimeSeconds`, `Accuracy`, ...), never a positional array (Unity's on-disk shape) or a
bare integer (the Backend's current `Score.Value`). A runner that can only carry one metric (the
Backend, today) reads `result.Score` and ignores the rest — that narrowing is exactly the gap
[`../README.md`](../README.md) records against issue #11.

Fixtures marked `"backendExecutable": false` are still canonical — they are the spec issues #9-#11
must satisfy — but nothing in `dev` can run them yet, so no Backend test claims they pass. See
[`../README.md` §10 "Compatibility tests"](../README.md#16-compatibility-tests) for exactly which
fixtures are wired to real code today, on which side.
