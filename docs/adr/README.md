# Architecture Decision Records

One decision per file, numbered, never deleted. To change a decision, add a new ADR that supersedes the
old one and mark the old one `Superseded by ADR-NNNN`. Start from [`0000-template.md`](0000-template.md).

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-portable-winui3-app.md) | A portable, unpackaged WinUI 3 app | Accepted |
| [0002](0002-core-library-and-tests.md) | Logic in a UI-free core library, tested first | Accepted |
| [0003](0003-firestore-rest-sync.md) | Sync through the user's own Firestore, over REST | Accepted |
| [0004](0004-windows-ocr.md) | Windows' built-in OCR for magazine photos | Accepted |
| [0005](0005-own-string-tables.md) | Our own string tables for English and Spanish | Accepted |
| [0006](0006-release-from-conventional-commits.md) | Releases and changelog from Conventional Commits | Accepted |
| [0007](0007-single-trimmed-exe.md) | One trimmed exe as the download | Packaging superseded by 0008 |
| [0008](0008-native-aot-and-a-starter-exe.md) | Native AOT and a starter exe | Accepted |
