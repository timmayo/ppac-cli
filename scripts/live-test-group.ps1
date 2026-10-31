<#
.SYNOPSIS
    Live test of 'ppac environment group' (create, duplicate names, add/remove environment, delete)
    using throwaway groups and ONE test environment.

.DESCRIPTION
    What it does, in order:
      1. Creates an EMPTY group named ppac-test-group-<timestamp>.
      2. Description and duplicate-name tests: create without a description (must be refused), create a second group
         with the SAME name (must be refused by ppac), then again with --allow-duplicate (must work).
      3. Adds ONE test environment (ppac-test or ppac-test2 only) to the first group.
      4. Tries to DELETE the first group while it still holds the environment (the service may
         refuse, or may delete it - this is the test).
      5. If the group is still there: removes the environment and deletes the group.
      6. Deletes the second group if it was created. Checks everything is back to the start.

    Safety: it only deletes group IDs returned by its own create calls, and only if the name still
    starts with 'ppac-test-group-'. It never touches existing groups. It stops before changing
    anything if the test environment is already in a group. The only environment it touches is the
    one named by -EnvironmentName (ppac-test or ppac-test2).

    Prerequisites:
      - Windows PowerShell 5.1 or PowerShell 7+.
      - .NET 10 SDK (to build ppac): https://dotnet.microsoft.com/download
      - ppac built in Release mode, from the repo root:
            dotnet build src\Ppac.Cli -c Release
      - An active ppac profile (check with: ppac auth who).
      - The Entra app registration has the delegated Power Platform API permissions
        EnvironmentManagement.Groups.ReadWrite and EnvironmentManagement.Environments.Read, with
        admin consent, and you signed in again after adding them:
            ppac auth create --deviceCode --tenant <tenant> --applicationId <app id>
      - The signed-in user is a Power Platform tenant administrator.
      - The test environment must be one the service allows in a group. The admin center says a
        group can only hold MANAGED environments with Dataverse. If the add fails with a 400, the
        script reports it, cleans up, and you can try the other test environment.

    Run from the repo root:
        .\scripts\live-test-group.ps1
        .\scripts\live-test-group.ps1 -EnvironmentName ppac-test
        .\scripts\live-test-group.ps1 -EnvironmentName ppac-test -EnableManaged   # full test, incl. delete-with-environment
        .\scripts\live-test-group.ps1 -SkipEnvironmentTests     # groups only
        .\scripts\live-test-group.ps1 -SkipDuplicateTest

.PARAMETER EnvironmentName
    ppac-test or ppac-test2 (default ppac-test2).

.PARAMETER Ppac
    Path to ppac.exe. Defaults to the Release build in this repo.

.PARAMETER SkipDuplicateTest
    Do not create the second, same-named group.

.PARAMETER EnableManaged
    A group can only hold a managed environment. If the test environment is not managed, turn Managed Environments
    on for it first ('ppac environment managed enable'), and turn it off again at the end. Never leaves a managed
    environment behind that the script turned on, unless a cleanup step fails (the script then prints the command).

.PARAMETER SkipEnvironmentTests
    Do not add an environment or test deleting a group that holds one.
#>
[CmdletBinding()]
param(
    [ValidateSet('ppac-test', 'ppac-test2')]
    [string]$EnvironmentName = 'ppac-test2',

    [string]$Ppac = (Join-Path $PSScriptRoot '..\src\Ppac.Cli\bin\Release\net10.0\ppac.exe'),

    [switch]$SkipDuplicateTest,

    [switch]$SkipEnvironmentTests,

    [switch]$EnableManaged
)

$ErrorActionPreference = 'Stop'
$Prefix = 'ppac-test-group-'
$AllowedEnvironments = @('ppac-test', 'ppac-test2')
$GroupName = $Prefix + (Get-Date -Format 'yyyyMMdd-HHmmss')
$Results = New-Object System.Collections.Generic.List[object]
$Created = New-Object System.Collections.Generic.List[string]
$Deleted = New-Object System.Collections.Generic.List[string]
$envId = $null
$groupA = $null
$envMayBeInGroup = $false
$deletedWithEnv = $false
$managedByScript = $false

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

