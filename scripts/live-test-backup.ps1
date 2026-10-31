<#
.SYNOPSIS
    Live test of 'ppac environment backup' (create, list, delete) against ONE test environment.

.DESCRIPTION
    Creates one real backup, lists it, then deletes it. Only the environments named
    ppac-test and ppac-test2 are accepted; any other name is refused, and the script
    also stops unless exactly one environment has that display name.

    Prerequisites:
      - Windows PowerShell 5.1 or PowerShell 7+.
      - .NET 10 SDK (to build ppac): https://dotnet.microsoft.com/download
      - ppac built in Release mode, from the repo root:
            dotnet build src\Ppac.Cli -c Release
      - An active ppac profile (check with: ppac auth who). If the sign-in expired, run
            ppac auth create --deviceCode --tenant <tenant> --applicationId <app id>
      - The signed-in user is a Power Platform admin. No API permission is documented for
        backups, so a 401/403 is possible; the script shows the exact error if so.

    Run from the repo root:
        .\scripts\live-test-backup.ps1
        .\scripts\live-test-backup.ps1 -EnvironmentName ppac-test

.PARAMETER EnvironmentName
    ppac-test or ppac-test2 (default ppac-test2).

.PARAMETER Ppac
    Path to ppac.exe. Defaults to the Release build in this repo.

.PARAMETER Label
    Label for the test backup. Defaults to a timestamped name.
#>
[CmdletBinding()]
param(
    [ValidateSet('ppac-test', 'ppac-test2')]
    [string]$EnvironmentName = 'ppac-test2',

    [string]$Ppac = (Join-Path $PSScriptRoot '..\src\Ppac.Cli\bin\Release\net10.0\ppac.exe'),

    [string]$Label = ('ppac-cli-live-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Stop'
$AllowedNames = @('ppac-test', 'ppac-test2')
$Results = New-Object System.Collections.Generic.List[object]

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

$backupId = $null
$deleted = $false

try {
    if (-not (Test-Path -Path $Ppac)) {
        throw "ppac.exe not found at '$Ppac'. From the repo root run: dotnet build src\Ppac.Cli -c Release"
    }

    Write-Step 'Step 0: check the active profile'
    & $Ppac auth who
    if ($LASTEXITCODE -ne 0) { throw 'No active profile. Run: ppac auth create --deviceCode --tenant <tenant> --applicationId <app id>' }

    Write-Step "Step 1: find the environment named '$EnvironmentName'"
    $list = Invoke-PpacJson @('environment', 'list', '--json')
    if ($list.ExitCode -ne 0) { throw "environment list failed (exit $($list.ExitCode)): $($list.Error)" }

    $found = @(($list.Output | ConvertFrom-Json).value | Where-Object { $_.displayName -eq $EnvironmentName })
    if ($found.Count -ne 1) {
        throw "Expected exactly one environment named '$EnvironmentName' but found $($found.Count). Nothing was changed."
    }
    $target = $found[0]
    if ($AllowedNames -notcontains $target.displayName) {
        throw "Refusing to use '$($target.displayName)'. Only $($AllowedNames -join ' and ') are allowed."
    }
    $envId = $target.id
    Write-Host "Environment: $($target.displayName)  type: $($target.type)  state: $($target.state)"
    Write-Host "ID:          $envId"
    Add-Result 'find environment' 0 "$($target.displayName) ($envId)"

    Write-Step 'Step 2: create --whatif (sends nothing)'
    & $Ppac environment backup create --environment $envId --label $Label --whatif
    Add-Result 'create --whatif' $LASTEXITCODE 'printed the request'
    if ($LASTEXITCODE -ne 0) { throw 'create --whatif failed.' }

    Write-Host ''
    $answer = Read-Host "This will create ONE real backup in '$($target.displayName)'. Type the environment name to continue"
    if ($answer -ne $target.displayName) {
        Write-Host 'Stopped. Nothing was changed.'
        return
    }

    Write-Step 'Step 3: create (real)'
    $create = Invoke-PpacJson @('environment', 'backup', 'create', '--environment', $envId, '--label', $Label, '--json')
    Write-Host $create.Output
    if ($create.Error) { Write-Host $create.Error -ForegroundColor Yellow }
    Add-Result 'create' $create.ExitCode $(if ($create.ExitCode -eq 0) { 'created' } else { 'FAILED - see error above' })
    if ($create.ExitCode -ne 0) { throw "create failed (exit $($create.ExitCode)). Paste the output above so the permission result can be recorded." }

    $backupId = ($create.Output | ConvertFrom-Json).id
    if (-not $backupId) { throw 'create succeeded but the response had no id. Paste the output above.' }

    Write-Step 'Step 4: list'
    & $Ppac environment backup list --environment $envId
    $listExit = $LASTEXITCODE
    $after = Invoke-PpacJson @('environment', 'backup', 'list', '--environment', $envId, '--json')
    $present = $false
    if ($after.ExitCode -eq 0) {
        $present = @(($after.Output | ConvertFrom-Json).collection | Where-Object { $_.id -eq $backupId }).Count -eq 1
    }
    Add-Result 'list' $listExit $(if ($present) { 'new backup is listed' } else { 'NEW BACKUP NOT LISTED' })

    Write-Step 'Step 5: delete --whatif (sends nothing)'
    & $Ppac environment backup delete --environment $envId --backup $backupId --whatif
    Add-Result 'delete --whatif' $LASTEXITCODE 'printed the request'

    Write-Step 'Step 6: delete (real) - answer the prompt with y'
    & $Ppac environment backup delete --environment $envId --backup $backupId
    $deleteExit = $LASTEXITCODE
    if ($deleteExit -eq 0) { $deleted = $true }
    Add-Result 'delete' $deleteExit $(if ($deleted) { 'deleted' } else { 'NOT DELETED' })

    Write-Step 'Step 7: list again'
    $final = Invoke-PpacJson @('environment', 'backup', 'list', '--environment', $envId, '--json')
    $stillThere = $true
    if ($final.ExitCode -eq 0) {
        $stillThere = @(($final.Output | ConvertFrom-Json).collection | Where-Object { $_.id -eq $backupId }).Count -ge 1
    }
    Write-Host $(if ($stillThere) { 'The backup is still listed.' } else { 'The backup is gone from the list.' })
    Add-Result 'list after delete' $final.ExitCode $(if ($stillThere) { 'STILL LISTED' } else { 'gone' })
}
finally {
    Write-Host ''
    Write-Host '== Summary' -ForegroundColor Cyan
    $Results | Format-Table -AutoSize | Out-String | Write-Host

    if ($backupId -and -not $deleted) {
        Write-Host 'A test backup may still exist. To remove it:' -ForegroundColor Yellow
        Write-Host "  & '$Ppac' environment backup delete --environment $envId --backup $backupId"
    }
    Write-Host 'Paste this whole output back so the results can be recorded.'
}
