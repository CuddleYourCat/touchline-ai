<#
.SYNOPSIS
    Previews, deploys or tears down the touchline-ai Azure environment.

.DESCRIPTION
    The single entry point for every operation against infra/main.bicep
    (ADR-012), so the deployment procedure is version-controlled.

    Nothing personal is ever stored in the template, a parameter file or this
    script, because the repository is public. Who is deploying is worked out
    at run time:
      - A user login gets developer roles, and budget alerts go to the user's
        sign-in email.
      - A service principal login (CI) gets no developer roles, and budget
        alerts go to -OwnerEmail or the BUDGET_ALERT_EMAIL environment variable.

    Before every whatif or deploy, the script reads the live state that the
    template would otherwise reset: the running Match Centre image, its ingress
    traffic rules (including revision labels such as "judging", ADR-009), the
    simulator job's image and the budget's start date. Redeploying the
    infrastructure therefore never rolls an app or the job back to the
    placeholder or drops a revision label.

.PARAMETER Action
    whatif    Preview changes without applying them. Run it before every deploy
              and read the summary line.
    deploy    Create the resource group if it's missing, then apply the template.
    teardown  Delete the whole resource group, then purge the Key Vault and
              Foundry account it held, so their names are released.
              Destructive; asks for confirmation unless -Force is supplied.
    principal Print who the deployment would run as, then exit.
    teardown-search
              Delete only the Azure AI Search service (the roughly 2 pounds a day
              resource), leaving everything else. Asks for confirmation unless
              -Force is supplied. Follow it with '-Action deploy -NoSearch' so the
              apps lose their Search__Endpoint setting and a deploy doesn't
              recreate the service.
    teardown-redis
              Delete only Azure Managed Redis (ADR-061, billed by the hour),
              leaving everything else. Asks for confirmation unless -Force is
              supplied. Follow it with '-Action deploy -NoRedis' so the Match
              Centre loses its MatchCentre__Redis__Endpoint setting and a deploy
              doesn't recreate the cache. The app carries on without it: the
              caches stay in memory and the fan-match limits in Cosmos DB.
    create-index
              Create or update the Azure AI Search index from
              infra/search/possessions-index.json (ADR-040). Needs the Search
              service (deploy with -DeploySearch first) and the Search Service
              Contributor role, which the deploy grants to the signed-in user.
    wipe-data Delete every stored match and everything built from one: the
              Cosmos DB containers (matches, events, commentary, recaps, the
              season projections, fan teams, fan match requests and the change
              feed leases) and the Search index. Then redeploy to recreate them
              empty, recreate the index and restart the apps. The Match Centre
              shows a maintenance page (ADR-060) from before the first delete to
              the end of the script, and the wipe stops if that page doesn't
              appear. Works through the control plane, so it needs no Cosmos DB
              data role. Refuses while a simulator execution is running, or while
              traffic is pinned to a revision. Destructive; asks for confirmation
              unless -Force is supplied. Takes the same feature switches as deploy.

.PARAMETER ResourceGroup
    Target resource group (ADR-011).

.PARAMETER Location
    Azure region for a new resource group, and for purging soft-deleted resources.

.PARAMETER MatchCentreImage
    Optional. The Match Centre image to deploy, for example
    crtouchline<suffix>.azurecr.io/matchcentre:<tag>. Leave it out to keep the
    image that's already running (or the placeholder on a first deploy).

.PARAMETER OwnerEmail
    Optional. Where budget alerts go. Defaults to the signed-in user's email;
    required (or BUDGET_ALERT_EMAIL) when signed in as a service principal.

.PARAMETER DeploySquadModels
    Optional. Deploy the squad's chat models, their content filter and the
    Squad__* app settings (ADR-051). Needed only for the first deploy that adds
    them: once the small chat deployment exists, every later run keeps the
    models on without it. On a fresh group, run once without it, then again
    with it, so a model quota failure can't hold up the rest.

.PARAMETER NoSquadModels
    Optional. Force the models off, which deletes the chat deployments, their
    content filter and the Squad__* settings. Can't be combined with
    -DeploySquadModels.

.PARAMETER SmallModelCapacity
    Optional. Tokens-per-minute limit, in thousands, for the small chat model.
    Leave it out for the template's default. Raise it only for demo days, then
    redeploy without it to lower it again (ADR-051).

.PARAMETER LargeModelCapacity
    Optional. The same for the large chat model.

.PARAMETER DeploySearch
    Create the Azure AI Search service (Basic, about 2 pounds a day, ADR-026).
    Off by default. Once the service exists, later deploys keep it without the
    switch, so a redeploy never drops the Search__Endpoint app settings.

.PARAMETER NoSearch
    Force deploySearch off, even when the Search service exists, so the deploy
    leaves the Search__Endpoint app settings out. Run it after
    '-Action teardown-search' so later deploys don't recreate the service.
    Cannot be combined with -DeploySearch.

.PARAMETER SearchLocation
    Region for the Azure AI Search service only (ADR-058). Defaults to uksouth,
    because Sweden Central had no Search capacity. Once the service exists, the
    script keeps its live region and warns if this value differs. Moving it to
    another region means tearing it down first ('-Action teardown-search').

.PARAMETER DeployNameGuard
    Point the Match Centre at Content Safety for the team builder's name guard
    (ADR-049). Load the blocklist first (infra/load-name-blocklist.ps1): the guard
    fails closed, so with it on and no blocklist, every team create is a 503. Once
    it's on, later deploys keep it without the switch, so a redeploy can't quietly
    drop back to the format-only guard.

.PARAMETER NoNameGuard
    Force the name guard off (the format-only guard). Can't be combined with
    -DeployNameGuard.

