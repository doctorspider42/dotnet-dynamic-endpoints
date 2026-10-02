#!/usr/bin/env pwsh
# A GitOps round trip with the dynamic-endpoints CLI of this repository, against the running sample:
#
#   dotnet run --project samples/08-GitOps          # in one terminal
#   ./samples/08-GitOps/gitops.ps1                  # in another
#
# In CI you'd install the tool instead (dotnet tool install --global DynamicEndpoints.Cli) and call `dynamic-endpoints …`.
# The steps: list → diff (the plan, exit code 2 = differences) → push --sync → diff again (exit code 0) → export.

param(
    [string] $Url = 'http://localhost:5108/api/ci/endpoints',   # the admin API behind the API key
    [string] $ApiKey = 'ci-secret-key'                          # Ci:ApiKey in appsettings.json – a secret of the pipeline in real life
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '../..')
$file = Join-Path $PSScriptRoot 'endpoints.yaml'

# The CLI reads the URL and the credentials from the environment, like it would in a pipeline.
$env:DYNAMIC_ENDPOINTS_URL = $Url
$env:DYNAMIC_ENDPOINTS_API_KEY = $ApiKey

$cli = Join-Path $repo 'src/DynamicEndpoints.Cli'
dotnet build $cli --verbosity quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Building the CLI failed.' }

# Runs the CLI, shows its output and returns its exit code.
function Invoke-Cli {
    dotnet run --project $cli --no-build -- @args | Out-Host
    $LASTEXITCODE
}

Write-Host "`n== 1. What's on the server" -ForegroundColor Cyan
$code = Invoke-Cli list
if ($code -ne 0) { throw "list failed with exit code $code – is the sample running?" }

Write-Host "`n== 2. The plan: what 'push --sync' would change (exit code 2 = there are differences)" -ForegroundColor Cyan
$code = Invoke-Cli diff $file
Write-Host "exit code $code"
if ($code -notin 0, 2) { throw "diff failed with exit code $code" }

Write-Host "`n== 3. Apply it: create, update and delete until the server matches the file" -ForegroundColor Cyan
$code = Invoke-Cli push $file --sync
if ($code -ne 0) { throw "push failed with exit code $code" }

Write-Host "`n== 4. Nothing left to do (exit code 0)" -ForegroundColor Cyan
$code = Invoke-Cli diff $file
Write-Host "exit code $code"

Write-Host "`n== 5. Export the server's state – stable, sorted, with ids – ready to commit" -ForegroundColor Cyan
$export = Join-Path ([IO.Path]::GetTempPath()) 'dynamic-endpoints-export.yaml'
$code = Invoke-Cli export -o $export
if ($code -ne 0) { throw "export failed with exit code $code" }
Get-Content $export | Select-Object -First 20
Write-Host "… written to $export"
