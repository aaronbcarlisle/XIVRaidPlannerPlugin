# XIVRaidPlannerPlugin Modernization — Design

**Date:** 2026-05-27
**Status:** Approved (pending spec review)
**Branch:** `chore/modernization-audit`
**Repo:** XIVRaidPlannerPlugin (companion PR in ffxiv-raid-planner)

## Summary

The Dalamud plugin has been idle since 2026-03-10 (v0.2.0). This is a full
modernization pass: restructure the codebase, bring it current with the latest
Dalamud SDK, stand up tests and CI quality gates, and add a browser-based
sign-in flow to remove the manual API-key setup friction.

**The plugin is not yet used by anyone.** There are therefore no
backwards-compatibility, config-migration, or regression constraints — we
refactor aggressively, with no compat shims and no dead-code retention.

## Goals

1. **Standardize + refactor** — break up the `Plugin.cs` god object, unify
   dependency injection, centralize colors/threading, add style + analyzer
   enforcement.
2. **Currency** — upgrade to the latest stable `Dalamud.NET.Sdk` (15.0.0) and
   adapt to API changes.
3. **Quality bar** — add a unit-test project for pure logic and harden CI.
4. **Browser sign-in** — replace mandatory API-key copy/paste with a one-click
   loopback OAuth flow that mints the existing `xrp_` API key.

## Non-Goals

- New end-user features beyond browser sign-in.
- Backwards compatibility / config migration (no users to protect).
- In-game behavior validation by the author — only the user can run FFXIV +
  Dalamud. The author's gate is a clean Release build + green unit tests after
  every commit; the user runs the in-game smoke test before merge.

## Constraints & Environment

- Author can build locally: `dotnet` 10.0.108 installed; dev Dalamud present at
  the XIVLauncher path (`Dalamud.NET.Sdk` auto-detects it when `DALAMUD_HOME` is
  unset). Clean Release build is the per-commit gate.
- Latest stable SDK is **Dalamud.NET.Sdk 15.0.0** (released 2026-04-29), a major
  bump from the pinned 14.0.1 — expect API-level changes (Lumina sheet access,
  ImGui bindings, service interfaces).
- **Git rule (absolute):** NEVER add AI attribution to commits or PRs.

---

## Current State (Audit Findings)

~5,670 lines of C# across `Api/`, `Services/`, `Windows/`, plus `Plugin.cs`,
`Configuration.cs`, `GameConstants.cs`.

**Structural / standardization**
- `Plugin.cs` is a 917-line god object: it orchestrates *and* owns heavy
  business logic — gear-sync diffing, loot/purchase/material logging, slot→floor
  mapping, leave-warning coordination.
- Inconsistent dependency access: Dalamud services are static `[PluginService]`
  on `Plugin`, injected into *some* services via constructor, but Windows reach
  back into `Plugin.DataManager` / `Plugin.Framework` statics directly.
- Repeated threading boilerplate: `Task.Run(async () => { … RunOnFrameworkThread(…) })`
  appears ~16× in `Plugin.cs` + ~5× in `ConfigWindow`, with no shared helper or
  centralized exception handling.
- Color/style duplication: role colors and `Vector4`s hardcoded across
  `GameConstants` + 4 windows.

**Tooling / quality**
- Zero tests — no test project at all, despite testable pure logic.
- No `.editorconfig` / `Directory.Build.props`; CI only runs `dotnet build`.
- `RaidPlannerClient` swallows all errors → returns `null`/`false`; callers
  can't distinguish auth failure / network / 404. No timeout or cancellation.

