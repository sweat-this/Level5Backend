# V2 OpenAPI contract

`level5-v2.openapi.json` in this directory is the canonical, committed OpenAPI document for the
Backend V2 API. It is generated, never hand-edited.

## Why a committed file, not just `/swagger/v1/swagger.json`

Swagger UI is only mapped in `Development` (see `Program.cs`) and is never exposed in production.
Consumers outside this repository - most notably `sweat-this/level5frontend` (issue #4) - need a
stable, reviewable contract they can pin to a specific commit without depending on a running
Backend V2 instance or its environment.

## Regenerating

```sh
v2/scripts/openapi/generate.sh
```

This boots the real `Level5.Api` host in-process (via `WebApplicationFactory<Program>`, the same
mechanism `Level5.Api.IntegrationTests` uses) with a syntactically valid but unreachable database
connection string and a throwaway JWT signing key, so it needs no running server, no reachable
Postgres, and no production configuration. It then reads `/swagger/v1/swagger.json` from that
in-process host and writes it, reformatted for stable diffs, to this file.

Run this after any change to a V2 controller, route, or request/response DTO, and commit the
result in the same change.

## Drift check (CI)

```sh
v2/scripts/openapi/check-drift.sh
```

Regenerates the document to a temp file and diffs it against the committed copy, failing if they
differ. This is what CI runs to enforce that a PR changing the API's public surface also updates
the committed contract - see `.github/workflows/ci.yml`.
