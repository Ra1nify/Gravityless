<#
.SYNOPSIS
    Calculates Gravityless release version: %verOfficial%.%verBuildTime%

.DESCRIPTION
    - verOfficial: queried automatically from the official Antigravity auto-updater endpoint:
      https://antigravity-hub-auto-updater-974169037036.us-central1.run.app/manifest/latest-x64-win.yml
      (with local fallback to Directory.Build.props / Antigravity.exe if offline).
    - verBuildTime: 2-digit sequence counter (01-99) determined by querying Git tags
      matching "${verOfficial}.*". If tag 2.33.0.01 exists, next is 02; if no tags exist, starts at 01.
    - If verBuildTime exceeds 99, raises an overflow circuit breaker error and halts the build.
    - Automatically keeps Directory.Build.props <OfficialVersion> synchronized on disk.
#>

[CmdletBinding()]
param(
    [string]$PropsPath = "Directory.Build.props",
    [switch]$ForCI
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }
$fullPropsPath = if ([System.IO.Path]::IsPathRooted($PropsPath)) { $PropsPath } else { Join-Path $root $PropsPath }

# ---------------------------------------------------------------------------
# 1. Resolve verOfficial from Official Auto-Updater Manifest (or Fallback)
# ---------------------------------------------------------------------------
$manifestUrl = "https://antigravity-hub-auto-updater-974169037036.us-central1.run.app/manifest/latest-x64-win.yml"
$officialVersion = $null

try {
    $rawYaml = $null
    if (Get-Command 'curl.exe' -ErrorAction SilentlyContinue) {
        $rawYaml = & curl.exe -s --max-time 10 $manifestUrl
    }
    if (-not $rawYaml -or $rawYaml -notmatch 'version:\s*([^\r\n]+)') {
        $rawYaml = Invoke-RestMethod -Uri $manifestUrl -TimeoutSec 10 -ErrorAction Stop
    }

    if ($rawYaml -match '(?m)^version:\s*([^\r\n]+)') {
        $officialVersion = $Matches[1].Trim()
    } elseif ($rawYaml -is [string] -and $rawYaml -match 'version:\s*([0-9]+\.[0-9]+\.[0-9]+)') {
        $officialVersion = $Matches[1].Trim()
    } elseif ($rawYaml.version) {
        $officialVersion = "$($rawYaml.version)".Trim()
    }
    if ($officialVersion) {
        Write-Host "Official Antigravity version resolved from endpoint: $officialVersion" -ForegroundColor Green
    }
} catch {
    Write-Warning "Failed to query official manifest from $manifestUrl : $_"
}

# Fallback: if network is unreachable, read from Directory.Build.props or local engine exe
if (-not $officialVersion) {
    if (Test-Path $fullPropsPath) {
        try {
            [xml]$propsXml = Get-Content $fullPropsPath -Raw
            $v = $propsXml.Project.PropertyGroup.OfficialVersion
            if ($v -is [System.Xml.XmlElement]) { $officialVersion = $v.InnerText.Trim() }
            elseif ($v) { $officialVersion = "$v".Trim() }
        } catch {}
    }
    if (-not $officialVersion) {
        $localExe = Join-Path $root "core\Antigravity.exe"
        if (Test-Path $localExe) {
            try {
                $verInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($localExe)
                if ($verInfo.ProductVersion) { $officialVersion = $verInfo.ProductVersion.Trim() }
            } catch {}
        }
    }
    if (-not $officialVersion) {
        $officialVersion = "2.33.0"
    }
    Write-Host "Using fallback official version: $officialVersion" -ForegroundColor Yellow
}

# Ensure Directory.Build.props is synchronized with the resolved official version
if (Test-Path $fullPropsPath) {
    try {
        $propsContent = Get-Content $fullPropsPath -Raw
        if ($propsContent -match '<OfficialVersion Condition="''\$\(OfficialVersion\)'' == ''">([^<]+)</OfficialVersion>') {
            $existingPropsVer = $Matches[1].Trim()
            if ($existingPropsVer -ne $officialVersion) {
                $updatedProps = $propsContent -replace '<OfficialVersion Condition="''\$\(OfficialVersion\)'' == ''">([^<]+)</OfficialVersion>', "<OfficialVersion Condition=""'`$(OfficialVersion)' == ''"">$officialVersion</OfficialVersion>"
                [System.IO.File]::WriteAllText($fullPropsPath, $updatedProps, (New-Object System.Text.UTF8Encoding($false)))
                Write-Host "Synchronized Directory.Build.props OfficialVersion to $officialVersion" -ForegroundColor Cyan
            }
        }
    } catch {}
}

# ---------------------------------------------------------------------------
# 2. Resolve verBuildTime from Git Tags
# ---------------------------------------------------------------------------
$maxTagSeq = 0
try {
    $tags = git tag -l "$officialVersion.*" "v$officialVersion.*" 2>$null
    if ($tags) {
        $escapedVer = [regex]::Escape($officialVersion)
        $tagPattern = "^v?$escapedVer\.(\d+)$"
        foreach ($t in ($tags -split "`r?`n")) {
            $t = $t.Trim()
            if ($t -match $tagPattern) {
                $seqVal = [int]$Matches[1]
                if ($seqVal -gt $maxTagSeq) {
                    $maxTagSeq = $seqVal
                }
            }
        }
    }
} catch {}

# Next build sequence is 1-based after highest existing tag for this official version
$nextSeq = $maxTagSeq + 1

# ---------------------------------------------------------------------------
# 3. Circuit Breaker: verBuildTime > 99
# ---------------------------------------------------------------------------
if ($nextSeq -gt 99) {
    $msg = "Version build sequence overflow: sequence counter ($nextSeq) exceeds maximum 99 for official version $officialVersion! Build blocked. Await next official Antigravity release."
    if ($ForCI -or $env:GITHUB_ACTIONS) {
        Write-Host "::error::$msg"
    }
    Write-Error $msg
    exit 1
}

# ---------------------------------------------------------------------------
# 4. Format Version Strings: verOfficial.verBuildTime
# ---------------------------------------------------------------------------
$revision = "{0:D2}" -f $nextSeq
$version = "$officialVersion.$revision"
$versionCode = [long]("$officialVersion$revision".Replace(".", ""))

$result = [PSCustomObject]@{
    OfficialVersion = $officialVersion
    BuildSeq        = $nextSeq
    Revision        = $revision
    Version         = $version
    VersionCode     = $versionCode
    MaxTagSeq       = $maxTagSeq
}

# ---------------------------------------------------------------------------
# 5. Export to GitHub Actions if in CI
# ---------------------------------------------------------------------------
if ($ForCI -or $env:GITHUB_OUTPUT) {
    if ($env:GITHUB_OUTPUT -and (Test-Path $env:GITHUB_OUTPUT)) {
        Add-Content -Path $env:GITHUB_OUTPUT -Value "official_version=$officialVersion"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "build_time=$nextSeq"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "revision=$revision"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$version"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "version_code=$versionCode"
    }
}

Write-Output $result
