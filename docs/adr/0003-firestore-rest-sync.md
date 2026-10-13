# ADR-0003: Sync through the user's own Firestore, over REST

- Status: Accepted
- Date: 2026-10-12

## Context

The same queue must be on every PC of the owner, for free, without running a server, and without the
owner's data passing through anyone else's account.

## Decision

Each user creates a free Google Firebase project (Spark plan). Queueify talks to Cloud Firestore through
its REST API, with no SDK and no login: a queue lives at `queues/{key}`, where the 40-hex-character random
key is the capability, shared between PCs as the sync code `queueify:{project}:{key}` (or by copying the
folder). Security rules in `tools/firestore.rules` allow only albums and settings under a key, with
limited fields and sizes, and no listing of queues.

Sync is incremental: dirty rows go up in one commit with a server-time transform; changes newer than the
last server time (minus a margin) come down; the newest `updatedAt` wins; deletions are soft until
uploaded.

## Consequences

- Free and private: the data is in the user's own project.
- Setup has a few steps in the Firebase console (the app lists them).
- Anyone who knows the project ID could use up the free quota by creating junk queues; stopping that needs
  Firebase Auth or App Check (documented as a known limit).
- Changing the rules means users must publish them again.

## Alternatives considered

- **A file in OneDrive/Dropbox**: simple, but concurrent edits corrupt or overwrite each other.
- **Firebase with Auth**: stronger, but needs a login flow and an OAuth client.
- **Our own server**: costs money and holds other people's data.
