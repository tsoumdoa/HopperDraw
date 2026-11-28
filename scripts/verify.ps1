[CmdletBinding(DefaultParameterSetName = 'Quick')]
param(
    [Parameter(ParameterSetName = 'Quick')][switch]$Quick,
    [Parameter(ParameterSetName = 'Full')][switch]$Full
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project = Get-Content hopperborder.csproj
    $version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $manifest = (Select-String -Path yak/manifest.yml -Pattern '^version:\s*(\S+)').Matches.Groups[1].Value
    if ($version -ne $manifest) { throw "Version mismatch: csproj=$version, manifest=$manifest" }
    Write-Host "Version check passed: $version"
    $configuration = if ($Full) { 'Release' } else { 'Debug' }
    $frameworks = if ($Full) {
        ([string]($project.Project.PropertyGroup.TargetFrameworks | Where-Object { $_ } | Select-Object -First 1)).Split(';')
    } else { @('net8.0-windows') }
    $builds = 0
    foreach ($framework in $frameworks) {
        Write-Host "Building $framework ($configuration)..."
        & dotnet build hopperborder.csproj -f $framework -c $configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $framework" }
        $builds++
    }
    $tests = @(Get-ChildItem Tests -Recurse -Filter *.csproj | Sort-Object FullName)
    foreach ($test in $tests) {
        Write-Host "Testing $($test.Directory.Name) ($configuration)..."
        & dotnet run --project $test.FullName -c $configuration --no-launch-profile
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($test.Directory.Name) (exit $LASTEXITCODE)" }
    }
    Write-Host "VERIFY PASSED: $builds plugin builds, $($tests.Count) test projects, version $version."
} catch {
    Write-Host "VERIFY FAILED: $_" -ForegroundColor Red
    exit 1
} finally { Pop-Location }
exit 0
