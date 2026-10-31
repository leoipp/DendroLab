param([switch]$Test,[switch]$Publish,[switch]$NoBuild,[string]$FixtureFolder)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'scripts/desktop.ps1') @PSBoundParameters
