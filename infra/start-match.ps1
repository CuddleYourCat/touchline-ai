<#
.SYNOPSIS
    Starts one execution of the simulator job with a fresh seed (ADR-024).

.DESCRIPTION
    The match ID comes from the seed, so a seed the processor has already seen
    is a redelivery test: every event is a duplicate and nothing changes. Pass
    a new seed for a new match.

    An execution can override the job's template, but the override replaces
    the whole template rather than merging with it. So this script reads the
    job's current template, sets the seed (and optionally the speed) of the
    first configured match, and starts the job with the result. The job's own
    configuration is unchanged.

    Needs Container Apps Jobs Operator (or Contributor) on job-simulator.

.PARAMETER Seed
    The seed for the match. Any whole number; the same seed replays the same match.

.PARAMETER Speed
    Optional. How many times faster than the match clock to publish. The job
    default is 10, about 9 minutes a match. At least 4: the job's replica
    timeout is 30 minutes (main.bicep), and a match plus added time runs to
    about 100 minutes of clock, so anything slower is killed part-way.

.PARAMETER ResourceGroup
    The resource group that holds the job (ADR-011).

.EXAMPLE
    ./infra/start-match.ps1 -Seed 101

.EXAMPLE
    ./infra/start-match.ps1 -Seed 102 -Speed 60
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Seed,

    [ValidateRange(4, 1000)]
    [double]$Speed,

    [string]$ResourceGroup = 'rg-touchline-ai'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Must match the name in main.bicep.
$JobName = 'job-simulator'

$json = az containerapp job show --name $JobName --resource-group $ResourceGroup --query properties.template -o json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
    throw "Could not read the '$JobName' job. Is it deployed, and are you signed in?"
}
$template = $json | ConvertFrom-Json

$overrides = [ordered]@{ 'Simulator__Matches__0__Seed' = "$Seed" }
if ($PSBoundParameters.ContainsKey('Speed')) {
    $overrides['Simulator__Matches__0__Speed'] = $Speed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

$container = $template.containers[0]
$variables = @($container.env | Where-Object { -not $overrides.Contains($_.name) })
foreach ($name in $overrides.Keys) {
    $variables += [pscustomobject]@{ name = $name; value = $overrides[$name] }
}
$container.env = $variables

# JSON is valid YAML, which is what --yaml expects.
$file = Join-Path ([System.IO.Path]::GetTempPath()) "touchline-ai-job-$([guid]::NewGuid()).yaml"
try {
    $template | ConvertTo-Json -Depth 20 | Set-Content -Path $file -Encoding utf8
    $execution = az containerapp job start --name $JobName --resource-group $ResourceGroup --yaml $file --query name -o tsv
    if ($LASTEXITCODE -ne 0) { throw "Could not start the job." }
}
finally {
    Remove-Item $file -ErrorAction SilentlyContinue
}

Write-Host "Started execution $execution with seed $Seed." -ForegroundColor Green
Write-Host "Follow it with:" -ForegroundColor DarkGray
Write-Host "  az containerapp job execution show --name $JobName --resource-group $ResourceGroup --job-execution-name $execution --query properties.status" -ForegroundColor DarkGray
