# DIFFERENTIAL SWEEP (roadmap 2.4) — turn --verify's coverage verdicts into differential ones.
#
# For each candidate plugin (installed here, inactive — the DIFFERENTIAL CANDIDATES list that
# `patchprobe --verify` prints), this: enables it synthetically, re-detects, and diffs the emitted
# conflict set against a base emit. ~50 s per candidate; the output per candidate ends in one of
#   DIFFERENTIAL-VERIFIED   repairs more than it creates
#   DIFFERENTIAL-NEUTRAL    repairs exactly as much as it creates (the TGC case)
#   DIFFERENTIAL-HARMFUL    creates more than it repairs
#   DIFFERENTIAL-INERT      touches nothing the detector sees
# A synthetic enable whose master set is not loadable is REFUSED by SyntheticOrder and shows up
# here as INERT with a REFUSED warning in the navownprobe output — read the log, not just the verdict.
#
# usage:  .\differential-sweep.ps1 [-OutDir _runs\sweep] "TGC Winterhold - OCW Patch.esp" "Another patch.esp"
#         (candidates are plain trailing arguments — one per plugin — so `powershell -File` works;
#          a comma-joined -Candidates list does NOT survive -File argument binding)
#         -Probe names the detector to sweep with (default navownprobe); any probe that takes
#         --enable and --emit and writes a ConflictFile works — overlapprobe for the overlap class.
param(
    [string]$OutDir = "$PSScriptRoot\_runs\sweep",
    [string]$Probe = 'navownprobe',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Candidates
)
if (-not $Candidates -or $Candidates.Count -eq 0) {
    Write-Error 'usage: differential-sweep.ps1 [-OutDir dir] "<Plugin.esp>" ["<Plugin2.esp>" ...]'
    exit 2
}

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$navown = Join-Path $PSScriptRoot $Probe
$diff = Join-Path $PSScriptRoot 'conflictdiff'
$base = Join-Path $OutDir 'base.json'

Write-Host "== base detection with $Probe (no intervention)"
Push-Location $navown
try { dotnet run -c Release -- --emit $base | Select-Object -Last 3 } finally { Pop-Location }

foreach ($cand in $Candidates) {
    $safe = ($cand -replace '[^A-Za-z0-9._-]', '_')
    $emit = Join-Path $OutDir "with-$safe.json"
    $log = Join-Path $OutDir "with-$safe.txt"
    Write-Host "== enabling '$cand'"
    Push-Location $navown
    try { dotnet run -c Release -- --enable $cand --emit $emit *> $log } finally { Pop-Location }
    Select-String -Path $log -Pattern 'REFUSED|MISSING MASTER' | ForEach-Object { Write-Host "   $_" }
    Push-Location $diff
    try { dotnet run -c Release -- $base $emit } finally { Pop-Location }
}
