# Implementation Plan: Phone Bridge Hardening (review remediation)

## Overview
Fix every defect found in the code review of the phone-control changeset: make
the 35 curated `phone_*` tools actually routable, close the push-file path hole,
stop reporting failed adb commands as success, target the configured device,
expose actionable messages when the bridge is unconfigured, and remove the
dead/fragile camera and settings code. Tests are added at the same layer that
would have caught each defect.

## Architecture Decisions
- Policy remains the single gate: phone tools get explicit entries in
  `PolicyEngine`, with a regression test over `PhoneTools.Curated`.
- Path confinement is enforced in the `IAdbPhone` seam via `IPathPolicy`, so the
  router stays ignorant of files and the rule cannot be forgotten per tool.
- `Run` now fails on non-zero exit unless the command is a shell pipeline
  (pipelines legitimately exit non-zero when `grep`/`find` matches nothing).
- Every adb call is pinned to the configured transport (`-s host:port`) so a
  second attached device can never receive the command.
- The bridge always registers its handlers; when disabled/unconfigured they
  return the actionable probe message instead of surfacing `UnknownTool`.
- Backend declarations and the C# curated set are checked for drift in Python CI.

## Task List

### Phase 1: Correctness gates (highest risk first)
- [x] Task 1: Add `phone_*` policy entries + policy/router regression tests.
- [x] Task 2: Confine `phone_push_file` to `IPathPolicy` + tests.
- [x] Task 3: Propagate adb exit codes (pipeline-aware) + tests.

### Checkpoint: Foundation
- [x] `dotnet test` green; a router call to `phone_device_info` executes.

### Phase 2: Device targeting and lifecycle
- [x] Task 4: Pin every command to the configured transport and fix `Connect`.
- [x] Task 5: Always register handlers; add `UnavailablePhone` + clear messages.
- [x] Task 6: Wire the unlock PIN through settings/UI (SelfieVerify was dead).

### Checkpoint: Lifecycle
- [x] Unauthenticated/disabled states return advisory failures, never `UnknownTool`.

### Phase 3: Camera, files, polish
- [x] Task 7: Split camera package vs intent; remove dead shutter code.
- [x] Task 8: Unique screenshot/recording files; check exit status.
- [x] Task 9: Fix `ReadSms` limit, `ListFiles` default, pull edge, `MakeCall`/`SendSms` false success.
- [x] Task 10: Namespace alignment, single `Build()`, backend/catalog drift test.

### Checkpoint: Complete
- [x] `dotnet build` clean, `dotnet test` green, `python backend/ci.py` passes.

## Risks and Mitigations
| Risk | Impact | Mitigation |
|------|--------|------------|
| Strict exit codes break best-effort probes | Med | Exempt shell pipelines; re-run all tests; audit grep call sites |
| `-s` breaks wireless pairing | High | Use the already-configured `host:port` transport id; `connect`/`devices` stay global |
| Changing camera shutter is unverifiable without hardware | Med | Keep the proven tap path; delete only unreachable/dead keycode code |
| New path check breaks legitimate pushes | Low | Canonicalise and test inside/outside/missing cases with a temp root |

## Open Questions
- None blocking. Camera shutter remains a coordinate tap because no device is
  available to validate `KEYCODE_CAMERA`; the dead keycode API is removed.

## Task List Location
Tasks and checkpoints are recorded in `tasks/todo-phone-hardening.md` (the
existing `tasks/plan.md` / `tasks/todo.md` belong to the proactive-ULTRON plan
and are left untouched).