.PARAMETER DeployFanMatches
    Give the Match Centre the simulator job's ID, which turns fan matches on
    (ADR-050). The limits (one live, five a day) are built in. Once it's on, later
    deploys keep it without the switch.

.PARAMETER NoFanMatches
    The kill switch: force fan matches off, so POST /api/matches answers 503.
    Can't be combined with -DeployFanMatches.

.PARAMETER DeployAppConfig
    Create the Azure App Configuration store (Free tier, keyless, ADR-062) and
    give both apps its endpoint, so feature flags and the squad's settings can
    change with no redeploy. The seed values and flags are written only when the
    store is first created. Once it's on, later deploys keep it without the
    switch. Free stores are limited to three per region per subscription. The
    seed values are written only by a user login, who is given App Configuration
    Data Owner; a service principal run creates the store but leaves it empty.
    A flag in the store can't be overridden by FeatureManagement__<Flag>=false
    (the store's schema wins). To hold a flag off whatever the store says, set
    Touchline__Kill__<Flag>=true on the app, for example
    'az containerapp update -n ca-matchcentre -g rg-touchline-ai --set-env-vars Touchline__Kill__FanQa=true'.
    For fan matches, -NoFanMatches is the other hold-fast switch.

.PARAMETER NoAppConfig
    Force App Configuration off: the apps lose their AppConfiguration__Endpoint
    setting and run on their appsettings defaults. The store itself is left in
    place (the template never deletes it). Can't be combined with -DeployAppConfig.

.PARAMETER ReseedAppConfig
    Write the seed key-values and feature flags again, even though the store
    exists. This resets every seeded value to its default, including a flag
    flipped in the portal, so it's off unless asked for. Use it if the first
    deploy's seeding failed (the developer's role can take a minute to apply).

.PARAMETER DeployRedis
    Create Azure Managed Redis (Balanced B0, high availability off, ADR-061)
    and give the Match Centre its endpoint, for the name-guard verdicts, the
    finished-match output cache and the fan-match limits. Billed by the hour, so
    record a teardown date in the resource ledger. Once the cache exists, later
    deploys keep it without the switch.

.PARAMETER NoRedis
    Force deployRedis off, even when the cache exists, so the deploy leaves
    MatchCentre__Redis__Endpoint out. Run it after '-Action teardown-redis'.
    Can't be combined with -DeployRedis.

.PARAMETER Force
    Skip the teardown confirmation. Intended for scripted use only.

.EXAMPLE
    ./infra/deploy.ps1 -Action whatif

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -DeployNameGuard -DeployFanMatches

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -DeployAppConfig
    ./infra/deploy.ps1 -Action deploy -DeployRedis

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -DeploySquadModels

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -MatchCentreImage crtouchline2ryqfwse6uqn4.azurecr.io/matchcentre:abc123
#>

#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('whatif', 'deploy', 'teardown', 'principal', 'create-index', 'teardown-search', 'teardown-redis', 'wipe-data')]
    [string]$Action,

    [string]$ResourceGroup = 'rg-touchline-ai',

    [string]$Location = 'swedencentral',

    [string]$MatchCentreImage,

    [string]$OwnerEmail,

    [switch]$DeploySquadModels,

    [switch]$NoSquadModels,

    [ValidateRange(1, 10000)]
    [int]$SmallModelCapacity,

    [ValidateRange(1, 10000)]
    [int]$LargeModelCapacity,

    [switch]$DeploySearch,

    [switch]$NoSearch,

    [ValidateNotNullOrEmpty()]
    [string]$SearchLocation = 'uksouth',

    [switch]$DeployNameGuard,

    [switch]$NoNameGuard,

    [switch]$DeployFanMatches,

    [switch]$NoFanMatches,

    [switch]$DeployAppConfig,

    [switch]$NoAppConfig,

    [switch]$ReseedAppConfig,
    [switch]$DeployRedis,

    [switch]$NoRedis,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DeploySearch -and $NoSearch) { throw "-DeploySearch and -NoSearch contradict each other." }
if ($DeployNameGuard -and $NoNameGuard) { throw "-DeployNameGuard and -NoNameGuard contradict each other." }
if ($DeployFanMatches -and $NoFanMatches) { throw "-DeployFanMatches and -NoFanMatches contradict each other." }
if ($DeployAppConfig -and $NoAppConfig) { throw "-DeployAppConfig and -NoAppConfig contradict each other." }
if ($ReseedAppConfig -and $NoAppConfig) { throw "-ReseedAppConfig and -NoAppConfig contradict each other." }
if ($DeployRedis -and $NoRedis) { throw "-DeployRedis and -NoRedis contradict each other." }

$TemplateFile = Join-Path $PSScriptRoot 'main.bicep'

# Must match the fixed names in main.bicep.
$MatchCentreAppName = 'ca-matchcentre'
$SimulatorJobName = 'job-simulator'
$BudgetName = 'budget-touchline-ai'
$BudgetApiVersion = '2023-11-01'
$SmallModelDeploymentName = 'gpt-5.4-mini' # smallModel.name

if ($DeploySquadModels -and $NoSquadModels) {
    throw "-DeploySquadModels and -NoSquadModels can't be used together."
}

# Azure AI Search (ADR-040, ADR-041). The index definition is committed beside this script.
$SearchIndexFile = Join-Path $PSScriptRoot 'search/possessions-index.json'
$SearchIndexName = 'possessions'
$SearchDataApiVersion = '2024-07-01'

# Maintenance mode (ADR-060). Must match MaintenanceOptions.SectionName and MaintenanceMode.HeaderName.
# While $script:MaintenanceOn is set, every deploy passes maintenanceMode=true, so the wipe's own
# redeploy can't turn the page off part way.
$MaintenanceSetting = 'MatchCentre__Maintenance__Enabled'
$MaintenanceHeader = 'X-Touchline-Maintenance'
$script:MaintenanceOn = $false

