<#
.SYNOPSIS
    READ-ONLY probe: reads the same environment (and optionally a group's member list) many times in a row
    and shows whether the service gives the same answer every time.

.DESCRIPTION
    Why: in the live group test, reads of one environment disagreed within seconds - protectionLevel went
    Standard, Basic, Standard, and environmentGroupId appeared and disappeared - with no change in between,
    and the membership filter answered True, False, False, True. This script checks whether that still happens
    when nothing is changing. It sends GET requests only (environment get, environment list). It changes nothing.

    It prints one line per read and then a summary of the distinct answers. If every read agrees, the earlier
    mismatch was a short delay after a change. If the reads disagree while nothing is changing, the service
    answers from copies that are not in step with each other, and ppac must not rely on a single read.

    Prerequisites:
      - Windows PowerShell 5.1 or PowerShell 7+.
      - ppac built in Release mode, from the repo root:  dotnet build src\Ppac.Cli -c Release
      - An active ppac profile (check with: ppac auth who).
      - The delegated permission EnvironmentManagement.Environments.Read (already granted).

    Run from the repo root:
        .\scripts\probe-read-consistency.ps1
        .\scripts\probe-read-consistency.ps1 -GroupId aa21ba38-ae60-4df1-ba1f-846967389251
        .\scripts\probe-read-consistency.ps1 -Reads 40 -DelaySeconds 1

.PARAMETER EnvironmentName
    ppac-test or ppac-test2 (default ppac-test).

.PARAMETER GroupId
    Optional. Also reads the member list of this group on every round (environment list filtered on
    environmentGroupId) and reports whether the test environment is in it.

.PARAMETER Reads
    How many rounds to read (default 20, 1 to 200).

.PARAMETER DelaySeconds
    Seconds between rounds (default 2, 0 to 60).

.PARAMETER Ppac
    Path to ppac.exe. Defaults to the Release build in this repo.
#>
[CmdletBinding()]
param(
    [ValidateSet('ppac-test', 'ppac-test2')]
    [string]$EnvironmentName = 'ppac-test',

    [string]$GroupId,

    [ValidateRange(1, 200)]
    [int]$Reads = 20,

    [ValidateRange(0, 60)]
    [int]$DelaySeconds = 2,

    [string]$Ppac = (Join-Path $PSScriptRoot '..\src\Ppac.Cli\bin\Release\net10.0\ppac.exe')
)

$ErrorActionPreference = 'Stop'

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

# Pulls a string value out of the raw JSON text, so dates stay exactly as the service wrote them.
function Get-JsonText([string]$Json, [string]$Name) {
    $m = [regex]::Match($Json, '"' + [regex]::Escape($Name) + '"\s*:\s*"([^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return '-'
}

if (-not (Test-Path -Path $Ppac)) {
    throw "ppac.exe not found at '$Ppac'. From the repo root run: dotnet build src\Ppac.Cli -c Release"
}

Write-Host '== Find the environment (read only)' -ForegroundColor Cyan
$list = Invoke-PpacJson @('environment', 'list', '--json')
if ($list.ExitCode -ne 0) { Write-Host $list.Error -ForegroundColor Yellow; throw "environment list failed (exit $($list.ExitCode))." }
$found = @(($list.Output | ConvertFrom-Json).value | Where-Object { $_.displayName -eq $EnvironmentName })
if ($found.Count -ne 1) { throw "Expected exactly one environment named '$EnvironmentName', found $($found.Count)." }
$envId = $found[0].id
Write-Host "Environment: $EnvironmentName ($envId)"
if ($GroupId) { Write-Host "Group:       $GroupId" }

Write-Host ''
Write-Host "== $Reads rounds, $DelaySeconds s apart (GET requests only)" -ForegroundColor Cyan
$rows = New-Object System.Collections.Generic.List[object]
for ($i = 1; $i -le $Reads; $i++) {
    $get = Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')
    if ($get.ExitCode -eq 0) {
        $level = Get-JsonText $get.Output 'protectionLevel'
        $envGroup = Get-JsonText $get.Output 'environmentGroupId'
        $avail = Get-JsonText $get.Output 'availableFromDateTime'
    }
    else {
        $level = "ERR$($get.ExitCode)"; $envGroup = '-'; $avail = '-'
    }

    $byFilter = 'n/a'
    if ($GroupId) {
        $mem = Invoke-PpacJson @('environment', 'list', '--filter', "environmentGroupId eq '$GroupId'", '--json')
        if ($mem.ExitCode -eq 0) {
            $byFilter = if (@(($mem.Output | ConvertFrom-Json).value | Where-Object { $_.id -eq $envId }).Count -ge 1) { 'member' } else { 'not-member' }
        }
        else {
            $byFilter = "ERR$($mem.ExitCode)"
        }
    }

    $row = [pscustomobject]@{
        Round           = $i
        Time            = (Get-Date).ToUniversalTime().ToString('HH:mm:ss')
        ProtectionLevel = $level
        EnvGroupId      = if ($envGroup.Length -ge 8) { $envGroup.Substring(0, 8) } else { $envGroup }
        FilterSays      = $byFilter
        AvailableFrom   = $avail
    }
    $rows.Add($row)
    Write-Host ('{0,3} {1}  level={2,-9} envGroupId={3,-8} filter={4,-10} availableFrom={5}' -f $row.Round, $row.Time, $row.ProtectionLevel, $row.EnvGroupId, $row.FilterSays, $row.AvailableFrom)
    if ($i -lt $Reads -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
}

Write-Host ''
Write-Host '== Summary' -ForegroundColor Cyan
$combos = $rows | Group-Object { "level=$($_.ProtectionLevel) envGroupId=$($_.EnvGroupId) filter=$($_.FilterSays)" } | Sort-Object Count -Descending
$combos | ForEach-Object { Write-Host ('{0,3} of {1} reads: {2}' -f $_.Count, $rows.Count, $_.Name) }

# availableFromDateTime looks like "now minus 7 days" (an inference from P7D), so it should only move forward.
$back = 0
for ($i = 1; $i -lt $rows.Count; $i++) {
    $a = $rows[$i - 1].AvailableFrom; $b = $rows[$i].AvailableFrom
    if ($a -ne '-' -and $b -ne '-' -and ([datetime]::Parse($b).ToUniversalTime() -lt [datetime]::Parse($a).ToUniversalTime())) { $back++ }
}
Write-Host "availableFromDateTime went backwards $back time(s)."

if ($combos.Count -eq 1) {
    Write-Host 'RESULT: every read agreed. The earlier mismatch looks like a delay after a change, not a standing difference.' -ForegroundColor Green
}
else {
    Write-Host "RESULT: $($combos.Count) different answers while nothing was changing. Single reads cannot be trusted." -ForegroundColor Yellow
}
Write-Host 'Paste this whole output back so the results can be recorded.'
