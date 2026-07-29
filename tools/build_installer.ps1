<#
.SYNOPSIS
    Builds the OpenVSA shell and packages it into an MSI.

.DESCRIPTION
    The installer project is deliberately NOT a member of OpenVSA.slnx -- the same rule the C++
    FFT project follows, because the dotnet CLI cannot evaluate every project type and
    `dotnet test OpenVSA.slnx` has to keep working. This script is how the two are built together.

    It also VERIFIES THE PAYLOAD before packaging. A developer machine may have a real
    local.secrets.config sitting in the output directory, and shipping it would leak that
    developer's Syncfusion key into a public release. The .wxs excludes it by name; this checks
    that the exclusion worked rather than trusting it, because a leaked credential is not the kind
    of thing to find out about afterwards.

.PARAMETER Version
    Product version, e.g. 0.1.0. Phase releases are prereleases until the final phase.

.PARAMETER EmbedLicenseKey
    Embed the Syncfusion key from SYNCFUSION_LICENSE_KEY so end users see no evaluation banner.
    The key is read from the build environment and written only into obj\, which is git-ignored.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [switch]$EmbedLicenseKey
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root "src\OpenVSA.Ui\bin\$Configuration\net472"

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1

if (-not $msbuild) { throw 'MSBuild was not found. Install the Visual Studio build tools.' }

Write-Output "MSBuild:  $msbuild"
Write-Output "Payload:  $payload"
Write-Output "Version:  $Version"

if ($EmbedLicenseKey -and -not $env:SYNCFUSION_LICENSE_KEY) {
    Write-Warning ('SYNCFUSION_LICENSE_KEY is not set, so the installer will carry a build that ' +
                   'shows the Syncfusion evaluation banner.')
}

# The shell first, because the installer harvests its output.
$embed = if ($EmbedLicenseKey) { 'true' } else { 'false' }

& $msbuild (Join-Path $root 'OpenVSA.slnx') `
    /t:Restore /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

& $msbuild (Join-Path $root 'OpenVSA.slnx') `
    /p:Configuration=$Configuration /p:Platform=x64 /p:EmbedSyncfusionLicenseKey=$embed `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (-not (Test-Path (Join-Path $payload 'OpenVSA.exe'))) {
    throw "no OpenVSA.exe in $payload"
}

# Refuse to package a payload carrying a real secrets file. Checked here as well as excluded in
# the .wxs: two independent barriers, because one of them silently failing is exactly how a key
# reaches a public release.
$secret = Join-Path $payload 'local.secrets.config'
if (Test-Path $secret) {
    Write-Warning ("$secret is present in the payload. It is excluded from the MSI by name, and " +
                   'the packaged file list is checked below.')
}

& $msbuild (Join-Path $root 'installer\OpenVSA.Installer\OpenVSA.Installer.wixproj') `
    /t:Restore /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'installer restore failed' }

& $msbuild (Join-Path $root 'installer\OpenVSA.Installer\OpenVSA.Installer.wixproj') `
    /p:Configuration=$Configuration /p:Platform=x64 `
    /p:ProductVersion=$Version /p:PayloadDirectory="$payload\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

$msi = Get-ChildItem -Path (Join-Path $root 'installer\OpenVSA.Installer\bin') -Recurse -Filter '*.msi' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $msi) { throw 'the installer built but produced no .msi' }

Write-Output ''
Write-Output "MSI: $($msi.FullName)  ($([math]::Round($msi.Length / 1MB, 1)) MB)"

# What actually went in. Read from the MSI's own File table rather than from the .wxs, so an
# exclusion that did not take effect is caught here and not by a user.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    'OpenDatabase', 'InvokeMethod', $null, $installer, @($msi.FullName, 0))
$view = $database.GetType().InvokeMember(
    'OpenView', 'InvokeMethod', $null, $database, @('SELECT FileName FROM File'))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

$packaged = @()
while ($true) {
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if (-not $record) { break }
    $packaged += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
}

Write-Output "Files packaged: $($packaged.Count)"

$forbidden = $packaged | Where-Object {
    $_ -match 'local\.secrets\.config' -or $_ -match '^Ivi\.Visa' -or $_ -match '^NationalInstruments'
}

if ($forbidden) {
    throw ("The MSI contains files it must never ship: " + ($forbidden -join ', '))
}

Write-Output 'Payload check: no secrets file, no NI redistributables. OK.'
