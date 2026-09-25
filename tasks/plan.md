# Implementation Plan: Proactive ULTRON / Jarvis-Grade Autonomy

## Overview

Turn ULTRON from a reactive assistant into a proactive one that (1) reaches the user
remotely when away, (2) executes long-running "missions" autonomously with remote
approval for risky steps, (3) learns knowledge (world news, tech, skills) from the
internet on a schedule and on demand, and later (4) can extend its own toolset with
approved, reviewed code. Outreach is message-first (Telegram/WhatsApp/SMS) with a
Twilio call bridge designed in as an optional, flag-gated upgrade.

## Decisions (user-confirmed)

- **Call mechanism:** Messages now (Telegram/WhatsApp/SMS), Twilio call bridge later.
- **Away detection:** Input events + webcam motion, on-device, never stored.
- **Self-extension:** Both, knowledge first (skills first, code self-extension deferred).
- **Mission autonomy:** Pre-approved scope runs autonomously; risky/out-of-scope steps
  text/call the user for approval.
- **World knowledge:** Scheduled digest + on-demand learn.

## Architecture Decisions

- **Outreach router (C#, single contact point).** All proactive events (guardian alert,
  reminder due, away-detection, mission progress/approval, monitor findings) become IPC
  `contact` events from the Python backend; C# owns remote channels (ADB/phone, Telegram,
  later Twilio) and routes per policy (priority + channel + away state). Local audible
  speak stays the Gemini Live turn for at-home use. Settings in `AppSettings`:
  `NotifyChannel`, `PhoneNumber`, `AwayMode`, per-area toggles.
- **Remote approval = two-round trip.** Backend emits `contact {ask:true, expect_reply:true}`
  with the question; C# sends over the configured channel (Telegram bot if available, else
  SMS poll via ADB), watches for a reply within a timeout, returns it via `contact_reply`
  IPC. Pending approvals are persisted so a backend restart doesn't lose them.
- **Away mode + detection.** `AwayMode` setting (`set_away`, with sensitivity). Idle sensor =
  C# `GetLastInputInfo` (keyboard/mouse + screen lock events forwarded as `away_event`).
  Motion sensor = Python `away_watch.py` using cv2 gray-frame diff on-device, no frames
  persisted. While away, guardian/monitor/missions route through outreach instead of
  local-only speak.
- **Missions = persistent autonomous tasks.** `missions.json` + `delegate_mission` /
  `list_missions` / `cancel_mission` tools. A `_run_mission_task` loop (mirrors existing
  guardian/monitor loops) advances each active mission via Gemini with full tool access,
  persists step state, enforces a per-mission tool whitelist. Out-of-scope/risky steps pause
  and go through the remote approval loop. Progress/completion/failure reported through
  outreach with cooldowns.
- **Learning — knowledge first.** Python-side JSON stores (`knowledge.json`, `skills.json`)
  consistent with `guardian.json`/`monitors.json`, with freshness timestamps. A
  `_run_knowledge_task` loop (daily digest, configurable topics + cadence) and on-demand
  `learn_skill` both use `web_search` + doc fetch, distill into briefs/recipes, store for
  recall, and inject into system prompt context when relevant. Code self-extension is a
  separate later stage (template scaffold + human review + register next reconnect).
