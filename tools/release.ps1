<#
.SYNOPSIS
    Prepares a release from the Conventional Commits since the last tag (see docs\RELEASING.md).

.EXAMPLE
    tools\release.ps1 -Preview           # what the next release would say; changes nothing
    tools\release.ps1                    # bump, write CHANGELOG.md, commit and tag locally
    tools\release.ps1 -Version 1.2.0-rc.1
    tools\release.ps1 -Notes v1.1.0      # the release notes for a tag (used by the release workflow)

The script never pushes. Review with `git show`, then `git push origin main vX.Y.Z`.
#>
param(
    [switch]$Preview,
    [string]$Version,
    [string]$Notes
)

$Projects = 'Queueify\Queueify.csproj', 'Queueify.Core\Queueify.Core.csproj'
$Groups = [ordered]@{
    feat = 'Features'
    fix = 'Fixes'
    perf = 'Performance'
    docs = 'Documentation'
    test = 'Tests'
    refactor = 'Refactoring'
    build = 'Build and CI'
    ci = 'Build and CI'
    chore = 'Other'
    style = 'Other'
    revert = 'Other'
}
$SubjectPattern = '^(?<type>[a-z]+)(\((?<scope>[a-z0-9-]+)\))?(?<breaking>!)?: (?<text>.+)$'

# Footers as Conventional Commits writes them, case-sensitive: "Breaking change: none" in prose isn't one.
$BreakingFooter = '(?m)^BREAKING[ -]CHANGE:'

function Test-Breaking([string]$subject, [string]$body) {
    ($subject -match $SubjectPattern -and $Matches.breaking) -or $body -cmatch $BreakingFooter
}

<#
    Next SemVer from the commits: breaking → major, feat → minor, anything else → patch. After a
    pre-release (1.2.0-rc.1) the next version is that release itself (1.2.0).
#>
function Get-NextVersion([string]$current, [string[]]$subjects, [string]$body = '') {
    if ($current -match '^(\d+\.\d+\.\d+)-') { return $Matches[1] }
    $parts = $current.Split('.') | ForEach-Object { [int]$_ }
    if ($subjects | Where-Object { Test-Breaking $_ '' }) { return "$($parts[0] + 1).0.0" }
    if ($body -cmatch $BreakingFooter) { return "$($parts[0] + 1).0.0" }
    if ($subjects | Where-Object { $_ -match '^feat(\(|!|:)' }) { return "$($parts[0]).$($parts[1] + 1).0" }
    "$($parts[0]).$($parts[1]).$($parts[2] + 1)"
}

<# One CHANGELOG section: breaking changes first, then one group per commit type. #>
function Get-ChangelogSection([string]$Version, [string]$Date, $Commits) {
    $lines = @("## [$Version] - $Date", '')
    $entries = foreach ($c in $Commits) {
        if ($c.Subject -like 'Merge *') { continue }
        if ($c.Subject -notmatch $SubjectPattern) {
            # git revert's "Revert "…"" and anything else that slipped past the hook: listed as is.
            [pscustomobject]@{ Group = 'Other'; Line = "- $($c.Subject) ($($c.Hash))" }
            continue
        }
        $scope = if ($Matches.scope) { "**$($Matches.scope):** " } else { '' }
        $group = if (Test-Breaking $c.Subject $c.Body) { 'Breaking changes' } elseif ($Groups[$Matches.type]) { $Groups[$Matches.type] } else { 'Other' }
        [pscustomobject]@{ Group = $group; Line = "- $scope$($Matches.text) ($($c.Hash))" }
    }
    foreach ($group in @('Breaking changes') + @($Groups.Values | Select-Object -Unique)) {
        $inGroup = @($entries | Where-Object { $_.Group -eq $group })
        if ($inGroup.Count -eq 0) { continue }
        $lines += "### $group", ''
        $lines += $inGroup.Line
        $lines += ''
    }
    ($lines -join "`n") + "`n"
}

<# The new section goes above the newest released one, below the file's introduction. #>
function Add-ChangelogSection([string]$Changelog, [string]$Section) {
    $at = $Changelog.IndexOf("`n## [")
    if ($at -lt 0) { return $Changelog.TrimEnd() + "`n`n" + $Section }
    $Changelog.Substring(0, $at + 1) + $Section + "`n" + $Changelog.Substring($at + 1)
}

