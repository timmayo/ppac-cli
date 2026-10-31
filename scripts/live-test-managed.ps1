<#
.SYNOPSIS
    Live test of 'ppac environment managed enable | disable' on ONE test environment.

.DESCRIPTION
    Pass 1 (default) changes NOTHING:
      1. Shows the environment as returned by 'environment get' (look at protectionLevel).
      2. enable --whatif                 (sends nothing)
    (The service rejects the documented ValidateOnly=true option with "not supported yet", so there is no server-side dry run.)
    Pass 2 (add -Apply) changes the environment, then puts it back:
      5. enable (real, waits)            -> records the HTTP status and how long the change takes to show up
      6. enable again                    -> must say "already managed" and send nothing
      7. disable --yes (real, waits)     -> restores the original state
      8. disable again                   -> must say "not managed" and send nothing
    Pass 2 only runs when the environment starts NOT managed, so it can always be restored.
    Only ppac-test and ppac-test2 are allowed. Never run this against any other environment.

    Prerequisites:
      - Windows PowerShell 5.1 or PowerShell 7+.
      - .NET 10 SDK: https://dotnet.microsoft.com/download
      - ppac built in Release mode, from the repo root:
            dotnet build src\Ppac.Cli -c Release
      - An active ppac profile (check with: ppac auth who).
      - The signed-in user has the Power Platform Administrator or Dynamics 365 Administrator role in
        Microsoft Entra ID (the admin center documentation says only these roles can change Managed
        Environments).
      - The API reference lists only the .default scope for these calls, so which delegated permission
        they need is NOT documented. EnvironmentManagement.Environments.Read is needed to find the
        environment. If a call returns 403, paste the output; do not guess permissions.
      - Managed Environments may need a license (see the admin center documentation).

    Run from the repo root:
        .\scripts\live-test-managed.ps1                              # pass 1 only, changes nothing
        .\scripts\live-test-managed.ps1 -Apply                       # pass 1 and pass 2
        .\scripts\live-test-managed.ps1 -EnvironmentName ppac-test2 -Apply

.PARAMETER EnvironmentName
    ppac-test or ppac-test2 (default ppac-test).

.PARAMETER Apply
    Also run pass 2 (enable, then disable again). Asks you to type the environment name first.

.PARAMETER Ppac
    Path to ppac.exe. Defaults to the Release build in this repo.
#>
[CmdletBinding()]
param(
    [ValidateSet('ppac-test', 'ppac-test2')]
    [string]$EnvironmentName = 'ppac-test',

    [switch]$Apply,

    [string]$Ppac = (Join-Path $PSScriptRoot '..\src\Ppac.Cli\bin\Release\net10.0\ppac.exe')
)

$ErrorActionPreference = 'Stop'
$AllowedEnvironments = @('ppac-test', 'ppac-test2')
$Results = New-Object System.Collections.Generic.List[object]
$envId = $null
$enabledByScript = $false

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Add-Result([string]$Step, $ExitCode, [string]$Outcome) {
    $Results.Add([pscustomobject]@{ Step = $Step; ExitCode = $ExitCode; Outcome = $Outcome })
}

