# DIFFERENTIAL SWEEP (roadmap 2.4) — turn --verify's coverage verdicts into differential ones.
#
# For each candidate plugin (installed here, inactive — the DIFFERENTIAL CANDIDATES list that
# `patchprobe --verify` prints), this: enables it synthetically, re-detects, and diffs the emitted
# conflict set against a base emit. ~50 s per candidate; the output per candidate ends in one of
#   DIFFERENTIAL-VERIFIED      physically repairs more than it creates
#   DIFFERENTIAL-NEUTRAL       repairs exactly as much as it creates
#   DIFFERENTIAL-HARMFUL       creates more than it repairs
#   DIFFERENTIAL-RECLASSIFIED  rows changed class without their physical predicate flipping (a sham reads this)
#   DIFFERENTIAL-UNMEASURED    only rows whose predicate could not be measured moved
#   DIFFERENTIAL-UNJUDGED      the changed arm's unjudgeable share rose — not judged
#   DIFFERENTIAL-INERT         touches nothing the detector sees
# Repair is credited only when a row's PHYSICAL predicate went Holds -> Gone (2026-09-03): the changed run is
# given the base emit (--base) so the probe re-measures every base row under the changed arm; conflictdiff
# reads both. A row that merely changed hands is RECLASSIFIED, never repaired.
# A synthetic enable whose master set is not loadable is REFUSED by SyntheticOrder and shows up
# here as INERT with a REFUSED warning in the navownprobe output — read the log, not just the verdict.
#
# CONFIGURATION (roadmap 4.20, 2026-09-11). Every emit now records what the run consumed — the loaded plugins in
# order with their content hashes, the asset providers in priority, the runtime overlays — and conflictdiff prints
# both identities plus the delta between them before it credits anything. Two consequences for a sweep:
#   - DIFFERENTIAL-UNJUDGED gained a second cause: identical configurations that produced different rows. Nothing
#     the identity covers changed, so whatever moved came from outside it — never a repair number.
#   - conflictdiff EXITS 3 on an emit that names no configuration, so a base emit left over from before 4.20 stops
#     the sweep at that candidate instead of being compared. Re-emit the base with the current probe.
# The precedence fix that shipped with it also changes what a sweep on a lab-style profile measures: SyntheticOrder
# used to rank switched-off mod folders above the game's Data folder, so a plugin present in both was read from the
# folder on every --enable arm and from Data on the base arm. On the SE conflict lab that was 21 Creation Club
# plugins' worth of different bytes inside each comparison, and it scored one row as GONE+NEW on a null intervention.
#
# PROGRESS (roadmap 4.16): one run for the sweep, one stage, one child run per candidate, so a
# sweep of twenty candidates says which one it is on and roughly how long is left instead of
# going quiet for twenty minutes. The candidate's output now STREAMS to the console as well as to
# its log — it used to be swallowed whole by `*> $log`, which is what made the sweep feel hung.
#
# usage:  .\differential-sweep.ps1 [-OutDir _runs\sweep] "TGC Winterhold - OCW Patch.esp" "Another patch.esp"
#         (candidates are plain trailing arguments — one per plugin — so `powershell -File` works;
#          a comma-joined -Candidates list does NOT survive -File argument binding)
#         -Probe names the detector to sweep with (default navownprobe); any probe that takes
#         --enable and --emit and writes a ConflictFile works — overlapprobe for the overlap class.
#         -Instance names the MO2 instance (required, or HOUSECARL_MO2_INSTANCE: the harness config's
#         instance is never a default); -Game and -Profile are passed to the probe when given.
param(
    [string]$OutDir = "$PSScriptRoot\_runs\sweep",
    [string]$Probe = 'navownprobe',
    [string]$Instance = '',
    [string]$Game = '',
    [string]$Profile = '',
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Candidates
)
if (-not $Candidates -or $Candidates.Count -eq 0) {
    Write-Error 'usage: differential-sweep.ps1 [-OutDir dir] "<Plugin.esp>" ["<Plugin2.esp>" ...]'
    exit 2
}

