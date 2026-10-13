# Releasing

Decision: [ADR-0006](adr/0006-release-from-conventional-commits.md).

## What a release is

A tag `vX.Y.Z` on `main`. Pushing it runs `.github/workflows/release.yml`, which tests, builds
`Queueify-X.Y.Z-win-x64.zip` (`tools\package.ps1`) and creates the GitHub release with the notes from
`CHANGELOG.md`. Tags with a suffix (`v1.2.0-rc.1`) become pre-releases.

## Versions

SemVer on what users notice (after a pre-release such as `1.2.0-rc.1`, the next release is `1.2.0`):

- **major**: something users rely on changes or goes away (a data format other PCs can't read, a removed
  setting)
- **minor**: new features
- **patch**: fixes only

The version lives in `<Version>` of `Queueify\Queueify.csproj` and `Queueify.Core\Queueify.Core.csproj`;
the About section shows it.

## Cutting a release

1. `main` is green in CI and the UI suites pass locally against `tools\package.ps1`'s build.
2. `tools\release.ps1 -Preview`: read the changelog as a user would. Reword bad commit subjects before
   releasing.
3. `tools\release.ps1` (or `-Version 1.2.0-rc.1`). It writes the new `CHANGELOG.md` section and the
   version, commits `chore(release): vX.Y.Z` and creates the tag, locally. It never pushes.
4. Check with `git show vX.Y.Z`, then `git push origin main vX.Y.Z`. Only the maintainer can push to `main`
   directly (the branch rule lets admins bypass it), so the release commit and its tag arrive together.
5. The release workflow publishes the zip. Download it once and run it on a clean folder.

If the security rules (`tools/firestore.rules`) changed, say in the release notes that users must copy
and publish them again (Sync → Copy security rules).

## Self-check of the zip

- Contains a single `Queueify` folder with `Queueify.exe`, `Queueify.pri`, the `.xbf` files and `Assets`.
- No `Data` folder: a download starts empty.
- Runs on a PC without the .NET or Windows App SDK runtimes installed (it's self-contained).
