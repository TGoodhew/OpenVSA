<#
.SYNOPSIS
    Measures REQ-NFR-025  -  cold start to first trace  -  on a machine that has just had OpenVSA
    installed for the first time (issue #410).

.DESCRIPTION
    Run this ONCE, immediately after installing, before opening OpenVSA by hand.

    "Cold" means a machine that has never had this product on it. That property is destroyed the
    first time the shell runs: after it, its assemblies are in the operating system's file cache
    and every later launch is a warm start. So the first launch is the only cold one there will
    ever be on this machine, and it cannot be repeated without a fresh installation.

    Nothing is installed by this script and nothing is sent anywhere. It writes one log file and
    stops; you send that file back.

    Windows PowerShell 5.1, which is part of Windows, is enough. No SDK, no build tools and no
    source code are needed.

.PARAMETER Runs
    Launches to make. The first is the cold one; the rest establish the warm figure it is compared
    against. At least three, because fewer cannot separate the two.

.PARAMETER LogPath
    Where to write the log. Defaults to a dated file beside this script.

.PARAMETER ShellPath
    Measure this OpenVSA.exe instead of the installed one. Not needed for the ordinary case, and
    the log says when it was used -- a figure taken against a copy somebody pointed at by hand is
    not the same claim as one taken against what the installer produced.
#>
[CmdletBinding()]
param(
    [ValidateRange(3, 20)]
    [int]$Runs = 5,

    [string]$LogPath,

    # NOT -Shell. PowerShell variable names are case-INSENSITIVE, so a parameter called $Shell and
    # the local $shell below are one variable: "$shell = $null" silently erased the parameter
    # before it was ever read, and the script reported that OpenVSA was not installed while
    # standing next to the copy it had been handed.
    [string]$ShellPath
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $LogPath) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $LogPath = Join-Path $here "coldstart-$env:COMPUTERNAME-$stamp.log"
}

$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Host $text
    $lines.Add($text)
}

function Section([string]$title) {
    Say ''
    Say "== $title =="
}

Say "OpenVSA cold-start measurement (REQ-NFR-025, issue #410)"
Say "taken $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"

# ---------------------------------------------------------------------------------------------
# Where the product is. Read from the registry rather than guessed, so a non-default install
# directory is measured rather than reported as missing.
# ---------------------------------------------------------------------------------------------
Section 'Installation'

$shell = $null

if ($ShellPath) {
    if (-not (Test-Path $ShellPath)) {
        Say "FAILED: -ShellPath was given as '$ShellPath' and there is nothing there."
        $lines | Set-Content -Path $LogPath -Encoding UTF8
        Write-Host ''
        Write-Host "Log written to $LogPath"
        exit 2
    }

    $shell = (Resolve-Path $ShellPath).Path
    Say "NOTE: -ShellPath was supplied, so this measures a named copy, not an installed one."
    Say "This is NOT the figure REQ-NFR-025 asks for unless that copy came from the installer."
}

foreach ($root in @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {

    $entry = Get-ItemProperty $root -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -like 'OpenVSA*' } |
             Select-Object -First 1

    if ($entry) {
        # Reported even when -ShellPath was supplied, because "which OpenVSA is installed here" is
        # worth knowing either way  -  but it must not overwrite an explicit choice.
        Say "product      : $($entry.DisplayName) $($entry.DisplayVersion)"
        if ($entry.InstallLocation -and -not $shell) {
            $candidate = Join-Path $entry.InstallLocation 'OpenVSA.exe'
            if (Test-Path $candidate) { $shell = $candidate }
        }
        break
    }
}

if (-not $shell) {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'OpenVSA\OpenVSA.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'OpenVSA\OpenVSA.exe'))) {
        if (Test-Path $candidate) { $shell = $candidate; break }
    }
}

if (-not $shell) {
    Say 'FAILED: OpenVSA does not appear to be installed. Run the MSI first, then run this again.'
    $lines | Set-Content -Path $LogPath -Encoding UTF8
    Write-Host ''
    Write-Host "Log written to $LogPath"
    exit 2
}

Say "shell        : $shell"
Say "file version : $((Get-Item $shell).VersionInfo.FileVersion)"
Say "built        : $((Get-Item $shell).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"