# Must match CosmosNames.cs and main.bicep's cosmosContainerDefinitions. Every container that holds
# a match, something built from one, or a checkpoint into one. wipe-data deletes these, and the
# redeploy that follows recreates them empty.
$CosmosDatabaseName = 'touchline'
$WipedContainers = @(
    'matches', 'events', 'commentary', 'recaps',
    'player-matches', 'player-seasons',
    'teams', 'match-requests',
    'leases'
)

# ── Who is deploying ──────────────────────────────────────────────────────────

function Get-Deployer {
    $account = az account show --query "{type: user.type, name: user.name, subscription: id}" -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $null -eq $account) {
        throw "Not signed in to Azure. Run 'az login' first."
    }

    if ($account.type -eq 'user') {
        $id = az ad signed-in-user show --query id -o tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($id)) {
            throw "Could not resolve the signed-in user's object ID."
        }
        $email = if ($OwnerEmail) { $OwnerEmail } else { $account.name }
        return [pscustomobject]@{ Kind = 'user'; PrincipalId = $id.Trim(); Email = $email; Subscription = $account.subscription }
    }

    # A service principal (CI) has no developer to grant roles to, and its
    # user.name is an app ID rather than an email address.
    $email = if ($OwnerEmail) { $OwnerEmail } else { $env:BUDGET_ALERT_EMAIL }
    if ([string]::IsNullOrWhiteSpace($email)) {
        throw "Signed in as a service principal: pass -OwnerEmail or set BUDGET_ALERT_EMAIL for budget alerts."
    }
    return [pscustomobject]@{ Kind = 'servicePrincipal'; PrincipalId = ''; Email = $email; Subscription = $account.subscription }
}

# ── Live state the template would otherwise reset ─────────────────────────────

# Returns $null when the resource doesn't exist yet (first deploy).
function Get-LiveResource {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Query,
        [string]$ApiVersion
    )

    $arguments = @('resource', 'show', '--ids', $Id, '--query', $Query, '-o', 'json')
    if ($ApiVersion) { $arguments += @('--api-version', $ApiVersion) }

    $json = az @arguments 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }
    return $json | ConvertFrom-Json
}

