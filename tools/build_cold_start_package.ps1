<#
.SYNOPSIS
    Builds the self-contained package that measures REQ-NFR-025 on a clean machine (issue #410).

.DESCRIPTION
    Produces one zip containing the installer, the measurement harness and a script that runs it
    and writes a log. It is meant to be copied to a machine that has never had OpenVSA on it --
    which is the only kind of machine the requirement's "cold" can be measured on, and which by
    definition has no repository, no SDK and no build tools.

    Nothing in the package needs installing except the MSI itself. Windows PowerShell 5.1 and the
    .NET Framework 4.7.2 runtime are both part of Windows.

    THE HARNESS IS THE REPOSITORY'S OWN, not a re-implementation. It is the same
    ColdStartMeasurement that --measure runs, told where the installed shell is instead of
    deriving it from a source tree. A second measurement written for the clean machine would be a
    second thing to keep in step, and the two would disagree eventually -- REQ-MKR-006's complaint
    about independently computed readouts, in another costume.

.PARAMETER Version
    Product version for the MSI, e.g. 0.2.0.

.PARAMETER Configuration
    Build configuration. Release unless there is a reason.

.PARAMETER SkipInstaller
    Reuse the MSI already in the installer's output rather than rebuilding it. For iterating on
    the script without waiting for WiX.

.PARAMETER EmbedLicenseKey
    Embed the Syncfusion key from SYNCFUSION_LICENSE_KEY so the shell shows no evaluation banner.
    STRONGLY RECOMMENDED HERE: the banner is a modal dialog on a dispatcher thread, and the
    harness drives the shell through UI Automation -- an unlicensed build does not merely look
    wrong, it fails to measure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Configuration = 'Release',

    [switch]$SkipInstaller,

    [switch]$EmbedLicenseKey
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$staging = Join-Path $root "artifacts\coldstart\OpenVSA-ColdStart-$Version"
$zip = "$staging.zip"

Write-Output "root    : $root"
Write-Output "staging : $staging"

# ---------------------------------------------------------------------------------------------
# The harness. Built at solution level: a csproj-level build with an explicit Platform writes to
# bin\x64\ instead of bin\, and the copy below would then take whichever tree was older.
# ---------------------------------------------------------------------------------------------
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1

if (-not $msbuild) { throw 'MSBuild was not found.' }

Write-Output ''
Write-Output "Building the harness ($Configuration, x64)..."

& $msbuild (Join-Path $root 'OpenVSA.slnx') `
    /restore /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'the solution build failed' }

# ---------------------------------------------------------------------------------------------
# The installer.
# ---------------------------------------------------------------------------------------------
if (-not $SkipInstaller) {
    Write-Output ''
    Write-Output 'Building the installer...'

    # A hashtable, not an array. Splatting an array passes its elements POSITIONALLY, so
    # @('-Version', $Version) arrives as two positional arguments and the second one lands on
    # -Configuration. Named parameters need a hashtable.
    $arguments = @{ Version = $Version; Configuration = $Configuration }
    if ($EmbedLicenseKey) { $arguments['EmbedLicenseKey'] = $true }

    & (Join-Path $root 'tools\build_installer.ps1') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'the installer build failed' }
}

$msi = Get-ChildItem (Join-Path $root 'installer\OpenVSA.Installer\bin') -Recurse -Filter '*.msi' |
       Sort-Object LastWriteTimeUtc -Descending |
       Select-Object -First 1

if (-not $msi) { throw 'no MSI was produced' }

Write-Output "MSI     : $($msi.FullName)"

# ---------------------------------------------------------------------------------------------
# Assemble.
# ---------------------------------------------------------------------------------------------
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null

# A versioned name, because "OpenVSA.msi" on a shared machine is ambiguous and because a label is
# not a filename -- the mistake made once when attaching a release asset.
Copy-Item $msi.FullName (Join-Path $staging "OpenVSA-$Version-x64.msi")

Copy-Item (Join-Path $root 'tools\coldstart\Measure-ColdStart.ps1') $staging
Copy-Item (Join-Path $root 'tools\coldstart\RUN-ME.cmd') $staging
Copy-Item (Join-Path $root 'tools\coldstart\README.txt') $staging

$harnessSource = Join-Path $root "tests\OpenVSA.Benchmarks\bin\$Configuration\net472"
if (-not (Test-Path $harnessSource)) { throw "no harness output at $harnessSource" }

$harness = Join-Path $staging 'harness'
New-Item -ItemType Directory -Force $harness | Out-Null

# Everything the harness needs to run, and nothing that only matters in a source tree. The
# BenchmarkDotNet artefacts folder and the .pdb files are excluded: they are large, and a symbol
# file tells a clean machine nothing it will be asked.
Get-ChildItem $harnessSource -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.config', '.json' } |
    ForEach-Object { Copy-Item $_.FullName $harness }

# ---------------------------------------------------------------------------------------------
# Refuse to ship a package that cannot work, or that carries what it must not.
#
# Checked rather than trusted, for the reason build_installer.ps1 checks its own payload: the
# failure is silent and it is discovered by the person on the clean machine, who cannot fix it.
# ---------------------------------------------------------------------------------------------
$required = @(
    'harness\OpenVSA.Benchmarks.exe',
    'Measure-ColdStart.ps1',
    'RUN-ME.cmd',
    'README.txt',
    "OpenVSA-$Version-x64.msi")

foreach ($item in $required) {
    if (-not (Test-Path (Join-Path $staging $item))) { throw "the package is missing $item" }
}

$forbidden = Get-ChildItem $staging -Recurse -File |
             Where-Object { $_.Name -match 'local\.secrets\.config|^Ivi\.Visa|^NationalInstruments' }

if ($forbidden) {
    throw ("the package carries files it must never ship: " +
           (($forbidden | ForEach-Object { $_.Name }) -join ', '))
}

# The harness drives the shell through UI Automation, so an unlicensed Syncfusion build does not
# merely show a banner: the trial dialog is modal on a dispatcher thread and the measurement
# never completes. Worth a warning at build time rather than a mystery on the clean machine.
if (-not $EmbedLicenseKey) {
    Write-Warning ('Built without -EmbedLicenseKey. If this build shows the Syncfusion evaluation ' +
                   'dialog, it is MODAL and the harness will time out rather than measure.')
}

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Output ''
Write-Output "Package : $zip  ($size MB)"
Write-Output ''
Write-Output 'Copy it to a machine that has NEVER had OpenVSA installed, unpack it, run the MSI,'
Write-Output 'then run RUN-ME.cmd without opening OpenVSA first. Send back the log it names.'
