# Session-end durability (.NET SDK) — capture clean app closes instead of relying on the reaper

- **Status:** READY-TO-IMPLEMENT (post-codex revision 3 — converged; server half split out at revision 4)
- **Owner:** Matthew Clendening
- **Created:** 2026-06-03
- **Slug:** `session-end-durability`
- **Repo:** `beacon-sdk-dotnet` (client SDK — Parts A/B)
- **Depends on:** `usage-tracking/docs/specs/session-end-server-reconciliation.prd.md` (Parts C/D — the shared server foundation + the recovery wire contract this SDK targets). Ship the server PRD first.
- **Sibling SDKs:** `beacon-sdk-cpp` (near-identical port — same gaps, has a disk queue), `beacon-sdk-js` (already sends a `normal` end on page-hide via `fetch(keepalive)`; needs only minor hardening, not this design). All three consume the same server PRD.
- **Targets:** must work across the full SDK matrix — `net8.0`, `net8.0-windows`, `net6.0`, `netstandard2.0`, `net48`.

---

## Problem

A clean application close does not record a session end. The session stays open on the server until the inactivity **reaper** closes it as `timeout` (default 24h after last activity). Consequences:

- `completed_session_count` is ~always 0 for desktop / short-lived apps — every session is eventually classified `timeout`, never `normal`.
- Session **duration** analytics are low-fidelity and lag by up to the reaper window + the 15-minute aggregation cadence.
- You cannot distinguish a deliberate close from abandonment — both look identical (`timeout`).

**Reproduction (observed 2026-06-03, localhost):** Ran `examples/winforms` against a local stack, generated events, then closed the app. The raw `sessions` row stayed `ended_at = NULL`, `end_reason = NULL`, `duration_seconds = NULL`; the Sessions page showed `session_count = 1, completed_session_count = 0`. Closing the window sent nothing.

### Why this is surprising

The SDK *can* send promptly — `StartSession` posts the session-start over the network mid-session and it lands reliably. The asymmetry (start is sent, end is dropped) is not an intentional symmetry; it is three compounding SDK gaps. (The server-side gaps that compound it — no correction of a reaper `timeout`, a 24h default — are addressed in the server PRD.)

---

## Root cause (SDK)

All refs verified against current source.

- **R1 — `Dispose()` never attempts a session-end.** `BeaconTracker.cs` ~`1054-1062`: on dispose it calls `WriteRemainingToDisk()` (persists in-memory **events** to the disk queue) and then clears `_sessionId` locally under `_sessionLock`. The inline comment states the intent explicitly: *"Clear session state locally — the server infers session end from inactivity or the next session-start event."* No end is sent or persisted.
- **R2 — every session-end path is fire-and-forget `Task.Run`.** `EndSession()` (public, `void`, ~`522`) does `_ = EndSessionInternal("normal")`. `EndSessionInternal` (~`1492`) returns a `Task.Run(...)` that calls `BeaconHttpClient.SendSessionEndAsync(...)` and is **never awaited** by any caller. On process exit the task is abandoned before the request completes — exactly the observed loss.
- **R3 — `FlushAsync()` does not cover session lifecycle.** `FlushAsync` (~`550`) drains the event queues only (disk → memory). Session start/end go through a **separate** `BeaconHttpClient` path, not the batch queue, so there is no awaitable way for an app to guarantee its end is delivered.

**Server dependency:** the corresponding server gaps (a reaper `timeout` cannot be corrected; the 24h default; the `sdk_recovery` reason code is server-accepted but SDK-unused) and the **create-on-recovery wire contract** are specified in the server PRD. This SDK PRD assumes that contract is available.

---

## Proposed fix

Two SDK layers, each independently shippable, that together capture clean closes reliably without ever blocking shutdown. They target the recovery wire contract defined in the server PRD.

### Part A — Durable session-end on `Dispose()`

On `Dispose()` (and on any `EndSession`), when a session is active:

