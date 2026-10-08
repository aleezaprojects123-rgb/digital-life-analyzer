# Decision: encryption of the local database

Status: **ACCEPTED by Aleeza on 2026-10-09: Option 1, SQLCipher with a Windows-protected key, adopted only if a Phase 1 benchmark stays within the CPU and memory budget. Not implemented yet.** Decide before Phase 1 recording ships, so no real user data ever has to be migrated. If the benchmark fails, the fallback below applies and this note is updated.
Owner: Aleeza (agent). Spec: "Secure: TLS in transit, encryption at rest, signed installer" (quality requirements); raw events "local device only".

## What we are protecting, and from whom

The local file `%LOCALAPPDATA%\DLA\dla.db` holds raw events (app, site, title, times) for up to 30 days. It is never uploaded.

| Threat | Example |
|---|---|
| A. Lost or stolen PC | Laptop left on a train |
| B. File copied off the PC | Disk image, backup, a shared folder, a support upload |
| C. Another Windows account on the same PC | Family PC, shared lab machine |
| D. Malware or a person already running as the user | Cannot be stopped by any option below |

No option stops D. That is a limit of every local-only design, and the doc should not promise otherwise.

## Options

### 1. SQLCipher (whole-file AES-256) with a Windows-protected key

The database file is encrypted page by page. A random 256-bit key is generated at first run and stored as a **DPAPI blob** (current-user scope) next to the database. DPAPI ties the key to the Windows account, so the key is unreadable from another account or another PC.

- Protects against A, B, C.
- **CPU:** page encryption adds a few percent on database work; our write rate is tiny (about one row per window switch). The one real cost is key derivation: with a passphrase SQLCipher runs a slow derivation on every connection open, and our code opens a connection per call. Using the **raw key form** (a random 256-bit key given directly) skips that cost. These are estimates; they must be measured against the 3% CPU budget in a Phase 1 benchmark.
- **Memory:** about 1-2 MB extra for the library and cipher context; negligible against the 150 MB budget (estimate, to be measured).
- **Secure delete:** still works, but matters less. Every page on disk, including the write-ahead log, is ciphertext, so freed pages hold no readable text.
- **Migration risk:** none if chosen before first real data. After that, moving a plaintext database means export to a new encrypted file, verify it, swap, and then remove the plaintext, which cannot be guaranteed to be unrecoverable on an SSD. **Key-loss risk:** if the Windows profile is recreated or the password is reset by an administrator, the key is lost and the database cannot be opened. Raw events are short-lived and summaries are in the cloud, so the plan is: detect the failure, set the old file aside, start a fresh database, and mark the lost period as unknown.
- **Other costs:** a different SQLite build (the community SQLCipher bundle for Microsoft.Data.Sqlite; **license, version and signing not yet verified**), and standard database tools cannot open the file (support and export must go through the agent).

### 2. Windows-only protection of the file (EFS)

Mark the DLA data folder as encrypted with the Windows Encrypting File System. No code change to the database.

- Protects against B (when copied off), C; partly A.
- **Not available on Windows Home editions**, which many students use. Cannot be relied on for all v1 users.
- **CPU/memory:** negligible, done by the file system.
- **Secure delete:** SQLite's overwrite still works; EFS adds nothing for freed pages.
- **Migration risk:** low. Encrypting an existing folder is one call, but results depend on edition and policy, and keys are recoverable only if the user backed up the EFS certificate.

### 3. Rely on the operating system

Per-user profile permissions (other accounts cannot read it) plus BitLocker/Device Encryption if the user has it on.

- Protects against C; against A only if BitLocker is on (not guaranteed, not under our control); does **not** protect a copied file (B).
- **CPU/memory:** zero. **Migration risk:** none.
- **Secure delete:** becomes the main defence for deleted data, which is why it is already on.
- Weakest answer to "encryption at rest" in a product whose brand is privacy.

## Comparison

| | 1. SQLCipher + DPAPI key | 2. EFS | 3. OS only |
|---|---|---|---|
| Lost PC (A) | Yes | Mostly | Only if BitLocker is on |
| File copied (B) | Yes | Yes | No |
| Other account (C) | Yes | Yes | Yes |
| Works on Windows Home | Yes | No | Yes |
| CPU / RAM | Small; measure | None | None |
| Effect on secure delete | Less needed | None | Essential |
| Migration risk | None now; real later | Low | None |
| Extra dependency | SQLCipher build | None | None |

## Recommendation

**Option 1: SQLCipher with a random raw 256-bit key protected by DPAPI (current user).**

- It is the only option that covers B and C on every Windows edition, and it matches the spec's "encryption at rest" without relying on settings the user may not have enabled.
- Cost is small if the raw key form is used, and it is cheapest to adopt now, before any user data exists.
- **Conditions before adopting:** (1) verify the SQLCipher package's license, version and Windows binaries; (2) a Phase 1 benchmark showing CPU stays inside the 3% budget with per-call connections (or switch to one long-lived connection); (3) the key-loss recovery path above is built and tested.
- **Fallback:** if the benchmark fails, use option 3 and state plainly in the privacy text that the file is protected by the Windows account, not encrypted by DLA.
- Say clearly in the privacy text that this protects the file at rest, not against malware running as the same user.

## Not decided here

- Whether the encrypted summaries queued for upload need any extra local protection (they are already ciphertext).
- The export-my-data format (it will be produced by the agent, since other tools cannot read the file).
