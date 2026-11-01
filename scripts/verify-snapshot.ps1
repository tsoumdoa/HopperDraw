[CmdletBinding(DefaultParameterSetName = 'Staged')]
param(
    [Parameter(ParameterSetName = 'Staged', Mandatory = $true)][switch]$Staged,
    [Parameter(ParameterSetName = 'Push', Mandatory = $true)][switch]$Push,
    [string]$Repository = (Get-Location).Path
)
$ErrorActionPreference = 'Stop'
$exitCode = 0
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$snapshot = $null
$shell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })

function Invoke-GitChecked([string[]]$GitArguments) {
    $result = & git -C $Repository @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArguments[0]) failed (exit $LASTEXITCODE)." }
    return $result
}

function Invoke-SnapshotVerification([string]$Directory, [string]$Mode) {
    $verifier = Join-Path $Directory 'scripts/verify.ps1'
    if (!(Test-Path -LiteralPath $verifier -PathType Leaf)) { throw 'Snapshot has no scripts/verify.ps1.' }
    # Hooks can inherit GIT_INDEX_FILE, GIT_DIR, etc. Respect those during export, but
    # do not let them redirect git commands issued by tests inside the snapshot.
    $localVariables = @(Invoke-GitChecked -GitArguments @('rev-parse', '--local-env-vars'))
    $savedVariables = @{}
    foreach ($name in $localVariables) {
        $savedVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
    Push-Location $Directory
    try {
        & $shell -NoProfile -ExecutionPolicy Bypass -File $verifier "-$Mode"
        if ($LASTEXITCODE -ne 0) { throw "Snapshot verification failed (exit $LASTEXITCODE)." }
    } finally {
        Pop-Location
        foreach ($name in $localVariables) {
            if ($null -eq $savedVariables[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $savedVariables[$name], 'Process') }
        }
    }
}

try {
    $Repository = [string](Invoke-GitChecked -GitArguments @('rev-parse', '--show-toplevel'))
    $snapshot = Join-Path $temporaryRoot ('HopperDraw-verify-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    if ($Staged) {
        # Reject unresolved index entries before exporting; never stash or rewrite user files.
        $tree = Invoke-GitChecked -GitArguments @('write-tree')
        Write-Host "Verifying staged tree $tree (Quick)..."
        $directory = Join-Path $snapshot 'staged'
        New-Item -ItemType Directory -Path $directory | Out-Null
        $prefix = $directory.Replace('\', '/') + '/'
        Invoke-GitChecked -GitArguments @('checkout-index', '--all', '--ignore-skip-worktree-bits', "--prefix=$prefix") | Out-Null
        Invoke-SnapshotVerification $directory 'Quick'
    } else {
        $seen = @{}
        # pre-push supplies: local-ref local-sha remote-ref remote-sha, one update per line.
        foreach ($line in ([Console]::In.ReadToEnd() -split '\r?\n')) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $fields = $line.Trim() -split '\s+'
            if ($fields.Count -ne 4) { throw 'Invalid pre-push ref update on stdin.' }
            $sha = $fields[1]
            if ($sha -match '^0+$') { continue } # Deletion has no outgoing content.
            if ($sha -notmatch '^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$') { throw 'Invalid outgoing object ID.' }
            $commit = [string](Invoke-GitChecked -GitArguments @('rev-parse', '--verify', "${sha}^{commit}"))
            if ($seen.ContainsKey($commit)) { continue }
            $seen[$commit] = $true
            Write-Host "Verifying pushed commit $commit (Full)..."
            $archive = Join-Path $snapshot "$commit.zip"
            $directory = Join-Path $snapshot $commit
            Invoke-GitChecked -GitArguments @('archive', '--format=zip', "--output=$archive", $commit) | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $directory
            Invoke-SnapshotVerification $directory 'Full'
        }
        if ($seen.Count -eq 0) { Write-Host 'No outgoing commit content to verify.' }
    }
} catch {
    Write-Host "SNAPSHOT VERIFY FAILED: $_" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($snapshot -and (Test-Path -LiteralPath $snapshot)) {
        # Recursive removal is restricted to the unique directory created above.
        $resolved = [IO.Path]::GetFullPath($snapshot)
        if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\', '/') -ne $temporaryRoot.TrimEnd('\', '/') -or
            [IO.Path]::GetFileName($resolved) -notmatch '^HopperDraw-verify-[0-9a-f]{32}$') {
            throw "Refusing to remove unexpected snapshot directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
exit $exitCode
