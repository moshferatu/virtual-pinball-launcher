<#
.SYNOPSIS
    Publishes the launcher and the optional ReturnToLauncher hotkey helper into one folder.

.DESCRIPTION
    Produces a Release build of both executables in a stable folder outside the repository.
    An existing config.json there is left alone.

        .\Publish-Launcher.ps1 -Destination C:\vPinball\Launcher
        .\Publish-Launcher.ps1 -Destination C:\vPinball\Launcher -SingleFile -FrameworkDependent

.PARAMETER Destination
    Folder to publish into.

.PARAMETER FrameworkDependent
    Publish against an installed .NET 8 Desktop Runtime instead of bundling it.
    This is the smallest option (about 43 MB, with or without -SingleFile).

.PARAMETER SingleFile
    Pack each program into one exe. The libvlc folder, ScreenRecorderLib.dll and flipper.wav
    still sit next to the launcher, because they can't be loaded from inside the exe.
    A self-contained single-file build is compressed (about 179 MB in total).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Destination,
    [switch]$FrameworkDependent,
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

$options = @()
if ($SingleFile) {
    $options += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true'
    if (-not $FrameworkDependent) { $options += '-p:EnableCompressionInSingleFile=true' }
}

foreach ($project in @('VirtualPinballLauncher\VirtualPinballLauncher.csproj', 'ReturnToLauncher\ReturnToLauncher.csproj')) {
    Write-Host "Publishing $project -> $Destination"
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained $selfContained @options -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Write-Host ''
Write-Host "Published. Run $Destination\VirtualPinballLauncher.exe once to create config.json, then edit it."
if ($FrameworkDependent) {
    Write-Host 'This build needs the .NET 8 Desktop Runtime (x64): winget install Microsoft.DotNet.DesktopRuntime.8'
}
