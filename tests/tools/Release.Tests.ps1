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

# ── Pre-releases ──
Check ((Get-NextVersion '1.2.0-rc.1' @('fix: a')) -eq '1.2.0') 'after a pre-release, the next version is the final one'
Check ((Get-NextVersion '1.2.0-rc.1' @('feat: a')) -eq '1.2.0') 'even with features: rc.1 of 1.2.0 leads to 1.2.0'
Check ((Select-LastTag @('v1.0.0', 'v3.0.0-rc.1', 'v3.0.0', 'v2.5.1')) -eq 'v3.0.0') 'the final tag beats its own pre-release'
Check ((Select-LastTag @('v1.0.0', 'v1.1.0-rc.2', 'v1.1.0-rc.10')) -eq 'v1.1.0-rc.10') 'pre-releases compare by number'
Check ($null -eq (Select-LastTag @())) 'no tags yet'

# ── Breaking-change footers, exactly as the spec writes them ──
Check ((Get-NextVersion '1.0.0' @('fix: a') 'BREAKING-CHANGE: y') -eq '2.0.0') 'BREAKING-CHANGE: (with a hyphen) bumps the major'
Check ((Get-NextVersion '1.0.0' @('fix: a') 'Breaking change: none') -eq '1.0.1') 'lowercase "Breaking change:" in prose does not'

# ── Commits that aren't features or fixes still show up ──
$others = @(
    [pscustomobject]@{ Hash = 'aaaaaaa'; Subject = 'chore: tidy the project files'; Body = '' }
    [pscustomobject]@{ Hash = 'bbbbbbb'; Subject = 'Revert "feat: something"'; Body = '' }
)
$otherSection = Get-ChangelogSection -Version '1.0.1' -Date '2026-10-13' -Commits $others
Check ($otherSection -match '### Other[\s\S]*tidy the project files \(aaaaaaa\)' -and $otherSection -match 'Revert "feat: something" \(bbbbbbb\)') 'chores and reverts are listed under Other'

# ── Versions given by hand ──
Check (Test-ReleaseVersion '1.2.0') 'a plain version is accepted'
Check (Test-ReleaseVersion '1.2.0-rc.1') 'a pre-release is accepted'
Check (-not (Test-ReleaseVersion 'v1.2.0')) 'a leading v is refused (the tag adds it)'
Check (-not (Test-ReleaseVersion '1.2')) 'two numbers are refused'

# ── Reading commits from git, with accents, from a console in an old code page ──
$repo = Join-Path ([IO.Path]::GetTempPath()) ("queueify-release-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $repo | Out-Null
$before = [Console]::OutputEncoding
Push-Location $repo
try {
    git init -q -b main; git config user.email t@example.com; git config user.name Test
    git commit -q --allow-empty -m 'chore: start'; git tag v1.0.0
    git commit -q --allow-empty -m "fix(i18n): show año … right" -m 'Body line one'
    git commit -q --allow-empty -m 'feat: two'
    [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(850)
    $read = @(Get-CommitsSinceLastTag)
    Check ($read.Count -eq 2) "reads only the commits since the last tag ($($read.Count))"
    Check (($read.Subject -contains 'fix(i18n): show año … right')) "accents survive an OEM console: $($read.Subject -join ' | ')"
    Check (($read | Where-Object Subject -like 'fix*').Body -match 'Body line one') 'bodies are read too'
}
finally {
    [Console]::OutputEncoding = $before
    Pop-Location
    Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
}

"`n$script:failures failure(s)"
exit $script:failures
