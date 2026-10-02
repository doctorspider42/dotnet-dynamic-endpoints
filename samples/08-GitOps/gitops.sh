#!/usr/bin/env bash
# A GitOps round trip with the dynamic-endpoints CLI of this repository, against the running sample:
#
#   dotnet run --project samples/08-GitOps          # in one terminal
#   ./samples/08-GitOps/gitops.sh                   # in another
#
# In CI you'd install the tool instead (dotnet tool install --global DynamicEndpoints.Cli) and call `dynamic-endpoints …`.
# The steps: list → diff (the plan, exit code 2 = differences) → push --sync → diff again (exit code 0) → export.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
file="$here/endpoints.yaml"

# The CLI reads the URL and the credentials from the environment, like it would in a pipeline.
export DYNAMIC_ENDPOINTS_URL="${DYNAMIC_ENDPOINTS_URL:-http://localhost:5108/api/ci/endpoints}"   # the admin API behind the API key
export DYNAMIC_ENDPOINTS_API_KEY="${DYNAMIC_ENDPOINTS_API_KEY:-ci-secret-key}"                    # Ci:ApiKey in appsettings.json

dotnet build "$repo/src/DynamicEndpoints.Cli" --verbosity quiet --nologo > /dev/null
cli() { dotnet run --project "$repo/src/DynamicEndpoints.Cli" --no-build -- "$@"; }

echo; echo "== 1. What's on the server"
cli list

echo; echo "== 2. The plan: what 'push --sync' would change (exit code 2 = there are differences)"
code=0; cli diff "$file" || code=$?
echo "exit code $code"
[ "$code" -eq 0 ] || [ "$code" -eq 2 ] || { echo "diff failed" >&2; exit "$code"; }

echo; echo "== 3. Apply it: create, update and delete until the server matches the file"
cli push "$file" --sync

echo; echo "== 4. Nothing left to do (exit code 0)"
code=0; cli diff "$file" || code=$?
echo "exit code $code"

echo; echo "== 5. Export the server's state – stable, sorted, with ids – ready to commit"
export_file="${TMPDIR:-/tmp}/dynamic-endpoints-export.yaml"
cli export -o "$export_file"
head -n 20 "$export_file"
echo "… written to $export_file"
