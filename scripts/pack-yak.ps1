[CmdletBinding()]
param(
    # Path to the Yak CLI. Defaults to $env:YAK_PATH, then the Rhino 8 install location.
    [string]$Yak,
    # Skip scripts/verify.ps1 -Full (use only when it has just passed for this commit).
    [switch]$SkipVerify
)
# Builds the Windows and macOS Yak distributions into artifacts/yak.
#   win: net48, net7.0 (net7.0-windows build) and net8.0 (net8.0-windows build), as in earlier releases.
#   mac: net7.0 (portable net7.0 build); Rhino 8 for Mac loads it on .NET 7 and .NET 8.
# Separate distributions keep the Windows payload independent of the Mac preview.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $Yak) {
        $Yak = if ($env:YAK_PATH) { $env:YAK_PATH } else { 'C:\Program Files\Rhino 8\System\Yak.exe' }
    }
    if (-not (Test-Path $Yak)) { throw "Yak CLI not found at '$Yak'. Pass -Yak or set YAK_PATH." }

    [xml]$project = Get-Content hopperborder.csproj
    $version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)

    if ($SkipVerify) {
        foreach ($framework in 'net48', 'net7.0-windows', 'net7.0', 'net8.0-windows') {
            & dotnet build hopperborder.csproj -f $framework -c Release --nologo -v minimal
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $framework" }
        }
    } else {
        & pwsh -NoProfile -File scripts/verify.ps1 -Full
        if ($LASTEXITCODE -ne 0) { throw 'verify.ps1 -Full failed' }
    }

    $layouts = [ordered]@{
        win = [ordered]@{ 'net48' = 'net48'; 'net7.0' = 'net7.0-windows'; 'net8.0' = 'net8.0-windows' }
        mac = [ordered]@{ 'net7.0' = 'net7.0' }
    }

    $output = Join-Path $root 'artifacts\yak'
    New-Item -ItemType Directory -Force $output | Out-Null
    foreach ($platform in $layouts.Keys) {
        $stage = Join-Path $root "artifacts\yak-stage\$platform"
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        New-Item -ItemType Directory -Force (Join-Path $stage 'misc') | Out-Null
        Copy-Item yak/manifest.yml (Join-Path $stage 'manifest.yml')
        Copy-Item yak/icon.png (Join-Path $stage 'icon.png')
        Copy-Item README.md (Join-Path $stage 'misc\README.md')
        foreach ($folder in $layouts[$platform].Keys) {
            $built = "bin\Release\$($layouts[$platform][$folder])\hopperborder.gha"
            if (-not (Test-Path $built)) { throw "Missing build output: $built" }
            New-Item -ItemType Directory -Force (Join-Path $stage $folder) | Out-Null
            Copy-Item $built (Join-Path $stage "$folder\hopperborder.gha")
        }
        Push-Location $stage
        try {
            & $Yak build --platform $platform
            if ($LASTEXITCODE -ne 0) { throw "yak build failed for $platform" }
            $package = Get-ChildItem -Filter "hopperdraw-$version-*-$platform.yak" | Select-Object -First 1
            if (-not $package) { throw "yak build produced no $platform package for $version" }
            Move-Item -Force $package.FullName (Join-Path $output $package.Name)
            Write-Host "Packed $($package.Name)"
        } finally { Pop-Location }
    }
    Write-Host "PACK PASSED: $output"
} catch {
    Write-Host "PACK FAILED: $_" -ForegroundColor Red
    exit 1
} finally { Pop-Location }
exit 0
