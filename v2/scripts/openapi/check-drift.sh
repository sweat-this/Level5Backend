#!/usr/bin/env bash
# Fails if the committed canonical OpenAPI document (v2/openapi/level5-v2.openapi.json) does not
# match what the current V2 API code actually generates - i.e. a controller/route/DTO changed
# without regenerating the committed contract. Run in CI; safe to run locally too.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
v2_dir="$(cd "${script_dir}/../.." && pwd)"
committed_path="${v2_dir}/openapi/level5-v2.openapi.json"
fresh_path="$(mktemp -d)/level5-v2.openapi.json"

if [[ ! -f "${committed_path}" ]]; then
  echo "No committed OpenAPI document at ${committed_path}. Run generate.sh and commit the result." >&2
  exit 1
fi

dotnet run --project "${v2_dir}/tools/Level5.Api.OpenApiExport" -- "${fresh_path}"

if ! diff -u "${committed_path}" "${fresh_path}"; then
  echo "" >&2
  echo "Backend OpenAPI drift detected: the committed contract at v2/openapi/level5-v2.openapi.json" >&2
  echo "does not match what the current API code generates. Run v2/scripts/openapi/generate.sh and" >&2
  echo "commit the result." >&2
  exit 1
fi

echo "No OpenAPI drift: v2/openapi/level5-v2.openapi.json matches the current API."
