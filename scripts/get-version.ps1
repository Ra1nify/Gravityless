<#
.SYNOPSIS
    Calculates Gravityless release version: %Version_Official%.%Version.commitTime%

.DESCRIPTION
    - Version_Official: read from Directory.Build.props (<OfficialVersion>)
    - commitTime: 2-digit commit counter (01-99) since OfficialVersion was introduced or changed.
    - When OfficialVersion changes, commitTime resets to 01.
    - If commitTime > 99, throws an error and exits with code 1 (overflow circuit breaker).
#>

[CmdletBinding()]
param(
    [string]$PropsPath = "Directory.Build.props",
    [switch]$ForCI
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }
$fullPropsPath = if ([System.IO.Path]::IsPathRooted($PropsPath)) { $PropsPath } else { Join-Path $root $PropsPath }

if (-not (Test-Path $fullPropsPath)) {
    throw "Property file not found: $fullPropsPath"
}

# 1. Read OfficialVersion from Directory.Build.props
[xml]$propsXml = Get-Content $fullPropsPath -Raw
$officialVersion = $propsXml.Project.PropertyGroup.OfficialVersion
if ($officialVersion -is [System.Xml.XmlElement]) {
    $officialVersion = $officialVersion.InnerText.Trim()
} elseif ($officialVersion) {
    $officialVersion = "$officialVersion".Trim()
}

if (-not $officialVersion) {
    # Fallback to 3-part prefix of Version tag
    $vNode = $propsXml.Project.PropertyGroup.Version
    $vText = if ($vNode -is [System.Xml.XmlElement]) { $vNode.InnerText.Trim() } else { "$vNode".Trim() }
    $parts = $vText.Split('.')
    if ($parts.Length -ge 3) {
        $officialVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
    } else {
        $officialVersion = "2.32.2"
    }
}

# 2. Find commit where OfficialVersion was first introduced in Directory.Build.props
$baseCommit = $null
try {
    # Match the exact string of the current official version in Directory.Build.props history
    $commits = git log -S "$officialVersion" --reverse --pretty=format:"%H" $fullPropsPath 2>$null
    if ($commits) {
        $baseCommit = ($commits -split "`r?`n")[0].Trim()
    }
} catch {}

if (-not $baseCommit) {
    # Fallback to the commit that last touched Directory.Build.props
    try {
        $baseCommit = (git log -1 --pretty=format:"%H" $fullPropsPath 2>$null).Trim()
    } catch {}
}

# 3. Calculate commit count from baseCommit to HEAD
$count = 0
if ($baseCommit) {
    try {
        $revCount = (git rev-list --count "$baseCommit..HEAD" 2>$null).Trim()
        if ($revCount) {
            $count = [int]$revCount
        }
    } catch {}
}

# 1-based commitTime: baseCommit itself is 1 (01)
$commitTime = $count + 1

# 4. Overflow check: if commitTime > 99, abort packaging
if ($commitTime -gt 99) {
    $msg = "Version commitTime overflow: commit counter ($commitTime) exceeds 99 for official version $officialVersion! Build blocked. Please bump official version or resolve commit backlog."
    if ($ForCI -or $env:GITHUB_ACTIONS) {
        Write-Host "::error::$msg"
    }
    Write-Error $msg
    exit 1
}

# 5. Format 2-digit zero-padded revision and full version
$revision = "{0:D2}" -f $commitTime
$version = "$officialVersion.$revision"
$versionCode = [long]("$officialVersion$revision".Replace(".", ""))

$result = [PSCustomObject]@{
    OfficialVersion = $officialVersion
    CommitTime      = $commitTime
    Revision        = $revision
    Version         = $version
    VersionCode     = $versionCode
}

# 6. If in GitHub Actions, set output variables
if ($ForCI -or $env:GITHUB_OUTPUT) {
    if ($env:GITHUB_OUTPUT -and (Test-Path $env:GITHUB_OUTPUT)) {
        Add-Content -Path $env:GITHUB_OUTPUT -Value "official_version=$officialVersion"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "commit_time=$commitTime"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "revision=$revision"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$version"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "version_code=$versionCode"
    }
}

Write-Output $result
