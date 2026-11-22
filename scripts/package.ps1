<#
.SYNOPSIS
    Builds the plugin, packages it for Jellyfin, and adds the version to manifest.json (the plugin repository).

.DESCRIPTION
    Produces artifacts/p2pstream_<version>.zip containing the DLLs and a meta.json (owner, version, target ABI),
    then adds or replaces that version's entry in manifest.json with the release download URL and MD5 checksum.
    Publish afterwards with:  gh release create v<version> artifacts/p2pstream_<version>.zip

.EXAMPLE
    ./scripts/package.ps1 -Changelog "Unsaved-changes bar"
#>
param(
    [string]$Changelog = ""
)

$ErrorActionPreference = 'Stop'

$Owner     = 'rejectedwaffle'
$Repo      = 'jellyfin-plugin-p2pstream'
$Guid      = 'fb38a8c4-7fa6-48e6-9c52-c093e7263aff'
$Name      = 'P2P Stream'
$TargetAbi = '12.4.0.0'
$Category  = 'General'
$Overview  = 'Stream torrents on demand, Elementum-style.'
$Description = 'Stream torrents straight into Jellyfin. Built-in torrent sources (no extra software), TMDB catalogs as libraries, ' +
               'search for any movie or show from the Jellyfin search bar, and Burst-style quality filters.'

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Jellyfin.Plugin.P2PStream/Jellyfin.Plugin.P2PStream.csproj'
[xml]$xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.AssemblyVersion } | Where-Object { $_ })[0]
if (-not $version) { throw "No AssemblyVersion in $project" }

Write-Host "Building $Name $version..."
dotnet build $project -c Release --nologo -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$output    = Join-Path $root 'src/Jellyfin.Plugin.P2PStream/bin/Release/net10.0'
$artifacts = Join-Path $root 'artifacts'
$stage     = Join-Path $artifacts 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $output '*.dll') $stage

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$utf8 = New-Object System.Text.UTF8Encoding $false   # no BOM

# meta.json: what Jellyfin shows for a manually installed plugin (owner, version, ...)
$meta = [ordered]@{
    category    = $Category
    changelog   = $Changelog
    description = $Description
    guid        = $Guid
    name        = $Name
    overview    = $Overview
    owner       = $Owner
    targetAbi   = $TargetAbi
    timestamp   = $timestamp
    version     = $version
    status      = 'Active'
    autoUpdate  = $true
    imagePath   = ''
    assemblies  = @()
}
[IO.File]::WriteAllText((Join-Path $stage 'meta.json'), ($meta | ConvertTo-Json -Depth 5), $utf8)

$zip = Join-Path $artifacts "p2pstream_$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

# manifest.json: the plugin repository Jellyfin reads (Dashboard > Plugins > Repositories)
$manifestPath = Join-Path $root 'manifest.json'
$entry = [ordered]@{
    version   = $version
    changelog = $Changelog
    targetAbi = $TargetAbi
    sourceUrl = "https://github.com/$Owner/$Repo/releases/download/v$version/p2pstream_$version.zip"
    checksum  = $checksum
    timestamp = $timestamp
}
$older = @()
if (Test-Path $manifestPath) {
    $existing = @(Get-Content $manifestPath -Raw | ConvertFrom-Json)
    $older = @($existing[0].versions | Where-Object { $_.version -ne $version })
}
$plugin = [ordered]@{
    guid        = $Guid
    name        = $Name
    description = $Description
    overview    = $Overview
    owner       = $Owner
    category    = $Category
    versions    = @($entry) + $older
}
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject @($plugin) -Depth 6), $utf8)

Write-Host "Packaged $zip (md5 $checksum) and updated manifest.json"
