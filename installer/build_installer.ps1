<#
.SYNOPSIS
    Builds BIMCamelSetup.exe locally from the CURRENT source tree — a local mirror of
    .github/workflows/release.yml's build steps, minus publishing a GitHub release.

.DESCRIPTION
    Compiles BIMCamel.dll once per supported Navisworks year (2024-2027; a plug-in must be
    compiled against the API of the release it runs in — see dist/README.md), stages a
    BIMCamel.bundle folder exactly like the release does, zips it, and embeds that zip into
    BIMCamelSetup.exe so double-clicking the resulting exe always installs whatever is
    currently in your working tree — no GitHub Actions or push required.

    Run this from a "Developer PowerShell for VS 2022" prompt (or any PowerShell — it only
    needs the .NET SDK, not Visual Studio itself) at the repository root:

        .\installer\build_installer.ps1

    Output: installer\output\BIMCamelSetup.exe

.PARAMETER Years
    Which Navisworks years to build into the bundle. Defaults to all four. Pass a subset
    (e.g. -Years 2025) to iterate faster while testing against one release.

.PARAMETER Version
    Numeric version stamped into the DLLs and the installer (e.g. 0.8.3). Defaults to the
    contents of dist/RELEASE_VERSION (stripping the leading "v"); falls back to a
    timestamped 0.0.0-dev version if that file is missing/unparseable.
#>
param(
    [string[]]$Years = @('2024', '2025', '2026', '2027'),
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root (this script lives in installer\)
Push-Location $root
try {
    if (-not $Version) {
        $verFile = Join-Path $root 'dist\RELEASE_VERSION'
        if (Test-Path $verFile) {
            $raw = (Get-Content $verFile -Raw).Trim()
            if ($raw -match '^v?(\d+\.\d+\.\d+)$') { $Version = $Matches[1] }
        }
        if (-not $Version) { $Version = "0.0.0-dev.$(Get-Date -Format 'yyyyMMddHHmmss')" }
    }
    Write-Host "=== Building BIMCamelSetup.exe, version $Version, years: $($Years -join ', ') ===" -ForegroundColor Cyan

    $staging = Join-Path $root 'installer\staging'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    foreach ($year in $Years) {
        Write-Host "--- Navisworks $year ---" -ForegroundColor Yellow
        # --no-incremental: sources are identical across years, so an up-to-date check would
        # otherwise skip recompiling against the next year's API version.
        dotnet build BIMCamel\BIMCamel.csproj -c Release --no-incremental `
            -p:NavisworksYear=$year -p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Navisworks $year" }

        $dll = Join-Path $root 'BIMCamel\bin\Release\net48\BIMCamel.dll'
        if (-not (Test-Path $dll)) { $dll = Join-Path $root 'BIMCamel\bin\x64\Release\net48\BIMCamel.dll' }
        if (-not (Test-Path $dll)) { throw "Missing build output for Navisworks ${year}: BIMCamel.dll" }

        $yearDir = Join-Path $staging "BIMCamel.bundle\$year"
        New-Item -ItemType Directory -Force -Path "$yearDir\en-US", "$yearDir\Resources" | Out-Null
        Copy-Item $dll -Destination $yearDir
        Copy-Item (Join-Path $root 'BIMCamel\BIMCamel.xaml') -Destination "$yearDir\en-US"
        Copy-Item (Join-Path $root 'BIMCamel\Resources\*.png') -Destination "$yearDir\Resources"
    }

    # Bundle manifest from the dist template, stamped with the version via XML DOM (never
    # string-edit this file — a corrupted <?xml?> declaration makes Navisworks silently
    # ignore the whole bundle).
    [xml]$doc = Get-Content (Join-Path $root 'dist\BIMCamel.bundle\PackageContents.xml') -Raw
    $doc.ApplicationPackage.Version = "$Version.0"
    $manifestPath = Join-Path $staging 'BIMCamel.bundle\PackageContents.xml'
    $doc.Save($manifestPath)

    Copy-Item (Join-Path $root 'dist\README.md') -Destination $staging
    Copy-Item (Join-Path $root 'LICENSE') -Destination $staging

    Write-Host '--- Packaging + embedding installer payload ---' -ForegroundColor Yellow
    $payload = Join-Path $staging 'payload.zip'   # inside installer\staging\, already gitignored
    if (Test-Path $payload) { Remove-Item $payload -Force }
    Compress-Archive -Path (Join-Path $staging 'BIMCamel.bundle') -DestinationPath $payload

    $outDir = Join-Path $root 'installer\output'
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    # --no-incremental is load-bearing: MSBuild's incremental check does not treat the added
    # EmbeddedResource as a change — an incremental build here would ship a payload-less exe.
    dotnet build installer\BIMCamel.Installer\BIMCamel.Installer.csproj -c Release `
        -p:BundlePayload=$payload -p:Version=$Version --no-incremental -o $outDir
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }

    $exe = Join-Path $outDir 'BIMCamelSetup.exe'
    if (-not (Test-Path $exe)) { throw 'Installer build produced no BIMCamelSetup.exe' }

    $payloadSize = (Get-Item $payload).Length
    $exeSize = (Get-Item $exe).Length
    if ($exeSize -le $payloadSize) { throw "BIMCamelSetup.exe ($exeSize bytes) is smaller than its payload ($payloadSize bytes) - the bundle was not embedded" }

    Remove-Item $payload -Force
    Write-Host "=== Done: $exe ($([math]::Round($exeSize / 1MB, 1)) MB) ===" -ForegroundColor Green
}
finally {
    Pop-Location
}
