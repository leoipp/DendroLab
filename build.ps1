param([switch]$Test,[switch]$Publish,[switch]$NoBuild,[string]$CompatibilityProject)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'scripts/desktop.ps1') @PSBoundParameters
