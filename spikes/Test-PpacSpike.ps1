<#
.SYNOPSIS
  ppac spike: can a service principal call the Power Platform API?
.DESCRIPTION
  -Mode Setup      : create (or reuse) an Entra app registration + service principal
                     and create a client secret.
  -Mode AssignRole : give the service principal a Power Platform RBAC role at tenant
                     scope (admin sign-in).
  -Mode Test       : as the service principal, call read-only Power Platform API endpoints.
  -Mode All        : Setup, AssignRole, wait, then Test.

  Setup stores the IDs and the client secret in environment variables of the CURRENT
  PowerShell session only (PPAC_TENANT_ID, PPAC_CLIENT_ID, PPAC_SP_OBJECT_ID,
  PPAC_CLIENT_SECRET). Run this script directly in your session so later modes can
  reuse them. The secret is never printed and never accepted as a parameter.

  Requires: PowerShell 7 (recommended), Az.Accounts, Az.Resources.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Setup', 'AssignRole', 'Test', 'All')][string]$Mode,
    [string]$TenantId = $env:PPAC_TENANT_ID,
    [string]$AppName = 'ppac-spike',
    [string]$ClientId,
    [string]$ServicePrincipalObjectId,
    [ValidateSet('Reader', 'Contributor')][string]$Role = 'Reader',
    [int]$PropagationWaitSeconds = 60
)

$ErrorActionPreference = 'Stop'
$Api        = 'https://api.powerplatform.com'
$ApiVersion = '2024-10-01'
$RoleIds    = @{
    Reader      = 'c886ad2e-27f7-4874-8381-5849b8d8a090'
    Contributor = 'ff954d61-a89a-4fbe-ace9-01c367b89f87'
}

if (-not $TenantId) { throw '-TenantId is required (or set $env:PPAC_TENANT_ID).' }

function Invoke-Pp {
    param([string]$Method, [string]$Path, [string]$Token, $Body)
    $params = @{
        Method      = $Method
        Uri         = "$Api$Path"
        Headers     = @{ Authorization = "Bearer $Token" }
        ContentType = 'application/json'
    }
    if ($Body) { $params.Body = $Body | ConvertTo-Json -Depth 5 }
    try {
        $result = Invoke-RestMethod @params
        Write-Host "OK   $Method $Path" -ForegroundColor Green
        return $result
    }
    catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'n/a' }
        Write-Host "FAIL $Method $Path -> HTTP $code" -ForegroundColor Red
        if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message }
        else { Write-Host $_.Exception.Message }
        return $null
    }
}

function Connect-Admin {
    param([switch]$Force)
    foreach ($m in 'Az.Accounts', 'Az.Resources') {
        if (-not (Get-Module -ListAvailable -Name $m)) {
            throw "Module $m not found. Run: Install-Module $m -Scope CurrentUser"
        }
    }
    $ctx = Get-AzContext
    if ($Force -or -not $ctx -or $ctx.Tenant.Id -ne $TenantId) {
        # The Graph scope is required for the app registration calls (MFA / conditional access).
        Connect-AzAccount -Tenant $TenantId -AuthScope MicrosoftGraphEndpointResourceId | Out-Null
    }
}

function Invoke-Setup {
    Connect-Admin -Force

    $app = Get-AzADApplication -DisplayName $AppName -ErrorAction Stop | Select-Object -First 1
    if ($app) {
        Write-Host "Reusing app registration '$AppName'"
    }
    else {
        $app = New-AzADApplication -DisplayName $AppName -SignInAudience AzureADMyOrg -ErrorAction Stop
        if (-not $app -or -not $app.AppId) { throw "Failed to create app registration '$AppName'." }
        Write-Host "Created app registration '$AppName'"
    }

    $sp = Get-AzADServicePrincipal -ApplicationId $app.AppId -ErrorAction Stop
    if (-not $sp) {
        $sp = New-AzADServicePrincipal -ApplicationId $app.AppId -ErrorAction Stop
        if (-not $sp) { throw 'Failed to create service principal.' }
        Write-Host 'Created service principal'
    }

    $expires = (Get-Date).AddMonths(6)
    $cred = New-AzADAppCredential -ObjectId $app.Id -EndDate $expires -ErrorAction Stop
    if (-not $cred -or -not $cred.SecretText) { throw 'New-AzADAppCredential did not return a secret.' }

    $env:PPAC_TENANT_ID     = $TenantId
    $env:PPAC_CLIENT_ID     = $app.AppId
    $env:PPAC_SP_OBJECT_ID  = $sp.Id
    $env:PPAC_CLIENT_SECRET = $cred.SecretText

    Write-Host "Client ID:                   $($app.AppId)"
    Write-Host "Service principal object ID: $($sp.Id)"
    Write-Host "Secret expires:              $expires (stored in this session only)"
}