# Whether the squad's models stay on (ADR-051): the switches decide if given; otherwise they stay
# on when the small chat deployment already exists, so a deploy that forgets -DeploySquadModels
# can't delete them. A lookup that fails throws, rather than guessing "off".
function Resolve-SquadModels {
    if ($NoSquadModels) { return [pscustomobject]@{ On = $false; Why = 'off (-NoSquadModels)' } }
    if ($DeploySquadModels) { return [pscustomobject]@{ On = $true; Why = 'on (-DeploySquadModels)' } }

    $exists = az group exists --name $ResourceGroup
    if ($LASTEXITCODE -ne 0) { throw "Could not check whether '$ResourceGroup' exists, so can't tell whether the squad models are deployed." }
    if ($exists -ne 'true') { return [pscustomobject]@{ On = $false; Why = 'off (new resource group)' } }

    $accounts = @(az resource list --resource-group $ResourceGroup --resource-type Microsoft.CognitiveServices/accounts --query "[].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list the Foundry accounts in '$ResourceGroup', so can't tell whether the squad models are deployed." }

    foreach ($account in $accounts | Where-Object { $_ }) {
        $found = az cognitiveservices account deployment list --resource-group $ResourceGroup --name $account `
            --query "[?name=='$SmallModelDeploymentName'].name" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Could not list the deployments on '$account', so can't tell whether the squad models are deployed." }
        if (-not [string]::IsNullOrWhiteSpace($found)) {
            return [pscustomobject]@{ On = $true; Why = "on (kept: $SmallModelDeploymentName is deployed)" }
        }
    }

    return [pscustomobject]@{ On = $false; Why = 'off (not deployed yet; pass -DeploySquadModels to add them)' }
}

# Whether a Match Centre feature stays on: the switches decide if given; otherwise it stays on when
# the live app already has the setting the feature adds. A first deploy (no app yet) starts it off.
function Resolve-Feature {
    param(
        [switch]$On,
        [switch]$Off,
        [string[]]$LiveEnv,
        [Parameter(Mandatory)][string]$Setting,
        [Parameter(Mandatory)][string]$OnSwitch,
        [Parameter(Mandatory)][string]$OffSwitch
    )

    if ($Off) { return [pscustomobject]@{ On = $false; Why = "off ($OffSwitch)" } }
    if ($On) { return [pscustomobject]@{ On = $true; Why = "on ($OnSwitch)" } }
    if ($LiveEnv -contains $Setting) { return [pscustomobject]@{ On = $true; Why = "on (kept: the Match Centre has $Setting)" } }
    return [pscustomobject]@{ On = $false; Why = "off (pass $OnSwitch to turn it on)" }
}

function Get-TemplateParameterFile {
    param([Parameter(Mandatory)]$Deployer)

    $groupId = "/subscriptions/$($Deployer.Subscription)/resourceGroups/$ResourceGroup"
    $values = [ordered]@{
        principalId = $Deployer.PrincipalId
        ownerEmail  = $Deployer.Email
    }

    $app = Get-LiveResource -Id "$groupId/providers/Microsoft.App/containerApps/$MatchCentreAppName" `
        -Query "{image: properties.template.containers[0].image, traffic: properties.configuration.ingress.traffic, env: properties.template.containers[0].env[].name}"
    if ($MatchCentreImage) {
        $values.matchCentreImage = $MatchCentreImage
    }
    elseif ($app) {
        $values.matchCentreImage = $app.image
    }
    if ($app -and $app.traffic) {
        $values.matchCentreTraffic = @($app.traffic)
    }

    # CI moves the job to each new simulator image (ADR-024), so keep whichever is live.
    $simulatorImage = Get-LiveResource -Id "$groupId/providers/Microsoft.App/jobs/$SimulatorJobName" `
        -Query "properties.template.containers[0].image"
    if ($simulatorImage) {
        $values.simulatorImage = $simulatorImage
    }

    $budgetStart = Get-LiveResource -Id "$groupId/providers/Microsoft.Consumption/budgets/$BudgetName" `
        -Query "properties.timePeriod.startDate" -ApiVersion $BudgetApiVersion
    if ($budgetStart) {
        # ConvertFrom-Json may already have turned it into a local DateTime; normalise to UTC.
        $values.budgetStartDate = ([datetime]$budgetStart).ToUniversalTime().ToString('yyyy-MM-dd')
    }

    # The squad's models (ADR-051). A capacity left out takes the template's default.
    $squad = Resolve-SquadModels
    $values.deploySquadModels = $squad.On
    if ($SmallModelCapacity) { $values.smallModelCapacity = $SmallModelCapacity }
    if ($LargeModelCapacity) { $values.largeModelCapacity = $LargeModelCapacity }
    $capacity = if ($squad.On) {
        ", small $(if ($SmallModelCapacity) { "${SmallModelCapacity}K" } else { 'default' }), large $(if ($LargeModelCapacity) { "${LargeModelCapacity}K" } else { 'default' }) TPM"
    } else { '' }

    # Keep the Search service once it exists: dropping the flag would remove the app settings.
    # A failed lookup must stop the run: treating it as "no service" would strip Search__Endpoint
    # from both apps. A group that doesn't exist yet (first deploy) has nothing to keep.
    $searchLive = $null
    $searchRegion = $SearchLocation
    if (-not $NoSearch -and (az group exists --name $ResourceGroup) -eq 'true') {
        $searchRow = az resource list --resource-group $ResourceGroup --resource-type Microsoft.Search/searchServices `
            --query "[0].[name, location]" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Could not check whether the Search service exists. Re-run, or pass -NoSearch to leave it out." }
        if (-not [string]::IsNullOrWhiteSpace($searchRow)) {
            $searchLive, $liveRegion = ($searchRow -split "`t").Trim()
            # A Search service can't change region in place, so a different value would fail the
            # whole deployment. Keep the live region; moving it needs teardown-search first (ADR-058).
            if ($liveRegion -and $liveRegion -ne $SearchLocation) {
                Write-Warning "Search service '$searchLive' lives in $liveRegion, not $SearchLocation. Keeping $liveRegion; run '-Action teardown-search' first to move it."
                $searchRegion = $liveRegion
            }
        }
    }
    $values.deploySearch = if ($NoSearch) { $false } else { [bool]($DeploySearch -or (-not [string]::IsNullOrWhiteSpace($searchLive))) }
    $values.searchLocation = $searchRegion # ADR-058

    # Keep Azure Managed Redis once it exists (ADR-061), as Search above: dropping the flag would
    # remove the Match Centre's MatchCentre__Redis__Endpoint. A failed lookup stops the run.
    $redisLive = $null
    if (-not $NoRedis -and (az group exists --name $ResourceGroup) -eq 'true') {
        $redisLive = az resource list --resource-group $ResourceGroup --resource-type Microsoft.Cache/redisEnterprise --query "[0].name" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Could not check whether Azure Managed Redis exists. Re-run, or pass -NoRedis to leave it out." }
    }
    $values.deployRedis = if ($NoRedis) { $false } else { [bool]($DeployRedis -or (-not [string]::IsNullOrWhiteSpace($redisLive))) }

    # The name guard (ADR-049) and fan matches (ADR-050): on when switched on, and kept on when the
    # live Match Centre already has the setting each one adds, so a redeploy that forgets the switch
    # can't turn the guard back into the format-only check or switch fan matches off.
    $liveEnv = if ($app -and $app.env) { @($app.env) } else { @() }
    $nameGuard = Resolve-Feature -On:$DeployNameGuard -Off:$NoNameGuard -LiveEnv $liveEnv `
        -Setting 'MatchCentre__ContentSafety__Endpoint' -OnSwitch '-DeployNameGuard' -OffSwitch '-NoNameGuard'
    $fanMatches = Resolve-Feature -On:$DeployFanMatches -Off:$NoFanMatches -LiveEnv $liveEnv `
        -Setting 'MatchCentre__FanMatches__JobResourceId' -OnSwitch '-DeployFanMatches' -OffSwitch '-NoFanMatches'
    $values.deployNameGuard = $nameGuard.On
    $values.deployFanMatches = $fanMatches.On

    # App Configuration (ADR-062): on when switched on, and kept on while the live Match Centre has the
    # endpoint setting. The seed values are written only when the store doesn't exist yet (or with
    # -ReseedAppConfig), because deploying the template again resets every seeded value, which would undo a
    # flag flipped in the portal. A failed lookup stops the run rather than guessing.
    $appConfig = Resolve-Feature -On:$DeployAppConfig -Off:$NoAppConfig -LiveEnv $liveEnv `
        -Setting 'AppConfiguration__Endpoint' -OnSwitch '-DeployAppConfig' -OffSwitch '-NoAppConfig'
    $appConfigExists = $false
    if ($appConfig.On -and (az group exists --name $ResourceGroup) -eq 'true') {
        $storeName = az resource list --resource-group $ResourceGroup --resource-type Microsoft.AppConfiguration/configurationStores `
            --query "[0].name" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Could not check whether the App Configuration store exists. Re-run, or pass -NoAppConfig to leave it out." }
        $appConfigExists = -not [string]::IsNullOrWhiteSpace($storeName)
    }
    $values.deployAppConfig = $appConfig.On
    $values.seedAppConfig = [bool]($ReseedAppConfig -or -not $appConfigExists)
    if ($appConfig.On -and $values.seedAppConfig -and -not $Deployer.PrincipalId) {
        # A service principal gets no Data Owner role (principalId is empty), so it can't write key-values.
        Write-Warning "Deploying as a service principal: the App Configuration store is created but its seed values and flags are not written. Run a user deploy with -ReseedAppConfig to seed them."
        $values.seedAppConfig = $false
    }
    $values.maintenanceMode = [bool]$script:MaintenanceOn

    Write-Host "  Deploying as: $($Deployer.Kind)" -ForegroundColor DarkGray
    Write-Host "  Squad models: $($squad.Why)$capacity" -ForegroundColor DarkGray
    Write-Host "  Azure AI Search: $(if ($values.deploySearch) { "deployed in $searchRegion (about 2 pounds a day)" } else { 'not deployed' })" -ForegroundColor DarkGray
    $redisWhy = if ($NoRedis) { 'off (-NoRedis)' } elseif ($DeployRedis) { 'on (-DeployRedis, billed by the hour)' } elseif ($values.deployRedis) { "on (kept: '$redisLive' exists, billed by the hour)" } else { 'off (pass -DeployRedis to turn it on)' }
    Write-Host "  Azure Managed Redis: $redisWhy" -ForegroundColor DarkGray
    Write-Host "  Name guard: $($nameGuard.Why)" -ForegroundColor DarkGray
    Write-Host "  Fan matches: $($fanMatches.Why)" -ForegroundColor DarkGray
    Write-Host "  App Configuration: $($appConfig.Why)$(if ($appConfig.On) { $(if ($values.seedAppConfig) { ', seeding values and flags' } else { ', store exists: values and flags left as they are' }) })" -ForegroundColor DarkGray
    if ($values.maintenanceMode) { Write-Host "  Maintenance mode: kept on (wipe-data in progress)" -ForegroundColor DarkGray }
    Write-Host "  Match Centre image: $(if ($values.Contains('matchCentreImage')) { $values.matchCentreImage } else { 'placeholder (first deploy)' })" -ForegroundColor DarkGray
    Write-Host "  Simulator image: $(if ($values.Contains('simulatorImage')) { $values.simulatorImage } else { 'placeholder (first deploy)' })" -ForegroundColor DarkGray
    if ($values.Contains('matchCentreTraffic')) {
        Write-Host "  Keeping $(@($values.matchCentreTraffic).Count) live traffic rule(s)" -ForegroundColor DarkGray
    }

    $parameters = [ordered]@{}
    foreach ($key in $values.Keys) { $parameters[$key] = @{ value = $values[$key] } }

    $file = Join-Path ([System.IO.Path]::GetTempPath()) "touchline-ai-params-$([guid]::NewGuid()).json"
    @{
        '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters     = $parameters
    } | ConvertTo-Json -Depth 10 | Set-Content -Path $file -Encoding utf8
    return $file
}