<# A version's section without its heading: the GitHub release notes. #>
function Get-ReleaseNotes([string]$Changelog, [string]$Version) {
    $Version = $Version.TrimStart('v')
    $m = [regex]::Match($Changelog, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(?<body>.*?)(?=^## \[|\z)")
    if (-not $m.Success) { throw "No section for $Version in CHANGELOG.md" }
    $m.Groups['body'].Value.Trim() + "`n"
}

function Set-ProjectVersion([string]$Project, [string]$Version) {
    [regex]::Replace($Project, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
}

<# A version given by hand: X.Y.Z or X.Y.Z-suffix, without the v (the tag adds it). #>
function Test-ReleaseVersion([string]$Version) { $Version -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' }

<# The newest release tag: by version number, and a final release beats its own pre-releases. #>
function Select-LastTag([string[]]$Tags) {
    $parsed = foreach ($tag in $Tags) {
        if ($tag -notmatch '^v(\d+)\.(\d+)\.(\d+)(-(.+))?$') { continue }
        $key = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
        $pre = $Matches[5]
        $preNumber = if ($pre -match '(\d+)$') { [int]$Matches[1] } else { 0 }
        [pscustomobject]@{
            Tag = $tag
            Key = $key
            Final = [int](-not $pre)
            Pre = $preNumber
        }
    }
    ($parsed | Sort-Object Key, Final, Pre | Select-Object -Last 1).Tag
}

<# git's output is UTF-8; an old console code page (850, 437) would garble accents in subjects. #>
function Invoke-Git {
    $before = [Console]::OutputEncoding
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
    try {
        $out = & git -c i18n.logOutputEncoding=UTF-8 @args
        if ($LASTEXITCODE -ne 0) { throw "git $args failed" }
        $out
    }
    finally {
        [Console]::OutputEncoding = $before
    }
}

function Get-CommitsSinceLastTag {
    $last = Select-LastTag @(Invoke-Git tag --list 'v*' --merged HEAD)
    $range = if ($last) { "$last..HEAD" } else { 'HEAD' }
    $raw = Invoke-Git log $range --no-merges --format='%h%x1f%s%x1f%b%x1e'
    foreach ($record in (($raw -join "`n") -split [char]0x1e)) {
        $f = $record.Trim("`n").Split([char]0x1f)
        if ($f.Count -ge 2 -and $f[0]) { [pscustomobject]@{ Hash = $f[0]; Subject = $f[1]; Body = $f[2] } }
    }
}

# ── Run, unless dot-sourced by the tests ──
if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$utf8 = New-Object Text.UTF8Encoding $false

if ($Notes) {
    Get-ReleaseNotes -Changelog ([IO.File]::ReadAllText('CHANGELOG.md')) -Version $Notes
    return
}

$commits = @(Get-CommitsSinceLastTag)
if ($commits.Count -eq 0) { throw 'Nothing to release: no commits since the last tag.' }
$current = ([regex]::Match([IO.File]::ReadAllText($Projects[0]), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $Version) { $Version = Get-NextVersion $current $commits.Subject ($commits.Body -join "`n") }
if (-not (Test-ReleaseVersion $Version)) { throw "Not a version: $Version. Use X.Y.Z or X.Y.Z-rc.N, without the v." }
$section = Get-ChangelogSection -Version $Version -Date (Get-Date -Format 'yyyy-MM-dd') -Commits $commits

if ($Preview) {
    "Current version: $current  →  next: $Version`n"
    $section
    return
}

if (Invoke-Git status --porcelain) { throw 'The working tree has changes. Commit or stash them first.' }
if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Releases are made from main.' }
if (Invoke-Git tag --list "v$Version") { throw "Tag v$Version already exists." }

[IO.File]::WriteAllText('CHANGELOG.md', (Add-ChangelogSection -Changelog ([IO.File]::ReadAllText('CHANGELOG.md')) -Section $section), $utf8)
foreach ($p in $Projects) { [IO.File]::WriteAllText($p, (Set-ProjectVersion -Project ([IO.File]::ReadAllText($p)) -Version $Version), $utf8) }
Invoke-Git add CHANGELOG.md @Projects | Out-Null
Invoke-Git commit -q -m "chore(release): v$Version" | Out-Null
Invoke-Git tag -a "v$Version" -m "Queueify $Version" | Out-Null
"Tagged v$Version. Check it with: git show v$Version"
"Then publish it with:  git push origin main v$Version"