function Invoke-AssignRole {
    $spo = if ($ServicePrincipalObjectId) { $ServicePrincipalObjectId } else { $env:PPAC_SP_OBJECT_ID }
    if (-not $spo) {
        throw '-ServicePrincipalObjectId (enterprise application object ID) is required, or run Setup first.'
    }

    Connect-Admin
    $token = (Get-AzAccessToken -TenantId $TenantId -ResourceUrl "$Api/").Token
    if ($token -is [securestring]) {
        $token = [System.Net.NetworkCredential]::new('', $token).Password
    }

    $body = @{
        roleDefinitionId  = $RoleIds[$Role]
        principalObjectId = $spo
        principalType     = 'ApplicationUser'
        scope             = "/tenants/$TenantId"
    }
    $created = Invoke-Pp -Method Post -Path "/authorization/roleAssignments?api-version=$ApiVersion" -Token $token -Body $body
    if (-not $created) { return $false }
    $created | Format-List | Out-Host

    $all = Invoke-Pp -Method Get -Path "/authorization/roleAssignments?api-version=$ApiVersion" -Token $token
    if ($all) {
        $all.value |
            Where-Object principalObjectId -eq $spo |
            Format-Table roleAssignmentId, roleDefinitionId, scope, principalType |
            Out-Host
    }
    return $true
}

function Invoke-Test {
    $cid = if ($ClientId) { $ClientId } else { $env:PPAC_CLIENT_ID }
    if (-not $cid) { throw '-ClientId is required, or run Setup first.' }
    if (-not $env:PPAC_CLIENT_SECRET) { throw 'Set $env:PPAC_CLIENT_SECRET first (Setup does this).' }

    try {
        $tokenResponse = Invoke-RestMethod -Method Post `
            -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
            -ContentType 'application/x-www-form-urlencoded' `
            -Body @{
                client_id     = $cid
                client_secret = $env:PPAC_CLIENT_SECRET
                scope         = "$Api/.default"
                grant_type    = 'client_credentials'
            }
    }
    catch {
        Write-Host 'FAIL token request' -ForegroundColor Red
        $msg = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        Write-Host $msg
        if ($msg -match 'AADSTS500011') {
            Write-Host 'Hint: create the API service principal once:' -ForegroundColor Yellow
            Write-Host '  New-MgServicePrincipal -AppId 8578e004-a5c6-46e7-913e-12f58912df43 -DisplayName "Power Platform API"'
        }
        return 3
    }
    $token = $tokenResponse.access_token
    Write-Host 'OK   token acquired' -ForegroundColor Green

    # Show who the token says we are (the token itself is never printed)
    $payload = $token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    $claims  = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    Write-Host "claims: aud=$($claims.aud) appid=$($claims.appid) azp=$($claims.azp) tid=$($claims.tid) roles=$($claims.roles -join ',')"

    $envs = Invoke-Pp -Method Get -Path "/environmentmanagement/environments?api-version=$ApiVersion" -Token $token
    if ($envs) {
        Write-Host "Environments returned: $(@($envs.value).Count)"
        $envs.value |
            Select-Object -First 5 name, @{ n = 'displayName'; e = { $_.properties.displayName } } |
            Format-Table |
            Out-Host
        if ($envs.'@odata.nextLink') { Write-Host 'nextLink present: paging needed' }
    }

    $groups = Invoke-Pp -Method Get -Path '/environmentmanagement/environmentGroups?api-version=2022-03-01-preview' -Token $token
    if ($groups) { Write-Host "Environment groups returned: $(@($groups.value).Count)" }

    if (-not $envs) { return 1 }
    return 0
}

switch ($Mode) {
    'Setup'      { Invoke-Setup }
    'AssignRole' { if (-not (Invoke-AssignRole)) { exit 1 } }
    'Test'       { exit (Invoke-Test) }
    'All' {
        Invoke-Setup
        if (-not (Invoke-AssignRole)) { exit 1 }
        Write-Host "Waiting $PropagationWaitSeconds s for the role assignment to take effect..."
        Start-Sleep -Seconds $PropagationWaitSeconds
        exit (Invoke-Test)
    }
}