**Currency / docs**
- SDK pinned at 14.0.1 while CI pulls Dalamud `latest.zip` — drift risk.
- `HANDOFF.md` is badly stale (claims the plugin "has not been compiled or
  tested yet"; it has shipped v0.2.0 with 4 feature PRs).

---

## Target Architecture (Approach A: service extraction + constructor DI)

`Plugin.cs` becomes a thin composition root: declare `[PluginService]`s,
construct the service/window graph, wire events, dispose. **Every Window and
Service receives its dependencies via constructor** — no `Plugin.X` static
reach-ins from outside `Plugin.cs`.

### Services

| Service | Responsibility | Testable core |
|---------|----------------|---------------|
| `RaidSessionService` *(new)* | Session hub: owns `_cachedPriority` + `_autoDetectedTier`; reacts to savage enter/exit; coordinates priority fetch → party match → BiS fetch; overlay-timing decisions (`DutyComplete`/`NeedGreed`). | Floor-name resolution, show/hide decisions |
| `GearSyncService` *(new)* | The ~120-line `SyncGear` logic: equipped-vs-API diff, tome-weapon detection, change counting, new-acquisition detection. | Diff + change-count + acquisition detection (pure) |
| `LootLogCoordinator` *(new)* | Routes loot/purchase/manual/confirm events to the API per `AutoLogMode`; builds log requests; owns `BuildSlotToFloorMapping`. | Slot→floor mapping; request building (pure) |
| `LeaveWarningService` *(expanded)* | Absorbs its own `SelectYesno` addon wiring + decision. | `CheckLeaveWarning` decision (pure) |
| Existing: `TerritoryService`, `PartyMatchingService`, `LootDetectionService`, `ItemMappingService`, `BiSDataService`, `InventoryService`, `AddonHighlightService` | Unchanged responsibility; converted to constructor injection where they currently use statics. | Party-name normalization (extracted off `IPartyList`) |

### Shared helpers (static, single source of truth)

- **`Theme`** — role colors + shared `Vector4`s; replaces hardcoded colors in
  `GameConstants` + 4 windows. Mirrors the web app's semantic-token approach.
- **`PluginThread`** — `RunBackground(Func<Task>)` / `RunOnUi(Action)` wrapping
  the `Task.Run` / `Framework.RunOnFrameworkThread` pattern (~21 sites) with
  centralized exception logging.

---

## SDK Currency + Build Standardization

- Bump `Dalamud.NET.Sdk` 14.0.1 → **15.0.0**; fix everything the build and
  analyzers flag (Lumina sheet access, ImGui bindings, service interfaces);
  regenerate `packages.lock.json`.
- Add **`Directory.Build.props`**: `Nullable=enable`, `EnableNETAnalyzers=true`,
  `AnalysisLevel=latest`, warnings-as-errors (start with `nullable` + analyzer
  IDs, widen once clean).
- Add **`.editorconfig`** codifying existing style: naming, `using` ordering,
  file-scoped namespaces, `var` usage.
- Confirm the SDK 15 target framework (likely `net9.0-windows`) and align the
  test project + CI `setup-dotnet` version to it.

---

## API Client Hardening (`RaidPlannerClient`)

- Replace blanket `null`/`false` swallowing with a small result type
  distinguishing auth-failure / network / not-found / server-error, so callers
  (and chat messages) can be specific.
- Add a request timeout and thread a `CancellationToken` through the async calls.
- Keep the existing endpoint surface and `Bearer xrp_` auth header.

---

## Testing

New project **`XIVRaidPlannerPlugin.Tests`** (xUnit). Scope = **pure logic with
no Dalamud/ImGui dependency** (the honestly testable surface):

- Gear-diff / change-counting and new-acquisition detection (`GearSyncService`).
- `BuildSlotToFloorMapping` (`LootLogCoordinator`).
- `LeaveWarningService.CheckLeaveWarning` decision logic.
- `Configuration` URL resolution (`EffectiveApiBaseUrl` / `EffectiveFrontendBaseUrl`).
- Party-name normalization / matching (extracted off `IPartyList`).
- `RaidPlannerClient` URL building + DTO (de)serialization via a fake
  `HttpMessageHandler`.
- Browser-auth PKCE: `code_challenge = BASE64URL(SHA256(code_verifier))` and
  `state` validation (pure).

Pure logic that currently depends on Dalamud services is extracted into plain
classes/methods so the test project can exercise it without the Dalamud runtime.

---

## Browser Sign-In (loopback OAuth → mints `xrp_` key)

Goal: remove the manual API-key copy/paste that deters setup. Reuses the
existing `api_keys` infrastructure end-to-end — nothing in the plugin's request
layer changes; we only replace *how the key gets into config*.

### Flow

1. Plugin generates `state` (CSRF) + PKCE `code_verifier` / `code_challenge`
   (S256), starts an ephemeral loopback `HttpListener` on an OS-assigned
   `127.0.0.1` port.
2. Plugin opens the browser to
   `{frontend}/plugin-auth?redirect_uri=http://127.0.0.1:{port}/callback&state=…&code_challenge=…&code_challenge_method=S256`.
3. User is already logged in via Discord (or logs in once), sees an "Authorize
   the XIV Raid Planner plugin" consent screen, clicks Approve.
4. Web app 302-redirects to the loopback `redirect_uri` with `?code=…&state=…`.
5. Plugin validates `state`, serves a "you can return to the game" page, stops
   the listener, then POSTs `code` + `code_verifier` to the backend exchange
   endpoint and receives an `xrp_` key.
6. Plugin stores the key in `Configuration.ApiKey`, refreshes client auth, runs
   the existing connection test, populates the static/tier pickers.

### Plugin side — new `BrowserAuthService`

- Owns listener lifecycle, PKCE/state generation, browser launch (Dalamud
  `Util.OpenLink`), callback await with timeout (~120s) + cancellation, and the
  code→key exchange.
- Graceful handling of: user-cancel, timeout, port-in-use, browser-won't-open.
- `ConfigWindow`: **"Sign in with browser"** is the primary action; manual key
  paste moves under an **Advanced** expander (kept for custom/self-hosted URLs
  and headless setups).

### Web app companion PR (`ffxiv-raid-planner`) — prerequisite, ships first

- **Frontend** `/plugin-auth` consent route: requires existing Discord session
  (if logged out, route through normal login then return). On approve → 302 to
  the loopback `redirect_uri` with `code` + `state`.
- **Backend** — extend the existing `api_keys` router:
  - `POST …/plugin-auth/authorize` — authenticated (cookie/JWT). Validates
    `redirect_uri` is loopback (`127.0.0.1`/`localhost` only), stores a
    single-use, ~5-min, PKCE-bound authorization record, returns the `code`.
  - `POST …/plugin-auth/exchange` — unauthenticated; proves possession via PKCE.
    Body `{ code, code_verifier }`. Verifies the code exists / unexpired /
    unused and that `SHA256(code_verifier) == code_challenge`, marks it used,
    mints a labeled `xrp_` API key for that user, returns `{ apiKey }`.
- **Security:** PKCE S256; single-use 5-min code; loopback-only `redirect_uri`
  allowlist; `state` echoed for plugin-side CSRF check; minted key is a normal
  scoped API key, revocable from the existing API-keys UI. Loopback `http` is
  acceptable for the exchange because it never leaves `127.0.0.1`.

> This side may warrant its own short spec/plan when implemented; it is captured
> here because it is tightly coupled to the plugin feature.

---

## Documentation

- Replace the stale `HANDOFF.md` with a current `ARCHITECTURE.md` (or fold into
  README); refresh README build/test/run instructions and the browser sign-in
  setup.
- Update the **workspace `CLAUDE.md`** plugin architecture section to match the
  new service layout.

---

## Delivery

### Web app PR first (separate repo, functional prerequisite)
`authorize` + `exchange` endpoints + `/plugin-auth` consent page. Must be
deployed before the plugin's browser sign-in works end-to-end.

### Plugin single PR (`chore/modernization-audit`) — staged commits
1. SDK 15 bump → green Release build (currency baseline).
2. `.editorconfig` + `Directory.Build.props` + fix surfaced warnings.
3. Architecture refactor (services + constructor DI + `Theme` / `PluginThread`).
4. API client hardening (result type + timeout/cancellation).
5. Browser-auth service + `ConfigWindow` rework (button primary, manual under Advanced).
6. Test project + tests (incl. PKCE/state pure logic).
7. CI update (`setup-dotnet` version, `dotnet test`, `dotnet format --verify-no-changes`; keep artifact upload).
8. Docs refresh (README + replace `HANDOFF.md`; update workspace `CLAUDE.md`).

Clean Release build verified after each commit; full unit suite green before the
PR. The PR body includes an **in-game smoke-test checklist** (overlay, gear sync,
loot logging, leave warning, BiS highlighting, browser sign-in) for the user to
run, since in-game behavior can't be validated by the author.

## Risks

- **SDK 14→15 is a major bump.** API breaks in Lumina / ImGui / service
  interfaces are likely; some may change in-game behavior only the user can
  catch. Mitigation: the user runs the smoke-test checklist before merge.
- **Browser sign-in spans two repos.** The plugin feature is non-functional
  until the web app PR is deployed. Mitigation: web app PR ships first; the
  plugin auth commit can be built/tested against a local backend.
- **Loopback listener edge cases** (port-in-use, blocked browser launch).
  Mitigation: explicit error handling + the retained manual-key Advanced path.
