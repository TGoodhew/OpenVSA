<#
.SYNOPSIS
    REQ-ARC-001: build the analysis stack with every real transport removed, and run its tests.

.DESCRIPTION
    The criterion is that the solution builds with OpenVSA.Hal.Visa, .File and .Sim removed from the
    build, substituting a single stub front end, and that all DSP and measurement unit tests pass
    unchanged.

    The architecture tests already assert that no analysis assembly *names* a transport. That is a
    weaker statement than it appears: an assembly can avoid a compile-time reference and still be
    unbuildable without one, through a shared build property, a copied plug-in, or a test fixture
    that quietly needs a real source. Building without them is what settles it.

    Nothing is deleted. The projects that need a transport are simply not built, the stub is built
    in their place, and the output tree is then searched for the three assemblies — if the analysis
    stack pulled one in transitively, it lands here and this fails.

.NOTES
    Run from anywhere:  pwsh tools/build_without_front_ends.ps1
    Exit 0 when the separation holds, 1 when it does not, 2 when the build itself failed.
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\no-front-ends"

$forbidden = @("OpenVSA.Hal.Visa", "OpenVSA.Hal.File", "OpenVSA.Hal.Sim")

# The analysis stack and its tests. OpenVSA.Dsp.Tests and OpenVSA.Measurement.Tests are named by
# the requirement; the stub is the substitute front end it asks for.
$projects = @(
    "src\OpenVSA.Hal.Stub\OpenVSA.Hal.Stub.csproj",
    "tests\OpenVSA.Dsp.Tests\OpenVSA.Dsp.Tests.csproj",
    "tests\OpenVSA.Measurement.Tests\OpenVSA.Measurement.Tests.csproj"
)

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
                 Select-Object -First 1
        if ($found) { return $found }
    }

    $onPath = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw "MSBuild not found."
}

Write-Output "REQ-ARC-001: building the analysis stack with no transport present."
Write-Output ""

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
$null = New-Item -ItemType Directory -Path $output -Force

$msbuild = Find-MSBuild

foreach ($project in $projects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $target = Join-Path $output $name

    Write-Output "  building $name"

    # Into a tree of its own so the search below cannot find a transport that some earlier ordinary
    # build left lying in the usual bin directory.
    & $msbuild (Join-Path $root $project) `
        /t:Restore /v:quiet /nologo `
        /p:Configuration=$Configuration /p:OutputPath="$target\" | Out-Null

    & $msbuild (Join-Path $root $project) `
        /v:quiet /nologo `
        /p:Configuration=$Configuration /p:OutputPath="$target\" | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Error "  $name did not build without the transports. REQ-ARC-001 does not hold."
        exit 2
    }
}

Write-Output ""

$intruders = Get-ChildItem $output -Recurse -Include *.dll, *.exe |
             Where-Object { $forbidden -contains [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }

if ($intruders) {
    Write-Output "  A transport reached the analysis stack's output:"
    $intruders | ForEach-Object { Write-Output ("    " + $_.FullName.Substring($root.Length + 1)) }
    Write-Output ""
    Write-Output "  REQ-ARC-001: layers L3 and above may reference only the HAL interface assembly."
    exit 1
}

$stub = Get-ChildItem $output -Recurse -Filter "OpenVSA.Hal.Stub.dll" | Select-Object -First 1

if (-not $stub) {
    Write-Error "  The stub front end was not built, so nothing was substituted."
    exit 1
}

Write-Output "  no transport assembly in the output; the stub front end stands in their place"
Write-Output ""

$assemblies = @(
    (Join-Path $output "OpenVSA.Dsp.Tests\OpenVSA.Dsp.Tests.dll"),
    (Join-Path $output "OpenVSA.Measurement.Tests\OpenVSA.Measurement.Tests.dll")
)

foreach ($assembly in $assemblies) {
    if (-not (Test-Path $assembly)) {
        Write-Error "  $assembly was not produced."
        exit 2
    }
}

Write-Output "  running the DSP and measurement suites against that build"
Write-Output ""

& dotnet vstest $assemblies --logger:"console;verbosity=minimal"
$testExit = $LASTEXITCODE

Write-Output ""

if ($testExit -ne 0) {
    Write-Output "  The DSP or measurement tests did not pass with the transports removed."
    exit 1
}

Write-Output "  REQ-ARC-001 holds: the analysis stack builds and passes with no transport present."
exit 0
