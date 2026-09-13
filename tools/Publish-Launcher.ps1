<#
.SYNOPSIS
    Publishes the launcher and the optional ReturnToLauncher hotkey helper into one folder.

.DESCRIPTION
    Produces a self-contained Release build of both executables in a stable folder
    outside the repository. An existing config.json there is left alone.

        .\Publish-Launcher.ps1 -Destination C:\vPinball\Launcher

.PARAMETER Destination
    Folder to publish into.

.PARAMETER FrameworkDependent
    Publish against an installed .NET 8 runtime instead of bundling it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Destination,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

foreach ($project in @('VirtualPinballLauncher\VirtualPinballLauncher.csproj', 'ReturnToLauncher\ReturnToLauncher.csproj')) {
    Write-Host "Publishing $project -> $Destination"
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained $selfContained -o $Destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Write-Host ''
Write-Host "Published. Run $Destination\VirtualPinballLauncher.exe once to create config.json, then edit it."