# Runs ppac and captures stdout and stderr separately.
function Invoke-PpacJson {
    param([string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & $Ppac @Arguments 2> $errFile
        $code = $LASTEXITCODE
        $stderr = Get-Content -Raw -Path $errFile
    }
    finally {
        Remove-Item -Path $errFile -ErrorAction SilentlyContinue
    }
    [pscustomobject]@{
        ExitCode = $code
        Output   = (@($stdout) -join [Environment]::NewLine)
        Error    = ("$stderr").Trim()
    }
}

function Show-Result($Result) {
    if ($Result.Output) { Write-Host $Result.Output }
    if ($Result.Error) { Write-Host $Result.Error -ForegroundColor Yellow }
    Write-Host "exit code: $($Result.ExitCode)"
}

function Get-Level {
    $r = Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')
    if ($r.ExitCode -ne 0) { return [pscustomobject]@{ Level = $null; Raw = $r.Output; Ok = $false } }
    $obj = $r.Output | ConvertFrom-Json
    [pscustomobject]@{ Level = $obj.protectionLevel; Raw = $r.Output; Ok = $true }
}

try {
    if (-not (Test-Path -Path $Ppac)) {
        throw "ppac.exe not found at '$Ppac'. From the repo root run: dotnet build src\Ppac.Cli -c Release"
    }

    Write-Step 'Step 0: check the active profile'
    & $Ppac auth who
    if ($LASTEXITCODE -ne 0) { throw 'No active profile. Run: ppac auth create --deviceCode --tenant <tenant> --applicationId <app id>' }

    Write-Step "Step 1: find the environment named '$EnvironmentName'"
    $envList = Invoke-PpacJson @('environment', 'list', '--json')
    if ($envList.ExitCode -ne 0) { throw "environment list failed (exit $($envList.ExitCode)): $($envList.Error)" }
    $found = @(($envList.Output | ConvertFrom-Json).value | Where-Object { $_.displayName -eq $EnvironmentName })
    if ($found.Count -ne 1) { throw "Expected exactly one environment named '$EnvironmentName' but found $($found.Count). Nothing was changed." }
    if ($AllowedEnvironments -notcontains $found[0].displayName) { throw "Refusing to use '$($found[0].displayName)'." }
    $envId = $found[0].id
    Write-Host "Environment: $($found[0].displayName)  type: $($found[0].type)  state: $($found[0].state)"
    Write-Host "ID:          $envId"

    Write-Host 'Environment as returned by get (BEFORE):'
    $start = Get-Level
    Write-Host $start.Raw
    if (-not $start.Ok) { throw 'environment get failed.' }
    $startLevel = $start.Level
    Write-Host "protectionLevel before: $startLevel"
    Add-Result 'find environment' 0 "$EnvironmentName ($envId) protectionLevel '$startLevel'"

    Write-Step 'Step 2: enable --whatif (sends nothing)'
    & $Ppac environment managed enable --environment $envId --whatif
    Add-Result 'enable --whatif' $LASTEXITCODE 'printed the request'

    if (-not $Apply) {
        Write-Host ''
        Write-Host 'Pass 1 only (nothing changed). Add -Apply to enable and then disable Managed Environments on this environment. There is no server-side dry run (ValidateOnly is not supported), so -Apply is the real test.'
    }
    elseif ($startLevel -ne 'Basic') {
        Write-Host ''
        Write-Host "Pass 2 NOT run: the environment's protectionLevel is '$startLevel', not 'Basic', so it may already be managed and could not be restored with certainty." -ForegroundColor Yellow
        Add-Result 'pass 2' 'n/a' "NOT RUN: protectionLevel was '$startLevel'"
    }
    else {
        Write-Host ''
        $answer = Read-Host "Pass 2 will turn Managed Environments ON for '$EnvironmentName' and then OFF again. Type the environment name to continue"
        if ($answer -ne $EnvironmentName) {
            Write-Host 'Stopped. Nothing was changed.'
            return
        }

        Write-Step 'Step 5: enable (real; waits for protectionLevel to change)'
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $en = Invoke-PpacJson @('environment', 'managed', 'enable', '--environment', $envId, '--json')
        $sw.Stop()
        Show-Result $en
        Write-Host ("elapsed: {0:N1} seconds" -f $sw.Elapsed.TotalSeconds)
        $enabledByScript = ($en.ExitCode -eq 0)
        $mid = Get-Level
        Write-Host 'Environment as returned by get (AFTER enable):'
        Write-Host $mid.Raw
        Write-Host "protectionLevel after enable: $($mid.Level)"
        Add-Result 'enable' $en.ExitCode ("{0}; protectionLevel now '{1}'; {2:N1}s" -f $(if ($en.ExitCode -eq 0) { 'accepted' } else { 'FAILED - see above' }), $mid.Level, $sw.Elapsed.TotalSeconds)
        if ($mid.Ok -and $mid.Level -ne 'Basic') { $enabledByScript = $true }

        if ($enabledByScript) {
            Write-Step 'Step 6: enable again (must say already managed and send nothing)'
            $en2 = Invoke-PpacJson @('environment', 'managed', 'enable', '--environment', $envId)
            Show-Result $en2
            Add-Result 'enable again' $en2.ExitCode $(if ($en2.ExitCode -eq 0 -and $en2.Output -match 'already managed') { 'nothing to do (as expected)' } else { 'UNEXPECTED - see above' })

            Write-Step 'Step 7: disable (real; answer the prompt with y; waits for protectionLevel to change)'
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            & $Ppac environment managed disable --environment $envId
            $disableCode = $LASTEXITCODE
            $sw.Stop()
            Write-Host ("elapsed: {0:N1} seconds" -f $sw.Elapsed.TotalSeconds)
            $end = Get-Level
            Write-Host 'Environment as returned by get (AFTER disable):'
            Write-Host $end.Raw
            Write-Host "protectionLevel after disable: $($end.Level)"
            if ($end.Ok -and $end.Level -eq 'Basic') { $enabledByScript = $false }
            Add-Result 'disable' $disableCode ("protectionLevel now '{0}'; {1:N1}s" -f $end.Level, $sw.Elapsed.TotalSeconds)

            if (-not $enabledByScript) {
                Write-Step 'Step 8: disable again (must say not managed and send nothing)'
                $dis2 = Invoke-PpacJson @('environment', 'managed', 'disable', '--environment', $envId, '--yes')
                Show-Result $dis2
                Add-Result 'disable again' $dis2.ExitCode $(if ($dis2.ExitCode -eq 0 -and $dis2.Output -match 'not managed') { 'nothing to do (as expected)' } else { 'UNEXPECTED - see above' })
            }
        }
        else {
            Write-Host ''
            Write-Host 'The enable did not succeed, so the rest of pass 2 was skipped. The environment was not changed.' -ForegroundColor Yellow
            Add-Result 'pass 2 remainder' 'n/a' 'NOT RUN: enable did not succeed'
        }
    }
}
finally {
    if ($enabledByScript -and $envId) {
        Write-Host ''
        Write-Host "WARNING: Managed Environments may still be ON for '$EnvironmentName'. Turn it off with:" -ForegroundColor Red
        Write-Host "    $Ppac environment managed disable --environment $envId"
    }

    if ($Results.Count -gt 0) {
        Write-Host ''
        Write-Host '== Summary' -ForegroundColor Cyan
        $Results | Format-Table -AutoSize -Wrap | Out-String | Write-Host
        Write-Host 'Paste this whole output back so the results can be recorded.'
    }
}
