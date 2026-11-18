# Builds and runs the MathMorph unit tests with the local .NET 8 SDK.
# Writes <root>\tests\unit_result.json. Exit code 0 when all tests pass.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = "$root\tools\dotnet_home"
$env:NUGET_PACKAGES = "$root\tools\nuget"
$env:MATHMORPH_HOME = $root
$dotnet = "$root\tools\dotnet8\dotnet.exe"
$proj = "$root\tests\Unit\MathMorph.Tests.csproj"
$out = "$root\tests\Unit\bin_out"

& $dotnet build $proj -c Debug -o $out
if ($LASTEXITCODE -ne 0) {
    # the src project may be built concurrently (file locks): wait and retry once
    Start-Sleep -Seconds 30
    & $dotnet build $proj -c Debug -o $out
    if ($LASTEXITCODE -ne 0) { Write-Host 'build failed'; exit 2 }
}

& $dotnet "$out\MathMorph.Tests.dll" @args
exit $LASTEXITCODE
