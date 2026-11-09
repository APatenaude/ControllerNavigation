#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the release files locally: dist\ControllerNavigation.exe (one self-contained file)
    and dist\update.json. The release workflow (.github/workflows/release.yml) runs this too.

.DESCRIPTION
    The version is Directory.Build.props'; with -Tag the tag must be exactly v<version>. The exe
    must carry that version as its file version (an installed copy compares it), and update.json
    says what an installed copy checks before it swaps anything in (Updater.cs,
    UpdateRules.ParseManifest):
        {"version":"x.y.z","exe":"ControllerNavigation.exe","sha256":"<hex>","size":<bytes>}
    dist\ is emptied first. Nothing is published from here.

.PARAMETER Tag
    The git tag being released (v1.2.3). Without it: a local build of the current version.

.PARAMETER CheckOnly
    Only checks the version (and -Tag against it); builds nothing.
#>
param(
    [string]$Tag,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d{1,6}\.\d{1,6}\.\d{1,6}$') { throw "Directory.Build.props: '$version' is not major.minor.patch" }
if ($Tag -and $Tag -cne "v$version") { throw "Tag $Tag does not match the version in Directory.Build.props ($version)" }
Write-Host "Version $version$(if ($Tag) { ", tag $Tag" })"
if ($CheckOnly) { return }

$dist = Join-Path $root 'dist'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
& dotnet publish (Join-Path $root 'src\ControllerNavigation') -c Release -o $dist --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

$exe = Join-Path $dist 'ControllerNavigation.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "$exe was not made" }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$fileVersion = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
if ($fileVersion -ne $version) { throw "The built exe's file version is $fileVersion, not $version" }

$manifest = [ordered]@{
    version = $version
    exe     = 'ControllerNavigation.exe'
    sha256  = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    size    = (Get-Item -LiteralPath $exe).Length
}
[IO.File]::WriteAllText((Join-Path $dist 'update.json'), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

foreach ($name in 'ControllerNavigation.exe', 'update.json') {
    Write-Host ("  {0,-28} {1,14:N0} bytes" -f $name, (Get-Item -LiteralPath (Join-Path $dist $name)).Length)
}
