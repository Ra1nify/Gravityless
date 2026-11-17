# Build Gravityless installers for win-x64 and win-arm64.
#
#   .\build.ps1                    # both architectures, unsigned
#   .\build.ps1 -Arch x64          # one architecture
#   .\build.ps1 -Sign              # sign the app exe and the setup exe
#   .\build.ps1 -SkipPublish       # only re-run Inno Setup on existing artifacts
#
# Requires: .NET 10 SDK, Inno Setup 6 or 7 (ISCC.exe).

[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Arch = 'all',

    [switch]$Sign,

    [switch]$SkipPublish,

    # Sign from an exported .pfx (default: Gravityless.pfx in script root or certs\ if present).
    [string]$PfxPath,
    [string]$PfxPassword = 'Gravityless',

    # Certificate thumbprint fallback.
    [string]$Thumbprint = '2095AFBE2EA9362DA8B44C92FAE740BE218031EE'
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$appProject = Join-Path $root 'src\Gravityless.App\Gravityless.App.csproj'
$issScript = Join-Path $root 'installer\Gravityless.iss'
$artifacts = Join-Path $root 'artifacts'

# ---------------------------------------------------------------------------
# Tool discovery / Environment preparation
# ---------------------------------------------------------------------------

# Ensure dotnet CLI is in PATH
if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    $dotnetDir = @(
        (Join-Path $env:USERPROFILE '.dotnet')
        'C:\Users\aka\.dotnet'
        "${env:ProgramFiles}\dotnet"
        "${env:ProgramFiles(x86)}\dotnet"
    ) | Where-Object { $_ -and (Test-Path (Join-Path $_ 'dotnet.exe')) } | Select-Object -First 1

    if ($dotnetDir) {
        $env:PATH = "$dotnetDir;$env:PATH"
    }
}

# Read or calculate the version
$versionCode = $null
if (-not $Version) {
    $getVerScript = Join-Path $root 'scripts\get-version.ps1'
    if (Test-Path $getVerScript) {
        try {
            $verObj = & $getVerScript -PropsPath (Join-Path $root 'Directory.Build.props')
            if ($verObj -and $verObj.Version) {
                $Version = $verObj.Version
                $versionCode = "$($verObj.VersionCode)"
            }
        } catch {
            Write-Warning "scripts\get-version.ps1 failed: $_"
        }
    }
}

if (-not $Version) {
    try {
        $Version = (dotnet msbuild $appProject -p:Configuration=Release -getProperty:Version -nologo 2>$null).Trim()
    } catch {}
}

if (-not $Version) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    if (Test-Path $propsPath) {
        [xml]$propsXml = Get-Content $propsPath -ErrorAction SilentlyContinue
        $vNode = $propsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if ($vNode -is [System.Xml.XmlElement]) {
            $Version = $vNode.InnerText.Trim()
        } elseif ($vNode) {
            $Version = "$vNode".Trim()
        }
    }
}
if (-not $Version) { throw 'Version not found in C# project or Directory.Build.props' }

if (-not $versionCode) {
    $versionCode = $Version.Replace('.', '')
}

$version = $Version
Write-Host "Gravityless $version (code: $versionCode)" -ForegroundColor Cyan

# Locate Inno Setup Compiler (ISCC.exe)
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
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

if (-not $iscc) {
    throw 'ISCC.exe not found. Install Inno Setup 6 or 7.'
}
Write-Host "ISCC: $iscc"