# Is the environment in the group? Uses ppac's list filter. Returns $true, $false, or $null when it cannot be checked.
function Test-Member([string]$GroupId, [string]$EnvironmentId) {
    $r = Invoke-PpacJson @('environment', 'list', '--filter', "environmentGroupId eq '$GroupId'", '--json')
    if ($r.ExitCode -ne 0) { return $null }
    return (@(($r.Output | ConvertFrom-Json).value | Where-Object { $_.id -eq $EnvironmentId }).Count -ge 1)
}

function Remove-TestGroup([string]$GroupId) {
    # Safety: only delete a group we created, and only if its name still has the test prefix.
    $check = Invoke-PpacJson @('environment', 'group', 'get', '--group', $GroupId, '--json')
    if ($check.ExitCode -ne 0) { return $check }
    $checkName = ($check.Output | ConvertFrom-Json).displayName
    if (-not $checkName.StartsWith($Prefix)) { throw "Refusing to delete '$checkName': it does not start with '$Prefix'." }
    Write-Host "Deleting $GroupId - answer the prompt with y"
    & $Ppac environment group delete --group $GroupId
    $code = $LASTEXITCODE
    if ($code -eq 0) { $Deleted.Add($GroupId) }
    return [pscustomobject]@{ ExitCode = $code }
}