1. **Persist a durable pending-session-end record locally first** — a local write that survives offline and force-kill. The record captures the **self-contained** payload (see "self-contained record" below) where **`ended_at` is stamped at write time** (the session's `last_activity_at` / dispose instant), NOT delivery time — so a deferred delivery still records the true end. The write happens **before** `_sessionId` is cleared. *Caveat:* "never blocks" is too strong — the existing SQLite store uses `PRAGMA busy_timeout=5000`, so a contended write can block up to 5s. Spec a **short busy timeout (e.g., 200ms) or a separate connection** for the session-end store so a dispose-time write is effectively non-blocking; the worst case is a small bounded wait, not zero.
2. **Best-effort immediate send** with a short bounded timeout (new option `ShutdownFlushTimeout`, default ~2s; `0` = skip the blocking send, disk-only). The send must use an **independent timeout `CancellationTokenSource`, NOT `_shutdownCts`** — `DisposeAsync` cancels `_shutdownCts` early (`BeaconTracker.cs` ~`1029`), so reusing it would cancel the send before it starts. If it succeeds while online, the end lands now as `end_reason = "normal"` and the durable record is deleted.
3. **Deferred delivery on next launch** — if the immediate send is skipped/fails/times out, the record persists and is delivered during the next SDK initialization (alongside the existing disk-queue drain) as `end_reason = "sdk_recovery"`, using the **original** persisted `ended_at`.

**Self-contained record (the start dependency).** A durable end is useless if the session **start** never reached the server: `StartSession` also sends fire-and-forget (`BeaconTracker.cs` ~`487`), and the end-handler returns `404 session_not_found` for an end on a missing session. So an app offline at session start, then closed, would persist an end that 404s on next-launch delivery. The durable record must therefore be **self-contained** — it carries the start context (`session_id, actor_id, source_app, product/product_version, started_at, account_id?, license_id?, ended_at, end_reason`) so the server's **create-on-recovery** path (server PRD) can materialize an absent session. When the start *did* land (the common online case), the recovery end simply matches the existing row. The SDK must treat a `404` on delivery as **non-terminal** for a recovery record (leave it for the create-on-recovery upsert), and must NOT retry a `404` indefinitely for a non-recovery end.

**Storage choice:** do NOT put the end record into the existing event disk-queue batch — that queue is homogeneous serialized event payloads delivered to the *events* endpoint, and mixing a session-lifecycle record would break the batch contract. Use a typed sibling store (a dedicated table in the same DB file, or a small typed record alongside `DiskQueue`).

**Multiple pending ends — must be a queue, not a single row:** one process run can produce several unsent ends while offline — `StartSession` ends the active session before starting a new one (`BeaconTracker.cs` ~`472`) and `EndSessionInternal` clears `_sessionId` immediately (~`1499`), so a single-row store would silently drop earlier ends. The durable store must be a **queue keyed by `session_id`** holding *N* pending ends, all delivered on next launch.

**Opt-out / consent:** opt-out state is persisted and suppresses delivery on next launch. **Purge pending session-ends on `OptOut()`** (do not deliver) — consistent with the SDK's pseudonymous, consent-respecting posture. `Reset()` likewise clears the pending-end store.

### Part B — Awaitable end

Give apps a synchronous guarantee for the online case:

- **Do NOT add a method to `IBeaconTracker`** — adding `EndSessionAsync` to the public interface breaks every external implementer/mock, and default interface methods are unavailable on the `netstandard2.0` / `net48` targets. Instead, surface the awaitable via: (a) have **`FlushAsync()` also drain the pending durable session-end** after the event queues — purely additive behavior on an existing method (preferred); and/or (b) add `EndSessionAsync` as a **concrete method on `BeaconTracker`** (and/or a *new* opt-in interface), not on `IBeaconTracker`.
- Keep `void EndSession()` as the fire-and-forget convenience (now backed by the durable record from Part A, so it is no longer lossy).

This lets a conscientious app do `tracker.EndSession(); await tracker.FlushAsync();` before close and be sure the end is delivered when online — **provided the session start has landed.** For a just-started session whose fire-and-forget start may still be in flight, the awaitable guarantee depends on the create-on-recovery path; `FlushAsync` draining the pending end must therefore tolerate a transient `404` (leave the self-contained record rather than treating `404` as terminal).

### Server foundation (dependency, not implemented here)

Parts C (real end supersedes a reaper `timeout`; create-on-recovery; client-time bounds) and D (shorten the reaper default) live in `usage-tracking/docs/specs/session-end-server-reconciliation.prd.md`, which also defines the recovery **wire contract** this SDK targets. Ship the server PRD first — it benefits existing clients (including the JS SDK) on its own.

---

## Test plan (SDK unit tests)

- `Dispose()` with an active session writes a durable pending-end record (assert persisted store).
- Immediate-send success path deletes the record; failure/timeout path leaves it persisted.
- Next-launch init delivers a persisted end as `sdk_recovery` with the **original** `ended_at` (not now()).
- The persisted record is **self-contained** (carries the start context); a `404` on delivery is non-terminal (left for the create-on-recovery path), not dropped.
- `EndSessionAsync()` / `FlushAsync()` drains a pending end and awaits delivery.
- `ShutdownFlushTimeout = 0` skips the blocking send but still persists.
- **Multiple pending ends:** two sessions ended offline in one run (`StartSession` → `StartSession` → `Dispose`) persist **two** records, both delivered next launch (queue, not single-row).
- Opt-out / `Enabled = false` / no active session → no record written, no send.
- **Opt-out after a record exists:** a pending end written before `OptOut()` is **purged, not delivered**; `Reset()` clears the pending-end store.
- Cross-TFM: the durable store and timeout APIs compile and run on `net48`, `netstandard2.0`, `net6.0`, `net8.0(-windows)` (extend the existing net48 runsettings coverage).

The server-side behavior (supersede, create-on-recovery, reaper default) is tested in the server PRD.

---

## Alternatives considered & rejected

- **Blocking synchronous send on `Dispose()` with no disk fallback.** Rejected: hangs shutdown when offline (up to the socket timeout) and still loses the end on force-kill / crash / power loss. The durable record is what makes it reliable; the blocking send is only an optimization for the online case.
- **Put the session-end into the existing event disk-queue batch.** Rejected: the queue is homogeneous event payloads delivered to the events endpoint; a session-lifecycle record breaks the batch contract and the `DequeueUpTo → events endpoint` delivery path. Use a typed sibling store.
- **Single pending-end slot.** Rejected: multiple sessions can end offline in one run; must be a queue keyed by `session_id`.

---

## Risks & open questions

- **Process-exit grace.** Even the short best-effort send may not finish before the OS reclaims the process; that is precisely why the durable record + next-launch delivery is the guarantee, not the immediate send. *Open:* default `ShutdownFlushTimeout` (proposed 2s; `0` allowed).
- **Next-launch latency.** If the app isn't reopened for a long time, the reaper closes the session first (sooner, per the server PRD's shorter default); the server's create/ supersede then corrects it when the app reopens — unless past the server's age bound.
- **Crash before the durable write.** A crash between last activity and the dispose write means reaper-only — acceptable and unchanged from today.
- **Cross-TFM parity.** No down-level-unavailable APIs (timeouts, SQLite store) — `DiskQueue` already cross-targets; mirror its guards.
- **SemVer.** Minor bump (**3.3.0**) — but only if the new awaitable is added **off** `IBeaconTracker` (see Part B). `ShutdownFlushTimeout` (new `BeaconOptions` field) and the durable-end behavior are additive.

