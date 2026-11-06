# Sign Gravityless binaries and installers with the code signing certificate.
#
# Usage:
#   .\sign.ps1 -Path artifacts\installer\Gravityless-2.29.0.01-x64.exe
#   .\sign.ps1 -Path artifacts\installer\*.exe,artifacts\win-x64\Gravityless.App.exe
#   .\sign.ps1 -Path file.exe -PfxPath path\to\cert.pfx -PfxPassword secret
#
# Prioritizes Gravityless.pfx in the repository root or certs\ folder if present; falls back
# to the local Windows certificate store (Cert:\CurrentUser\My).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    # Sign from an exported .pfx (default: Gravityless.pfx in script root or certs\ if present).
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
    $nugetDirs = @(
        "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools",
        "C:\Users\aka\.nuget\packages\microsoft.windows.sdk.buildtools"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($nd in $nugetDirs) {
        $signtool = Get-ChildItem $nd -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Select-Object -First 1 -ExpandProperty FullName
        if ($signtool) { break }
    }
}

if (-not $signtool) {
    $cmd = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = $cmd.Source }
}

# Auto-detect Gravityless.pfx in repository root or certs\ folder if PfxPath was not explicitly specified.
if (-not $PfxPath) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'Gravityless.pfx'),
        (Join-Path $PSScriptRoot 'certs\Gravityless.pfx'),
        'F:\Gravityless.pfx'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $PfxPath = $c
            break
        }
    }
}
if ($env:GRAVITYLESS_PFX_PASSWORD) {
    $PfxPassword = $env:GRAVITYLESS_PFX_PASSWORD
} elseif ($env:PFX_PASSWORD) {
    $PfxPassword = $env:PFX_PASSWORD
}

$certObj = $null
$signArgs = if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "Certificate not found: $PfxPath" }
    $resolvedPfx = (Resolve-Path $PfxPath).Path
    Write-Host "Signing using PFX: $resolvedPfx"
    try {
        $certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($resolvedPfx, $PfxPassword)
    } catch {
        Write-Warning "Failed to load certificate directly from PFX with given password: $_"
    }
    $base = @('sign', '/f', $resolvedPfx)
    if ($PfxPassword) { $base += @('/p', $PfxPassword) }
    $base += @('/fd', 'SHA256')
    $base
} else {
    # Store lookup keeps the private key on the machine instead of in a file.
    $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Certificate $Thumbprint not found in Cert:\CurrentUser\My and no PFX at $PfxPath" }
    if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint has no private key" }
    $certObj = $cert
    Write-Host "Signing using Certificate Store: $($cert.Subject)"
    @('sign', '/sha1', $Thumbprint, '/fd', 'SHA256')
}

if (-not $signtool -and -not $certObj) {
    throw 'Neither signtool.exe nor a valid certificate object could be loaded for signing.'
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

        if ($signtool) {
            foreach ($server in $timestampServers) {
                & $signtool @signArgs '/tr' $server '/td' 'SHA256' $file.FullName 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    $signed = $true
                    break
                }
                Write-Host -NoNewline " [$server unavailable]"
            }

            if (-not $signed) {
                Write-Host -NoNewline ' [no timestamp, signing without]'
                & $signtool @signArgs $file.FullName 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) { $signed = $true }
            }
        }

        # Fallback to PowerShell Set-AuthenticodeSignature
        if (-not $signed -and $certObj) {
            foreach ($server in $timestampServers) {
                $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $certObj -HashAlgorithm 'SHA256' -TimestampServer $server -ErrorAction SilentlyContinue
                if ($sig.Status -eq 'Valid' -or $sig.Status -eq 'UnknownError') {
                    $signed = $true
                    break
                }
            }
            if (-not $signed) {
                $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $certObj -HashAlgorithm 'SHA256' -ErrorAction SilentlyContinue
                if ($sig.Status -eq 'Valid' -or $sig.Status -eq 'UnknownError') {
                    $signed = $true
                }
            }
        }

        if (-not $signed) {
            Write-Host ' FAILED' -ForegroundColor Red
            $failed += $file.Name
            continue
        }

        $sigCheck = Get-AuthenticodeSignature $file.FullName
        if ($sigCheck.Status -eq 'Valid' -or $sigCheck.Status -eq 'UnknownError') {
            Write-Host " OK ($($sigCheck.Status))" -ForegroundColor Green
        } else {
            Write-Host " signed with status $($sigCheck.Status)" -ForegroundColor Yellow
        }
    }
}

if ($failed.Count -gt 0) {
    throw "Signing failed for: $($failed -join ', ')"
}

Write-Host 'All files signed successfully.' -ForegroundColor Green
