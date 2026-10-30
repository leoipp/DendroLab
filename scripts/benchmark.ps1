param([ValidateRange(1,5)][int]$Rounds=2)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location $root
try{
    & dotnet build 'tests/Dendro.Benchmarks' -c Release --verbosity minimal
    if($LASTEXITCODE -ne 0){throw 'Falha ao compilar a medição'}
    for($round=1;$round -le $Rounds;$round++){
        & dotnet run --project 'tests/Dendro.Benchmarks' -c Release --no-build -- (Join-Path $root ('.build/benchmarks/csharp-round-'+$round+'.json'))
        if($LASTEXITCODE -ne 0){throw 'Falha na medição'}
    }
}finally{Pop-Location}
