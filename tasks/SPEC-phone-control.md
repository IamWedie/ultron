# Spec: ULTRON Phone Control (ADB) — Wire-up + Vendor Awareness

## Objective

Make ULTRON actually able to control the owner's Android phone over ADB, and make it
**know the phone it is talking to** so it uses the correct, vendor-specific operations
(e.g. the Honor camera package) instead of hardcoded assumptions.

Today `Services\AdbClient.cs` (~869 lines, ~100 ops) and `Services\PhoneTools.cs`
(~319 lines, ~122 tool defs) are **dead code**: nothing constructs `AdbClient`, nothing
calls `PhoneTools.All`/`Execute`, no `phone_*` tool is declared to the model, `adb` is
not installed, and there is no UI to configure pairing. The camera code is hardcoded to
`com.hihonor.camera` with blind screen-coordinate taps (`AdbClient.cs:425,436,444,452,459`).

Success looks like: the user configures adb + pairing once; the model can call a curated
set of `phone_*` tools; those tools execute against the real device; and any camera/flash
operation resolves the correct package for the *detected* brand (Honor today, others later).

## Capability Map (Phase 0 — scope check)

This requirement bundles several independently testable capabilities. Proposed modules:

| Module id | Responsibility | Depends on | Ships independently |
|---|---|---|---|
| `phone-adb` | Locate/validate the `adb` binary; run args with real error propagation | — | ✅ |
| `phone-config` | Persist + edit phone settings (address/port/serial/pin/adb path/enabled); pairing test | `phone-adb` | ✅ |
| `phone-detect` | Query device profile (brand/model/Android/resolution/SDK/density); cache it | `phone-adb` | ✅ |
| `phone-vendor` | Map detected profile → vendor ops (camera package, flash action, intent fallbacks) | `phone-detect` | ✅ |
| `phone-tools` | Curated, de-duplicated `phone_*` catalog + `ToolDef`s exposed to the model | `phone-detect`, `phone-vendor` | ✅ |
| `phone-exec` | Execute `phone_*` calls via `AdbClient`; route from `ToolRouter`; typed `ToolResult`s | `phone-tools`, `phone-adb` | ✅ |

**Build order:** `phone-adb` → `phone-config` → `phone-detect` → `phone-vendor` →
`phone-tools` → `phone-exec`.

Each module ships green (tests + build) before the next starts.

## Assumptions I'm making (correct me or I proceed)

1. **`adb` will be installed by the user** (platform-tools on PATH, or path set in Settings).
   We do not bundle or auto-download adb in this spec. `phone-adb` must locate it from
   settings / PATH / common locations and report clearly if missing.
2. **Wireless debugging** over `host:port` (VPN address or LAN) with optional `serial`
   verification, matching the existing `AppSettings` fields. USB serial pairing is out of scope.
3. **Curated tool exposure**: we do NOT send all ~122 phone tools to Gemini (context bloat,
   large risk surface). We expose a curated subset (~25–35) covering device info, screen,
   input, apps, camera, files, calls/SMS, and common settings toggles. Full catalog stays
   available internally for future expansion.
4. **The backend's `TOOL_DECLARATIONS` in `backend\gemini_backend.py` is the single source
   of model-visible tools.** Phone tools not handled by the backend fall through its `else`
   branch and arrive in C# as `tool_call` events (existing mechanism).
5. **The duplicate-name fix ships first**, as a tiny standalone change inside `phone-tools`.
6. **Risky/destructive phone ops stay gated** by the existing `PolicyEngine` approval flow
   (calls, SMS, deletes, unlock).

## The immediate bug (part of `phone-tools`)

`Services\PhoneTools.cs` declares these twice:
- `phone_screen_on` at lines 44 and 47
- `phone_screen_off` at lines 45 and 48

Fix: keep one declaration each. Also remove the redundant `flight_finder` dispatch branch
(`gemini_backend.py:1923`) which duplicates `:1905` — verify-only, no behavior change.

## Tech Stack

