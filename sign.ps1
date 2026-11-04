# Sign Gravityless binaries and installers with the code signing certificate.
#
# Usage:
#   .\sign.ps1 -Path artifacts\installer\Gravityless-2.29.0.01-x64.exe
#   .\sign.ps1 -Path artifacts\installer\*.exe,artifacts\win-x64\Gravityless.App.exe
#   .\sign.ps1 -Path file.exe -PfxPath path\to\cert.pfx -PfxPassword secret
#
# Prioritizes Gravityless.pfx in the repository root if present; falls back
# to the local Windows certificate store (Cert:\CurrentUser\My).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    # Sign from an exported .pfx (default: Gravityless.pfx in script root if present).
    [string]$PfxPath,
    [string]$PfxPassword = 'Gravityless',

    # Defaults to the Ra1nify / Gravityless Sound Team certificate thumbprint.
    [string]$Thumbprint = '2095AFBE2EA9362DA8B44C92FAE740BE218031EE',

    # Tried in order. Public timestamp services go down independently, so a single
    # hard-coded host turns every release into a manual retry.
    [string[]]$TimestampServer = @(
        'http://timestamp.digicert.com'
        'http://timestamp.sectigo.com'
        'http://time.certum.pl'
        'http://timestamp.globalsign.com/tsa/r6advanced1'
    )
)

$ErrorActionPreference = 'Stop'

# signtool ships with the Windows SDK, ClickOnce SDK, or NuGet build tools.
$pfX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$pf = [Environment]::GetFolderPath('ProgramFiles')

$signtool = @(
    (Join-Path $pfX86 'Microsoft SDKs\ClickOnce\SignTool\signtool.exe')
    (Join-Path $pf 'Microsoft SDKs\ClickOnce\SignTool\signtool.exe')
    (Join-Path $pfX86 'Windows Kits\10\bin\x64\signtool.exe')
    (Join-Path $pf 'Windows Kits\10\bin\x64\signtool.exe')
    (Get-ChildItem (Join-Path $pfX86 'Windows Kits\10\bin') -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1 -ExpandProperty FullName)
    (Get-ChildItem (Join-Path $pf 'Windows Kits\10\bin') -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1 -ExpandProperty FullName)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $signtool) {
    $signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
        -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $signtool) {
    throw 'signtool.exe not found. Install the Windows 10/11 SDK or the Microsoft.Windows.SDK.BuildTools package.'
}

# Auto-detect Gravityless.pfx in repository root if PfxPath was not explicitly specified.
if (-not $PfxPath) {
    $repoPfx = Join-Path $PSScriptRoot 'Gravityless.pfx'
    if (Test-Path $repoPfx) {
        $PfxPath = $repoPfx
    }
}
if ($env:PFX_PASSWORD) {
    $PfxPassword = $env:PFX_PASSWORD
}

$signArgs = if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "Certificate not found: $PfxPath" }
    Write-Host "Signing using PFX: $PfxPath"
    $base = @('sign', '/f', (Resolve-Path $PfxPath).Path)
    if ($PfxPassword) { $base += @('/p', $PfxPassword) }
    $base += @('/fd', 'SHA256')
    $base
} else {
    # Store lookup keeps the private key on the machine instead of in a file.
    $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Certificate $Thumbprint not found in Cert:\CurrentUser\My and no PFX at $PfxPath" }
    if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint has no private key" }
    Write-Host "Signing using Certificate Store: $($cert.Subject)"
    @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256')
}

# Timestamping keeps signatures valid after the certificate expires (2036-11-01).
$timestampServers = @($TimestampServer | Where-Object { $_ })

$failed = @()
foreach ($pattern in $Path) {
    $files = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue
    if (-not $files) {
        Write-Warning "No file matched: $pattern"
        continue
    }

    foreach ($file in $files) {
        Write-Host "Signing $($file.Name) ..." -NoNewline

        $signed = $false
        foreach ($server in $timestampServers) {
            & $signtool @signArgs '/tr' $server '/td' 'SHA256' $file.FullName | Out-Null
            if ($LASTEXITCODE -eq 0) {
                $signed = $true
                break
            }

            Write-Host -NoNewline " [$server unavailable]"
        }

        # A timestamp is preferable but not mandatory: an untimestamped signature is still
        # valid now and only stops verifying once the certificate itself expires.
        if (-not $signed) {
            Write-Host -NoNewline ' [no timestamp, signing without]'
            & $signtool @signArgs $file.FullName | Out-Null
            if ($LASTEXITCODE -eq 0) { $signed = $true }
        }

        if (-not $signed) {
            Write-Host ' FAILED' -ForegroundColor Red
            $failed += $file.Name
            continue
        }

        & $signtool verify /pa /q $file.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host ' signed but verification failed' -ForegroundColor Yellow
            $failed += $file.Name
        } else {
            Write-Host ' OK' -ForegroundColor Green
        }
    }
}

if ($failed.Count -gt 0) {
    throw "Signing failed for: $($failed -join ', ')"
}

Write-Host 'All files signed successfully.' -ForegroundColor Green
