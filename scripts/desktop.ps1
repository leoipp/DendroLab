param([switch]$Test,[switch]$Publish,[switch]$NoBuild,[string]$FixtureFolder)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $staging=Join-Path $root '.build/managed-desktop'
    if(-not $NoBuild){
        & dotnet publish 'src/Dendro.Desktop/Dendro.Desktop.csproj' -c Release -o $staging --verbosity minimal
        if($LASTEXITCODE -ne 0){throw 'Falha ao compilar o aplicativo C#'}
    }
    if(-not(Test-Path -LiteralPath (Join-Path $staging 'DendroLab.exe'))){throw 'Compile o aplicativo antes de usar -NoBuild'}
    foreach($name in @('Dendro.Native.dll','hdf5.dll','hdf5_hl.dll','vcruntime140.dll','vcruntime140_1.dll')){if(Test-Path -LiteralPath (Join-Path $staging $name)){throw ('Dependência nativa antiga no resultado: '+$name)}}
    if($Test){
        & dotnet test 'tests/Dendro.Core.Tests/Dendro.Core.Tests.csproj' -c Release --verbosity minimal --logger 'trx;LogFileName=core.trx' --results-directory '.build/test-results'
        if($LASTEXITCODE -ne 0){throw 'Falha nos testes do núcleo C#'}
        $testRoot=Join-Path $root ('.build/desktop-tests/'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $testRoot | Out-Null
        if(-not $FixtureFolder){$FixtureFolder=Join-Path $root 'tests/Fixtures'}
        $FixtureFolder=[IO.Path]::GetFullPath($FixtureFolder)
        $arguments=@('--self-test',('"'+$testRoot+'"'),('"'+(Join-Path $FixtureFolder 'inventário.xlsx')+'"'),('"'+(Join-Path $FixtureFolder 'cadastro.xlsx')+'"'))
        $failure=Join-Path $staging 'self-test-error.txt'
        if(Test-Path -LiteralPath $failure){Remove-Item -LiteralPath $failure}
        $qa=Start-Process -FilePath (Join-Path $staging 'DendroLab.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if(-not $qa.WaitForExit(60000)){Stop-Process -Id $qa.Id;throw ('Tempo excedido nos testes WPF: '+$testRoot)}
        if($qa.ExitCode -ne 0){if(Test-Path -LiteralPath $failure){Get-Content -LiteralPath $failure};throw 'Falha nos testes WPF'}
        Get-Content -LiteralPath (Join-Path $testRoot 'self-test.log')
        $duplicateRoot=Join-Path $root ('.build/desktop-tests/duplicates-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $duplicateRoot | Out-Null
        if(Test-Path -LiteralPath $failure){Remove-Item -LiteralPath $failure}
        $duplicateArguments=@('--self-test-duplicates',('"'+$duplicateRoot+'"'))
        $duplicateTest=Start-Process -FilePath (Join-Path $staging 'DendroLab.exe') -ArgumentList $duplicateArguments -WindowStyle Hidden -PassThru
        if(-not $duplicateTest.WaitForExit(60000)){Stop-Process -Id $duplicateTest.Id;throw ('Tempo excedido no teste de duplicadas: '+$duplicateRoot)}
        if($duplicateTest.ExitCode -ne 0){if(Test-Path -LiteralPath $failure){Get-Content -LiteralPath $failure};throw 'Falha no teste WPF de duplicadas'}
        Get-Content -LiteralPath (Join-Path $duplicateRoot 'self-test.log')
        $removalRoot=Join-Path $root ('.build/desktop-tests/removal-discard-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $removalRoot | Out-Null
        if(Test-Path -LiteralPath $failure){Remove-Item -LiteralPath $failure}
        $removalArguments=@('--self-test-remove-discard',('"'+$removalRoot+'"'))
        $removalTest=Start-Process -FilePath (Join-Path $staging 'DendroLab.exe') -ArgumentList $removalArguments -WindowStyle Hidden -PassThru
        if(-not $removalTest.WaitForExit(60000)){Stop-Process -Id $removalTest.Id;throw ('Tempo excedido no teste de remoção transacional: '+$removalRoot)}
        if($removalTest.ExitCode -ne 0){if(Test-Path -LiteralPath $failure){Get-Content -LiteralPath $failure};throw 'Falha no teste WPF de remoção transacional'}
        Get-Content -LiteralPath (Join-Path $removalRoot 'self-test.log')
        $menuRoot=Join-Path $root ('.build/desktop-tests/context-menu-icons-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $menuRoot | Out-Null
        if(Test-Path -LiteralPath $failure){Remove-Item -LiteralPath $failure}
        $menuArguments=@('--self-test-context-menu-icons',('"'+$menuRoot+'"'))
        $menuTest=Start-Process -FilePath (Join-Path $staging 'DendroLab.exe') -ArgumentList $menuArguments -WindowStyle Hidden -PassThru
        if(-not $menuTest.WaitForExit(60000)){Stop-Process -Id $menuTest.Id;throw ('Tempo excedido no teste dos menus de contexto: '+$menuRoot)}
        if($menuTest.ExitCode -ne 0){if(Test-Path -LiteralPath $failure){Get-Content -LiteralPath $failure};throw 'Falha no teste WPF dos menus de contexto'}
        Get-Content -LiteralPath (Join-Path $menuRoot 'self-test.log')
        Write-Output ('Artefatos WPF: '+$testRoot)
    }
    if($Publish){
        $app=Join-Path $root 'app'
        $exe=Join-Path $app 'DendroLab.exe'
        if(Get-Process DENDRO,DendroLab -ErrorAction SilentlyContinue|Where-Object {$_.Path -in @($exe,(Join-Path $app 'DENDRO.exe'))}){throw 'Feche o DendroLab antes de publicar a nova versão.'}
        $candidate=Join-Path $root ('.build/publish-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $candidate | Out-Null
        foreach($file in Get-ChildItem -LiteralPath $staging -File|Where-Object {$_.Extension -eq '.dll' -and $_.Name -ne 'DENDRO.dll' -or $_.Name -in @('DendroLab.exe','DendroLab.deps.json','DendroLab.runtimeconfig.json')}){
            Copy-Item -LiteralPath $file.FullName -Destination $candidate
            if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $candidate $file.Name)).Hash){throw ('Arquivo publicado divergente: '+$file.Name)}
        }
        foreach($culture in Get-ChildItem -LiteralPath $staging -Directory){Copy-Item -LiteralPath $culture.FullName -Destination $candidate -Recurse}
        $licenses=Join-Path $candidate 'licenses'
        New-Item -ItemType Directory -Force $licenses | Out-Null
        Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $candidate 'LICENSE')
        Copy-Item -LiteralPath 'docs/DEPENDENCIAS.md' -Destination $licenses
        Get-ChildItem -LiteralPath 'docs/licenses' -File | Copy-Item -Destination $licenses
        $dependencies=(Get-Content -LiteralPath (Join-Path $staging 'DendroLab.deps.json') -Raw|ConvertFrom-Json).libraries.PSObject.Properties
        foreach($dependency in $dependencies){
            if($dependency.Value.type -ne 'package'){continue}
            $packageFolder=Join-Path $env:USERPROFILE ('.nuget/packages/'+$dependency.Name.ToLowerInvariant())
            if(-not(Test-Path -LiteralPath $packageFolder)){continue}
            $prefix=$dependency.Name.Replace('/','-')
            foreach($notice in Get-ChildItem -LiteralPath $packageFolder -File|Where-Object {$_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|.*\.nuspec)'}){Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($prefix+'-'+$notice.Name))}
        }
        $previous=Join-Path $root ('.build/previous-app-'+[guid]::NewGuid().ToString('N'))
        if(Test-Path -LiteralPath $app){
            if([IO.Path]::GetFullPath($app) -ne (Join-Path $root 'app')){throw 'Pasta de publicação fora do projeto'}
            Move-Item -LiteralPath $app -Destination $previous
        }
        try {Move-Item -LiteralPath $candidate -Destination $app}
        catch {if(Test-Path -LiteralPath $previous){Move-Item -LiteralPath $previous -Destination $app};throw}
        Write-Output ('Publicado: '+$exe)
    }
}finally{Pop-Location}