- **Core:** .NET 10 / WinUI 3 (`net10.0-windows10.0.19041.0`, win-x64, C#).
- **Backend:** existing Python 3.12 Gemini Live process; IPC JSON-lines over stdio.
- **Phone bridge:** `adb` CLI invoked from C# `AdbClient` (no new NuGet dependency).
- **Tests:** xUnit (`tools\Tests\Ultron.Tests.csproj`), SUT sources compiled WinUI-free.

## Commands

- **Build:** `dotnet build -c Debug` (from `ultron_winui/`; stop a running ULTRON first, else MSB3021).
- **C# tests:** `dotnet test tools/Tests/Ultron.Tests.csproj --nologo`
- **Backend checks:** `python backend/ci.py` (expects `ALL CHECKS PASSED`).

## Project Structure

```
ultron_winui/
├── Services/
│   ├── AdbClient.cs          # hardened: error propagation, no vendor hardcoding
│   ├── PhoneTools.cs         # de-duplicated; curated set exposed (Build/Execute)
│   ├── AdbShell.cs           # arg quoting (existing, tested)
│   ├── AppSettings.cs        # +PhoneAdbPath, +PhoneEnabled (existing phone fields)
│   └── Tools/Phone/
│       ├── PhoneToolService.cs   # IToolHandler: name → AdbClient call
│       ├── PhoneProbe.cs         # adb discovery + availability ("phone-adb")
│       ├── DeviceProfile.cs      # brand/model/sdk/resolution ("phone-detect")
│       └── PhoneVendors.cs       # brand → camera/flash intents ("phone-vendor")
├── MainWindow.xaml.cs        # +PhoneToolService in BuildToolRouter; settings section
├── backend/gemini_backend.py # +curated phone_* declarations
└── tools/Tests/              # new: PhoneProbeTests, DeviceProfileTests, PhoneVendorTests,
                              #      PhoneToolsCatalogTests, PhoneToolServiceTests
```

## Code Style

- C#: nullable enabled, WinUI conventions, xUnit theories, no WinUI types in the test project.
- Pure logic (profile parsing, vendor mapping, catalog) lives in WinUI-free classes so tests
  compile them directly, mirroring `Services\CallStateRules.cs`.
- No hardcoded brand strings in `AdbClient`; vendor decisions belong in `PhoneVendors`.
- Errors are returned as typed `ToolResult` (never silently swallowed into `""`).

## Testing Strategy

- **xUnit unit tests** for every pure piece:
  - `PhoneProbeTests` — resolves adb from settings → PATH → common dirs; missing → clear message.
  - `DeviceProfileTests` — parses `getprop`/`wm` fixtures into brand/model/sdk/resolution.
  - `PhoneVendorTests` — Honor → `com.hihonor.camera` + flash broadcast; Samsung/Google/Pixel/
    Xiaomi/unknown → correct package or generic intent fallback.
  - `PhoneToolsCatalogTests` — no duplicate names; curated set is a subset of the full catalog.
  - `PhoneToolServiceTests` — routes names to a fake `IAdbClient`; unknown name → `Unsupported`.
- **Integration:** facade test that an unreachable adb returns a clear `ToolResult` failure,
  not an empty success (`BackendIntegrationTests`-style, auto-skip when adb absent).
- End-to-end (manual): configure adb + pairing → "Ultron, take a selfie" → photo pulled.

## Boundaries

- **Always:** run `dotnet test` + `python backend/ci.py` before commits; keep phone disabled
  by default; keep risky ops behind `PolicyEngine`; propagate real errors.
- **Ask first:** adding a NuGet package; bundling/downloading adb; changing IPC message schema;
  exposing more than the curated tool set to the model.
- **Never:** commit adb binaries, serials, PINs, or IPs; hardcode a brand in `AdbClient`;
  auto-enable phone control; run destructive phone ops without approval.

## Success Criteria

1. `PhoneTools` contains no duplicate tool names; catalog test asserts uniqueness.
2. `AdbClient` performs no run that hides a failure — every exec path surfaces stderr/exit.
3. No brand literal (e.g. `hihonor`) remains in `AdbClient`; all vendor branching is in
   `PhoneVendors`, unit-tested for Honor + at least two other brands + unknown.
4. `phone-adb` reports a clear, actionable message when adb is missing.
5. A curated `phone_*` set is declared to the model and routed to `PhoneToolService`.
6. Settings exposes phone config (address/port/serial/pin/adb path/enable + "Test connection").
7. All C# tests pass; `python backend/ci.py` passes; app builds with phone disabled by default.

## Decisions (user-confirmed)

1. **adb install:** NOT installed. Add a Settings path field for `adb.exe`; discovery order is
   settings path → PATH → common dirs; show a clear install hint when missing. No bundling.
2. **Pairing transport:** **Wireless only** (`address:port`, Wi-Fi/VPN). USB serial out of scope.
3. **Curated set:** expose a curated ~25–35 `phone_*` tools (not all ~122), covering
   info / screen / input / apps / camera / files / calls+SMS / toggles.
4. **Brands:** map **Honor** (`com.hihonor.camera` + flash broadcast) **plus common brands**
   (Samsung, Pixel/Google, Xiaomi, OnePlus), with a generic camera-intent fallback for unknown.