$signtool = $null
$certObj = $null
if ($Sign) {
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

    $tempPfxCreated = $false
    $tempPfxPath = Join-Path $root 'temp_ci_signing.pfx'

    # Auto-detect signing material from environment secrets, PFX, or PEM key pair
    if (-not $PfxPath) {
        if ($env:GRAVITYLESS_PFX_BASE64 -or $env:SIGNING_PFX_BASE64) {
            $b64 = if ($env:GRAVITYLESS_PFX_BASE64) { $env:GRAVITYLESS_PFX_BASE64 } else { $env:SIGNING_PFX_BASE64 }
            [System.IO.File]::WriteAllBytes($tempPfxPath, [System.Convert]::FromBase64String($b64.Trim()))
            $PfxPath = $tempPfxPath
            $tempPfxCreated = $true
            Write-Host "Decoded PFX from base64 environment secret." -ForegroundColor Cyan
        } elseif (($env:GRAVITYLESS_PRIVATE_KEY -or $env:SIGNING_KEY_PEM) -and ($env:GRAVITYLESS_CERT_PUBLIC -or $env:SIGNING_CERT_PEM)) {
            $keyPem = if ($env:GRAVITYLESS_PRIVATE_KEY) { $env:GRAVITYLESS_PRIVATE_KEY } else { $env:SIGNING_KEY_PEM }
            $certPem = if ($env:GRAVITYLESS_CERT_PUBLIC) { $env:GRAVITYLESS_CERT_PUBLIC } else { $env:SIGNING_CERT_PEM }
            try {
                $certObj = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($certPem, $keyPem)
                $pfxBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $PfxPassword)
                [System.IO.File]::WriteAllBytes($tempPfxPath, $pfxBytes)
                $PfxPath = $tempPfxPath
                $tempPfxCreated = $true
                Write-Host "Synthesized PFX from PEM key and public cert environment secrets." -ForegroundColor Cyan
            } catch {
                Write-Warning "Failed to assemble certificate from PEM secrets: $_"
            }
        }
    }

    # Auto-detect Gravityless.pfx in repository root, certs\, or external drive
    if (-not $PfxPath) {
        $candidates = @(
            (Join-Path $root 'Gravityless.pfx'),
            (Join-Path $root 'certs\Gravityless.pfx'),
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

    if ($PfxPath) {
        if (-not (Test-Path $PfxPath)) { throw "Certificate not found: $PfxPath" }
        $resolvedPfx = (Resolve-Path $PfxPath).Path
        try {
            $certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($resolvedPfx, $PfxPassword)
        } catch {
            Write-Warning "Failed to load certificate directly from PFX with given password: $_"
        }
        $signBase = @('/f', $resolvedPfx)
        if ($PfxPassword) { $signBase += @('/p', $PfxPassword) }
        Write-Host "Signing with PFX: $resolvedPfx"
    } else {
        $cert = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $cert) { throw "Signing certificate $Thumbprint not found in Cert:\CurrentUser\My and no PFX at $PfxPath" }
        if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint has no private key" }
        $certObj = $cert
        $signBase = @('/sha1', $Thumbprint)
        Write-Host "Signing with Cert Store: $($cert.Subject)"
    }

    if (-not $signtool -and -not $certObj) {
        throw 'Neither signtool.exe nor a valid PowerShell certificate object could be resolved for signing.'
    }
    if ($signtool) {
        Write-Host "signtool: $signtool"
    } else {
        Write-Host "signtool not found, using PowerShell Set-AuthenticodeSignature fallback." -ForegroundColor Yellow
    }
}

# Public timestamp services fail independently, so each candidate is tried in turn.
# A timestamp is preferable but not required: without it the signature stays valid
# until the certificate itself expires (2036-11-01).
$timestampServers = @(
    'http://timestamp.digicert.com'
    'http://timestamp.sectigo.com'
    'http://time.certum.pl'
    'http://timestamp.globalsign.com/tsa/r6advanced1'
)

# Signs one file, falling back through the timestamp servers. Throws only when the
# signature itself cannot be produced.
function Invoke-SignFile {
    param([string]$FilePath)

    if ($signtool) {
        foreach ($server in $timestampServers) {
            & $signtool sign @signBase '/fd' 'SHA256' '/tr' $server '/td' 'SHA256' $FilePath 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { return }
            Write-Host "  timestamp server unavailable: $server" -ForegroundColor DarkYellow
        }

        & $signtool sign @signBase '/fd' 'SHA256' $FilePath 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  signed without a timestamp; valid until the certificate expires' -ForegroundColor Yellow
            return
        }
    }

    # Fallback to PowerShell Set-AuthenticodeSignature
    if ($certObj) {
        foreach ($server in $timestampServers) {
            $sig = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $certObj -HashAlgorithm 'SHA256' -TimestampServer $server -ErrorAction SilentlyContinue
            if ($sig.Status -eq 'Valid' -or $sig.Status -eq 'UnknownError') { return }
            Write-Host "  PowerShell timestamp server unavailable: $server" -ForegroundColor DarkYellow
        }
        $sig = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $certObj -HashAlgorithm 'SHA256' -ErrorAction SilentlyContinue
        if ($sig.Status -eq 'Valid' -or $sig.Status -eq 'UnknownError') {
            Write-Host '  signed with PowerShell without a timestamp' -ForegroundColor Yellow
            return
        }
    }

    throw "Signing failed: $FilePath"
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

$targets = if ($Arch -eq 'all') { @('x64', 'arm64') } else { @($Arch) }
$setups = @()

$installerOutDir = Join-Path $artifacts 'installer'
if (-not (Test-Path $installerOutDir)) {
    New-Item -ItemType Directory -Path $installerOutDir -Force | Out-Null
}

foreach ($a in $targets) {
    $rid = "win-$a"
    $publishDir = Join-Path $artifacts $rid

    if (-not $SkipPublish) {
        Write-Host "`n=== Publishing $rid ===" -ForegroundColor Cyan
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

        dotnet publish $appProject `
            -c Release -r $rid --self-contained false `
            -p:Version=$version -p:VersionCode=$versionCode `
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
            Invoke-SignFile -FilePath $_.FullName
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
        Invoke-SignFile -FilePath $setup
    }
}

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------

if ($tempPfxCreated -and (Test-Path $tempPfxPath)) {
    Remove-Item $tempPfxPath -Force -ErrorAction SilentlyContinue
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