try {
    if (-not (Test-Path -Path $Ppac)) {
        throw "ppac.exe not found at '$Ppac'. From the repo root run: dotnet build src\Ppac.Cli -c Release"
    }

    Write-Step 'Step 0: check the active profile'
    & $Ppac auth who
    if ($LASTEXITCODE -ne 0) { throw 'No active profile. Run: ppac auth create --deviceCode --tenant <tenant> --applicationId <app id>' }

    Write-Step 'Step 1: list the existing groups (read only)'
    $before = Invoke-PpacJson @('environment', 'group', 'list', '--json')
    if ($before.ExitCode -ne 0) { throw "group list failed (exit $($before.ExitCode)): $($before.Error)" }
    $existing = @(($before.Output | ConvertFrom-Json).value)
    Write-Host "Existing groups: $($existing.Count)"
    if (@($existing | Where-Object { $_.displayName -eq $GroupName }).Count -gt 0) {
        throw "A group named '$GroupName' already exists. Nothing was changed."
    }
    Add-Result 'list before' 0 "$($existing.Count) groups"

    if (-not $SkipEnvironmentTests) {
        Write-Step "Step 1b: find the environment named '$EnvironmentName' and check it is not already in a group"
        $envList = Invoke-PpacJson @('environment', 'list', '--json')
        if ($envList.ExitCode -ne 0) { throw "environment list failed (exit $($envList.ExitCode)): $($envList.Error)" }
        $found = @(($envList.Output | ConvertFrom-Json).value | Where-Object { $_.displayName -eq $EnvironmentName })
        if ($found.Count -ne 1) { throw "Expected exactly one environment named '$EnvironmentName' but found $($found.Count). Nothing was changed." }
        if ($AllowedEnvironments -notcontains $found[0].displayName) { throw "Refusing to use '$($found[0].displayName)'." }
        $envId = $found[0].id
        Write-Host "Environment: $($found[0].displayName)  type: $($found[0].type)  state: $($found[0].state)"
        Write-Host "ID:          $envId"

        Write-Host 'Environment as returned by get (BEFORE):'
        $envBefore = Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')
        Write-Host $envBefore.Output

        $alreadyIn = @()
        $filterWorks = $true
        foreach ($g in $existing) {
            $m = Test-Member $g.id $envId
            if ($null -eq $m) { $filterWorks = $false }
            elseif ($m) { $alreadyIn += $g.displayName }
        }
        Write-Host "Membership filter accepted by the service: $filterWorks"
        if ($alreadyIn.Count -gt 0) {
            throw "'$EnvironmentName' is already in group(s): $($alreadyIn -join ', '). Nothing was changed."
        }
        Add-Result 'find environment' 0 "$EnvironmentName ($envId); in no existing group; membership filter accepted: $filterWorks"
    }

    Write-Step 'Step 2: create --whatif (sends nothing)'
    & $Ppac environment group create --name $GroupName --description 'ppac live test - safe to delete' --whatif
    Add-Result 'create --whatif' $LASTEXITCODE 'printed the request'
    if ($LASTEXITCODE -ne 0) { throw 'create --whatif failed.' }

    Write-Host ''
    if ($SkipEnvironmentTests) {
        $expected = $GroupName
        $prompt = "This will create empty group(s) named '$GroupName' and delete them again. Type the group name to continue"
    }
    else {
        $expected = $EnvironmentName
        $managedText = if ($EnableManaged) { "turn Managed Environments on for it if it is not managed (and off again at the end), " } else { "" }
        $prompt = "This will create empty group(s) named '$GroupName', ${managedText}add environment '$EnvironmentName' to one of them, try to delete that group while it holds the environment, and then clean up. Type the environment name to continue"
    }
    $answer = Read-Host $prompt
    if ($answer -ne $expected) {
        Write-Host 'Stopped. Nothing was changed.'
        return
    }

    if (-not $SkipEnvironmentTests -and $EnableManaged) {
        Write-Step 'Step 2b: make sure the test environment is managed (a group can only hold managed environments)'
        $levelBefore = ($envBefore.Output | ConvertFrom-Json).protectionLevel
        if ($levelBefore -eq 'Basic') {
            $en = Invoke-PpacJson @('environment', 'managed', 'enable', '--environment', $envId, '--json')
            Show-Result $en
            Add-Result 'enable managed' $en.ExitCode $(if ($en.ExitCode -eq 0) { 'Managed Environments turned on for the test' } else { 'FAILED - see above' })
            if ($en.ExitCode -ne 0) { throw 'Could not turn on Managed Environments. Nothing else was changed.' }
            $managedByScript = $true
        }
        else {
            Write-Host "protectionLevel is '$levelBefore': already managed, leaving it as it is."
            Add-Result 'enable managed' 0 "already managed (protectionLevel '$levelBefore'); left as is"
        }
    }

    Write-Step 'Step 3: create group A (real)'
    $create = Invoke-PpacJson @('environment', 'group', 'create', '--name', $GroupName, '--description', 'ppac live test - safe to delete', '--json')
    Show-Result $create
    Add-Result 'create A' $create.ExitCode $(if ($create.ExitCode -eq 0) { 'created' } else { 'FAILED - see above' })
    if ($create.ExitCode -ne 0) { throw 'create failed. Paste the output above so the result can be recorded.' }
    $groupA = ($create.Output | ConvertFrom-Json).id
    if (-not $groupA) { throw 'create succeeded but the response had no id. Run: ppac environment group list   and paste the output.' }
    $Created.Add($groupA)

    Write-Step 'Step 4: get group A, then list'
    & $Ppac environment group get --group $groupA
    $getExit = $LASTEXITCODE
    $mid = Invoke-PpacJson @('environment', 'group', 'list', '--json')
    $listed = $false
    if ($mid.ExitCode -eq 0) { $listed = @(($mid.Output | ConvertFrom-Json).value | Where-Object { $_.id -eq $groupA }).Count -eq 1 }
    Add-Result 'get + list after create' $getExit $(if ($listed) { 'group A is listed' } else { 'GROUP A NOT LISTED' })

    $groupB = $null
    if (-not $SkipDuplicateTest) {
        Write-Step 'Step 5a: NO DESCRIPTION test - create without --description (the service rejects it, so ppac must refuse before sending anything)'
        $nodesc = Invoke-PpacJson @('environment', 'group', 'create', '--name', "$GroupName-nodesc", '--json')
        Show-Result $nodesc
        Add-Result 'no description' $nodesc.ExitCode $(if ($nodesc.ExitCode -eq 2) { 'refused locally (exit 2, as expected)' } else { 'UNEXPECTED - expected exit 2' })

        Write-Step 'Step 5b: DUPLICATE NAME guard - create a group with the same name as A (ppac must refuse)'
        $dup = Invoke-PpacJson @('environment', 'group', 'create', '--name', $GroupName, '--description', 'ppac live test - duplicate, safe to delete', '--json')
        Show-Result $dup
        if ($dup.ExitCode -eq 0) {
            $groupB = ($dup.Output | ConvertFrom-Json).id
            if ($groupB) { $Created.Add($groupB) }
            Add-Result 'duplicate name guard' 0 'GUARD FAILED: a second group with the same name was created'
        }
        else {
            Add-Result 'duplicate name guard' $dup.ExitCode $(if ($dup.ExitCode -eq 1 -and $dup.Error -match 'already exists') { 'refused (exit 1, as expected)' } else { 'UNEXPECTED - see message above' })
        }

        Write-Step 'Step 5c: --allow-duplicate - create a second group with the same name on purpose'
        $dup2 = Invoke-PpacJson @('environment', 'group', 'create', '--name', $GroupName, '--description', 'ppac live test - allowed duplicate, safe to delete', '--allow-duplicate', '--json')
        Show-Result $dup2
        if ($dup2.ExitCode -eq 0) {
            $groupC = ($dup2.Output | ConvertFrom-Json).id
            if ($groupC) { $Created.Add($groupC) }
            Add-Result 'allow-duplicate' 0 'created (as expected; the service allows duplicate names)'
        }
        else {
            Add-Result 'allow-duplicate' $dup2.ExitCode 'FAILED - see message above'
        }
    }

    if (-not $SkipEnvironmentTests) {
        Write-Step 'Step 6: add-environment --whatif (sends nothing)'
        & $Ppac environment group add-environment --group $groupA --environment $envId --whatif
        Add-Result 'add-environment --whatif' $LASTEXITCODE 'printed the request'

        Write-Step 'Step 7: add-environment (real; waits for the change to show up)'
        $add = Invoke-PpacJson @('environment', 'group', 'add-environment', '--group', $groupA, '--environment', $envId, '--json')
        Show-Result $add
        $addOk = ($add.ExitCode -eq 0)
        Add-Result 'add-environment' $add.ExitCode $(if ($addOk) { 'accepted - see the membership field above' } else { 'FAILED - see above (a 400 may mean the environment is not a managed environment)' })

        Write-Host 'Environment as returned by get (AFTER the add):'
        $envAfterAdd = Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')
        Write-Host $envAfterAdd.Output
        $member = Test-Member $groupA $envId
        Write-Host "Member of group A according to ONE read of the list filter: $member (the service can answer the same read differently from one moment to the next, so this line is informational)"
        Add-Result 'membership after add (one read)' 0 "filter says: $member"

        # Only treat the environment as possibly in the group if the add was accepted or the filter says so.
        $envMayBeInGroup = ($addOk -or ($member -eq $true))
        if (-not $addOk) {
            Write-Host ''
            Write-Host 'The add did not succeed, so the DELETE-WITH-ENVIRONMENT test could NOT run in this pass.' -ForegroundColor Yellow
            Write-Host 'If the message above says the environment is not managed, run again with -EnableManaged.' -ForegroundColor Yellow
            Add-Result 'delete-with-environment test' 'n/a' 'NOT RUN: the environment could not be added to the group'
        }

        if ($addOk) {
            Write-Step 'Step 8: DELETE group A while it holds the environment (this is the test; --yes, no prompt)'
            $del = Invoke-PpacJson @('environment', 'group', 'delete', '--group', $groupA, '--yes', '--json')
            Show-Result $del
            $stillThere = Invoke-PpacJson @('environment', 'group', 'get', '--group', $groupA, '--json')
            Write-Host "group A still readable after the delete attempt: exit $($stillThere.ExitCode)"
            $memberAfter = Test-Member $groupA $envId
            Write-Host "Environment reported as a member of group A by ONE read of the filter: $memberAfter (informational; single reads can be wrong)"
            Write-Host 'Environment as returned by get (AFTER the delete attempt):'
            Write-Host ((Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')).Output)

            if ($del.ExitCode -eq 0 -and $stillThere.ExitCode -ne 0) {
                $Deleted.Add($groupA)
                $deletedWithEnv = $true
                Add-Result 'delete A while it holds the environment' 0 "UNEXPECTED: DELETE SUCCEEDED; group A is gone; environment still a member per filter: $memberAfter"
            }
            elseif ($del.ExitCode -eq 1 -and $stillThere.ExitCode -eq 0 -and $del.Output -match 'groupHasEnvironments') {
                Add-Result 'delete A while it holds the environment' 1 'refused by ppac before sending anything (exit 1, as expected); group A and the membership are unchanged'
            }
            elseif ($del.ExitCode -eq 1 -and $stillThere.ExitCode -eq 0 -and $del.Output -match 'refusedByService') {
                Add-Result 'delete A while it holds the environment' 1 'refused by the SERVICE (Conflict); ppac explained it (the membership reads did not show the environment, so its own check did not fire); group A is unchanged'
            }
            else {
                Add-Result 'delete A while it holds the environment' $del.ExitCode "UNEXPECTED (group A still readable: exit $($stillThere.ExitCode)); see the message above"
            }
        }

        if ($envMayBeInGroup -and $Created.Contains($groupA) -and -not $Deleted.Contains($groupA)) {
            Write-Step 'Step 9: remove-environment --whatif, then real (answer the prompt with y)'
            & $Ppac environment group remove-environment --group $groupA --environment $envId --whatif
            Add-Result 'remove-environment --whatif' $LASTEXITCODE 'printed the request'
            & $Ppac environment group remove-environment --group $groupA --environment $envId
            $rm = $LASTEXITCODE
            Add-Result 'remove-environment' $rm $(if ($rm -eq 0) { 'removed' } else { 'NOT REMOVED - see above' })
            if ($rm -eq 0) { $envMayBeInGroup = $false }
            Write-Host 'Environment as returned by get (AFTER the remove):'
            Write-Host ((Invoke-PpacJson @('environment', 'get', '--environment', $envId, '--json')).Output)
        }
    }

    foreach ($id in @($Created)) {
        if ($Deleted.Contains($id)) { continue }
        Write-Step "Step 10: delete test group $id"
        $r = Remove-TestGroup $id
        Add-Result "delete $id" $r.ExitCode $(if ($r.ExitCode -eq 0) { 'deleted' } else { 'NOT DELETED' })
    }

    if ($managedByScript) {
        Write-Step 'Step 10b: put the environment back (turn Managed Environments off again)'
        if ($envMayBeInGroup) {
            Write-Host 'The environment may still be in a test group, so Managed Environments was NOT turned off.' -ForegroundColor Yellow
            Add-Result 'disable managed' 'n/a' 'NOT RUN: environment may still be in a group'
        }
        else {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $dis = Invoke-PpacJson @('environment', 'managed', 'disable', '--environment', $envId, '--yes', '--json')
            $sw.Stop()
            Show-Result $dis
            Add-Result 'disable managed' $dis.ExitCode $(if ($dis.ExitCode -eq 0) { ('turned off again; {0:N1}s' -f $sw.Elapsed.TotalSeconds) } else { 'FAILED - see above' })
            if ($dis.ExitCode -eq 0) { $managedByScript = $false }
        }
    }

    Write-Step 'Step 11: get after delete, delete again, and list at the end'
    $gone = Invoke-PpacJson @('environment', 'group', 'get', '--group', $groupA, '--json')
    Write-Host "get group A: exit $($gone.ExitCode)"
    Add-Result 'get after delete' $gone.ExitCode $(if ($gone.ExitCode -eq 4) { 'not found (as expected)' } else { 'UNEXPECTED - see above' })
    $again = Invoke-PpacJson @('environment', 'group', 'delete', '--group', $groupA, '--yes')
    Write-Host "delete again: exit $($again.ExitCode)  $($again.Error)"
    Add-Result 'delete again' $again.ExitCode $(if ($again.ExitCode -eq 4) { 'not found (as expected)' } else { 'UNEXPECTED - see above' })
    $final = Invoke-PpacJson @('environment', 'group', 'list', '--json')
    if ($final.ExitCode -eq 0) {
        $count = @(($final.Output | ConvertFrom-Json).value).Count
        Add-Result 'list at the end' 0 "$count groups (started with $($existing.Count))"
    }
}
finally {
    Write-Host ''
    Write-Host '== Summary' -ForegroundColor Cyan
    $Results | Format-Table -AutoSize -Wrap | Out-String | Write-Host

    foreach ($id in $Created) {
        if (-not $Deleted.Contains($id)) {
            Write-Host "A test group may still exist ($id). To remove it:" -ForegroundColor Yellow
            if ($envMayBeInGroup -and $id -eq $groupA -and $envId) {
                Write-Host "  & '$Ppac' environment group remove-environment --group $id --environment $envId"
            }
            Write-Host "  & '$Ppac' environment group delete --group $id"
        }
    }
    if ($managedByScript -and $envId) {
        Write-Host "Managed Environments is still ON for '$EnvironmentName'. Turn it off with:" -ForegroundColor Yellow
        Write-Host "  & '$Ppac' environment managed disable --environment $envId"
    }
    if ($deletedWithEnv) {
        Write-Host "Group A was deleted while the environment was in it. Check the environment's group membership in the admin center and paste the output above." -ForegroundColor Yellow
    }
    Write-Host 'Paste this whole output back so the results can be recorded.'
}
