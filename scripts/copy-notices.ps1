param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$licenses=Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $Destination 'LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'docs/DEPENDENCIAS.md') -Destination $licenses
Get-ChildItem -LiteralPath (Join-Path $root 'docs/licenses') -File | Copy-Item -Destination $licenses
$packages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
$dependencies=(Get-Content -LiteralPath (Join-Path $Source 'DendroLab.deps.json') -Raw|ConvertFrom-Json).libraries.PSObject.Properties
$roots=@($packages)
if($env:NUGET_FALLBACK_PACKAGES){$roots+=@($env:NUGET_FALLBACK_PACKAGES.Split(';')|Where-Object {$_})}
$folders=@()
foreach($cache in $roots){
    $folders+=@($dependencies|Where-Object {$_.Value.type -eq 'package'}|ForEach-Object {Join-Path $cache $_.Name.ToLowerInvariant()})
    foreach($name in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')){
        $package=Join-Path $cache $name
        if(Test-Path -LiteralPath $package){$folders+=@(Get-ChildItem -LiteralPath $package -Directory|ForEach-Object {$_.FullName})}
    }
}
foreach($folder in $folders|Select-Object -Unique){
    if(-not(Test-Path -LiteralPath $folder)){continue}
    $prefix=(Split-Path -Leaf (Split-Path -Parent $folder))+'-'+(Split-Path -Leaf $folder)
    foreach($notice in Get-ChildItem -LiteralPath $folder -File|Where-Object {$_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD.PARTY.*|.*\.nuspec)'}){
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($prefix+'-'+$notice.Name))
    }
}