- **IPC contract additions (backend -> C#):** `contact`, `away_event`.
  **C# -> backend:** `contact_reply`.
  Existing event vocabulary preserved.

## Task List

### Phase 1 — Remote reach (foundation)

- [x] Task 1: Outreach router (C#) — `Services/Outreach.cs`, contact IPC handling, policy
      routing, message + local speak fallback, new settings, redacted logging.
- [ ] Task 2: Remote approval loop — ask/reply via channel, `contact_reply` IPC, persistence.
- [ ] Task 3: Away mode + presence — setting, `set_away` voice command, guardian/monitor
      routing changes while away.
- [ ] Task 4a: Idle input sensor (C# `GetLastInputInfo` + lock events) -> `away_event` IPC.
- [ ] Task 4b: Webcam motion sensor (Python cv2 gray-diff, no storage) -> `away_event`.

### Checkpoint 1
- [ ] All tests pass, build clean
- [ ] Remote contact works over at least one message channel
- [ ] Away mode changes guardian/monitor routing end-to-end
- [ ] Human review before Phase 2

### Phase 2 — Missions (autonomous long-running tasks)

- [ ] Task 5: Mission store + tools (`missions.json`; delegate/list/cancel; whitelist).
- [ ] Task 6: Mission runner loop (6a core, 6b progress reporting).
- [ ] Task 7: Approval-at-step (risky -> remote question -> resume/abort, persisted).

### Checkpoint 2
- [ ] Mission runs unattended across a restart
- [ ] Remote approval round-trip works live
- [ ] Completion/failure notification reaches user
- [ ] Human review before Phase 3

### Phase 3 — Learning (knowledge first)

- [ ] Task 8: Knowledge & skills store (JSON, freshness, recall integration).
- [ ] Task 9: Scheduled digest loop (web_search -> distill -> store).
- [ ] Task 10: On-demand `learn_skill` / `recall_skill`.
- [ ] Task 11: (deferred) Code self-extension scaffold — design notes + ADR only.

### Checkpoint 3
- [ ] Knowledge digest scheduled and fresh
- [ ] learn/recall works across sessions
- [ ] No regressions

### Phase 4 — Call bridge (optional, flag-gated)

- [ ] Task 12: Twilio outbound call (summary-only), off by default.
- [ ] Task 13: (roadmap) Twilio <-> Gemini Live two-way audio — design note.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|-------------|
| Reply-polling unreliable (SMS read via ADB needs phone paired/awake) | Remote approvals stall | Telegram-bot-first for replies; SMS fallback; timeout = "hold, don't fail"; persist pending |
| Webcam motion sensing = privacy surface | High | On-device diff only, no storage, arm/disarm + sensitivity, default off |
| Missions executing destructive steps | High | Per-mission tool whitelist + delegation approval + at-step approval + notify every risky action |
| Self-extension writing/running code | High | Phase 3 = knowledge only; code stage: template, human review, register-next-restart, no raw shell injection |
| Background web_search/API cost & rate limits | Med | Cadence + cooldowns + min interval; idle-gated |
| Backend restart loses in-flight state | Med | missions/pending-approval/knowledge all persisted to disk JSON |

## Open Questions

- **Reply channel:** Telegram bot vs WhatsApp vs SMS — plan defaults to Telegram bot + SMS fallback; pending user preference for a bot token.
- **Motion sensor source:** camera index / sensitivity default; user confirmed on-device analysis OK (never saved).
- **Call bridge provider/budget:** TBD before Phase 4 (default Twilio, monthly cap).

---

## Remediation Pass — Backend, IPC, and Telegram Hardening (2026-09-25)

This section is additive; the proactive-assistant roadmap above remains open.

### Completed

- [x] Repaired the IPC stdout path and consolidated Python stdin handling into one restart-safe reader.
- [x] Added no-API-key startup, bounded IPC messages/queues, boolean/numeric validation, and a persistent session supervisor.
- [x] Fixed microphone bridge initialization, sample-rate conversion, VAD chunk duplication/retrigger behavior, and DSP drain/reset handling.
- [x] Replaced the unsupported Telegram named-pipe transport with paced 10 ms `ntgcalls` external frames, playback source setup, peer binding, and bounded cleanup.
- [x] Added atomic JSON persistence, corruption guards, model-input validation, document containment, and durable task/document limits.
- [x] Hardened C# backend lifecycle, approval dispatch, application/URL launch restrictions, mute propagation, and file-tool bounds.
- [x] Added Python regression tests (31) and wired Python checks into GitHub Actions.

### Verification

- [x] `python backend/ci.py`: compileall, pyflakes, isolated imports, and unit tests all pass.
- [x] `dotnet build -c Debug --nologo`: 0 errors.
- [x] `dotnet test tools/Tests/Ultron.Tests.csproj --nologo`: 57/57 pass.
- [x] No-key IPC smoke test completes cleanly.

### Remaining Risks / Follow-ups

- [ ] Run a live Telegram call acceptance test; the external-frame and playback path is unit-tested but not yet exercised against a real peer.
- [ ] Tighten `file_processor` from a user-profile guard to a per-operation allowlist with reparse-point-safe handles.
- [ ] Commit a hashed Python dependency lockfile and pin GitHub Actions to immutable revisions.
- [ ] Complete live login/resolve acceptance and the remaining call UX items in the Telegram workstream below.
