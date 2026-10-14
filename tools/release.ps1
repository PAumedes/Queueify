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

$Projects = 'Queueify\Queueify.csproj', 'Queueify.Core\Queueify.Core.csproj', 'Queueify.Launcher\Queueify.Launcher.csproj'
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
        # Merges, and the release commits of earlier pre-releases, aren't changes.
        if ($c.Subject -like 'Merge *' -or $c.Subject -match '^chore\(release\): v') { continue }
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
    ($lines -join "`n").TrimEnd() + "`n"
}

<# The new section goes above the newest released one, below the file's introduction. #>
function Add-ChangelogSection([string]$Changelog, [string]$Section) {
    $at = $Changelog.IndexOf("`n## [")
    if ($at -lt 0) { return $Changelog.TrimEnd() + "`n`n" + $Section }
    $Changelog.Substring(0, $at + 1) + $Section.TrimEnd() + "`n`n" + $Changelog.Substring($at + 1)
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

<#
    The newest release tag: by version number, and a final release beats its own pre-releases.
    -FinalOnly skips pre-releases: a final release's notes cover everything since the last final one.
    Pre-releases are expected as -rc.N and compare by N.
#>
function Select-LastTag([string[]]$Tags, [switch]$FinalOnly) {
    $parsed = foreach ($tag in $Tags) {
        if ($tag -notmatch '^v(\d+)\.(\d+)\.(\d+)(-(.+))?$') { continue }
        if ($FinalOnly -and $Matches[5]) { continue }
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

<#
    One argument quoted for a Windows command line (the rules programs like git use to split it):
    backslashes are literal unless they come before a quote, where they must be doubled.
#>
function ConvertTo-CommandLineArgument([string]$Value) {
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

<#
    Runs git and reads its output as UTF-8 directly, not through the console: an old console code page
    (850, 437) or the way PowerShell decodes native output would garble accents in commit subjects.
#>
function Invoke-Git {
    $utf8 = New-Object Text.UTF8Encoding $false
    $start = New-Object System.Diagnostics.ProcessStartInfo 'git'
    $start.Arguments = (@('-c', 'i18n.logOutputEncoding=UTF-8') + $args | ForEach-Object { ConvertTo-CommandLineArgument ([string]$_) }) -join ' '
    $start.WorkingDirectory = (Get-Location).ProviderPath   # PowerShell's location, not the process's
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "git $args failed: $($errors.Result.Trim())" }
    }
    finally {
        $process.Dispose()
    }
    if ($output.Length -gt 0) { $output.TrimEnd("`n").Split("`n") }
}

function Get-CommitsSinceLastTag([switch]$FinalOnly) {
    $last = Select-LastTag @(Invoke-Git tag --list 'v*' --merged HEAD) -FinalOnly:$FinalOnly
    $range = if ($last) { "$last..HEAD" } else { 'HEAD' }
    $raw = Invoke-Git log $range --no-merges --format='%h%x1f%s%x1f%b%x1e'
    foreach ($record in (($raw -join "`n") -split [char]0x1e)) {
        $f = $record.Trim("`n").Split([char]0x1f)
        if ($f.Count -ge 2 -and $f[0]) { [pscustomobject]@{ Hash = $f[0]; Subject = $f[1]; Body = $f[2] } }
    }
}

function Test-Feature($Commit) { $Commit.Subject -match '^feat(\(|!|:)' -or (Test-Breaking $Commit.Subject $Commit.Body) }

<#
    What to release: the version, the commits its changelog lists, and a note for the maintainer.
    - The version is -Version, or the final version of a pre-release (1.2.0-rc.1 → 1.2.0, even with
      nothing new: promoting an rc), or the bump the commits since the last tag call for.
    - A final release lists everything since the last final release; a pre-release, since the last tag.
    - The note says when features came after the pre-release being finalized.
#>
function Get-ReleasePlan([string]$Current, [string]$Version) {
    $sinceLastTag = @(Get-CommitsSinceLastTag)
    $given = [bool]$Version
    if (-not $Version) {
        if ($Current -match '^(\d+\.\d+\.\d+)-') { $Version = $Matches[1] }
        elseif ($sinceLastTag.Count -gt 0) { $Version = Get-NextVersion $Current $sinceLastTag.Subject ($sinceLastTag.Body -join "`n") }
        else { throw 'Nothing to release: no commits since the last tag.' }
    }
    if (-not (Test-ReleaseVersion $Version)) { throw "Not a version: $Version. Use X.Y.Z or X.Y.Z-rc.N, without the v." }

    $commits = if ($Version -match '-') { $sinceLastTag } else { @(Get-CommitsSinceLastTag -FinalOnly) }
    if (@($commits).Count -eq 0) { throw 'Nothing to release: no commits since the last release.' }

    $note = $null
    if (-not $given -and $Current -match '-' -and ($sinceLastTag | Where-Object { Test-Feature $_ })) {
        $note = "Features or breaking changes came after $Current; the release is still $Version unless you pass -Version."
    }
    [pscustomobject]@{ Version = $Version; Commits = @($commits); Note = $note }
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

$current = ([regex]::Match([IO.File]::ReadAllText($Projects[0]), '<Version>([^<]+)</Version>')).Groups[1].Value
$plan = Get-ReleasePlan -Current $current -Version $Version
$Version = $plan.Version
$section = Get-ChangelogSection -Version $Version -Date (Get-Date -Format 'yyyy-MM-dd') -Commits $plan.Commits

if ($Preview) {
    "Current version: $current  →  next: $Version`n"
    if ($plan.Note) { "Note: $($plan.Note)`n" }
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