---

## Implementation order

Ship the server PRD (D → C) first. Then:

1. **Part A — durable end** (`beacon-sdk-dotnet`). Typed durable queue + persist-on-dispose + best-effort immediate send + next-launch `sdk_recovery` delivery. (~1–1.5 day)
2. **Part B — awaitable end** (`beacon-sdk-dotnet`). `FlushAsync`/`EndSessionAsync` drains pending end. (~0.5 day)

Released together as **3.3.0**.

---

## Iteration log

### Revision 0 — initial draft (2026-06-03)
Authored from a live localhost investigation: closing `examples/winforms` left the session open (`ended_at = NULL`); the data was present but never end-stamped, and the Sessions page reflected the reaper-only model. Verified the three SDK gaps (R1–R3), the server no-op-on-already-closed (R4a) and 24h reaper default (R4b), and that `sdk_recovery` is a server-accepted but SDK-unused reason code. Sent for review.

### Revision 1 — post-codex round 1 (2026-06-03)
Codex confirmed R1–R4b and "`sdk_recovery` accepted-but-SDK-unused" are factually correct, and that the blocking-send / shared-event-queue rejections are justified. 8 findings (1 High, 4 Medium, 3 Low), all verified against source and incorporated:
- **High:** single pending end is unsound — one offline run can produce several unsent ends (`StartSession` ends the prior session, ~`472`/`1499`). → Durable store is now a **queue keyed by `session_id`**, not a single row.
- **Medium:** `EndSessionAsync` on `IBeaconTracker` is **not** SemVer-minor safe (breaks implementers; no DIM on net48/netstandard2.0). → Part B now adds the awaitable via `FlushAsync()` draining the pending end (preferred) or a concrete `BeaconTracker` method / new interface — never on `IBeaconTracker`. SemVer note corrected.
- **Medium:** dispose send must not reuse `_shutdownCts` (cancelled early at ~`1029`); durable write must precede clearing `_sessionId`. → Specified an independent timeout CTS and write-before-clear ordering.
- **Medium:** "instant, never blocks" overstated — `DiskQueue` uses `PRAGMA busy_timeout=5000` (~`46`). → Spec a short busy timeout / separate connection; worst case is a small bounded wait.
- **Medium:** opt-out for an already-persisted pending end was undefined. → Explicit decision: **purge on `OptOut()`/`Reset()`, do not deliver**; test added.
- **Low:** Part C metric was misstated — `completed_session_count` counts any non-null `ended_at`, so a reaper timeout is already 1; the supersede flips **`timed_out_session_count` 1→0**. → Corrected (now in the server PRD).
- **Low:** "rewind" is imprecise — the handler bumps `LastAggregatableChangeAt` to mutation time. → Wording fixed (server PRD).
- **Low:** client `ended_at` needs a **future**-skew clamp. → Added to the server bounds.

