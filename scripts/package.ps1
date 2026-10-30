param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version='1.0.0',
    [ValidateSet('Zip','Installer','Both')][string]$Format='Both',
    [string]$InnoCompiler
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$environmentNames=@('TEMP','TMP','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_FALLBACK_PACKAGES')
$previousEnvironment=@{}
foreach($key in $environmentNames){$previousEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
Push-Location $root
try{
    $previousPackages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
    $projectCache=Join-Path $root '.build/nuget-packages'
    $fallbacks=@($previousPackages)
    if($env:NUGET_FALLBACK_PACKAGES){$fallbacks+=$env:NUGET_FALLBACK_PACKAGES.Split(';')}
    $env:NUGET_FALLBACK_PACKAGES=($fallbacks|Where-Object {$_ -and $_ -ne $projectCache}|Select-Object -Unique)-join ';'
    $env:NUGET_PACKAGES=$projectCache
    $env:NUGET_HTTP_CACHE_PATH=Join-Path $root '.build/nuget-http'
    $env:TEMP=Join-Path $root '.build/temp';$env:TMP=$env:TEMP
    New-Item -ItemType Directory -Force $env:TEMP | Out-Null
    $output=Join-Path $root ('dist/'+$Version)
    $stage=Join-Path $root ('.build/package-'+[guid]::NewGuid().ToString('N'))
    $payload=Join-Path $stage 'DendroLab'
    New-Item -ItemType Directory -Force $payload,$output | Out-Null
    & dotnet publish 'src/Dendro.Desktop/Dendro.Desktop.csproj' -c Release -r win-x64 --self-contained true -o $payload --verbosity minimal "-p:Version=$Version" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE -ne 0){throw 'Falha na publicação com o runtime incluído.'}
    & (Join-Path $PSScriptRoot 'copy-notices.ps1') -Source $payload -Destination $payload
    Copy-Item -LiteralPath 'docs/DISTRIBUICAO.md' -Destination (Join-Path $payload 'LEIA-ME.md')
    $artifacts=@()
    if($Format -in @('Zip','Both')){
        $zip=Join-Path $output "DendroLab-$Version-win-x64.zip"
        Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal -Force
        $artifacts+=$zip
    }
    if($Format -in @('Installer','Both')){
        if(-not $InnoCompiler){
            $found=Get-Command ISCC.exe -ErrorAction SilentlyContinue
            $candidates=@((Join-Path $root '.build/tools/InnoSetup/ISCC.exe'),(Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'))
            if(${env:ProgramFiles(x86)}){$candidates+=(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')}
            if($env:ProgramFiles){$candidates+=(Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')}
            $InnoCompiler=if($found){$found.Source}else{$candidates|Where-Object {Test-Path -LiteralPath $_}|Select-Object -First 1}
        }
        if(-not $InnoCompiler -or -not(Test-Path -LiteralPath $InnoCompiler)){throw 'Instale o Inno Setup 6 ou informe -InnoCompiler com o caminho do ISCC.exe. O ZIP pode ser gerado com -Format Zip.'}
        & $InnoCompiler '/Qp' "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputPath=$output" (Join-Path $root 'installer/DendroLab.iss')
        if($LASTEXITCODE -ne 0){throw 'Falha na compilação do instalador.'}
        $artifacts+=(Join-Path $output "DendroLab-$Version-win-x64-Setup.exe")
    }
    $sums=@($artifacts|ForEach-Object {((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+[IO.Path]::GetFileName($_)})
    [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$sums,(New-Object Text.UTF8Encoding($false)))
    $artifacts|ForEach-Object {Write-Output $_}
    Write-Output (Join-Path $output 'SHA256SUMS.txt')
}finally{
    Pop-Location
    foreach($key in $environmentNames){[Environment]::SetEnvironmentVariable($key,$previousEnvironment[$key],'Process')}
}
