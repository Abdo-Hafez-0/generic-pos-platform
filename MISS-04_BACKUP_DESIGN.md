# MISS-04 Backup Design (draft for approval)

Status: **APPROVED** by the user on 2026-10-10 (all decisions in section 12). Build order in section 11.

Sources: `Generic Offline-First Inventory & POS Platform.md` sections 23 (local backup) and 24 (cloud backup); `Architecture & Solution Design.md` sections 63 (cloud backup architecture), 64 (backup independence) and the "no plain-text secrets in configuration" rule; `PROJECT_STATE.md` Stage 9 decision 7 (backup access) and the Stage 11 deferred-limitations register ("needs a key-management design that must not be improvised").

---

## 1. What already exists

- **BackupServer.Api** (Stage 9, Stage 11 hardening): stores **opaque bytes** per license. The client authenticates with a per-license bearer **backup token**. The vendor issues the token, it is shown once, and the server keeps only its SHA-256. Uploads need an active license with the `cloud-backup` module. Listing, downloading and deleting your own backups keep working while the license is suspended or expired. Limits: 256 MiB per backup and 10 backups per license; the oldest are removed after an upload. All of this is audited and throttled. The server never sees or needs a key.
- **Updater restore points** (Stage 7): `SqliteDataSafeguard` already takes a consistent copy of the live database with the SQLite online backup API, and puts a copy back after deleting the `-wal`/`-shm` side files. Backup reuses the same technique.
- **Client.Security** (Stage 11): `ISecretProtector`, Windows DPAPI for the current user, bound to a purpose. Consumers never invent a cipher of their own.
- **Capabilities** carry a `LicenseRequirement`, and the Updater shows that a non-module client component can declare its own capability (`updates.manage`).
- **Not built:** anything on the desktop that backs up, restores, encrypts, schedules or calls BackupServer.Api.

## 2. What a backup contains, and what it does not