# The file's own timestamps are the BUILD, not the install: the MSI copies the payload and the
# copy carries the build's times with it. The first log taken with this harness said
# "installed 2026-08-08" for an MSI that had been run three weeks later, which is the sort of
# wrong that gets believed. The install date belongs to Windows, so it is asked.
$installedOn = $null

foreach ($hive in @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
    try {
        $entry = Get-ItemProperty $hive -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'OpenVSA*' } | Select-Object -First 1

        if ($entry -and $entry.InstallDate) {
            $installedOn = $entry.InstallDate
            break
        }
    } catch { }
}

Say "installed    : $(if ($installedOn) { $installedOn } else { 'not recorded by Windows' })"

# ---------------------------------------------------------------------------------------------
# THE MEASUREMENT IS ONLY VALID IF THE SHELL HAS NEVER RUN HERE.
#
# If it has, the figure below is a warm start wearing a cold start's name, and nothing in the
# number itself would show that. So it is checked and stated rather than assumed: OpenVSA writes
# its preferences under %APPDATA% on exit, so their presence means it has been run before.
# ---------------------------------------------------------------------------------------------
Section 'Is this really a cold machine?'

$settings = Join-Path $env:APPDATA 'OpenVSA'
$everRun = Test-Path $settings

if ($everRun) {
    Say "WARNING: $settings already exists, so OpenVSA has been run on this machine before."
    Say 'The first launch below is therefore NOT a cold start, and the figure does not answer'
    Say 'REQ-NFR-025. Recorded rather than hidden  -  a warm number reported as a cold one is worse'
    Say 'than no number. To get a valid figure, use a machine that has never had OpenVSA on it.'
} else {
    Say "$settings does not exist, so the shell has not been run here. Good."
}

# ---------------------------------------------------------------------------------------------
# Native images. The installer asks the framework to generate them at idle priority, so they may
# not be ready yet  -  and cold start is most of what they are for. A figure taken before they
# exist measures a different product from one taken after.
# ---------------------------------------------------------------------------------------------
Section 'Native images (NGen)'

$ngen = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'