# Runs one teardown step and reports failure without aborting the rest, so a
# resource that is already gone doesn't stop the next one being cleaned up.
function Invoke-TeardownStep {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Step
    )

    Write-Host "  $Description" -ForegroundColor Cyan
    try {
        & $Step
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    not found or already removed - continuing" -ForegroundColor DarkYellow
        }
    }
    catch {
        Write-Host "    $($_.Exception.Message) - continuing" -ForegroundColor DarkYellow
    }
}

# ── Actions ───────────────────────────────────────────────────────────────────

function Invoke-WhatIf {
    Write-Host "`nPreviewing deployment to '$ResourceGroup'...`n" -ForegroundColor Cyan
    $parameterFile = Get-TemplateParameterFile -Deployer (Get-Deployer)
    try {
        az deployment group what-if `
            --resource-group $ResourceGroup `
            --template-file $TemplateFile `
            --parameters "@$parameterFile"
        if ($LASTEXITCODE -ne 0) { throw "what-if failed." }
    }
    finally {
        Remove-Item $parameterFile -ErrorAction SilentlyContinue
    }

    Write-Host "`nRead the 'Resource changes' summary above before deploying." -ForegroundColor Yellow
    Write-Host "On a fresh group, anything other than Create deserves a second look.`n" -ForegroundColor Yellow
}

function Invoke-Deploy {
    $exists = az group exists --name $ResourceGroup
    if ($exists -ne 'true') {
        # Tags are applied by the template (Microsoft.Resources/tags), not here.
        Write-Host "`nCreating resource group '$ResourceGroup' in $Location..." -ForegroundColor Cyan
        az group create --name $ResourceGroup --location $Location --output none
        if ($LASTEXITCODE -ne 0) { throw "Could not create the resource group." }
    }

    Write-Host "`nDeploying to '$ResourceGroup'...`n" -ForegroundColor Cyan
    $parameterFile = Get-TemplateParameterFile -Deployer (Get-Deployer)
    try {
        az deployment group create `
            --resource-group $ResourceGroup `
            --name main `
            --template-file $TemplateFile `
            --parameters "@$parameterFile" `
            --output none # the full deployment JSON buries the outputs; they're printed below
        $failed = $LASTEXITCODE -ne 0
    }
    finally {
        Remove-Item $parameterFile -ErrorAction SilentlyContinue
    }

    if ($failed) {
        Write-Host "`nDeployment failed." -ForegroundColor Red
        Write-Host "If a role assignment hasn't reached the container app yet (an ACR pull or" -ForegroundColor Yellow
        Write-Host "UNAUTHORIZED error), wait a minute and re-run. Deployments are idempotent.`n" -ForegroundColor Yellow
        throw "Deployment failed."
    }

    Write-Host "`nDeployed. Outputs:`n" -ForegroundColor Green
    az deployment group show --resource-group $ResourceGroup --name main --query properties.outputs
}

# The Search endpoint from the deployment outputs and an Entra ID token for its data plane. The
# service has local auth off, so it never takes an API key.
# Given a service name, the endpoint is built from it, because a failed deploy leaves the 'main'
# deployment with no outputs and a re-run wipe still has to reach the index.
function Get-SearchDataPlane {
    param([string]$ServiceName)

    $endpoint = if ($ServiceName) {
        "https://$ServiceName.search.windows.net"
    }
    else {
        az deployment group show --resource-group $ResourceGroup --name main `
            --query properties.outputs.searchEndpoint.value -o tsv
    }
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($endpoint)) {
        throw "No search endpoint in the deployment outputs. Run '-Action deploy -DeploySearch' first."
    }

    $token = az account get-access-token --resource https://search.azure.com --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw "Could not get a Search access token." }
    return [pscustomobject]@{ Endpoint = $endpoint.Trim(); Headers = @{ Authorization = "Bearer $($token.Trim())" } }
}

function Invoke-CreateIndex {
    $search = Get-SearchDataPlane
    $endpoint = $search.Endpoint
    $headers = $search.Headers

    Write-Host "`nCreating or updating index '$SearchIndexName' on $endpoint..." -ForegroundColor Cyan
    $body = Get-Content -Path $SearchIndexFile -Raw
    Invoke-RestMethod -Method Put -Uri "$endpoint/indexes/${SearchIndexName}?api-version=$SearchDataApiVersion" `
        -Body $body -ContentType 'application/json' -Headers $headers | Out-Null

    $stats = Invoke-RestMethod -Method Get -Uri "$endpoint/indexes/${SearchIndexName}/stats?api-version=$SearchDataApiVersion" `
        -Headers $headers
    Write-Host "Index ready. Documents: $($stats.documentCount)" -ForegroundColor Green
}

function Invoke-TeardownSearch {
    $names = @(az resource list --resource-group $ResourceGroup --resource-type Microsoft.Search/searchServices --query "[].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list the Search service." }
    $names = @($names | Where-Object { $_ })
    if ($names.Count -eq 0) {
        Write-Host "`nNo Search service in '$ResourceGroup'. Nothing to delete.`n" -ForegroundColor Green
        return
    }

    Write-Host "`nAbout to delete the Search service '$($names -join ', ')' and its indexes." -ForegroundColor Yellow
    Write-Host "Everything else in '$ResourceGroup' is kept. The index can be rebuilt by redeploying and re-indexing."

    if (-not $Force) {
        $confirm = (Read-Host "Type the resource group name to confirm").Trim()
        if ($confirm -ne $ResourceGroup) {
            Write-Host "`nCancelled. No changes made." -ForegroundColor Green
            return
        }
    }

    foreach ($name in $names) {
        Write-Host "  Deleting '$name'..." -ForegroundColor Cyan
        az resource delete --resource-group $ResourceGroup --resource-type Microsoft.Search/searchServices --name $name
        if ($LASTEXITCODE -ne 0) { throw "Could not delete '$name'." }
    }

    Write-Host "`nSearch deleted. Run '-Action deploy -NoSearch' next, and record the date in the resource ledger.`n" -ForegroundColor Green
}

# Deletes only Azure Managed Redis (ADR-061). Nothing in it has to be kept: the caches refill, and
# the fan-match limits fall back to Cosmos DB, which the next deploy (-NoRedis) makes permanent.
function Invoke-TeardownRedis {
    $names = @(az resource list --resource-group $ResourceGroup --resource-type Microsoft.Cache/redisEnterprise --query "[].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list Azure Managed Redis." }
    $names = @($names | Where-Object { $_ })
    if ($names.Count -eq 0) {
        Write-Host "`nNo Azure Managed Redis in '$ResourceGroup'. Nothing to delete.`n" -ForegroundColor Green
        return
    }

    Write-Host "`nAbout to delete Azure Managed Redis '$($names -join ', ')' and everything cached in it." -ForegroundColor Yellow
    Write-Host "Everything else in '$ResourceGroup' is kept. The Match Centre falls back to memory and Cosmos DB."

    if (-not $Force) {
        $confirm = (Read-Host "Type the resource group name to confirm").Trim()
        if ($confirm -ne $ResourceGroup) {
            Write-Host "`nCancelled. No changes made." -ForegroundColor Green
            return
        }
    }

    foreach ($name in $names) {
        Write-Host "  Deleting '$name'..." -ForegroundColor Cyan
        az resource delete --resource-group $ResourceGroup --resource-type Microsoft.Cache/redisEnterprise --name $name
        if ($LASTEXITCODE -ne 0) { throw "Could not delete '$name'." }
    }

    Write-Host "`nRedis deleted. Run '-Action deploy -NoRedis' next, and record the date in the resource ledger.`n" -ForegroundColor Green
}

# The name of the one resource of a type in the group, or $null if there is none. More than one
# means the group isn't the shape main.bicep makes, so the wipe stops rather than guess.
function Get-SingleResourceName {
    param([Parameter(Mandatory)][string]$Type)

    $names = @(az resource list --resource-group $ResourceGroup --resource-type $Type --query "[].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list the $Type resources in '$ResourceGroup'." }
    $names = @($names | Where-Object { $_ })
    if ($names.Count -gt 1) { throw "Expected one $Type in '$ResourceGroup', found $($names.Count): $($names -join ', ')." }
    if ($names.Count -eq 0) { return $null }
    return $names[0].Trim()
}

# A live match would keep writing events into the containers as they're deleted, and its
# half-finished documents would land in the new ones. 'Processing' is an execution that has been
# started but isn't running yet, so it counts as live too.
function Assert-NoLiveMatch {
    $running = @(az containerapp job execution list --name $SimulatorJobName --resource-group $ResourceGroup `
            --query "[?properties.status=='Running' || properties.status=='Processing'].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list the simulator's executions, so can't tell whether a match is live." }
    $running = @($running | Where-Object { $_ })
    if ($running.Count -gt 0) {
        throw "A match is still being simulated ($($running -join ', ')). Wait for it to finish, then re-run."
    }
}

# Maintenance mode (ADR-060). A settings change makes a new revision, which only takes the traffic
# when the ingress sends 100% to the latest revision. A pinned rule (a "judging" label, ADR-009)
# would leave the maintenance revision with no traffic, so the wipe refuses to start.
function Assert-TrafficFollowsLatest {
    $rules = az containerapp ingress traffic show --name $MatchCentreAppName --resource-group $ResourceGroup -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read the Match Centre's traffic rules." }
    $latest = @($rules | Where-Object { $_.PSObject.Properties['latestRevision'] -and $_.latestRevision -and $_.PSObject.Properties['weight'] -and $_.weight -eq 100 })
    if ($latest.Count -eq 0) {
        throw "The Match Centre's traffic is pinned to a revision, so a maintenance revision would get none. Send 100% to the latest revision first."
    }
}

# Asks the live app until it answers with the maintenance header (or stops answering with it).
function Wait-MaintenanceState {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][bool]$On,
        [int]$TimeoutSeconds = 300
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method Get -SkipHttpErrorCheck -TimeoutSec 30 -UseBasicParsing
            $marked = $response.Headers.ContainsKey($MaintenanceHeader)
            if ($marked -eq $On) { return $true }
        }
        catch {
            # A revision starting from zero replicas can refuse or time out for a while.
        }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Get-MatchCentreUrl {
    $fqdn = az containerapp show --name $MatchCentreAppName --resource-group $ResourceGroup `
        --query properties.configuration.ingress.fqdn -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($fqdn)) { throw "Could not read the Match Centre's address." }
    return "https://$($fqdn.Trim())/api/matches"
}

function Enable-Maintenance {
    param([Parameter(Mandatory)][string]$Url)

    Write-Host "`n  Turning maintenance mode on..." -ForegroundColor Cyan
    # Any deploy from here on (the wipe's own) keeps it on, because the template adds the setting.
    $script:MaintenanceOn = $true
    az containerapp update --name $MatchCentreAppName --resource-group $ResourceGroup `
        --set-env-vars "$MaintenanceSetting=true" --output none
    if ($LASTEXITCODE -ne 0) { throw "Could not turn maintenance mode on. No data deleted." }

    # The setting does nothing on an image built before ADR-060, so check the page is really up.
    if (-not (Wait-MaintenanceState -Url $Url -On $true)) {
        throw "The Match Centre isn't showing the maintenance page (no $MaintenanceHeader header). Is the ADR-060 image deployed? No data deleted."
    }
    Write-Host "  Maintenance page is up." -ForegroundColor DarkGray
}

function Disable-Maintenance {
    param([Parameter(Mandatory)][string]$Url)

    Write-Host "`n  Turning maintenance mode off..." -ForegroundColor Cyan
    $script:MaintenanceOn = $false
    # A new revision with fresh replicas, so no change feed position into a deleted container survives.
    az containerapp update --name $MatchCentreAppName --resource-group $ResourceGroup `
        --remove-env-vars $MaintenanceSetting --output none
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not turn maintenance mode off. Run: az containerapp update -n $MatchCentreAppName -g $ResourceGroup --remove-env-vars $MaintenanceSetting"
        return
    }
    if (-not (Wait-MaintenanceState -Url $Url -On $false)) {
        Write-Warning "The Match Centre still shows the maintenance page. Check the latest revision in the portal."
        return
    }
    Write-Host "  The Match Centre is back." -ForegroundColor DarkGray
}

function Invoke-WipeData {
    $cosmos = Get-SingleResourceName -Type 'Microsoft.DocumentDB/databaseAccounts'
    if (-not $cosmos) { throw "No Cosmos DB account in '$ResourceGroup'. Nothing to wipe." }
    $processor = Get-SingleResourceName -Type 'Microsoft.Web/sites'
    $searchService = Get-SingleResourceName -Type 'Microsoft.Search/searchServices'

    Assert-NoLiveMatch
    Assert-TrafficFollowsLatest
    $matchCentreUrl = Get-MatchCentreUrl

    # Look the Search endpoint up before anything is deleted, so a missing output or token stops the
    # wipe while the data is still whole.
    $search = if ($searchService) { Get-SearchDataPlane -ServiceName $searchService } else { $null }

    $live = @(az cosmosdb sql container list --account-name $cosmos --resource-group $ResourceGroup `
            --database-name $CosmosDatabaseName --query "[].name" -o tsv)
    if ($LASTEXITCODE -ne 0) { throw "Could not list the containers in '$cosmos'." }
    $live = @($live | Where-Object { $_ } | ForEach-Object { $_.Trim() })
    $toDelete = @($WipedContainers | Where-Object { $live -contains $_ })

    Write-Host "`nAbout to delete every stored match and everything built from one:" -ForegroundColor Yellow
    Write-Host "  Cosmos DB '$cosmos', database '$CosmosDatabaseName': $($toDelete -join ', ')"
    Write-Host "  Search index '$SearchIndexName': $(if ($searchService) { "on '$searchService'" } else { 'no Search service, skipped' })"
    Write-Host "Fan teams and their edit keys go too. The demo match is a recording, so it stays."
    Write-Host "The Match Centre shows a maintenance page throughout, the processor is stopped, and a"
    Write-Host "deploy recreates the containers empty."
    Write-Host "`nThis cannot be undone." -ForegroundColor Red

    if (-not $Force) {
        $confirm = (Read-Host "Type the resource group name to confirm").Trim()
        if ($confirm -ne $ResourceGroup) {
            Write-Host "`nCancelled. No changes made." -ForegroundColor Green
            return
        }
    }

    # Maintenance goes on first, so nobody sees half-deleted data, and fans can't start a match:
    # the maintenance page answers every POST. It goes off when the script ends, whatever happens.
    try {
        Enable-Maintenance -Url $matchCentreUrl

        # Stop the processor, so no trigger writes into a container between its delete and the
        # redeploy. It's started again whatever happens below.
        if ($processor) {
            Write-Host "  Stopping '$processor'..." -ForegroundColor Cyan
            az functionapp stop --name $processor --resource-group $ResourceGroup --output none
            if ($LASTEXITCODE -ne 0) { throw "Could not stop '$processor'. No data deleted." }
        }

        try {
            # Check again: a match started before maintenance went on could still be playing.
            Assert-NoLiveMatch

            foreach ($name in $toDelete) {
                Write-Host "  Deleting container '$name'..." -ForegroundColor Cyan
                az cosmosdb sql container delete --account-name $cosmos --resource-group $ResourceGroup `
                    --database-name $CosmosDatabaseName --name $name --yes
                if ($LASTEXITCODE -ne 0) { throw "Could not delete container '$name'." }
            }

            if ($search) {
                Write-Host "  Deleting index '$SearchIndexName'..." -ForegroundColor Cyan
                try {
                    Invoke-RestMethod -Method Delete -Uri "$($search.Endpoint)/indexes/${SearchIndexName}?api-version=$SearchDataApiVersion" `
                        -Headers $search.Headers | Out-Null
                }
                catch {
                    # An index that's already gone is the state we want. A failure with no HTTP response
                    # (DNS, TLS) has no Response property, and StrictMode would throw on reading it.
                    $response = $_.Exception.PSObject.Properties['Response']
                    if (-not $response -or $null -eq $response.Value -or [int]$response.Value.StatusCode -ne 404) { throw }
                }
            }

            # The deploy recreates every container from main.bicep, with its partition key and time to
            # live, and keeps the live images, traffic rules and feature switches as any deploy does.
            # It also keeps maintenance mode on, because $script:MaintenanceOn is set.
            Invoke-Deploy
            if ($search) { Invoke-CreateIndex }
        }
        catch {
            # Re-running the wipe deletes only what's left, then redeploys and restarts everything.
            Write-Host "`nThe wipe stopped part way: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "Re-run '-Action wipe-data' to finish. It skips what's already gone, then redeploys and restarts the apps." -ForegroundColor Yellow
            throw
        }
        finally {
            if ($processor) {
                Write-Host "`n  Starting '$processor'..." -ForegroundColor Cyan
                az functionapp start --name $processor --resource-group $ResourceGroup --output none
                if ($LASTEXITCODE -ne 0) { Write-Warning "Could not start '$processor'. Start it in the portal." }
            }
        }
    }
    finally {
        if ($script:MaintenanceOn) { Disable-Maintenance -Url $matchCentreUrl }
    }

    Write-Host "`nWiped. Play a fresh match with ./infra/start-match.ps1 to check the pipeline end to end.`n" -ForegroundColor Green
}