- **Contains:** the single business database `genericpos.db` (Platform and all module tables). That includes customers' personal data, user password hashes, cost prices, sales and the audit log. **Backups are therefore sensitive data.**
- **Does not contain:** the `Licensing` folder (installation identity, stored license, clock mark). Those files are DPAPI-bound to this Windows user on this PC and are worthless anywhere else. After a restore on a new PC, the license is transferred the existing way (the vendor's `release-installation`, then activate again). Business data never depends on the license (rule 10).
- **Does not contain** application files, updates or logs.

## 3. Threats this design addresses

| Threat | Answer |
|---|---|
| A USB stick, network folder or old PC with backups is lost or stolen | **Accepted risk (user decision 2):** local backups are plain SQLite files so any tool can open them. The screen says so next to the destination ("this copy is not encrypted, keep it somewhere safe"). |
| Breach of the vendor's cloud storage | Cloud backups are encrypted on the shop's PC (section 4). The storage servers never hold the shop key or the escrow private key, so a breach of the servers alone exposes nothing. |
| A dishonest insider at the vendor, or a demand made to the vendor | **Accepted with escrow (user decision 1):** whoever holds the escrow private key AND the files can read cloud backups. This is limited by keeping that key offline, off every server, with a written, audited recovery procedure (section 5B). |
| Someone pretends to be the shop to get its data recovered | The vendor procedure checks identity before any recovery, and a recovery opens one named backup only (section 5B). |
| Traffic intercepted | HTTPS (already enforced outside Development). It comes on top of the encryption, not instead of it. |
| A tampered or foreign file is restored | Authenticated encryption (AES-256-GCM). A changed byte, or a file not made with this shop's key, is refused before anything is touched. |
| An old backup is restored to undo sales (rollback) | Restore needs a sensitive capability, the user confirms in plain words, and it is audited in both the old and the restored database. |
| The PC dies or is replaced | The recovery code (section 5) opens the backups on any PC. |
| A backup is silently corrupt | The copy is checked when it is made, and on demand with "Verify backup" (section 7). |

**Not addressed (stated honestly):**
- The live database on disk is **not** encrypted. Anyone who can run code as this Windows user can read it, and the DPAPI-held backup key too. Encrypting the live database (for example with SQLCipher) is a separate, larger change and is not part of MISS-04.
- Because the vendor can recover cloud backups, the vendor's terms and privacy notice must say so. That is a business and legal task, not a code task, and it must be done before the cloud module is sold.

## 4. File formats

**Local backups (user decision 2: not encrypted):** a plain SQLite file `genericpos-<yyyyMMdd-HHmmss>.db`, made with the online backup API and checked with `PRAGMA integrity_check`. Its SHA-256, the application version and each module's schema version go into the backup history (section 9), so Verify and Restore can detect a changed or damaged file. A local file without a history entry (for example on a new PC) can still be restored: it is checked with `integrity_check` and its schema versions are read from the file itself.

**Cloud backups (`.gpbak`, encrypted):**

- Uses only what is built into .NET: `AesGcm`, `HKDF`, `RandomNumberGenerator`, `BrotliStream`. **No new package.**
- Pipeline: **online backup copy -> `PRAGMA integrity_check` on the copy -> compress (Brotli) -> encrypt in 1 MiB chunks -> write `.gpbak` -> delete the plain copy.**
  - Chunked AES-256-GCM ("STREAM" construction). Each chunk is sealed with a nonce made from the chunk counter, and the last chunk carries a "final" flag. Reordered, dropped, truncated or extended files are therefore detected, and a 200 MB database never has to fit in memory.
  - Each backup has its own random 256-bit **data key**. The data key is wrapped (AES-GCM) under the shop's **backup key**. This means changing the backup key never requires re-encrypting old files.
- Header (unencrypted, but authenticated as associated data): magic `GPBK`, format version, **key id**, the data key wrapped under the shop key, the **escrow slot** (section 5B) and creation time. A second, **encrypted** metadata block holds the application version, each module's schema version, the plaintext size and the SHA-256 of the plaintext database.
- The plain temporary copy is written next to the database, in the same protected user folder, and deleted in a `finally`. On startup, temporary files left over from a crash are swept away.

## 5. Key management (cloud backups only)

**User decision 1: recovery code plus vendor escrow = 5A and 5B together.** 5C was rejected.

### 5A. Shop-held recovery code

- **Setup (once, before the first backup):** the PC generates a random **recovery code** of 128 bits, shown as about 26 characters in groups, for example `K7QF-M2XA-...`, with a check character that catches typing mistakes. The **backup key** is derived from the code with HKDF-SHA256. The **key id** is a separate HKDF output; it is not secret and lets a restore say "this backup needs a different recovery code" instead of a vague failure. Because the code is long and random, a slow password hash is not needed.
- **The owner must keep the code:** it is shown once, with "print it" (to the receipt printer or a normal printer) and "I wrote it down". To confirm, the owner types back two groups of it. Backups cannot start until this is done.
- **This PC keeps the key, not the code:** the backup key, and earlier keys (the key ring), are stored DPAPI-protected under `%LOCALAPPDATA%\GenericPOS\Backup\`. Scheduled backups therefore run without anyone typing anything. The code itself is never stored, so it can **never be shown again**. Someone with the backup settings permission cannot read it off the screen and then open a stolen USB stick.
- **Restore on the same PC:** no code is needed (the key ring is here). **Restore on a new or rebuilt PC:** the owner types the recovery code.
- **Rotation ("make a new recovery code"):** used when the code may have been seen, or was lost while the PC still works. A new code and key are made, and a new backup is taken at once. Old backups stay readable on this PC through the key ring, and elsewhere only with the old code. Cloud copies under the old key age out through the 10-backup retention. Rotation is audited (without the code, obviously).
- On its own, 5A would leave a shop that lost both the PC and the code with no way back. 5B closes that gap.

### 5B. Vendor escrow (approved 2026-10-10)

- **Escrow key pair:** the vendor generates a separate **ECDH P-256** key pair for escrow. It is never the ES256 signing key: one key, one purpose. The private key is created and kept **offline**, the same way as the package-signing key: not on any server, not in the repository, not in AdminPortal. Only the **public key** and its id ship with the client. It is trusted the same way as the license and update keys, and moves into the signed binary with PKG-05.
- **Escrow slot:** each `.gpbak` data key is also sealed to the escrow public key, ECIES style: an ephemeral ECDH P-256 key, HKDF-SHA256, then AES-256-GCM. This is built into .NET, so no new package is needed. The slot holds the escrow key id, the ephemeral public key and the sealed data key. It is part of the authenticated header, so it cannot be swapped without the file being refused.
- **Per backup, not per shop:** escrow opens one backup's data key, never the shop key. A recovery therefore gives access to the one backup that was asked for, and the shop's other backups and future backups stay closed.
- **Recovery procedure:**
  1. On the new PC, the owner enters the backup token (the vendor re-issues one if it was lost too) and picks the cloud backup. With no recovery code, the screen offers "Ask the vendor to recover this backup". This produces a short **recovery request**: license id, backup id, key id and the escrow slot. It is text the owner can send; it contains no business data and is useless without the escrow private key.
  2. The vendor **checks who is asking**, following a written procedure (a contact on the customer record, a callback). The check and its outcome are recorded in the AdminPortal audit log.
  3. On an offline machine that holds the escrow private key, a vendor CLI tool (`tools/BackupEscrowRecovery`) opens the escrow slot. It re-seals the data key under a **one-time restore code** made for this request and gives back a **restore key**: a short text or file, valid for that one backup only.
  4. The owner pastes the restore key, the backup is restored (section 8), and the PC asks for a **new recovery code** at once (5A setup). The old shop key is gone with the old PC.
- **Escrow key rotation:** a new escrow key id is used for new backups when the client ships with a new public key. Old private keys are kept offline for as long as backups under them can exist; downloaded copies have no expiry, so this means indefinitely. They are listed in the vendor's key register.
- **What escrow does not change:** the shop still has its own code and normally never needs the vendor. Local backups are plain (user decision 2), so escrow plays no part in them.
- **Vendor duties before selling the cloud module:** escrow key custody (offline machine, at least two people know where it is), the identity-check procedure, and the terms and privacy notice (section 3).

## 6. Module layout (user decision 3: local backup for every shop, cloud as a paid module)

The documents make **local** backup a platform feature (section 23) and **cloud** backup an optional paid module (sections 24, 63, 64). Recommended:

| Project | Role |
|---|---|
| `Client.Backup` (new, in the Client layer like Client.Updater) | Snapshot, local folder destination (plain files), verify, restore staging, scheduler, history. Declares the capabilities. Works with no network and no extra license. |
| `Client.Backup` destination abstraction `IBackupDestination` | `LocalFolderDestination` (a folder, USB drive or network share) is built in. |
| `CloudBackup` optional module (new) | Adds the cloud destination together with everything only the cloud needs: the `.gpbak` format, the recovery code and key ring (through `ISecretProtector`), the escrow slot, `IBackupClient` (Application) and `HttpBackupClient` (Infrastructure, the only HttpClient user, the same pattern as `Client.Licensing.Http`). Needs the license module `cloud-backup`. Removing it changes nothing else (section 64). |
| `tools/BackupEscrowRecovery` (new, vendor only) | Offline CLI that turns a recovery request into a one-time restore key (section 5B). It is never deployed to a server. |

The backup token for the cloud is typed in once by the owner (the vendor gives it with the license) and stored DPAPI-protected, never in `appsettings.json` (user decision 6; no LicenseServer protocol change).

## 7. Capabilities

| Capability | Covers | Sensitive | License |
|---|---|---|---|
| `backup.create` | Make a backup now, verify a backup, see the history | no | none |
| `backup.restore` | Restore a backup | **yes** | none (restoring must work with any license state, rule 10) |
| `backup.delete` | Delete local or cloud backups | **yes** | none |
| `backup.configure` (user decision 4) | Destinations, schedule, retention, recovery-code setup and rotation, cloud token | **yes** | none (cloud destination: `cloud-backup`) |

Reminder from FIX-09: the Administrator role only gets capabilities that existed at first run. New capabilities must be ticked under Roles and permissions on existing installations. That separate task remains open.

## 8. Restore flow (the database is in use)

The running app holds the database open, so a restore cannot happen in place. It happens across a restart:

1. **Choose:** the user picks a backup from the history (or a `.gpbak` file / cloud entry). The screen shows its date, the application version and the PC it came from.
2. **Prepare (app still running, nothing changed yet):** copy a local file into a staging file, or for a cloud backup download it, check GCM authentication, decrypt and decompress it. In both cases compare the SHA-256 (when the history has one) and run `PRAGMA integrity_check`, then compare schema versions. A backup from a **newer** application version is refused ("update this PC first"). A backup from an older version is fine, because the module migrations bring it up to date at the next start.
3. **Confirm:** a plain sentence such as "Everything entered after 14:05 on 3 October will be replaced. A copy of the current data is kept." The user confirms, and the request is audited in the current database.
4. **Restart:** a `restore-pending` marker is written and the app closes itself.
5. **Swap (next start, before any hosted service opens the database):** move the current database aside as `before-restore-<timestamp>.db` (it is **never deleted automatically**; it is listed in the history so a wrong restore can be undone), delete `-wal`/`-shm`, move the staged file in, and remove the marker. If anything fails, put the original back and say so in plain words. This step runs at the same early point where the corrupt-database check of Stage 12 runs.
6. **After:** migrations run as usual, the restore is audited in the restored database, and sign-in uses the **users and passwords contained in the backup**. This is said on the confirmation screen.

## 9. Scheduling, retention and offline

- **In-app scheduler** (a hosted service, no Windows Task Scheduler and no service account). It runs a daily backup at a set time (default 23:00) while the app is running. If the last good backup is older than the interval, it catches up at the next start. No backup is triggered by closing a drawer shift (user decision 7).
- A scheduled backup never interrupts a sale: it uses the online backup API and runs in the background. If it fails, the shell shows a plain notice and keeps working.
- **Cloud while offline:** the encrypted file is written locally first (an outbox folder) and uploaded when the server can be reached, with retry. A cloud failure never fails the local backup.
- **Retention:** local destination keeps the last N (default 14); cloud uses the server's per-license count (10). `before-restore` copies do not count and are never removed automatically.
- **History:** stored in the Backup folder as a small JSON index next to the key ring, **not** in the business database, so restoring a backup cannot erase the record of backups. It records each backup's time, destination, size, key id, the result of the last verify, and its outcome.

## 10. Screens

Administration > **Backup**: status ("last good backup: today 23:00, USB drive E:"), Back up now, Verify, history list with Restore and Delete, settings (destinations, schedule, retention), recovery code setup and rotation, and for the cloud module, the cloud token and cloud list. All strings are in `.resx` (English and Arabic) and the layout is right-to-left safe.

## 11. Proposed build order (each item built, tested and with a real-exe run before the next)

- **MISS-04a** `Client.Backup` core: snapshot, local folder destination (plain files), verify, capabilities, history.
- **MISS-04b** Restore across restart, with the `before-restore` copy.
- **MISS-04c** Scheduler, retention, shell notice.
- **MISS-04d** Backup screen (local).
- **MISS-04e** `CloudBackup` optional module: `.gpbak` format, recovery code and key ring, escrow slot, `IBackupClient`/`HttpBackupClient`, token, outbox, cloud part of the screen. End to end against a real BackupServer.Api.
- **MISS-04f** `tools/BackupEscrowRecovery` and restore with a one-time restore key. End to end: back up, lose the PC and the code, recover through the vendor tool.

## 12. Decisions

Made by the user on 2026-10-10:

1. **Key management:** recovery code **plus vendor escrow** (5A + 5B).
2. **Encryption:** **cloud backups only**; local backups are plain SQLite files (the lost-USB risk is accepted and shown on screen).
3. **Module layout:** local backup in `Client.Backup` for every shop, plus a paid `CloudBackup` module.
4. **Configuration permission:** a separate sensitive `backup.configure`.

5. **Escrow mechanism** of section 5B approved as written.
6. **Cloud token:** typed in by the owner.
7. **No backup trigger** on closing the last shift of the day.