if (-not $Instance -and -not $env:HOUSECARL_MO2_INSTANCE) {
    Write-Error 'refused: instance-not-explicit - pass -Instance <MO2 instance dir> (or set HOUSECARL_MO2_INSTANCE for this run); the harness config''s instance is never a default'
    exit 2
}
$target = @()
if ($Instance) { $target += @('--instance', $Instance) }
if ($Game) { $target += @('--game', $Game) }
if ($Profile) { $target += @('--profile', $Profile) }

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\RunLog.psm1') -Force
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$navown = Join-Path $PSScriptRoot $Probe
$diff = Join-Path $PSScriptRoot 'conflictdiff'
$base = Join-Path $OutDir 'base.json'

$run = Start-HcRun -Op 'differential-sweep' -Mode $Probe -StageSource counted
Add-HcFeature -Run $run -Name 'candidates' -Value $Candidates.Count -Unit 'plugins'
$est = Get-HcEstimate -Op 'differential-sweep' -Mode $Probe -Features @{ candidates = $Candidates.Count }
if ($est.seconds) {
    Write-Host ("estimate: about {0:N0} min for {1} candidate(s) - {2}, {3} comparable run(s)" -f `
                ($est.seconds / 60), $Candidates.Count, $est.basis, $est.samples)
} else {
    Write-Host ("estimate: none yet - {0}" -f ($est.limitations -join '; '))
}

try {
    Set-HcStage -Run $run -Id 'base' -Title 'base detection' -Total 1 -Unit 'runs' -Weight 1.0
    Write-Host "== base detection with $Probe (no intervention)"
    $code = Invoke-HcChild -Run $run -Op $Probe -File 'dotnet' `
                -Arguments (@('run', '-c', 'Release', '--') + $target + @('--emit', $base)) `
                -WorkingDirectory $navown -LogicalId 'base'
    if ($code -ne 0) { Deny-HcRun -Run $run -Reason "base detection exited $code" -ExitCode $code; exit $code }
    Step-HcProgress -Run $run

    Set-HcStage -Run $run -Id 'candidates' -Title 'per-candidate detection and diff' `
                -Total $Candidates.Count -Unit 'candidates' -Weight ([double]$Candidates.Count)
    foreach ($cand in $Candidates) {
        $safe = ($cand -replace '[^A-Za-z0-9._-]', '_')
        $emit = Join-Path $OutDir "with-$safe.json"
        $log = Join-Path $OutDir "with-$safe.txt"
        Write-Host "== enabling '$cand'"
        # -LogPath tees the child's output to the log AND to the console. The old `*> $log` kept
        # the operator staring at nothing for the ~50 s each candidate takes.
        $code = Invoke-HcChild -Run $run -Op $Probe -File 'dotnet' `
                    -Arguments (@('run', '-c', 'Release', '--') + $target + @('--enable', $cand, '--base', $base, '--emit', $emit)) `
                    -WorkingDirectory $navown -LogPath $log -LogicalId "candidate:$safe"
        if ($code -ne 0) {
            Add-HcTally -Run $run -Failed 1
            Write-Host "   $Probe exited $code for '$cand' - see $log"
        }
        Select-String -Path $log -Pattern 'REFUSED|MISSING MASTER' | ForEach-Object { Write-Host "   $_" }
        $dcode = Invoke-HcChild -Run $run -Op 'conflictdiff' -File 'dotnet' `
                    -Arguments @('run', '-c', 'Release', '--', $base, $emit) `
                    -WorkingDirectory $diff -LogicalId "diff:$safe"
        if ($dcode -eq 0 -and $code -eq 0) { Add-HcTally -Run $run -Ok 1 }
        Add-HcArtifact -Run $run -Path $emit
        Step-HcProgress -Run $run
    }
    Complete-HcRun -Run $run
} catch {
    Stop-HcRunFailed -Run $run -ErrorText $_.Exception.Message
    throw
}