function Invoke-Teardown {
    # Read the soft-deleting resources' names before the group disappears.
    $keyVaults = @(az resource list --resource-group $ResourceGroup --resource-type Microsoft.KeyVault/vaults --query "[].name" -o tsv)
    $foundryAccounts = @(az resource list --resource-group $ResourceGroup --resource-type Microsoft.CognitiveServices/accounts --query "[].name" -o tsv)

    Write-Host "`nAbout to delete the resource group '$ResourceGroup' and everything in it," -ForegroundColor Yellow
    Write-Host "including the container apps, registry images, Foundry models and Key Vault secrets."
    Write-Host "`nThis cannot be undone." -ForegroundColor Red

    if (-not $Force) {
        $confirm = (Read-Host "Type the resource group name to confirm").Trim()
        if ($confirm -ne $ResourceGroup) {
            Write-Host "`nCancelled. No changes made." -ForegroundColor Green
            return
        }
    }

    Write-Host ""
    Invoke-TeardownStep "Deleting resource group '$ResourceGroup' (several minutes)..." {
        az group delete --name $ResourceGroup --yes
    }

    # Key Vault and Foundry accounts soft-delete and keep holding their names.
    # Purge them, or the next deploy fails with a name conflict that doesn't
    # mention soft delete.
    foreach ($name in $keyVaults | Where-Object { $_ }) {
        Invoke-TeardownStep "Purging Key Vault '$name'..." {
            az keyvault purge --name $name --location $Location
        }
    }
    foreach ($name in $foundryAccounts | Where-Object { $_ }) {
        Invoke-TeardownStep "Purging Foundry account '$name'..." {
            az cognitiveservices account purge --name $name --resource-group $ResourceGroup --location $Location
        }
    }

    Write-Host "`nTeardown complete. Record the date in the scoreboard's resource ledger.`n" -ForegroundColor Green
}

function Show-Principal {
    $deployer = Get-Deployer
    Write-Host "Signed in as: $($deployer.Kind)"
    Write-Host "Object ID:    $(if ($deployer.PrincipalId) { $deployer.PrincipalId } else { '(none: no developer roles granted)' })"
    Write-Host "Alert email:  $($deployer.Email)"
}

# ── Main ──────────────────────────────────────────────────────────────────────

switch ($Action) {
    'whatif'    { Invoke-WhatIf }
    'deploy'    { Invoke-Deploy }
    'teardown'  { Invoke-Teardown }
    'principal' { Show-Principal }
    'create-index' { Invoke-CreateIndex }
    'teardown-search' { Invoke-TeardownSearch }
    'teardown-redis' { Invoke-TeardownRedis }
    'wipe-data' { Invoke-WipeData }
}
