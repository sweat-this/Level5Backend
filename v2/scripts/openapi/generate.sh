#!/usr/bin/env bash
# Regenerates the canonical V2 OpenAPI document in place at v2/openapi/level5-v2.openapi.json.
#
# Usage: v2/scripts/openapi/generate.sh
#
# Run this after any change to a V2 controller, route, or request/response DTO, then commit the
# result alongside that change - check-drift.sh (run in CI) fails the build otherwise.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
v2_dir="$(cd "${script_dir}/../.." && pwd)"
output_path="${v2_dir}/openapi/level5-v2.openapi.json"

dotnet run --project "${v2_dir}/tools/Level5.Api.OpenApiExport" -- "${output_path}"
