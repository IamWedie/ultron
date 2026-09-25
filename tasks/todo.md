# ULTRON Proactive — Task List

## Remediation pass — completed 2026-09-25

- [x] **IPC / session lifecycle:** one restart-safe stdin reader, no-key startup, bounded
      messages/queues, input validation, deterministic shutdown, safe `open_app`/`undo` approval.
- [x] **Mic + VAD:** bridge initialization, 16k→48k routing, 512-sample VAD chunks,
      duplicate/retrigger fix, interruption + turn-end DSP drain/reset.
- [x] **Telegram transport:** `send_external_frame` replaces the unsupported named-pipe
      TX, 10ms/480-sample frame pacing, playback source after connect, separate Gemini/mic
      TX buffers, continuous silence frames, thread-safe handoff, peer access-hash binding.
- [x] **Call cleanup:** bounded native disconnect, stale-event filtering, connection-state
      monitor, `finally` guards, config-driven calls enabled (off by default), atomic
      session save, invalid-credential handling.
- [x] **Persistence / model I/O:** shared atomic `json_store`, corruption handling, model
      computer-action schema validation, document containment/atomic generation, formula
      injection guard, memory/prompt bounds.
- [x] **C# boundaries:** `open_app`/`browser_control`/per-action approvals on the UI thread,
      user-profile path guard, read/write byte caps, http/https URL check, mic-mute
      persistence + replay, no-key backend start, stale-process guards, restart race fix.
- [x] **CI + tests:** Python compileall/pyflakes/import checks wired into GitHub Actions,
      31 Python regression tests, 57/57 C# tests, no-key IPC smoke test.

- [ ] **Follow-ups:** live Telegram call acceptance; `file_processor` per-operation allowlist
      with reparse-safe handles; hashed Python dependency lockfile; GitHub Actions SHA pins.

## Phase 1 — Remote reach (foundation)

