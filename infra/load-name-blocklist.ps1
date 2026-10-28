<#
.SYNOPSIS
    Creates or updates the Content Safety blocklist the team builder checks names against (ADR-049).

.DESCRIPTION
    Reads one term per line from a text file, and adds them to the blocklist 100 at a time
    (the API's limit for one request). The list is passed in by path: this repository never
    holds it. Re-running is safe, because adding a term
    that is already there updates it. Terms are never removed: to remove one, delete it in the
    portal or call the removeBlocklistItems operation.

    Blocklist changes take about five minutes to apply to analysis requests.

    You need the Cognitive Services User role on the Foundry account (main.bicep grants it to the
    developer principal). The script signs in with an Entra ID token, never a key. By default it
    asks `az account get-access-token`; if that fails (for example when a proxy breaks TLS
    verification against login.microsoftonline.com), pass a token you got another way:

        # Azure CLI, run wherever it works:
        az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv

        # Az PowerShell module:
        (Get-AzAccessToken -ResourceUrl https://cognitiveservices.azure.com -AsSecureString).Token

    then pass it as -AccessToken. The token is not echoed or logged.

.PARAMETER Endpoint
    The Foundry account's Cognitive Services endpoint, such as https://ais-touchline-xxxx.cognitiveservices.azure.com/
    (the Bicep output is the account name; the host is <name>.cognitiveservices.azure.com).

.PARAMETER Path
    A UTF-8 text file with one term per line. Blank lines and lines starting with # are skipped.

.PARAMETER BlocklistName
    The blocklist to create or update. Must match MatchCentre:ContentSafety:BlocklistName. Letters, digits, - . _ ~

.PARAMETER AccessToken
    An Entra ID token for https://cognitiveservices.azure.com, as plain text or a SecureString.
    Optional: when omitted, the script runs `az account get-access-token`, then tries Get-AzAccessToken.

.EXAMPLE
    ./infra/load-name-blocklist.ps1 -Endpoint https://ais-touchline-xxxx.cognitiveservices.azure.com/ -Path ./real-names.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Endpoint,

    [Parameter(Mandatory)]
    [string] $Path,

    [ValidatePattern('^[0-9A-Za-z._~-]{1,64}$')]
    [string] $BlocklistName = 'touchline-real-names',

    [object] $AccessToken
)

$ErrorActionPreference = 'Stop'
$apiVersion = '2024-09-01'
$batchSize = 100     # the API accepts at most 100 items per request
$maxTermLength = 128 # and each term at most 128 characters
$resource = 'https://cognitiveservices.azure.com'

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "The term file '$Path' doesn't exist."
}

function ConvertTo-PlainText([object] $Value) {
    if ($Value -is [securestring]) {
        return [System.Net.NetworkCredential]::new('', $Value).Password
    }
    return [string] $Value
}

function Get-Token {
    if ($AccessToken) {
        return ConvertTo-PlainText $AccessToken
    }

    $token = $null
    if (Get-Command az -ErrorAction SilentlyContinue) {
        $token = (az account get-access-token --resource $resource --query accessToken -o tsv 2>$null)
    }
    if (-not $token -and (Get-Command Get-AzAccessToken -ErrorAction SilentlyContinue)) {
        $token = ConvertTo-PlainText (Get-AzAccessToken -ResourceUrl $resource -AsSecureString).Token
    }
    if (-not $token) {
        throw "Couldn't get a token. Run 'az login' (or Connect-AzAccount), or pass -AccessToken."
    }
    return $token
}

# The Match Centre checks a name in normalised form (NameNormaliser.cs): decomposed, accents removed,
# look-alike letters folded to Latin, lower case, and apostrophes, full stops and hyphens dropped. So the
# terms are loaded in that form too, each as typed-with-spaces and with the spaces removed (a name
# typed "Blocked-United" normalises to "blockedunited"). Look-alike folding isn't needed here: the
# list is plain Latin text.
function ConvertTo-NormalisedTerm([string] $Term) {
    $decomposed = $Term.Normalize([System.Text.NormalizationForm]::FormKD)
    $kept = $decomposed.ToCharArray() | Where-Object {
        $category = [System.Globalization.CharUnicodeInfo]::GetUnicodeCategory($_)
        $category -notin @('NonSpacingMark', 'SpacingCombiningMark', 'EnclosingMark') -and $_ -notin @([char]39, [char]0x2019, '.', '-')
    }
    return ((-join $kept).ToLowerInvariant() -replace '\s+', ' ').Trim()
}

$terms = Get-Content -LiteralPath $Path -Encoding UTF8 |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object {
        $normalised = ConvertTo-NormalisedTerm $_
        $normalised
        $normalised -replace ' ', ''
    } |
    Where-Object { $_ } |
    Select-Object -Unique

$tooLong = @($terms | Where-Object { $_.Length -gt $maxTermLength })
if ($tooLong.Count -gt 0) {
    throw "$($tooLong.Count) term(s) are longer than $maxTermLength characters. Shorten them first."
}
if (@($terms).Count -eq 0) {
    throw "The term file has no terms."
}

$base = $Endpoint.TrimEnd('/') + '/contentsafety/text/blocklists'
$headers = @{ Authorization = "Bearer $(Get-Token)" }

Write-Host "Creating or updating blocklist '$BlocklistName'..."
$create = @{ description = 'Real clubs, nicknames, grounds, abbreviations and players. Fan-built teams must not use them (ADR-049).' } | ConvertTo-Json
Invoke-RestMethod -Method Patch -Uri "${base}/${BlocklistName}?api-version=$apiVersion" -Headers $headers `
    -ContentType 'application/json' -Body $create | Out-Null

$added = 0
for ($i = 0; $i -lt @($terms).Count; $i += $batchSize) {
    $batch = @($terms[$i..([Math]::Min($i + $batchSize, @($terms).Count) - 1)])
    $body = @{ blocklistItems = @($batch | ForEach-Object { @{ text = $_ } }) } | ConvertTo-Json -Depth 4
    Invoke-RestMethod -Method Post -Uri "${base}/${BlocklistName}:addOrUpdateBlocklistItems?api-version=$apiVersion" -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
    $added += $batch.Count
    Write-Host "  $added of $(@($terms).Count) terms loaded"
}

Write-Host "Done. Allow about five minutes before the new terms take effect."