### Revision 2 — post-codex round 2 (2026-06-03)
Findings shrank 8 → 2 (1 High, 1 Low); codex signed off the round-1 incorporations and confirmed duplicate/retried `sdk_recovery` ends won't double-count. Both incorporated:
- **High:** a durable *end* is useless if the session *start* never landed — `StartSession` is also fire-and-forget (~`487`) and the end-handler `404`s on a missing session. → The durable record is now **self-contained**; the server gains **create-on-recovery** (server PRD) instead of 404; the Part B claim was qualified; `404` is non-terminal for the recovery path.
- **Low:** a stale server test bullet said `completed_session_count` flips `0→1` on timeout supersede. → Corrected to `timed_out_session_count` 1→0.

### Revision 3 — post-codex round 3 (2026-06-03, CONVERGED)
Focused adversarial pass on the create-on-recovery seam. 1 High + 1 Medium of substance + 2 confirmations; codex closed with "Everything else looks ready." Incorporated (now carried by the server PRD): create-on-recovery must reuse the start path's auth-derived fields + validation + plan-gating, and use an atomic `ON CONFLICT DO NOTHING` insert; a delayed start after a recovery-create is an intentional no-op. **Materiality check: PASS** (genuine durability/correctness feature, not a reorg).

### Revision 4 — server half split out (2026-06-03)
Parts C (server reconciliation + create-on-recovery) and D (reaper default) extracted to `usage-tracking/docs/specs/session-end-server-reconciliation.prd.md` as the shared foundation consumed by all three SDKs, with the SDK-facing recovery **wire contract** made explicit there. This PRD is now SDK-only (Parts A/B) and lists the server PRD as a dependency. No design change — the converged decisions are preserved, just relocated. The C++ SDK gets a sibling PRD (`beacon-sdk-cpp`) mirroring Parts A/B against the same server contract; the JS SDK needs only a minor hardening pass (it already sends a `normal` end on page-hide via `fetch(keepalive)`).