- [x] **T1: Outreach router (C#)**
  - [x] `Services/Outreach.cs` handles backend `contact` IPC events
  - [x] Policy routing: away vs at-home, priority, per-area toggles, channel preference
  - [x] Sends via Telegram bot + SMS (phone_* ADB path); local Gemini speak stays backend-owned
  - [x] New settings: NotifyChannel, NotifyNumber, per-area toggles
  - [x] Secrets redacted in all logs; token via env or DPAPI config
  - [x] Build 0 warnings + 49/49 tests pass (8 new tests)

- [ ] **T2: Remote approval loop**
  - [ ] Backend `contact {ask:true, expect_reply:true}` support
  - [ ] C# sends question, polls reply (Telegram bot / SMS via ADB), returns via `contact_reply`
  - [ ] Pending approvals persisted (survive backend restart); timeout = hold

- [ ] **T3: Away mode + presence**
  - [ ] `AwayMode` setting + `set_away` voice command
  - [ ] While away: guardian/monitor/reminders route through outreach
  - [ ] Both toggles voice-usable

- [ ] **T4a: Idle input sensor**
  - [ ] C# `GetLastInputInfo` + lock events -> `away_event` IPC
  - [ ] No events when unarmed

- [ ] **T4b: Webcam motion sensor**
  - [ ] Python `away_watch.py` cv2 gray-diff, on-device, no frames persisted
  - [ ] Motion while armed -> `away_event` -> one-shot contact with cooldown

## Checkpoint 1
- [ ] All tests pass, build clean
- [ ] Remote contact works over at least one message channel
- [ ] Away mode changes guardian/monitor routing end-to-end
- [ ] Human review before Phase 2

## Phase 2 — Missions (not started)
- [ ] T5: Mission store + tools
- [ ] T6: Mission runner loop (6a core, 6b reporting)
- [ ] T7: Approval-at-step
- [ ] Checkpoint 2

## Phase 3 — Learning (knowledge first, not started)
- [ ] T8: Knowledge & skills store
- [ ] T9: Scheduled digest loop
- [ ] T10: learn_skill / recall_skill
- [ ] Checkpoint 3

## Phase 4 — Call bridge (not started; superseded by Telegram voice call below)
- [ ] ~~T12: Twilio outbound call (summary-only)~~ -> superseded by Telegram voice call
- [ ] ~~T13: Twilio <-> Gemini Live bridge (roadmap note)~~ -> superseded by Telegram voice call

---

# Telegram Voice Calling (separate workstream — spec: `tasks/SPEC-telegram-voice-call.md`, plan: `tasks/telegram-call-plan.md`)

## Phase 1 — Account login (plumbing done; live login pending user creds)
- [x] C1: `backend/telegram_client.py` auth client (Telethon, DPAPI session, redacted logs)
- [x] C2: backend IPC handlers `telegram_login`/`telegram_logout`/`telegram_status`/`telegram_code`
      + events (`telegram_status`, `telegram_code_required`, `telegram_code_result`); `telegram` capability in hello
- [x] C3: C# AppSettings + GeminiBackend IPC methods/events + env passthrough
- [x] C4: tests — python regression suite 31/31, C# 57/57, build 0 errors, relaunch clean
- [x] C4-fix: login bugfix — `send_code_request` returns `auth.SentCode`, not the hash string;
      `sign_in` now gets `.phone_code_hash` (previously: `ValueError` then `TypeError`).
      One live Telethon client is reused across code request + submit.
- [ ] C4b: LIVE login acceptance — user provides api_id/api_hash (my.telegram.org),
      presses Connect, verifies "CONNECTED" survives backend restart (gated on user creds)

## Phase 2 — Target resolution (code done; live resolve pending user creds)
- [x] C5: resolve/cache call target (`@username`/phone) in `telegram_client.py`
      — `resolve_target()` validates a real (non-bot) User entity, caches user id,
      emits `telegram_target_result`; IPC `telegram_resolve_target`; C# settings
      `TelegramCallTarget`/`TelegramCallUserId` + env override; "Resolve Target" button
      in settings; py regression suite 31/31, C# 57/57
- [ ] C5b: LIVE resolve acceptance — enter `@yourusername`, press Resolve Target,
      confirm "Target resolved" + cached user id (gated on user creds)

## Phase 3 — Media-engine spike (not started, risk-gate)
- [ ] C6: prove `py-tgcalls` (prebuilt Windows wheel) on Windows — place a real PCM test call
      ring→answer with streaming PCM I/O; STOP if it cannot

## Phases 4+ — Calls (pending spike result)
- [x] C7: outgoing call state machine (requestCall/accept/discard + updates)
      - [x] IPC handlers: `telegram_call_start`, `telegram_call_stop`, `telegram_call_status`
      - [x] Backend events: `telegram_call_state`, `telegram_call_result`
      - [x] C# events: `TelegramCallStateChanged`, `TelegramCallResult`
      - [x] C# send methods: `SendTelegramCallStartAsync`, `SendTelegramCallStopAsync`, `SendTelegramCallStatusAsync`
      - [x] Full call flow: create_p2p_call → getDhConfig → init_exchange → RequestCall → PhoneCallAccepted → exchange_keys → ConfirmCall → connect_p2p → FILE tone → RX PCM logging → DiscardCall
      - [x] Gate: TelegramCallEnabled (off by default), target must be resolved
- [x] C8.1: call_start with payload + fallback logic (ring→answer→discard evaluation)
- [x] C8.2: C# events + send methods (TelegramCallStateChanged, TelegramCallResult, TelegramCallFallbackSent)
- [x] C8.3: Guardian wired to call_start with payload (away mode + high-priority)
- [x] C8.4: UI status display + fallback notification in MainWindow
- [x] C8.5: Config settings (CallFallbackEnabled, CallFallbackThresholdSeconds, CallFallbackChatId) + env overrides
- [x] C8.6: InCallAudioBridge (48k<->16k/24k adapters) + mic bypass + barge-in
      — external-frame TX, 10ms/480-sample pacing, playback source, separate Gemini/mic
        buffers, continuous silence frames, thread-safe handoff (unit-tested; live call
        acceptance still tracked under C6/C10)
- [ ] C9: ring/accept/dial UI, tools live in-call, secure auth UX
- [ ] C10: hang-up/disconnect/concurrent/reconnect edge cases + tests
  - [x] Bounded native cleanup, connection-state monitor, stale-event filtering, `finally` guards
  - [ ] Live hang-up, simultaneous-call, and reconnect acceptance