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
    create-index
              Create or update the Azure AI Search index from
              infra/search/possessions-index.json (ADR-040). Needs the Search
              service (deploy with -DeploySearch first) and the Search Service
              Contributor role, which the deploy grants to the signed-in user.

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

.PARAMETER Force
    Skip the teardown confirmation. Intended for scripted use only.

.EXAMPLE
    ./infra/deploy.ps1 -Action whatif

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -DeploySquadModels

.EXAMPLE
    ./infra/deploy.ps1 -Action deploy -MatchCentreImage crtouchline2ryqfwse6uqn4.azurecr.io/matchcentre:abc123
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('whatif', 'deploy', 'teardown', 'principal', 'create-index', 'teardown-search')]
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

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DeploySearch -and $NoSearch) { throw "-DeploySearch and -NoSearch contradict each other." }

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

function Get-TemplateParameterFile {
    param([Parameter(Mandatory)]$Deployer)

    $groupId = "/subscriptions/$($Deployer.Subscription)/resourceGroups/$ResourceGroup"
    $values = [ordered]@{
        principalId = $Deployer.PrincipalId
        ownerEmail  = $Deployer.Email
    }

    $app = Get-LiveResource -Id "$groupId/providers/Microsoft.App/containerApps/$MatchCentreAppName" `
        -Query "{image: properties.template.containers[0].image, traffic: properties.configuration.ingress.traffic}"
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

    Write-Host "  Deploying as: $($Deployer.Kind)" -ForegroundColor DarkGray
    Write-Host "  Squad models: $($squad.Why)$capacity" -ForegroundColor DarkGray
    Write-Host "  Azure AI Search: $(if ($values.deploySearch) { "deployed in $searchRegion (about 2 pounds a day)" } else { 'not deployed' })" -ForegroundColor DarkGray
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

function Invoke-CreateIndex {
    $endpoint = az deployment group show --resource-group $ResourceGroup --name main `
        --query properties.outputs.searchEndpoint.value -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($endpoint)) {
        throw "No search endpoint in the deployment outputs. Run '-Action deploy -DeploySearch' first."
    }
    $endpoint = $endpoint.Trim()

    # The service has local auth off, so the data plane takes an Entra ID token, never an API key.
    $token = az account get-access-token --resource https://search.azure.com --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw "Could not get a Search access token." }
    $headers = @{ Authorization = "Bearer $($token.Trim())" }

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
}
