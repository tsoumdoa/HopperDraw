$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'Could not install hooks.' }
    Write-Host 'Installed pre-commit (Quick) and pre-push (Full) hooks.'
    Write-Host 'Bypass deliberately with git commit --no-verify or git push --no-verify.'
} finally { Pop-Location }
