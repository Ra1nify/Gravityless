# Build Gravityless installers for win-x64 and win-arm64.
#
#   .\build.ps1                    # both architectures, unsigned
#   .\build.ps1 -Arch x64          # one architecture
#   .\build.ps1 -Sign              # sign the app exe and the setup exe
#   .\build.ps1 -SkipPublish       # only re-run Inno Setup on existing artifacts
#
# Requires: .NET 8 SDK, Inno Setup 6 or 7 (ISCC.exe).

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Arch = 'all',

    [switch]$Sign,

    [switch]$SkipPublish,

    # Certificate thumbprint; defaults to the Ra1nify code-signing certificate.
    [string]$Thumbprint = '2095AFBE2EA9362DA8B44C92FAE740BE218031EE',

    # Sign from an exported .pfx instead of the local user store.
    [string]$PfxPath,
    [string]$PfxPassword
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appProject = Join-Path $root 'src\Gravityless.App\Gravityless.App.csproj'
$issScript = Join-Path $root 'installer\Gravityless.iss'
$artifacts = Join-Path $root 'artifacts'

# Read the version once so the issue and the output names stay in sync.
[xml]$proj = Get-Content $appProject
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Gravityless.App.csproj' }

Write-Host "Gravityless $version" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Tool discovery
# ---------------------------------------------------------------------------

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe"
    "${env:LOCALAPPDATA}\Programs\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

# Fall back to the registered install location, which may be on a non-system drive.
if (-not $iscc) {
    $iscc = Get-ItemProperty `
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', `
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', `
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'Inno Setup' -and $_.InstallLocation } |
        ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

if (-not $iscc) {
    throw 'ISCC.exe not found. Install Inno Setup 6 or 7.'
}
Write-Host "ISCC: $iscc"

$signtool = $null
if ($Sign) {
    $signtool = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe"
        "${env:ProgramFiles}\Windows Kits\10\bin\x64\signtool.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $signtool) {
        $signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
            -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK.' }

    if ($PfxPath) {
        if (-not (Test-Path $PfxPath)) { throw "Certificate not found: $PfxPath" }
        $signBase = @('/f', (Resolve-Path $PfxPath).Path)
        if ($PfxPassword) { $signBase += @('/p', $PfxPassword) }
        Write-Host "Signing with: $PfxPath"
    } else {
        $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $cert) { throw "Signing certificate $Thumbprint not found in Cert:\CurrentUser\My" }
        if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint has no private key" }
        $signBase = @('/sha1', $Thumbprint)
        Write-Host "Signing with: $($cert.Subject)"
    }
    $signBase += @('/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

$targets = if ($Arch -eq 'all') { @('x64', 'arm64') } else { @($Arch) }
$setups = @()

foreach ($a in $targets) {
    $rid = "win-$a"
    $publishDir = Join-Path $artifacts $rid

    if (-not $SkipPublish) {
        Write-Host "`n=== Publishing $rid ===" -ForegroundColor Cyan
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

        dotnet publish $appProject `
            -c Release -r $rid --self-contained false `
            -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

        # Symbols are not shipped; they bloat the installer and leak source paths.
        Get-ChildItem $publishDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    if (-not (Test-Path $publishDir)) { throw "Publish output missing: $publishDir" }

    if ($Sign) {
        Write-Host "Signing $rid binaries..." -ForegroundColor Cyan
        Get-ChildItem $publishDir -Filter '*.exe' -Recurse | ForEach-Object {
            & $signtool sign @signBase $_.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($_.Name)" }
        }
    }

    Write-Host "`n=== Building installer ($a) ===" -ForegroundColor Cyan
    & $iscc "/DAppArch=$a" "/DPublishDir=..\artifacts\$rid" "/DAppVersion=$version" $issScript
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $a" }

    $setups += Join-Path $artifacts "installer\Gravityless-$version-$a.exe"
}

# The setup exe is not covered by ISCC, so sign it afterwards.
if ($Sign) {
    Write-Host "`n=== Signing installers ===" -ForegroundColor Cyan
    foreach ($setup in $setups) {
        & $signtool sign @signBase $setup | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $setup" }
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

Write-Host "`n=== Result ===" -ForegroundColor Green
foreach ($setup in $setups) {
    if (Test-Path $setup) {
        $size = [math]::Round((Get-Item $setup).Length / 1MB, 2)
        $status = if ($Sign) { (Get-AuthenticodeSignature $setup).Status } else { 'NotSigned' }
        Write-Host ("  {0,-40} {1,6} MB  {2}" -f (Split-Path $setup -Leaf), $size, $status)
    }
}