if (Test-Path $ngen) {
    # ---------------------------------------------------------------------------------------
    # EVERY assembly beside the shell, not just the one called OpenVSA.
    #
    # This used to ask `ngen display OpenVSA` and report the answer as though it covered the
    # installation. It does not: ngen matches a PARTIAL ASSEMBLY NAME, and a partial name still
    # has to match the simple name exactly -- so "OpenVSA" says nothing whatever about
    # OpenVSA.Core, OpenVSA.Dsp, or the 21.7 MB of Syncfusion that the start-up path spends most
    # of its time reading. The v0.1.1 log read like a clean bill of health and was silent on
    # everything that mattered.
    #
    # The installer's NativeImage element runs `ngen install` on the shell, whose documented
    # behaviour is to take the dependency closure with it. Documented is not measured, and this
    # is the measurement: what is asked for here is the LIST OF ASSEMBLIES THAT WERE MISSED,
    # because that list is the difference between a cold start that jits 20 MB and one that
    # does not.
    # ---------------------------------------------------------------------------------------
    $payload = Get-ChildItem -Path (Split-Path $shell) -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.dll' -or $_.Extension -eq '.exe' }

    $withImage = New-Object System.Collections.Generic.List[string]
    $without = New-Object System.Collections.Generic.List[string]
    $unmanaged = New-Object System.Collections.Generic.List[string]

    foreach ($file in $payload) {
        $simple = [IO.Path]::GetFileNameWithoutExtension($file.Name)

        try {
            [Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null
        } catch {
            # Native code -- OpenVSA.Fft.Native.dll is the one that matters. Not a candidate for
            # a native image because it already is one, and counting it as "missed" would be a
            # false alarm on every run.
            $unmanaged.Add($simple)
            continue
        }

        $one = & $ngen display $simple 2>&1 | Out-String

        if ($one -match 'not installed' -or $one -notmatch [regex]::Escape($simple)) {
            $without.Add($simple)
        } else {
            $withImage.Add($simple)
        }
    }

    $native = $withImage.Count -gt 0

    Say "payload assemblies : $($payload.Count) beside the shell"
    Say "native images      : $($withImage.Count) present, $($without.Count) missing, $($unmanaged.Count) unmanaged"

    if ($without.Count -gt 0) {
        Say ''
        Say 'NO NATIVE IMAGE (these are jitted on the cold launch):'
        foreach ($name in $without) { Say "  $name" }
    }

    if ($unmanaged.Count -gt 0) {
        Say "unmanaged (native already): $($unmanaged -join ', ')"
    }

    Say ''
    $queued = & $ngen queue status 2>&1 | Out-String
    foreach ($line in ($queued -split "`r?`n" | Where-Object { $_.Trim() })) { Say "  $line" }

    if (-not $native) {
        Say ''
        Say 'The installer schedules native image generation at IDLE priority, so it may not have'
        Say 'finished. The measurement below is still taken and still worth having  -  it is what a'
        Say 'user who installs and immediately runs would see  -  but it is not the figure the'
        Say 'native images were added to produce. Both are worth reporting; this line says which.'
    }
} else {
    Say "ngen.exe not found at $ngen"
}

# ---------------------------------------------------------------------------------------------
# The machine. Cold start is dominated by reading files that are not yet in the cache, so the
# storage medium matters more than the processor does.
# ---------------------------------------------------------------------------------------------
Section 'Machine'

$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1

Say "os           : $($os.Caption) $($os.Version) ($($os.OSArchitecture))"
Say "computer     : $($cs.Manufacturer) $($cs.Model)"
Say "processor    : $($cpu.Name.Trim())"
Say "cores        : $($cpu.NumberOfCores) physical, $($cpu.NumberOfLogicalProcessors) logical"
Say "memory       : $([math]::Round($cs.TotalPhysicalMemory / 1GB, 1)) GB"
Say "virtual      : $(if ($cs.HypervisorPresent) { 'hypervisor present' } else { 'no hypervisor reported' })"

try {
    $drive = (Get-Item $shell).PSDrive.Name
    $disk = Get-PhysicalDisk -ErrorAction Stop |
            Where-Object { $_.DeviceId -ne $null } |
            Select-Object -First 1
    Say "storage      : $($disk.FriendlyName), media type $($disk.MediaType), bus $($disk.BusType)"
} catch {
    Say "storage      : could not be read ($($_.Exception.Message))"
}

$release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
Say ".NET Fx      : release $release (4.7.2 needs 461808 or higher)"

# ---------------------------------------------------------------------------------------------
# The measurement itself. The harness drives the real shell through UI Automation, exactly as it
# does in the repository  -  this is the same code, told where the installed shell is.
# ---------------------------------------------------------------------------------------------
Section 'Measurement'

$harness = Join-Path $here 'harness\OpenVSA.Benchmarks.exe'

if (-not (Test-Path $harness)) {
    Say "FAILED: the harness is missing at $harness. Unpack the whole package, not just the MSI."
    $lines | Set-Content -Path $LogPath -Encoding UTF8
    Write-Host ''
    Write-Host "Log written to $LogPath"
    exit 2
}

Say "harness      : $harness"
Say "runs         : $Runs (the first is the cold one)"
Say ''
Say 'Launching OpenVSA. It will open and close by itself several times  -  please do not touch the'
Say 'keyboard or mouse while it does: the harness drives the menus, and a click of your own lands'
Say 'in the middle of the measurement.'
Say ''

$output = & $harness --gate --cold-start --shell $shell --runs $Runs 2>&1
$code = $LASTEXITCODE

foreach ($line in ($output | Out-String) -split "`r?`n") {
    if ($line.Trim() -or $lines[-1].Trim()) { Say $line.TrimEnd() }
}

Say ''
Say "harness exit code: $code  (0 = within 3 s, 1 = over, 2 = could not measure)"

Section 'Done'
Say 'Send the log file below back. Nothing else is needed, and nothing has been sent anywhere.'

$lines | Set-Content -Path $LogPath -Encoding UTF8

Write-Host ''
Write-Host "Log written to $LogPath" -ForegroundColor Green
exit $code
