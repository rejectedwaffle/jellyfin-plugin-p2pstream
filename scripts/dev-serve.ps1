<#
.SYNOPSIS
    Serves artifacts/dev/ (development builds from `package.ps1 -Dev`) on the LAN, as a Jellyfin plugin repository.

.DESCRIPTION
    Add http://<this PC>:8765/manifest.json in Jellyfin (Dashboard > Plugins > Repositories). Development builds then
    install from there seconds after they're built, with no GitHub release. Windows may ask to allow Python through
    the firewall the first time (private network).
#>
param([int]$Port = 8765)
$dir = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/dev'
New-Item -ItemType Directory -Force $dir | Out-Null
python -m http.server $Port --bind 0.0.0.0 --directory $dir
