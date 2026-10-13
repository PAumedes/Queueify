# Tests for tools\release.ps1 (version bump, changelog and release notes from Conventional Commits).
#
#   powershell -ExecutionPolicy Bypass -File tests\tools\Release.Tests.ps1
$script:failures = 0
function Check([bool]$ok, [string]$what) {
    if (-not $ok) { $script:failures++ }
    '{0}  {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what
}

. (Join-Path $PSScriptRoot '..\..\tools\release.ps1')

# ── Next version ──
Check ((Get-NextVersion '1.0.0' @('fix(ui): a')) -eq '1.0.1') 'a fix bumps the patch'
Check ((Get-NextVersion '1.0.3' @('fix: a', 'feat(ocr): b')) -eq '1.1.0') 'a feature bumps the minor and resets the patch'
Check ((Get-NextVersion '1.4.2' @('feat(sync)!: new format')) -eq '2.0.0') 'a breaking change bumps the major'
Check ((Get-NextVersion '1.4.2' @('fix: a') 'BREAKING CHANGE: b') -eq '2.0.0') 'a BREAKING CHANGE footer bumps the major'
Check ((Get-NextVersion '1.0.0' @('docs: a', 'test: b')) -eq '1.0.1') 'anything else is a patch'

# ── Changelog section ──
$commits = @(
    [pscustomobject]@{ Hash = 'aaaaaaa'; Subject = 'feat(settings): turn Settings into a page'; Body = '' }
    [pscustomobject]@{ Hash = 'bbbbbbb'; Subject = 'fix(ui): leave a rail where the panel was'; Body = '' }
    [pscustomobject]@{ Hash = 'ccccccc'; Subject = 'docs: explain sync'; Body = '' }
    [pscustomobject]@{ Hash = 'ddddddd'; Subject = 'ci: build on windows'; Body = '' }
    [pscustomobject]@{ Hash = 'eeeeeee'; Subject = 'feat(sync)!: store albums differently'; Body = '' }
    [pscustomobject]@{ Hash = 'fffffff'; Subject = 'test: cover the token cache'; Body = '' }
    [pscustomobject]@{ Hash = '1111111'; Subject = "Merge branch 'fix/x'"; Body = '' }
)
$section = Get-ChangelogSection -Version '1.1.0' -Date '2026-10-13' -Commits $commits
Check ($section -match '(?m)^## \[1\.1\.0\] - 2026-10-13$') 'section starts with the version and date'
Check ($section -match '### Breaking changes[\s\S]*\*\*sync:\*\* store albums differently \(eeeeeee\)') 'breaking changes come first'
Check ($section.IndexOf('### Breaking') -lt $section.IndexOf('### Features')) 'breaking changes before features'
Check ($section -match '### Features[\s\S]*- \*\*settings:\*\* turn Settings into a page \(aaaaaaa\)') 'features listed with scope and hash'
Check ($section -match '### Fixes[\s\S]*- \*\*ui:\*\* leave a rail where the panel was \(bbbbbbb\)') 'fixes listed'
Check ($section -match '### Documentation[\s\S]*- explain sync \(ccccccc\)') 'docs without a scope'
Check ($section -match '### Build and CI[\s\S]*- build on windows \(ddddddd\)') 'build and CI grouped'
Check ($section -notmatch 'Merge branch') 'merge commits are left out'
Check ($section -match '### Tests[\s\S]*cover the token cache') 'tests have their own group'

# ── Release notes come back out of the changelog ──
$changelog = "# Changelog`n`nIntro.`n`n$section`n## [1.0.0] - 2026-10-12`n`n### Features`n`n- first`n"
$notes = Get-ReleaseNotes -Changelog $changelog -Version '1.1.0'
Check ($notes -match 'turn Settings into a page' -and $notes -notmatch 'first' -and $notes -notmatch '## \[1\.1\.0\]') 'notes are the version''s section, without its heading'

# ── Inserting into the changelog and the project files ──
$updated = Add-ChangelogSection -Changelog $changelog -Section "## [1.2.0] - 2026-11-01`n`n### Fixes`n`n- x`n"
Check ($updated.IndexOf('## [1.2.0]') -lt $updated.IndexOf('## [1.1.0]') -and $updated.StartsWith("# Changelog`n`nIntro.")) 'a new section goes above the previous one, below the intro'
$csproj = "<Project>`n  <PropertyGroup>`n    <Version>1.0.0</Version>`n  </PropertyGroup>`n</Project>"
Check ((Set-ProjectVersion -Project $csproj -Version '1.1.0') -match '<Version>1\.1\.0</Version>') 'the project version is replaced'

"`n$script:failures failure(s)"
exit $script:failures
