# Phone Bridge Hardening - Task List

## Task 1: Phone tools must pass policy
**Description:** `PolicyEngine` has no `phone_*` entries, so `ToolRouter` denies
every phone call before the handler runs. Add risk entries for all 35 curated
tools and pin the behaviour with tests.
**Acceptance criteria:**
- [x] Every `PhoneTools.Curated` name evaluates to Allow or RequireApproval, never Deny.
- [x] `make_call`, `send_sms`, `push_file`, `selfie_verify` are High + approval.
- [x] `device_info` / `battery` / status reads are Allow.
- [x] A router integration test executes `phone_device_info` with a fake phone.
**Verification:** `dotnet test tools/Tests/Ultron.Tests.csproj --nologo`
**Dependencies:** None
**Files:** `Services/Tools/PolicyEngine.cs`, `tools/Tests/PhonePolicyTests.cs`, `tools/Tests/PhoneRouterIntegrationTests.cs`, `tools/Tests/Ultron.Tests.csproj`
**Scope:** S

## Task 2: Push-file path confinement
**Description:** `phone_push_file` forwards an arbitrary `local` path to `adb push`.
Validate it with the same `IPathPolicy` as file tools and canonicalise before use.
**Acceptance criteria:**
- [x] Paths outside the profile root are denied.
- [x] Missing files return NotFound.
- [x] In-root paths are rewritten to the canonical path before `adb push`.
**Verification:** `dotnet test`; new guard tests.
**Dependencies:** Task 1
**Files:** `Services/Tools/Phone/PhoneToolService.cs` (or new `PhonePushGuard.cs`), `tools/Tests/PhonePushGuardTests.cs`
**Scope:** S

## Task 3: Real adb failure propagation
**Description:** `AdbClient.Run` ignores `ExitCode`, so failed commands with empty
stderr report success. Fail on non-zero exit except shell pipelines.
**Acceptance criteria:**
- [x] Non-zero, non-pipelined command sets `LastError`.
- [x] Pipeline probes keep working when grep finds nothing.
- [x] Test starts a real process that exits non-zero with no output.
**Verification:** `dotnet test`
**Dependencies:** None
**Files:** `Services/AdbClient.cs`, `tools/Tests/PhoneAdbErrorTests.cs`
**Scope:** S

## Task 4: Target the configured device
**Description:** Commands currently go to adb's default device and `Connect()`
accepts any attached device. Pin commands with `-s host:port`; require the
configured transport/serial.
**Acceptance criteria:**
- [x] `shell`/`pull`/`push` include `-s <host:port>`.
- [x] `connect`, `disconnect`, `devices` stay global.
- [x] `IsConnected` checks the configured id and device-list parsing is unit-tested.
**Verification:** `dotnet test`
**Dependencies:** Task 3
**Files:** `Services/AdbClient.cs`, `tools/Tests/PhoneTransportTests.cs`
**Scope:** S

## Task 5: Actionable unconfigured state
**Description:** Backend always declares phone tools; when disabled/unconfigured
the bridge returned no handlers and the model got `UnknownTool`. Register handlers
that return the probe/disabled message.
**Acceptance criteria:**
- [x] Disabled bridge returns "disabled in Settings", not UnknownTool.
- [x] Missing adb returns `PhoneProbe.MissingMessage()`.
**Verification:** `dotnet build`; manual review of `BuildPhoneHandlers`.
**Dependencies:** Task 4
**Files:** `MainWindow.xaml.cs`, `Services/Tools/Phone/PhoneToolService.cs`, `tools/Tests/PhoneToolServiceTests.cs`
**Scope:** S

## Task 6: Unlock PIN wiring
**Description:** `SelfieVerify` hardcodes an empty PIN, so it can never unlock.
Wire `AppSettings.PhonePin` into `AdbClient` and the settings dialog.
**Acceptance criteria:**
- [x] PIN is passed to `AdbClient`; `SelfieVerify` uses it.
- [x] Settings dialog persists `PhonePin`.
- [x] Unlock swipe uses detected screen size.
**Verification:** `dotnet build`; settings round-trip inspection.
**Dependencies:** Task 4
**Files:** `Services/AdbClient.cs`, `MainWindow.xaml.cs`, `Services/AppSettings.cs`
**Scope:** S

## Task 7: Camera package vs intent
**Description:** `CameraPackage` returns an intent action for unknown brands, which
is then fed to `am start -n pkg/.Camera`. Return null for unknown; use the intent
explicitly. Remove the dead `ShutterKeycode` API.
**Acceptance criteria:**
- [x] Unknown brand → `CameraPackage` returns null and `OpenCamera` uses the intent action.
- [x] `ShutterKeycode`/`CameraShutterKeycode` removed; tests updated.
**Verification:** `dotnet test`
**Dependencies:** None
**Files:** `Services/Tools/Phone/PhoneVendors.cs`, `Services/AdbClient.cs`, `tools/Tests/PhoneVendorTests.cs`
**Scope:** S

## Task 8: No stale screenshot success
**Description:** Screenshot/recording reuse fixed temp names and can return an old
file after a failed capture. Use unique names and fail on adb errors.
**Acceptance criteria:**
- [x] Each capture gets a unique temp path.
- [x] Capture/pull errors return a failure sentence.
**Verification:** `dotnet build`; code review.
**Dependencies:** Task 3
**Files:** `Services/AdbClient.cs`
**Scope:** XS

## Task 9: Small correctness fixes
**Description:** `ReadSms` ignores its limit, `ListFiles("")` fails, directory pulls
report failure, and `MakeCall`/`SendSms` claim success unconditionally.
**Acceptance criteria:**
- [x] Limit honoured (clamped 1..100).
- [x] Empty list path defaults to `/sdcard/`.
- [x] Directory pulls return the directory.
- [x] Call/SMS surface adb errors and "am" error text.
**Verification:** `dotnet test`
**Dependencies:** Task 3
**Files:** `Services/AdbClient.cs`
**Scope:** S

## Task 10: Structure + drift guard
**Description:** Move `PhoneToolService` to the phone namespace, reuse `All` in
`Curated`, and add a Python test asserting backend declarations match the C# set.
**Acceptance criteria:**
- [x] Namespace/location consistent; no double `Build()`.
- [x] `python backend/ci.py` fails if the two catalogs drift.
**Verification:** `dotnet test`, `python backend/ci.py`
**Dependencies:** Task 1
**Files:** `Services/PhoneTools.cs`, `Services/Tools/Phone/PhoneToolService.cs`, `backend/tests/test_phone_declarations.py`
**Scope:** S

## Checkpoint: Complete
- [x] `dotnet build -c Debug` succeeds with no new warnings.
- [x] `dotnet test` green.
- [x] `python backend/ci.py` prints ALL CHECKS PASSED.
- [x] Review the diff before any commit (no commit without explicit request).
