# Contributing

Thanks for helping. Queueify aims for a small, correct and boring codebase. Read
[docs/ENGINEERING.md](docs/ENGINEERING.md) before your first pull request.

## Setup

- Windows 10 2004+ or Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins the feature band)
- For `tools\package.ps1`: Visual Studio's C++ build tools (the *Desktop development with C++* workload, or
  Build Tools with `Microsoft.VisualStudio.Workload.VCTools`): the app is compiled ahead of time to native code
- Git, with the repository's hooks: `git config core.hooksPath .githooks`
- Optional, for the sync tests: Java 21+ (the Firestore emulator jar is downloaded on first use)

## Everyday commands

| Command | What it does |
| --- | --- |
| `dotnet test --project Queueify.Tests` | Unit tests (the TDD loop) |
| `tools\firestore-emulator.ps1 -Background` then `$env:QUEUEIFY_FIRESTORE_EMULATOR = '127.0.0.1:8080'` | Runs the sync tests too instead of skipping them |
| `tools\package.ps1` | Builds the portable app (about 5 minutes): one `dist\Queueify\Queueify.exe`, the starter with the app appended, plus a versioned copy for the release |
| `tests\ui\Queueify.UiTests.ps1 -Exe dist\Queueify\Queueify.exe` | UI tests through Windows UI Automation |
| `tests\ui\Queueify.SyncUiTests.ps1 -Exe dist\Queueify\Queueify.exe` | Sync UI test (needs the emulator) |
| `tests\tools\Release.Tests.ps1` | Tests for the release tool |
| `tools\release.ps1 -Preview` | What the next release's changelog will say |

Run the PowerShell scripts with `powershell -ExecutionPolicy Bypass -File <script>` if your execution
policy blocks them. The UI tests move the `Data` folder next to the exe aside and put it back afterwards,
and give the app a scratch `%LOCALAPPDATA%`, so they never touch your real queue. Details: [docs/TESTING.md](docs/TESTING.md).

## Workflow

1. Open or pick an issue.
2. Branch from `main`: `feat/short-name`, `fix/short-name`, `docs/…`, `chore/…`.
3. Work test-first ([docs/TESTING.md](docs/TESTING.md#tdd-cycle)): a failing test, then the code.
4. Keep pull requests small: one change, ideally under 400 changed lines.
5. Fill in the pull request template. CI must be green.
6. Refactor in separate `refactor:` commits, with no behaviour change.

`main` is protected on GitHub: changes land through pull requests. The one exception is the release commit,
which the maintainer pushes together with its tag ([docs/RELEASING.md](docs/RELEASING.md)).

## Commits

[Conventional Commits](https://www.conventionalcommits.org/), enforced by `.githooks/commit-msg`.
Subjects become the changelog at release time, so write them for users.

```text
feat(settings): turn Settings into a page with a card per service
fix(ocr): keep the offer to install after declining
docs(adr): record the choice of Firestore
```

Scopes: `ui`, `settings`, `queue`, `search`, `ocr`, `sync`, `i18n`, `core`, `build`, `ci`, `docs`, `adr`.

## Translations

Every user-visible text lives in `Queueify.Core\Services\Strings.En.cs` and `Strings.Es.cs`, used as
`Loc.T("key")` in code and `{l:Tr Key=key}` in XAML. Tests fail if a key is missing in either language,
defined twice, or has different placeholders. The Spanish is Rioplatense (vos).

## Reviews

Every pull request gets a review. Changes are also reviewed by adversarial agent reviewers
([docs/AGENT_WORKFLOW.md](docs/AGENT_WORKFLOW.md)); their findings are treated like any other review
comment: verified, then fixed or answered.

## Using AI assistants

Allowed, under the same bar as any other code: you must understand and be able to defend every line you
submit. No prompt-like comments, no invented APIs, no dead code.
