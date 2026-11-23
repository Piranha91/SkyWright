# Builds SkyWright: houseCARL's rulebook when it is missing, made from another pin, or asked for (-Rulebook), then the Debug and the Release build,
# each into bin\<Configuration>\<.NET version>\ (SkyWright.exe, Probes\, Tools\, Data\). Windows PowerShell 5.1.
#   powershell -File scripts\build.ps1 [-Rulebook] [-Configuration Debug|Release]
param(
    [switch]$Rulebook,
    [ValidateSet('Debug', 'Release', 'Both')][string]$Configuration = 'Both'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $generator = 'external\houseCARL\src\housecarl-generator'
    $corpus = 'external\houseCARL\generated\corpus.json'
    # the rulebook belongs to the pinned houseCARL: remade when missing, asked for, or made from another pin
    $stamp = 'external\houseCARL\generated\corpus.pin'
    $pin = (git -C external\houseCARL rev-parse HEAD).Trim()
    $made = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
    if ($Rulebook -or -not (Test-Path $corpus) -or $made -ne $pin) {
        Write-Host "making the rulebook ($corpus) for houseCARL $pin"
        dotnet build $generator -c Release -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "the rulebook generator did not build; see the errors above." }
        dotnet run --project $generator -c Release --no-build -- external\houseCARL\generated external\houseCARL\generated\reference
        if ($LASTEXITCODE -ne 0) { throw "the rulebook generator failed; see the errors above." }
        Set-Content -Path $stamp -Value $pin -Encoding ASCII
    }
    $configs = if ($Configuration -eq 'Both') { @('Debug', 'Release') } else { @($Configuration) }
    foreach ($c in $configs) {
        Write-Host "building $c"
        dotnet build SkyWright.sln -c $c -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "the $c build failed; see the errors above (a running SkyWright or probe locks its files)." }
        $app = Get-ChildItem -Path "bin\$c" -Filter SkyWright.exe -Recurse -Depth 1 | Select-Object -First 1
        Write-Host "  $($app.FullName)"
    }
}
finally { Pop-Location }
