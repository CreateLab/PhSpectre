# Local Google Play build: signed AAB. CI/GitHub keeps building plain APKs.
# Usage: .\scripts\build-play-aab.ps1 -VersionCode 17
# Keystore settings come from scripts\play-signing.local.ps1 (git-ignored) or env vars:
#   PHSPECTRE_KEYSTORE, PHSPECTRE_KEYSTORE_PASS, PHSPECTRE_KEY_ALIAS, PHSPECTRE_KEY_PASS
param(
    [Parameter(Mandatory)][int]$VersionCode,
    [string]$DisplayVersion
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $PSScriptRoot 'play-signing.local.ps1'
if (Test-Path $local) { . $local }

foreach ($v in 'PHSPECTRE_KEYSTORE','PHSPECTRE_KEYSTORE_PASS','PHSPECTRE_KEY_ALIAS','PHSPECTRE_KEY_PASS') {
    if (-not (Get-Item "env:$v" -ErrorAction SilentlyContinue)) { throw "Missing $v (set env var or scripts\play-signing.local.ps1)" }
}

$dotnetArgs = @(
    'publish', "$root\PhSpectre.Android\PhSpectre.Android.csproj", '-c', 'Release',
    '-p:PlayStoreBuild=true', '-p:AndroidPackageFormat=aab',
    "-p:ApplicationVersion=$VersionCode",
    '-p:AndroidKeyStore=true',
    "-p:AndroidSigningKeyStore=$env:PHSPECTRE_KEYSTORE",
    "-p:AndroidSigningStorePass=env:PHSPECTRE_KEYSTORE_PASS",
    "-p:AndroidSigningKeyAlias=$env:PHSPECTRE_KEY_ALIAS",
    "-p:AndroidSigningKeyPass=env:PHSPECTRE_KEY_PASS"
)
if (-not $DisplayVersion) {
    # Default to the version committed in the csproj so the in-app "PhSpectre x.y.z" matches the store.
    $csproj = [xml](Get-Content "$root\PhSpectre.Android\PhSpectre.Android.csproj")
    $DisplayVersion = ($csproj.Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { $_ } | Select-Object -First 1)
}
# AppVersionInfo reads InformationalVersion (local builds default to "0.0.0-dev"), so stamp it explicitly.
$dotnetArgs += "-p:ApplicationDisplayVersion=$DisplayVersion"
$dotnetArgs += "-p:InformationalVersion=$DisplayVersion"
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# A library can add permissions of its own to the merged manifest; the Play build must not reach the network.
$merged = Get-ChildItem "$root\PhSpectre.Android\obj\Release" -Recurse -Filter AndroidManifest.xml |
    Where-Object { $_.Directory.Name -eq 'android' } | Select-Object -First 1
if (-not $merged) { throw "Merged AndroidManifest.xml not found; cannot check the permissions" }
$network = Select-String -Path $merged.FullName -Pattern 'android\.permission\.(INTERNET|ACCESS_NETWORK_STATE)|TelemetryInitializer'
if ($network) { throw "The Play build must not use the network:`n$($network.Line -join "`n")" }
Get-ChildItem "$root\PhSpectre.Android\bin\Release" -Recurse -Filter *-Signed.aab | Select-Object -ExpandProperty FullName
