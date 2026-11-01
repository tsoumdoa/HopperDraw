$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts/verify-snapshot.ps1'
$shell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporaryRoot ('HopperDraw-hook-tests-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $fixture 'repo'
$log = Join-Path $fixture 'verifications.txt'
$oldLog = $env:HOPPERDRAW_SNAPSHOT_TEST_LOG
$passed = 0
$exitCode = 0

function Invoke-FixtureGit([string[]]$Arguments) {
    $result = & git -C $repository @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($Arguments[0])" }
    return $result
}
function Write-Text([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false)) }
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Run-Snapshot([string]$Mode, [string]$Updates = '') {
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    if ($Mode -eq 'Staged') {
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $runner -Staged -Repository $repository
    } else {
        $output = $Updates | & $shell -NoProfile -ExecutionPolicy Bypass -File $runner -Push -Repository $repository
    }
    $result = $LASTEXITCODE
    if ($result -ne 0 -and ($output -join "`n") -notmatch 'Snapshot verification failed') { throw ($output -join "`n") }
    return $result
}
function Pass([string]$Name) { $script:passed++; Write-Host "PASS: $Name" }

try {
    New-Item -ItemType Directory -Path (Join-Path $repository 'scripts') -Force | Out-Null
    Invoke-FixtureGit -Arguments @('init', '-q', '--initial-branch=main') | Out-Null
    Invoke-FixtureGit -Arguments @('config', 'user.name', 'Snapshot fixture') | Out-Null
    Invoke-FixtureGit -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null
    Invoke-FixtureGit -Arguments @('config', 'commit.gpgsign', 'false') | Out-Null
    Invoke-FixtureGit -Arguments @('config', 'core.hooksPath', (Join-Path $fixture 'no-hooks')) | Out-Null
    Invoke-FixtureGit -Arguments @('config', 'core.autocrlf', 'false') | Out-Null
    $stub = @'
param([switch]$Quick, [switch]$Full)
$marker = [IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'verdict.txt')).Trim()
$mode = if ($Quick) { 'Quick' } elseif ($Full) { 'Full' } else { throw 'Missing verification mode.' }
[IO.File]::AppendAllText($env:HOPPERDRAW_SNAPSHOT_TEST_LOG, "$mode $marker`n")
if ($marker -ne 'PASS') { exit 1 }
exit 0
'@
    $verifier = Join-Path $repository 'scripts/verify.ps1'
    $marker = Join-Path $repository 'verdict.txt'
    Write-Text $verifier $stub
    Write-Text $marker 'PASS'
    Invoke-FixtureGit -Arguments @('add', '--all') | Out-Null
    Invoke-FixtureGit -Arguments @('commit', '-qm', 'passing snapshot') | Out-Null
    $good = [string](Invoke-FixtureGit -Arguments @('rev-parse', 'HEAD'))
    $env:HOPPERDRAW_SNAPSHOT_TEST_LOG = $log

    Write-Text $marker 'FAIL'
    Invoke-FixtureGit -Arguments @('add', 'verdict.txt') | Out-Null
    Write-Text $marker 'PASS'
    Write-Text (Join-Path $repository 'untracked.txt') 'untouched'
    $indexBefore = [string](Invoke-FixtureGit -Arguments @('write-tree'))
    $workingBefore = (Invoke-FixtureGit -Arguments @('diff', '--binary')) -join "`n"
    Check ((Run-Snapshot 'Staged') -eq 1) 'A passing working file hid a failing staged snapshot.'
    Check ((Get-Content -LiteralPath $log -Raw).Trim() -eq 'Quick FAIL') 'Did not verify staged content with Quick.'
    Check ($indexBefore -eq [string](Invoke-FixtureGit -Arguments @('write-tree'))) 'Snapshot runner changed the index.'
    Check ($workingBefore -eq ((Invoke-FixtureGit -Arguments @('diff', '--binary')) -join "`n")) 'Snapshot runner changed working files.'
    Check ((Get-Content (Join-Path $repository 'untracked.txt') -Raw) -eq 'untouched') 'Snapshot runner touched untracked files.'
    Pass 'staged failure is rejected despite passing working content; index/worktree/untracked files unchanged'

    Write-Text $marker 'PASS'
    Invoke-FixtureGit -Arguments @('add', 'verdict.txt') | Out-Null
    Write-Text $marker 'FAIL'
    Write-Text $verifier 'exit 99' # Must execute the staged verifier, not this working-tree one.
    Check ((Run-Snapshot 'Staged') -eq 0) 'Unstaged breakage blocked a passing staged snapshot.'
    Check ((Get-Content -LiteralPath $log -Raw).Trim() -eq 'Quick PASS') 'Executed the wrong verifier.'
    Pass 'unstaged source/verifier breakage does not block a passing staged snapshot'

    Write-Text $verifier $stub
    Write-Text $marker 'FAIL'
    Invoke-FixtureGit -Arguments @('add', '--all') | Out-Null
    Invoke-FixtureGit -Arguments @('commit', '-qm', 'failing pushed snapshot') | Out-Null
    $bad = [string](Invoke-FixtureGit -Arguments @('rev-parse', 'HEAD'))
    Write-Text $marker 'PASS'
    $zeros = '0' * 40
    Check ((Run-Snapshot 'Push' "refs/heads/other $bad refs/heads/other $zeros") -eq 1) 'Passing working content hid a failing outgoing SHA.'
    Check ((Get-Content -LiteralPath $log -Raw).Trim() -eq 'Full FAIL') 'Outgoing SHA did not run Full.'
    Pass 'pre-push checks the outgoing SHA rather than the current working file'

    Write-Text $marker 'FAIL'
    Check ((Run-Snapshot 'Push' "refs/heads/good $good refs/heads/good $zeros") -eq 0) 'Wrong checkout blocked a passing outgoing SHA.'
    Check ((Get-Content -LiteralPath $log -Raw).Trim() -eq 'Full PASS') 'Did not export the passing outgoing SHA.'
    Pass 'pre-push accepts a passing outgoing SHA even when current HEAD/working content fails'

    $updates = "refs/heads/good $good refs/heads/good $zeros`nrefs/heads/bad $bad refs/heads/bad $zeros"
    Check ((Run-Snapshot 'Push' $updates) -eq 1) 'A failing second ref was not verified.'
    Check (@(Get-Content -LiteralPath $log).Count -eq 2) 'Did not check both unique outgoing commits.'
    Pass 'pre-push checks every distinct outgoing ref tip'

    $updates = "refs/heads/a $good refs/heads/a $zeros`nrefs/heads/b $good refs/heads/b $zeros"
    Check ((Run-Snapshot 'Push' $updates) -eq 0) 'Duplicate passing tips failed.'
    Check (@(Get-Content -LiteralPath $log).Count -eq 1) 'Duplicate tips repeated verification.'
    Pass 'identical outgoing tips reuse verification'

    Check ((Run-Snapshot 'Push' "(delete) $zeros refs/heads/old $bad") -eq 0) 'Deletion should not verify nonexistent content.'
    Check (!(Test-Path -LiteralPath $log)) 'Deletion invoked the verifier.'
    Pass 'deletion-only pushes do not run builds'
} catch {
    Write-Host "FAIL: hook snapshot tests: $_" -ForegroundColor Red
    $exitCode = 1
} finally {
    $env:HOPPERDRAW_SNAPSHOT_TEST_LOG = $oldLog
    if (Test-Path -LiteralPath $fixture) {
        $resolved = [IO.Path]::GetFullPath($fixture)
        if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\', '/') -ne $temporaryRoot.TrimEnd('\', '/') -or
            [IO.Path]::GetFileName($resolved) -notmatch '^HopperDraw-hook-tests-[0-9a-f]{32}$') {
            throw "Refusing to remove unexpected test directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host "Hook snapshots: $passed passed."
exit $exitCode
