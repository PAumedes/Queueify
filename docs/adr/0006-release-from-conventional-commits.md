# ADR-0006: Releases and changelog from Conventional Commits

- Status: Accepted
- Date: 2026-10-13

## Context

Each release needs a version, a changelog and a downloadable exe. Hand-written changelogs drift and
conflict between branches. Commits already follow Conventional Commits, enforced by the `commit-msg` hook.
The approach comes from [incus-monitor](https://github.com/PAumedes/incus-monitor).

## Decision

`tools\release.ps1` collects every non-merge commit since the previous tag, groups them by type
(breaking changes first; chores, style changes and reverts under Other), writes a new section in `CHANGELOG.md`, sets the version in both projects,
commits `chore(release): vX.Y.Z` and tags it locally. The version bump comes from the commits (breaking →
major, `feat` → minor, anything else → patch) unless given. The maintainer reviews and pushes; the tag runs
the release workflow, which builds the exe and publishes the GitHub release with the notes taken from
`CHANGELOG.md`.

## Consequences

- One source of truth for release notes; no changelog merge conflicts.
- Commit subjects end up in front of users, so they're written for them.
- The script is small PowerShell with its own tests (`tests\tools\Release.Tests.ps1`).

## Alternatives considered

- **semantic-release or git-cliff**: capable, but an extra toolchain for little logic.
- **Changelog fragment files**: no conflicts, but a manual step per change.
