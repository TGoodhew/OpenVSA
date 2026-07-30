# Runs REQ-TST-009's soak several times over and judges the set together.
#
# WHY THREE RUNS. The collected managed floor is a staircase that moves in both directions, so one
# run's fitted standard error describes how well its own points sit on their own line and not
# whether the next run will agree. Two runs of one identical configuration have fitted 0.06 +/-0.70
# and 54.96 +/-2.06 KiB/hour. ReplicatedGate takes its uncertainty from the spread BETWEEN runs,
# and needs three before that spread means anything.
#
# SEQUENTIALLY, NOT IN PARALLEL. Two shells measuring at once contend for cores, which changes the
# update rate each of them reports and the frame rate the memory claim is measured against. These
# are meant to be replicates of one another, so nothing about the machine may differ between them.
#
# Nothing else should be running on this machine while it goes: a build part way through perturbs
# the very quantity being measured.
param(
    [int]$Runs = 3,
    [double]$Hours = 8.0,
    [string]$OutputDirectory = "artifacts/soak/replicates"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PSScriptRoot "..\tests\OpenVSA.Soak\bin\Release\net472\OpenVSA.Soak.exe"

if (-not (Test-Path $exe)) {
    Write-Error "Build OpenVSA.Soak in Release first: $exe is not there."
}

if (-not (Test-Path $OutputDirectory)) {
    $null = New-Item -ItemType Directory -Force $OutputDirectory
}

$logs = @()

for ($run = 1; $run -le $Runs; $run++) {
    $log = Join-Path $OutputDirectory ("soak-{0}.tsv" -f $run)
    $logs += $log

    Write-Output ("=== run {0} of {1}, {2} hours, started {3} ===" -f
        $run, $Runs, $Hours, (Get-Date -Format "u"))

    # Exit 1 is expected and not a failure of the run: the single-run gate judges each log on its
    # own terms and the managed claim is exactly the one that cannot be settled that way. The set
    # is judged below.
    & $exe --hours $Hours --log $log
}

Write-Output ""
Write-Output ("=== judging {0} runs together, {1} ===" -f $Runs, (Get-Date -Format "u"))

$judge = @()
foreach ($log in $logs) { $judge += "--judge-run"; $judge += $log }

& $exe @judge

Write-Output ("=== finished {0} ===" -f (Get-Date -Format "u"))
