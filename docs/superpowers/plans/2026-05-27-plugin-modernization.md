# Plugin Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Modernize the XIVRaidPlannerPlugin — restructure `Plugin.cs` into focused services with constructor DI, upgrade to Dalamud SDK 15, add a unit-test project and CI gates, and add a one-click browser sign-in that mints the existing `xrp_` API key.

**Architecture:** `Plugin.cs` becomes a thin composition root. Business logic moves into `RaidSessionService`, `GearSyncService`, `LootLogCoordinator`, and an expanded `LeaveWarningService`. Pure logic is extracted off Dalamud APIs so it can be unit-tested. Two static helpers (`Theme`, `PluginThread`) become single sources of truth for colors and background/UI threading. Browser sign-in is a loopback OAuth flow (`BrowserAuthService`) that exchanges a PKCE code for an `xrp_` key.

**Tech Stack:** C# / .NET 9 (via Dalamud.NET.Sdk 15.0.0) / ImGui.NET / xUnit.

---

## IMPORTANT EXECUTION NOTES

- **Git rule (absolute, non-negotiable):** NEVER add AI attribution to commits or PRs. No "Co-Authored-By", no "Generated with Claude Code", nothing.
- **Branch:** All work lands on `chore/modernization-audit` (already created) as staged commits → one PR.
- **Per-commit gate:** After every task, `dotnet build --configuration Release` MUST be clean and `dotnet test` MUST be green. The author cannot run FFXIV/Dalamud — in-game behavior is validated by the user before merge (smoke-test checklist in Task 20).
- **SDK-dependency caveat:** Task 1 (SDK 14→15) is a discovery task — it establishes the real SDK-15 API surface. Code in later tasks that touches Dalamud APIs (ImGui calls, Lumina sheets, `IPartyList`, FFXIVClientStructs) is written against the *current* (SDK 14) patterns in the repo; if Task 1 changed a signature, adjust these to match the post-Task-1 baseline. Pure-logic tasks (slot→floor, gear-diff math, party matcher, PKCE, config URLs, `ApiResult`, `Theme`) are SDK-independent and stable.
- **Out of scope for this plan:** The `ffxiv-raid-planner` web app companion endpoints (`/plugin-auth` consent page, `authorize`/`exchange`). Those ship in a separate PR in that repo and get their own plan. This plan builds and tests the plugin side against a local backend; Task 16 notes the contract it expects.

---

## File Structure

**Created:**
- `.editorconfig` — C# style rules (repo root)
- `Directory.Build.props` — nullable, analyzers, warnings-as-errors (repo root)
- `XIVRaidPlannerPlugin/Theme.cs` — semantic + role colors (single source of truth)
- `XIVRaidPlannerPlugin/PluginThread.cs` — `RunBackground`/`RunOnUi` helpers
- `XIVRaidPlannerPlugin/Api/ApiResult.cs` — typed API outcome
- `XIVRaidPlannerPlugin/Services/RaidSessionService.cs` — session hub
- `XIVRaidPlannerPlugin/Services/GearSyncService.cs` — gear sync/diff logic
- `XIVRaidPlannerPlugin/Services/LootLogCoordinator.cs` — loot routing + slot→floor
- `XIVRaidPlannerPlugin/Services/PartyMatcher.cs` — pure matching algorithm
- `XIVRaidPlannerPlugin/Services/ItemSourceClassifier.cs` — pure name/iLv classification
- `XIVRaidPlannerPlugin/Auth/PkceCodes.cs` — PKCE/state generation (pure)
- `XIVRaidPlannerPlugin/Auth/BrowserAuthService.cs` — loopback OAuth flow
- `XIVRaidPlannerPlugin.Tests/XIVRaidPlannerPlugin.Tests.csproj` — xUnit project
- `XIVRaidPlannerPlugin.Tests/*` — test files (one per pure unit)
- `ARCHITECTURE.md` — replaces stale `HANDOFF.md`

**Modified:**
- `XIVRaidPlannerPlugin/XIVRaidPlannerPlugin.csproj` — SDK bump
- `XIVRaidPlannerPlugin/Plugin.cs` — slimmed to composition root
- `XIVRaidPlannerPlugin/Services/InventoryService.cs` — delegate classification to `ItemSourceClassifier`
- `XIVRaidPlannerPlugin/Services/PartyMatchingService.cs` — delegate to `PartyMatcher`
- `XIVRaidPlannerPlugin/Services/LeaveWarningService.cs` — own its `SelectYesno` wiring
- `XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs` — return `ApiResult`, timeout/cancellation
- `XIVRaidPlannerPlugin/Windows/*.cs` — constructor injection; use `Theme`
- `XIVRaidPlannerPlugin/Windows/ConfigWindow.cs` — browser sign-in button + Advanced expander
- `.github/workflows/ci.yml` — dotnet version, test step, format check
- `README.md` — refreshed
- `D:\FFXIV\Dev\xrp-dev\CLAUDE.md` — updated plugin architecture section

---

## Phase 1 — SDK 15 Baseline

### Task 1: Upgrade Dalamud.NET.Sdk 14.0.1 → 15.0.0

**Files:**
- Modify: `XIVRaidPlannerPlugin/XIVRaidPlannerPlugin.csproj:2`
- Regenerate: `XIVRaidPlannerPlugin/packages.lock.json`

This is a build-fix discovery task: bump, build, fix each error against the Dalamud 15 API, repeat until clean. There is no pre-written code because the breakages are unknown until the build runs.

- [ ] **Step 1: Bump the SDK version**

In `XIVRaidPlannerPlugin.csproj`, change line 2:
```xml
<Project Sdk="Dalamud.NET.Sdk/15.0.0">
```

- [ ] **Step 2: Restore and observe**

Run: `dotnet restore --force-evaluate`
Expected: regenerates `packages.lock.json` for the new SDK.

- [ ] **Step 3: Build and capture errors**

Run: `dotnet build --configuration Release 2>&1 | tee /tmp/sdk15-build.log`
Expected: likely FAILS. Common break areas to triage: Lumina `GetExcelSheet`/`GetRowOrDefault` signatures (`InventoryService`, `BiSViewerWindow`, `ItemMappingService`, `TerritoryService`), ImGui.NET signature changes (all `Windows/*`), `IPartyList`/`IPlayerState`/`IClientState` members, FFXIVClientStructs `InventoryManager`/`InventoryItem` layout.

- [ ] **Step 4: Fix each error against Dalamud 15**

For each compiler error, consult the current API (the dev Dalamud assemblies at `$HOME/AppData/Roaming/XIVLauncher/addon/Hooks/dev/`, the Dalamud repo, or `goatcorp.github.io/Dalamud`) and apply the minimal fix. Do NOT change behavior — only adapt to new signatures. Rebuild after each fix.

- [ ] **Step 5: Confirm clean build**

Run: `dotnet build --configuration Release`
Expected: `Build succeeded. 0 Error(s)`. Note any new warnings for Task 3.

- [ ] **Step 6: Commit**

```bash
git add XIVRaidPlannerPlugin/XIVRaidPlannerPlugin.csproj XIVRaidPlannerPlugin/packages.lock.json XIVRaidPlannerPlugin/
git commit -m "build: upgrade to Dalamud.NET.Sdk 15.0.0"
```

---

## Phase 2 — Build Standardization

### Task 2: Add `.editorconfig`

**Files:**
- Create: `.editorconfig` (repo root)

- [ ] **Step 1: Write the file**

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space

[*.cs]
indent_size = 4
# Namespaces & usings
csharp_style_namespace_declarations = file_scoped:warning
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false
# var usage (matches existing codebase style)
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_elsewhere = true:suggestion
# Expression-bodied members
csharp_style_expression_bodied_methods = when_on_single_line:suggestion
# Naming: private fields _camelCase
dotnet_naming_rule.private_fields_underscore.severity = warning
dotnet_naming_rule.private_fields_underscore.symbols = private_fields
dotnet_naming_rule.private_fields_underscore.style = underscore_prefix
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private
dotnet_naming_style.underscore_prefix.required_prefix = _
dotnet_naming_style.underscore_prefix.capitalization = camel_case
# Quality nudges
dotnet_diagnostic.CA1822.severity = none
dotnet_diagnostic.IDE0058.severity = none

[*.{json,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false
```

- [ ] **Step 2: Verify build still clean**

Run: `dotnet build --configuration Release`
Expected: `Build succeeded` (editorconfig severities don't fail the build yet — Task 3 adds enforcement).

- [ ] **Step 3: Commit**

```bash
git add .editorconfig
git commit -m "chore: add .editorconfig codifying C# style"
```

### Task 3: Add `Directory.Build.props` and fix surfaced warnings

**Files:**
- Create: `Directory.Build.props` (repo root)

- [ ] **Step 1: Write the file**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <!-- Start strict on nullability + analyzer correctness; widen once clean. -->
    <WarningsAsErrors>nullable</WarningsAsErrors>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Build and capture warnings**

Run: `dotnet build --configuration Release 2>&1 | tee /tmp/analyzer-warnings.log`
Expected: nullable warnings now ERROR; analyzer suggestions appear as warnings.

- [ ] **Step 3: Fix nullable errors**

For each `CS86xx` nullable error, apply the minimal correct fix (add `?`, guard a null, or annotate). Do not suppress with `!` unless the invariant is genuinely guaranteed and obvious.

- [ ] **Step 4: Confirm clean**

Run: `dotnet build --configuration Release`
Expected: `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props XIVRaidPlannerPlugin/
git commit -m "chore: enable analyzers and nullable-as-error; fix surfaced nullable warnings"
```

### Task 4: Scaffold the xUnit test project

**Files:**
- Create: `XIVRaidPlannerPlugin.Tests/XIVRaidPlannerPlugin.Tests.csproj`
- Create: `XIVRaidPlannerPlugin.Tests/SmokeTest.cs`

The test project is a plain `net9.0` library (NOT a Dalamud plugin) referencing the plugin project. It only exercises Dalamud-free code. Set `<TargetFramework>` to match the SDK-15 plugin target confirmed in Task 1 (verify with `dotnet build` output; likely `net9.0-windows`).

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <!-- Tests intentionally exercise pure logic only; relax plugin-wide strictness. -->
    <WarningsAsErrors></WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\XIVRaidPlannerPlugin\XIVRaidPlannerPlugin.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write a smoke test**

```csharp
namespace XIVRaidPlannerPlugin.Tests;

public class SmokeTest
{
    [Fact]
    public void TestProjectRuns()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Run the test**

Run: `dotnet test XIVRaidPlannerPlugin.Tests/XIVRaidPlannerPlugin.Tests.csproj`
Expected: 1 passed. If the project reference fails because the plugin targets a windows TFM the test SDK can't load, align `<TargetFramework>` to the plugin's exact target from Task 1.

- [ ] **Step 4: Commit**

```bash
git add XIVRaidPlannerPlugin.Tests/
git commit -m "test: scaffold xUnit test project"
```

---

## Phase 3 — Shared Helpers

### Task 5: Add `Theme` color source of truth

**Files:**
- Create: `XIVRaidPlannerPlugin/Theme.cs`
- Test: `XIVRaidPlannerPlugin.Tests/ThemeTests.cs`

Consolidate role colors (currently `GameConstants.RoleColors`) plus the semantic colors duplicated across windows (status yellow `(1,1,0,1)`, success green `(0,1,0,1)`, error red `(1,0.3,0.3,1)`, accent teal `(0.298,0.722,0.659,1)`, muted gray `(0.6,0.6,0.6,1)`, floor colors from `PriorityOverlayWindow`).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
namespace XIVRaidPlannerPlugin.Tests;

public class ThemeTests
{
    [Fact]
    public void RoleColor_KnownRole_ReturnsExpected()
    {
        Assert.Equal(new Vector4(0.353f, 0.624f, 0.831f, 1f), Theme.RoleColor("tank"));
    }

    [Fact]
    public void RoleColor_UnknownRole_ReturnsWhite()
    {
        Assert.Equal(new Vector4(1, 1, 1, 1), Theme.RoleColor("nonsense"));
    }

    [Fact]
    public void FloorColor_OutOfRange_ReturnsMuted()
    {
        Assert.Equal(Theme.Muted, Theme.FloorColor(99));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter ThemeTests`
Expected: FAIL — `Theme` does not exist.

- [ ] **Step 3: Write `Theme.cs`**

```csharp
using System.Collections.Generic;
using System.Numerics;

namespace XIVRaidPlannerPlugin;

/// <summary>Single source of truth for plugin colors. Mirrors the web app design tokens.</summary>
public static class Theme
{
    // Role colors (web app design system)
    public static readonly Vector4 Tank = new(0.353f, 0.624f, 0.831f, 1f);
    public static readonly Vector4 Healer = new(0.353f, 0.831f, 0.565f, 1f);
    public static readonly Vector4 Melee = new(0.831f, 0.353f, 0.353f, 1f);
    public static readonly Vector4 Ranged = new(0.831f, 0.627f, 0.353f, 1f);
    public static readonly Vector4 Caster = new(0.706f, 0.353f, 0.831f, 1f);

    // Semantic colors
    public static readonly Vector4 Accent = new(0.298f, 0.722f, 0.659f, 1f);
    public static readonly Vector4 Success = new(0.133f, 0.773f, 0.369f, 1f);
    public static readonly Vector4 Warning = new(1f, 1f, 0f, 1f);
    public static readonly Vector4 Error = new(1f, 0.3f, 0.3f, 1f);
    public static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    private static readonly Dictionary<string, Vector4> Roles = new()
    {
        ["tank"] = Tank, ["healer"] = Healer, ["melee"] = Melee,
        ["ranged"] = Ranged, ["caster"] = Caster,
    };

    // Floor accent colors (1-4)
    private static readonly Dictionary<int, Vector4> Floors = new()
    {
        [1] = new(0.133f, 0.773f, 0.369f, 1f),
        [2] = new(0.231f, 0.510f, 0.965f, 1f),
        [3] = new(0.659f, 0.333f, 0.969f, 1f),
        [4] = new(0.961f, 0.620f, 0.043f, 1f),
    };

    public static Vector4 RoleColor(string role) => Roles.GetValueOrDefault(role, White);
    public static Vector4 FloorColor(int floor) => Floors.GetValueOrDefault(floor, Muted);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ThemeTests`
Expected: PASS.

- [ ] **Step 5: Migrate windows to `Theme`**

In each `Windows/*.cs` file, replace hardcoded `new Vector4(...)` semantic/role/floor literals (see grep list in spec) with the `Theme.*` equivalents and the `RoleColors` dictionary references with `Theme.RoleColor(...)`. Remove the now-unused `GameConstants.RoleColors` and any window-local color constants that `Theme` now owns. Leave purely cosmetic one-off literals (e.g. window-bg alpha tints) in place if they have no semantic name.

- [ ] **Step 6: Build and test**

Run: `dotnet build --configuration Release && dotnet test`
Expected: clean build, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add XIVRaidPlannerPlugin/Theme.cs XIVRaidPlannerPlugin.Tests/ThemeTests.cs XIVRaidPlannerPlugin/Windows/ XIVRaidPlannerPlugin/GameConstants.cs
git commit -m "refactor: centralize colors in Theme"
```

### Task 6: Add `PluginThread` helper

**Files:**
- Create: `XIVRaidPlannerPlugin/PluginThread.cs`

`PluginThread` wraps the `Task.Run` + `Framework.RunOnFrameworkThread` pattern with centralized exception logging. It needs `IFramework` and `IPluginLog`, injected once at construction in `Plugin.cs`. Not unit-tested (it is a thin Dalamud wrapper); its callers' logic is tested elsewhere.

- [ ] **Step 1: Write `PluginThread.cs`**

```csharp
using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin;

/// <summary>Centralizes background/UI thread marshaling with exception logging.</summary>
public sealed class PluginThread
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public PluginThread(IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;
    }

    /// <summary>Run async work off the game thread; logs unhandled exceptions.</summary>
    public void RunBackground(Func<Task> work)
    {
        Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { _log.Error($"[Background] {ex}"); }
        });
    }

    /// <summary>Marshal an action onto the framework (game) thread.</summary>
    public void RunOnUi(Action action) => _framework.RunOnFrameworkThread(action);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build --configuration Release`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add XIVRaidPlannerPlugin/PluginThread.cs
git commit -m "feat: add PluginThread background/UI helper"
```

> Note: `PluginThread` is wired into `Plugin.cs` and adopted by callers in Task 13 (when `Plugin.cs` is slimmed) and Task 16/17 (browser auth). Do not rewrite all 21 call sites here.

---

## Phase 4 — API Client Hardening

### Task 7: Add `ApiResult` type

**Files:**
- Create: `XIVRaidPlannerPlugin/Api/ApiResult.cs`
- Test: `XIVRaidPlannerPlugin.Tests/ApiResultTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using XIVRaidPlannerPlugin.Api;
namespace XIVRaidPlannerPlugin.Tests;

public class ApiResultTests
{
    [Fact]
    public void Ok_IsSuccess_WithValue()
    {
        var r = ApiResult<int>.Ok(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
        Assert.Equal(ApiError.None, r.Error);
    }

    [Fact]
    public void Fail_IsNotSuccess_WithError()
    {
        var r = ApiResult<int>.Fail(ApiError.Unauthorized);
        Assert.False(r.IsSuccess);
        Assert.Equal(ApiError.Unauthorized, r.Error);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter ApiResultTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Write `ApiResult.cs`**

```csharp
namespace XIVRaidPlannerPlugin.Api;

public enum ApiError
{
    None,
    Unauthorized,   // 401/403 — bad/expired key
    NotFound,       // 404
    Network,        // connection/timeout/DNS
    Server,         // 5xx
    Unknown,
}

/// <summary>Outcome of an API call: a value on success, or a categorized error.</summary>
public readonly struct ApiResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public ApiError Error { get; }

    private ApiResult(bool ok, T? value, ApiError error)
    {
        IsSuccess = ok; Value = value; Error = error;
    }

    public static ApiResult<T> Ok(T value) => new(true, value, ApiError.None);
    public static ApiResult<T> Fail(ApiError error) => new(false, default, error);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ApiResultTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add XIVRaidPlannerPlugin/Api/ApiResult.cs XIVRaidPlannerPlugin.Tests/ApiResultTests.cs
git commit -m "feat: add ApiResult type for categorized API outcomes"
```

### Task 8: Refactor `RaidPlannerClient` to return `ApiResult` with timeout + cancellation

**Files:**
- Modify: `XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs`
- Modify: callers in `Plugin.cs` / services (compile-driven)
- Test: `XIVRaidPlannerPlugin.Tests/RaidPlannerClientTests.cs`

Map HTTP status → `ApiError` in the private helpers; set `HttpClient.Timeout`; accept an optional `CancellationToken`. Keep public method shapes but change return types from `T?`/`bool` to `ApiResult<T>`/`ApiResult<bool>`. The `error` lets `ConfigWindow`/chat messages be specific (e.g. "API key rejected" vs "Couldn't reach the server").

- [ ] **Step 1: Write the failing test (status → ApiError mapping via fake handler)**

```csharp
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using XIVRaidPlannerPlugin.Api;
namespace XIVRaidPlannerPlugin.Tests;

public class RaidPlannerClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body = "{}") { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiError.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ApiError.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, ApiError.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, ApiError.Server)]
    public async Task MapsStatusToApiError(HttpStatusCode code, ApiError expected)
    {
        var result = await RaidPlannerClient.SendForTest(new StubHandler(code), "/api/auth/me");
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter RaidPlannerClientTests`
Expected: FAIL — `SendForTest` not defined.

- [ ] **Step 3: Refactor the client**

Add a static test seam and a status→error mapper, and route the existing `GetAsync`/`PostAsync`/`PutAsync` through `ApiResult`. Key additions (full mapper + seam shown; apply the `ApiResult` return type uniformly to the public methods):

```csharp
// In RaidPlannerClient:
private static ApiError MapStatus(System.Net.HttpStatusCode code) => (int)code switch
{
    401 or 403 => ApiError.Unauthorized,
    404 => ApiError.NotFound,
    >= 500 => ApiError.Server,
    _ => ApiError.Unknown,
};

private HttpClient CreateHttpClient()
{
    var baseUrl = _config.EffectiveApiBaseUrl.TrimEnd('/');
    var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    return client;
}

private async Task<ApiResult<T>> GetAsync<T>(string endpoint, CancellationToken ct = default) where T : class
{
    try
    {
        var response = await _httpClient.GetAsync(endpoint, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.Error($"GET {endpoint} -> {(int)response.StatusCode}");
            return ApiResult<T>.Fail(MapStatus(response.StatusCode));
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return value is null ? ApiResult<T>.Fail(ApiError.Unknown) : ApiResult<T>.Ok(value);
    }
    catch (TaskCanceledException) { return ApiResult<T>.Fail(ApiError.Network); }
    catch (HttpRequestException) { return ApiResult<T>.Fail(ApiError.Network); }
    catch (Exception ex) { _log.Error($"GET {endpoint}: {ex.Message}"); return ApiResult<T>.Fail(ApiError.Unknown); }
}

// Test-only seam: build a client over a custom handler and run one GET.
internal static async Task<ApiResult<UserInfo>> SendForTest(HttpMessageHandler handler, string endpoint)
{
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
    var resp = await client.GetAsync(endpoint);
    if (!resp.IsSuccessStatusCode) return ApiResult<UserInfo>.Fail(MapStatus(resp.StatusCode));
    return ApiResult<UserInfo>.Ok(new UserInfo());
}
```

Apply analogous `ApiResult` changes to `PostAsync`/`PutAsync` (return `ApiResult<bool>` with `Ok(true)` on success) and update every public method (`TestConnectionAsync`, `GetPriorityAsync`, `CreateLootLogEntryAsync`, etc.) to return and propagate `ApiResult`. Update callers in `Plugin.cs`/services to read `.IsSuccess`/`.Value`/`.Error` (compiler will list them).

- [ ] **Step 4: Run tests + build**

Run: `dotnet test --filter RaidPlannerClientTests && dotnet build --configuration Release`
Expected: tests PASS, build clean.

- [ ] **Step 5: Commit**

```bash
git add XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs XIVRaidPlannerPlugin.Tests/RaidPlannerClientTests.cs XIVRaidPlannerPlugin/
git commit -m "refactor: RaidPlannerClient returns categorized ApiResult with timeout"
```

---

## Phase 5 — Architecture Refactor

### Task 9: Extract pure `ItemSourceClassifier`

**Files:**
- Create: `XIVRaidPlannerPlugin/Services/ItemSourceClassifier.cs`
- Modify: `XIVRaidPlannerPlugin/Services/InventoryService.cs`
- Test: `XIVRaidPlannerPlugin.Tests/ItemSourceClassifierTests.cs`

Move the pure `ClassifyByNameAndLevel` + `SourceMatchesBis` + the iLv constants out of `InventoryService` (lines 40-46, 159-170, 277-331) into a static `ItemSourceClassifier`. `InventoryService.ClassifySource(itemId)` keeps the Lumina lookup, then delegates to `ItemSourceClassifier.Classify(name, iLv)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using XIVRaidPlannerPlugin.Services;
namespace XIVRaidPlannerPlugin.Tests;

public class ItemSourceClassifierTests
{
    [Theory]
    [InlineData("Augmented Quetzalli Helm", 790, "tome_up")]
    [InlineData("Heavyweight Cuirass", 795, "savage")]
    [InlineData("Quetzalli Mail", 780, "tome")]
    [InlineData("Claro Walnut Ring", 770, "crafted")]
    [InlineData("Manderville Blade", 770, "relic")]
    [InlineData("Some Normal Chest", 760, "normal")]
    [InlineData("Junk", 100, "unknown")]
    public void Classify_NameAndLevel(string name, int ilv, string expected)
        => Assert.Equal(expected, ItemSourceClassifier.Classify(name, ilv));

    [Theory]
    [InlineData("savage", "raid", true)]
    [InlineData("tome_up", "tome", true)]
    [InlineData("crafted", "raid", false)]
    public void SourceMatchesBis(string equipped, string bis, bool expected)
        => Assert.Equal(expected, ItemSourceClassifier.SourceMatchesBis(equipped, bis));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter ItemSourceClassifierTests`
Expected: FAIL — `ItemSourceClassifier` not defined.

- [ ] **Step 3: Create `ItemSourceClassifier.cs`**

Move the constants and methods verbatim from `InventoryService` into a static class, making `Classify` (renamed from `ClassifyByNameAndLevel`) and `SourceMatchesBis` `public static`:

```csharp
namespace XIVRaidPlannerPlugin.Services;

/// <summary>Pure source classification by item name + item level. Mirrors backend bis.py.</summary>
public static class ItemSourceClassifier
{
    private const int IL_SAVAGE = 795;
    private const int IL_SAVAGE_ARMOR = 790;
    private const int IL_CATCHUP = 780;
    private const int IL_CRAFTED = 770;
    private const int IL_NORMAL = 760;

    public static string Classify(string name, int iLv)
    {
        var lowerName = name.ToLowerInvariant();
        if (iLv >= IL_SAVAGE_ARMOR)
        {
            if (lowerName.StartsWith("aug") || lowerName.Contains("augmented")) return "tome_up";
            if (lowerName.Contains("ascension") || lowerName.Contains("cruiserweight") ||
                lowerName.Contains("grand champion") || lowerName.Contains("heavyweight")) return "savage";
            if (iLv >= IL_SAVAGE) return "savage";
            return "tome_up";
        }
        if (iLv >= IL_CATCHUP)
        {
            if (lowerName.Contains("quetzalli") || lowerName.Contains("neo kingdom") ||
                lowerName.Contains("bygone")) return "tome";
            return "catchup";
        }
        if (iLv >= IL_CRAFTED)
        {
            if (lowerName.Contains("claro") || lowerName.Contains("agonist") ||
                lowerName.Contains("archeo kingdom")) return "crafted";
            if (lowerName.Contains("relic") || lowerName.Contains("manderville")) return "relic";
            return "prep";
        }
        if (iLv >= IL_NORMAL) return "normal";
        return "unknown";
    }

    public static bool SourceMatchesBis(string equippedSource, string bisSource) => bisSource switch
    {
        "raid" => equippedSource is "savage" or "raid",
        "tome" => equippedSource is "tome" or "tome_up",
        "base_tome" => equippedSource is "tome" or "base_tome",
        "crafted" => equippedSource == "crafted",
        _ => false,
    };
}
```

- [ ] **Step 4: Update `InventoryService` to delegate**

In `InventoryService.cs`: delete the moved constants (40-46) and the `SourceMatchesBis`/`ClassifyByNameAndLevel` methods; change `ClassifySource` to call `ItemSourceClassifier.Classify(name, iLv)` and `BuildGearUpdate` to call `ItemSourceClassifier.SourceMatchesBis(...)`.

- [ ] **Step 5: Run tests + build**

Run: `dotnet test --filter ItemSourceClassifierTests && dotnet build --configuration Release`
Expected: PASS + clean.

- [ ] **Step 6: Commit**

```bash
git add XIVRaidPlannerPlugin/Services/ItemSourceClassifier.cs XIVRaidPlannerPlugin/Services/InventoryService.cs XIVRaidPlannerPlugin.Tests/ItemSourceClassifierTests.cs
git commit -m "refactor: extract pure ItemSourceClassifier from InventoryService"
```

### Task 10: Extract `PartyMatcher` (pure)

**Files:**
- Create: `XIVRaidPlannerPlugin/Services/PartyMatcher.cs`
- Modify: `XIVRaidPlannerPlugin/Services/PartyMatchingService.cs`
- Test: `XIVRaidPlannerPlugin.Tests/PartyMatcherTests.cs`

Extract the matching algorithm (currently `PartyMatchingService.MatchParty` body, lines 54-81) into a pure function taking party names + planner players + overrides, returning matches/unmatched. `PartyMatchingService` reads names off `IPartyList` then delegates.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
namespace XIVRaidPlannerPlugin.Tests;

public class PartyMatcherTests
{
    private static PlayerInfo P(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void ExactNameMatch_CaseInsensitive()
    {
        var players = new List<PlayerInfo> { P("1", "Cloud Strife"), P("2", "Tifa Lockhart") };
        var result = PartyMatcher.Match(new[] { "cloud strife" }, players, new Dictionary<string, string>());
        Assert.Equal("1", result.Matches["cloud strife"]);
        Assert.Single(result.UnmatchedPlayers);
    }

    [Fact]
    public void Override_TakesPrecedence()
    {
        var players = new List<PlayerInfo> { P("1", "Cloud Strife") };
        var overrides = new Dictionary<string, string> { ["Cloudy McCloud"] = "1" };
        var result = PartyMatcher.Match(new[] { "Cloudy McCloud" }, players, overrides);
        Assert.Equal("1", result.Matches["Cloudy McCloud"]);
    }

    [Fact]
    public void NoMatch_GoesToUnmatchedPartyMembers()
    {
        var result = PartyMatcher.Match(new[] { "Random Person" }, new List<PlayerInfo>(), new Dictionary<string, string>());
        Assert.Contains("Random Person", result.UnmatchedPartyMembers);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter PartyMatcherTests`
Expected: FAIL — `PartyMatcher` not defined.

- [ ] **Step 3: Create `PartyMatcher.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

public sealed class PartyMatchResult
{
    public Dictionary<string, string> Matches { get; } = new();
    public List<PlayerInfo> UnmatchedPlayers { get; } = new();
    public List<string> UnmatchedPartyMembers { get; } = new();
}

/// <summary>Pure party-name → planner-player matching algorithm.</summary>
public static class PartyMatcher
{
    public static PartyMatchResult Match(
        IEnumerable<string> partyNames,
        IReadOnlyList<PlayerInfo> plannerPlayers,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new PartyMatchResult();
        result.UnmatchedPlayers.AddRange(plannerPlayers);

        foreach (var partyName in partyNames)
        {
            if (overrides.TryGetValue(partyName, out var overrideId))
            {
                var op = result.UnmatchedPlayers.FirstOrDefault(p => p.Id == overrideId);
                if (op != null)
                {
                    result.Matches[partyName] = overrideId;
                    result.UnmatchedPlayers.Remove(op);
                    continue;
                }
            }

            var exact = result.UnmatchedPlayers.FirstOrDefault(
                p => string.Equals(p.Name.Trim(), partyName, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                result.Matches[partyName] = exact.Id;
                result.UnmatchedPlayers.Remove(exact);
                continue;
            }

            result.UnmatchedPartyMembers.Add(partyName);
        }

        return result;
    }
}
```

- [ ] **Step 4: Update `PartyMatchingService.MatchParty` to delegate**

Replace the loop body (54-81) with: build `partyNames` off `_partyList` as today, call `PartyMatcher.Match(partyNames, plannerPlayers, _config.PlayerNameOverrides)`, assign `CurrentMatches`/`UnmatchedPlayers`/`UnmatchedPartyMembers` from the result, keep the existing `_log.Information(...)` summary.

- [ ] **Step 5: Run tests + build**

Run: `dotnet test --filter PartyMatcherTests && dotnet build --configuration Release`
Expected: PASS + clean.

- [ ] **Step 6: Commit**

```bash
git add XIVRaidPlannerPlugin/Services/PartyMatcher.cs XIVRaidPlannerPlugin/Services/PartyMatchingService.cs XIVRaidPlannerPlugin.Tests/PartyMatcherTests.cs
git commit -m "refactor: extract pure PartyMatcher algorithm"
```

### Task 11: Extract `LootLogCoordinator` (incl. pure slot→floor mapping)

**Files:**
- Create: `XIVRaidPlannerPlugin/Services/LootLogCoordinator.cs`
- Modify: `XIVRaidPlannerPlugin/Plugin.cs` (remove moved methods)
- Test: `XIVRaidPlannerPlugin.Tests/SlotToFloorMappingTests.cs`

Move loot routing (`OnLootObtained`, `OnItemPurchased`, `LogPurchaseAsync`, `OnManualLog`, `OnLootConfirmed`, `LogLootAsync`, `OnMarkFloorCleared`, `LogNewAcquisitionsAsync`, `BuildSlotToFloorMapping`) from `Plugin.cs` into `LootLogCoordinator`, which takes `RaidPlannerClient`, `PluginThread`, `IPluginLog`, and accessors for current floor + cached priority. Extract `BuildSlotToFloorMapping` as a pure static taking `PriorityResponse`.

- [ ] **Step 1: Write the failing test for the pure mapping**

```csharp
using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
namespace XIVRaidPlannerPlugin.Tests;

public class SlotToFloorMappingTests
{
    [Fact]
    public void LaterFloorOverridesEarlier_ForSharedSlot()
    {
        var priority = new PriorityResponse
        {
            TierFloors = new List<string> { "M9S", "M10S" },
            Priority = new Dictionary<string, Dictionary<string, List<PriorityEntry>>>
            {
                ["floor1"] = new() { ["earring"] = new() },
                ["floor2"] = new() { ["earring"] = new(), ["body"] = new() },
            },
        };
        var map = LootLogCoordinator.BuildSlotToFloorMapping(priority);
        Assert.Equal("M10S", map["earring"]); // later floor wins
        Assert.Equal("M10S", map["body"]);
    }
}
```

> Verify the exact `PriorityResponse` / `Priority` value type against `Api/Models.cs` and adjust the test's generic types to match before implementing.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter SlotToFloorMappingTests`
Expected: FAIL — `LootLogCoordinator` not defined.

- [ ] **Step 3: Create `LootLogCoordinator` with the pure mapping + moved routing**

Port `BuildSlotToFloorMapping` (Plugin.cs:882-900) to `public static Dictionary<string,string> BuildSlotToFloorMapping(PriorityResponse priority)` and move the loot-routing methods in, replacing `Task.Run`/`RunOnFrameworkThread` with the injected `PluginThread`. Constructor: `LootLogCoordinator(RaidPlannerClient api, PluginThread thread, IChatGui chat, IPluginLog log, Func<int?> currentFloor, Func<string?> currentFloorName, Func<PriorityResponse?> cachedPriority)`. (The `Func` accessors come from `RaidSessionService` in Task 12.)

- [ ] **Step 4: Remove the moved methods from `Plugin.cs`**

Delete the now-moved methods from `Plugin.cs`; the wiring will be reconnected in Task 13.

- [ ] **Step 5: Run tests + build**

Run: `dotnet test --filter SlotToFloorMappingTests && dotnet build --configuration Release`
Expected: PASS + clean (build may require Task 13 wiring; if `Plugin.cs` references break, complete the wiring move in Task 13 and keep this commit's `LootLogCoordinator` + test self-contained by temporarily leaving event handlers in `Plugin.cs` delegating to the coordinator).

- [ ] **Step 6: Commit**

```bash
git add XIVRaidPlannerPlugin/Services/LootLogCoordinator.cs XIVRaidPlannerPlugin/Plugin.cs XIVRaidPlannerPlugin.Tests/SlotToFloorMappingTests.cs
git commit -m "refactor: extract LootLogCoordinator with pure slot-to-floor mapping"
```

### Task 12: Extract `RaidSessionService` + `GearSyncService`; move LeaveWarning wiring

**Files:**
- Create: `XIVRaidPlannerPlugin/Services/RaidSessionService.cs`
- Create: `XIVRaidPlannerPlugin/Services/GearSyncService.cs`
- Modify: `XIVRaidPlannerPlugin/Services/LeaveWarningService.cs`
- Modify: `XIVRaidPlannerPlugin/Plugin.cs`
- Test: `XIVRaidPlannerPlugin.Tests/GearSyncDiffTests.cs`

`RaidSessionService` owns `_cachedPriority` + `_autoDetectedTier` and the savage enter/exit + overlay-timing handlers (Plugin.cs:340-513) and `RefreshPriority` (902-916). `GearSyncService` owns the `SyncGear` logic (212-336, 845-879). `LeaveWarningService` gains its own `SelectYesno` addon registration/handlers (Plugin.cs:440-487). Test the pure gear-diff change-counting via an injectable classifier.

- [ ] **Step 1: Write the failing gear-diff test**

```csharp
using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
namespace XIVRaidPlannerPlugin.Tests;

public class GearSyncDiffTests
{
    [Fact]
    public void CountsNewlyAcquiredSlots()
    {
        var fresh = new List<GearSlotStatusDto>
        {
            new() { Slot = "head", HasItem = false, CurrentSource = "none" },
            new() { Slot = "body", HasItem = true,  CurrentSource = "savage" },
        };
        var updated = new List<GearSlotStatusDto>
        {
            new() { Slot = "head", HasItem = true,  CurrentSource = "savage" },
            new() { Slot = "body", HasItem = true,  CurrentSource = "savage" },
        };
        var diff = GearSyncService.Diff(updated, fresh);
        Assert.Equal(1, diff.ChangeCount);
        Assert.Equal(new[] { "head" }, diff.NewlyAcquired.ToArray());
    }
}
```

> Confirm `GearSlotStatusDto` property names against `Api/Models.cs` before writing.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter GearSyncDiffTests`
Expected: FAIL — `GearSyncService.Diff` not defined.

- [ ] **Step 3: Create `GearSyncService` with a pure `Diff`**

Extract the change-counting loop (Plugin.cs:271-289) into:
```csharp
public sealed class GearDiff
{
    public int ChangeCount { get; init; }
    public List<string> NewlyAcquired { get; init; } = new();
}

public static GearDiff Diff(List<GearSlotStatusDto> updated, List<GearSlotStatusDto> fresh)
{
    var changes = 0; var acquired = new List<string>();
    for (var i = 0; i < updated.Count && i < fresh.Count; i++)
    {
        if (updated[i].CurrentSource != fresh[i].CurrentSource ||
            updated[i].HasItem != fresh[i].HasItem ||
            updated[i].IsAugmented != fresh[i].IsAugmented) changes++;
        if (updated[i].HasItem && !fresh[i].HasItem) acquired.Add(updated[i].Slot);
    }
    return new GearDiff { ChangeCount = changes, NewlyAcquired = acquired };
}
```
Then move the rest of `SyncGear` into an instance method that uses `Diff`, the injected `RaidPlannerClient`, `InventoryService`, `BiSDataService`, `PluginThread`, and `IChatGui`.

- [ ] **Step 4: Create `RaidSessionService` and move LeaveWarning wiring**

Move the territory/overlay-timing handlers + `_cachedPriority`/`_autoDetectedTier` + `RefreshPriority` into `RaidSessionService`, exposing `CurrentFloor`/`CurrentFloorName`/`CachedPriority` accessors (consumed by `LootLogCoordinator`). Move `SelectYesno` register/unregister + `OnSelectYesnoSetup/Close` into `LeaveWarningService` (it gets `IAddonLifecycle`, the session accessors, `PartyMatchingService`, `IPlayerState`).

- [ ] **Step 5: Build**

Run: `dotnet build --configuration Release`
Expected: clean (Task 13 finishes the `Plugin.cs` wiring; if references dangle, proceed to Task 13 in the same working session and build there).

- [ ] **Step 6: Run tests**

Run: `dotnet test --filter GearSyncDiffTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add XIVRaidPlannerPlugin/Services/RaidSessionService.cs XIVRaidPlannerPlugin/Services/GearSyncService.cs XIVRaidPlannerPlugin/Services/LeaveWarningService.cs XIVRaidPlannerPlugin/Plugin.cs XIVRaidPlannerPlugin.Tests/GearSyncDiffTests.cs
git commit -m "refactor: extract RaidSessionService and GearSyncService; move leave-warning wiring"
```

### Task 13: Slim `Plugin.cs` to a composition root with constructor DI

**Files:**
- Modify: `XIVRaidPlannerPlugin/Plugin.cs`
- Modify: `XIVRaidPlannerPlugin/Windows/*.cs` (remove `Plugin.X` static reach-ins)

`Plugin.cs` should now only: declare `[PluginService]`s, construct `PluginThread` + services + windows (passing dependencies via constructor), wire events, register the command + UI builder callbacks, and dispose. Windows that currently use `Plugin.DataManager`/`Plugin.Framework` (BiSViewerWindow:496,574; ConfigWindow:90,168,314,410,537) receive those via constructor instead.

- [ ] **Step 1: Convert window static reach-ins to injected fields**

For each window using `Plugin.X`, add the dependency (`IDataManager`, `IFramework` — or better, `PluginThread`) as a constructor parameter + private field, and replace `Plugin.X` usages.

- [ ] **Step 2: Rewire `Plugin.cs`**

Construct `PluginThread` first, then services (passing it + the session accessors), then windows. Wire the existing events to the new service methods. Keep the command handler + lifecycle hooks. Target: `Plugin.cs` well under 300 lines.

- [ ] **Step 3: Build**

Run: `dotnet build --configuration Release`
Expected: `0 Error(s)`. Grep to confirm no external statics remain:
`grep -rn "Plugin\.\(DataManager\|Framework\|ChatGui\|ClientState\|PartyList\)" XIVRaidPlannerPlugin/Services XIVRaidPlannerPlugin/Windows` → expect no results.

- [ ] **Step 4: Run full suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add XIVRaidPlannerPlugin/Plugin.cs XIVRaidPlannerPlugin/Windows/
git commit -m "refactor: slim Plugin.cs to composition root with constructor injection"
```

---

## Phase 6 — Browser Sign-In (plugin side)

### Task 14: PKCE/state generation (pure)

**Files:**
- Create: `XIVRaidPlannerPlugin/Auth/PkceCodes.cs`
- Test: `XIVRaidPlannerPlugin.Tests/PkceCodesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using XIVRaidPlannerPlugin.Auth;
namespace XIVRaidPlannerPlugin.Tests;

public class PkceCodesTests
{
    [Fact]
    public void Challenge_IsBase64UrlSha256OfVerifier()
    {
        var codes = PkceCodes.Generate();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(codes.Verifier));
        var expected = Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, codes.Challenge);
    }

    [Fact]
    public void Generate_ProducesDistinctVerifierAndState()
    {
        var a = PkceCodes.Generate();
        var b = PkceCodes.Generate();
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.State, b.State);
        Assert.True(a.Verifier.Length >= 43);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter PkceCodesTests`
Expected: FAIL — `PkceCodes` not defined.

- [ ] **Step 3: Write `PkceCodes.cs`**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace XIVRaidPlannerPlugin.Auth;

/// <summary>PKCE verifier/challenge + CSRF state for the browser sign-in flow.</summary>
public sealed class PkceCodes
{
    public required string Verifier { get; init; }
    public required string Challenge { get; init; }
    public required string State { get; init; }

    public static PkceCodes Generate()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var sha = SHA256.Create();
        var challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return new PkceCodes { Verifier = verifier, Challenge = challenge, State = state };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter PkceCodesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add XIVRaidPlannerPlugin/Auth/PkceCodes.cs XIVRaidPlannerPlugin.Tests/PkceCodesTests.cs
git commit -m "feat: add PKCE/state generation for browser sign-in"
```

### Task 15: `BrowserAuthService` (loopback OAuth flow)

**Files:**
- Create: `XIVRaidPlannerPlugin/Auth/BrowserAuthService.cs`
- Modify: `XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs` (add `ExchangePluginAuthCodeAsync`)

**Backend contract this expects (web app companion PR):**
- Browser URL: `{frontend}/plugin-auth?redirect_uri={loopback}&state={state}&code_challenge={challenge}&code_challenge_method=S256`
- After consent, web app 302s to `{loopback}?code={code}&state={state}`
- Exchange: `POST {api}/api/api-keys/plugin-auth/exchange` body `{ "code": "...", "code_verifier": "..." }` → `200 { "apiKey": "xrp_..." }`

- [ ] **Step 1: Add the exchange call to `RaidPlannerClient`**

```csharp
public async Task<ApiResult<string>> ExchangePluginAuthCodeAsync(string code, string codeVerifier, CancellationToken ct = default)
{
    var body = new { code, code_verifier = codeVerifier };
    var json = JsonSerializer.Serialize(body, JsonOptions);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    try
    {
        var resp = await _httpClient.PostAsync("/api/api-keys/plugin-auth/exchange", content, ct);
        if (!resp.IsSuccessStatusCode) return ApiResult<string>.Fail(MapStatus(resp.StatusCode));
        var payload = JsonSerializer.Deserialize<PluginAuthExchangeResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return string.IsNullOrEmpty(payload?.ApiKey)
            ? ApiResult<string>.Fail(ApiError.Unknown)
            : ApiResult<string>.Ok(payload.ApiKey);
    }
    catch (TaskCanceledException) { return ApiResult<string>.Fail(ApiError.Network); }
    catch (HttpRequestException) { return ApiResult<string>.Fail(ApiError.Network); }
}
```
Add to `Api/Models.cs`: `public class PluginAuthExchangeResponse { public string ApiKey { get; set; } = string.Empty; }`

- [ ] **Step 2: Write `BrowserAuthService.cs`**

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Auth;

/// <summary>One-click browser sign-in: loopback listener + PKCE code exchange → xrp_ key.</summary>
public sealed class BrowserAuthService
{
    private readonly Configuration _config;
    private readonly RaidPlannerClient _api;
    private readonly IPluginLog _log;

    public BrowserAuthService(Configuration config, RaidPlannerClient api, IPluginLog log)
    {
        _config = config; _api = api; _log = log;
    }

    /// <summary>Runs the full flow. Returns true and stores the key on success.</summary>
    public async Task<ApiResult<string>> SignInAsync(CancellationToken ct = default)
    {
        var pkce = PkceCodes.Generate();
        using var listener = new HttpListener();
        var port = GetFreeLoopbackPort();
        var redirect = $"http://127.0.0.1:{port}/callback/";
        listener.Prefixes.Add(redirect);
        listener.Start();

        var url = $"{_config.EffectiveFrontendBaseUrl.TrimEnd('/')}/plugin-auth" +
                  $"?redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&state={pkce.State}&code_challenge={pkce.Challenge}&code_challenge_method=S256";
        Dalamud.Utility.Util.OpenLink(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return ApiResult<string>.Fail(ApiError.Network); }

        var query = context.Request.QueryString;
        var code = query["code"]; var returnedState = query["state"];
        await WriteBrowserResponse(context, "You're signed in. Return to the game.");

        if (returnedState != pkce.State || string.IsNullOrEmpty(code))
        {
            _log.Error("[BrowserAuth] state mismatch or missing code");
            return ApiResult<string>.Fail(ApiError.Unauthorized);
        }

        var result = await _api.ExchangePluginAuthCodeAsync(code, pkce.Verifier, ct);
        if (result.IsSuccess)
        {
            _config.ApiKey = result.Value!;
            _config.Save();
            _api.UpdateAuth();
        }
        return result;
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WriteBrowserResponse(HttpListenerContext ctx, string message)
    {
        var html = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif'>{message}</body></html>");
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = html.Length;
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
    }
}
```

> Verify the `Dalamud.Utility.Util.OpenLink` namespace/signature against the SDK-15 baseline from Task 1; adjust if moved.

- [ ] **Step 3: Build**

Run: `dotnet build --configuration Release`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add XIVRaidPlannerPlugin/Auth/BrowserAuthService.cs XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs XIVRaidPlannerPlugin/Api/Models.cs
git commit -m "feat: add BrowserAuthService loopback sign-in flow"
```

### Task 16: ConfigWindow — browser sign-in primary, manual under Advanced

**Files:**
- Modify: `XIVRaidPlannerPlugin/Windows/ConfigWindow.cs`
- Modify: `XIVRaidPlannerPlugin/Plugin.cs` (inject `BrowserAuthService` into ConfigWindow)

- [ ] **Step 1: Inject `BrowserAuthService` into `ConfigWindow`**

Add a constructor parameter + field; pass it from `Plugin.cs`.

- [ ] **Step 2: Add the sign-in button to the Connection tab**

In the Connection tab draw method (around the API-key field, ConfigWindow ~lines 60-185), add a primary button that runs the flow off-thread and reports status via the existing `_connectionStatus`/`_connectionStatusColor` fields:
```csharp
if (ImGui.Button("Sign in with browser"))
{
    _connectionStatus = "Waiting for browser sign-in...";
    _connectionStatusColor = Theme.Warning;
    _thread.RunBackground(async () =>
    {
        var result = await _browserAuth.SignInAsync();
        _thread.RunOnUi(() =>
        {
            _connectionStatus = result.IsSuccess ? "Signed in!" :
                result.Error == ApiError.Unauthorized ? "Sign-in rejected." : "Sign-in failed or timed out.";
            _connectionStatusColor = result.IsSuccess ? Theme.Success : Theme.Error;
        });
    });
}
```
(`_thread` is the injected `PluginThread`.)

- [ ] **Step 3: Move manual key entry under an Advanced expander**

Wrap the existing manual `ApiKey` `InputText` + custom-URL controls in:
```csharp
if (ImGui.CollapsingHeader("Advanced (manual API key / custom server)")) { /* existing manual controls */ }
```

- [ ] **Step 4: Build**

Run: `dotnet build --configuration Release`
Expected: clean.

- [ ] **Step 5: Commit**

```bash
git add XIVRaidPlannerPlugin/Windows/ConfigWindow.cs XIVRaidPlannerPlugin/Plugin.cs
git commit -m "feat: browser sign-in button in ConfigWindow; manual key under Advanced"
```

---

## Phase 7 — CI Hardening

### Task 17: Update `ci.yml`

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Update the workflow**

Set the dotnet version to match the SDK-15 target (confirm from Task 1; likely 9.0.x), add a test step and a format check, keep the artifact upload:
```yaml
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      # ... existing Download Dalamud + Restore steps ...
      - name: Build (Release)
        run: dotnet build --configuration Release --no-restore
      - name: Test
        run: dotnet test --configuration Release --no-build
      - name: Format check
        run: dotnet format --verify-no-changes
```

- [ ] **Step 2: Verify locally**

Run: `dotnet format --verify-no-changes`
Expected: exit 0 (no diffs). If it reports changes, run `dotnet format`, review, and include the formatting fixes.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml XIVRaidPlannerPlugin/
git commit -m "ci: add test and format-check steps; bump .NET to 9"
```

---

## Phase 8 — Documentation

### Task 18: Replace `HANDOFF.md`, refresh README + workspace CLAUDE.md

**Files:**
- Delete: `HANDOFF.md`
- Create: `ARCHITECTURE.md`
- Modify: `README.md`
- Modify: `D:\FFXIV\Dev\xrp-dev\CLAUDE.md`

- [ ] **Step 1: Write `ARCHITECTURE.md`**

Document the post-refactor layout: composition root (`Plugin.cs`), services (`RaidSessionService`, `GearSyncService`, `LootLogCoordinator`, `LeaveWarningService`, `PartyMatchingService`+`PartyMatcher`, `InventoryService`+`ItemSourceClassifier`, `ItemMappingService`, `BiSDataService`, `AddonHighlightService`, `TerritoryService`), windows, `Api/` (`RaidPlannerClient` + `ApiResult`), `Auth/` (`BrowserAuthService` + `PkceCodes`), helpers (`Theme`, `PluginThread`), the browser sign-in flow + backend contract, and the build/test commands.

- [ ] **Step 2: Delete the stale handoff**

Run: `git rm HANDOFF.md`

- [ ] **Step 3: Refresh README**

Update install/build/test instructions, the in-game command table, and add the "Sign in with browser" setup (with manual-key fallback noted under Advanced).

- [ ] **Step 4: Update workspace `CLAUDE.md`**

In `D:\FFXIV\Dev\xrp-dev\CLAUDE.md`, update the Dalamud Plugin "Architecture" tree to match the new service layout and add `Auth/` + the browser sign-in note.

- [ ] **Step 5: Commit**

```bash
git add ARCHITECTURE.md README.md ../CLAUDE.md
git rm HANDOFF.md
git commit -m "docs: replace stale HANDOFF with ARCHITECTURE; refresh README and workspace guide"
```

---

## Final: Verification & PR

### Task 19: Full verification and PR

- [ ] **Step 1: Full clean build + test**

Run: `dotnet build --configuration Release && dotnet test`
Expected: clean build, all tests pass.

- [ ] **Step 2: Confirm no AI attribution in any commit**

Run: `git log origin/main..HEAD --format='%an <%ae>%n%b' | grep -iE 'co-authored|claude|generated with' || echo CLEAN`
Expected: `CLEAN`.

- [ ] **Step 3: Push the branch**

```bash
git push -u origin chore/modernization-audit
```

- [ ] **Step 4: Open the PR with an in-game smoke-test checklist**

PR body includes:
```
## In-game smoke test (requires the web app companion PR deployed)
- [ ] /xrp config opens; "Sign in with browser" completes and stores a key
- [ ] Manual key entry still works under Advanced
- [ ] Entering a savage instance shows the priority overlay
- [ ] /xrp bis shows gear; /xrp sync updates the web app
- [ ] Chat loot detection logs (Confirm + Auto modes)
- [ ] Leave-warning fires on abandon-duty with unclaimed priority loot
- [ ] BiS highlighting in NeedGreed / shops
```

---

## Self-Review

**Spec coverage:**
- Refactor / DI / god-object → Tasks 9-13 ✓
- `Theme` + `PluginThread` → Tasks 5-6 ✓
- SDK 15 currency → Task 1 ✓
- `.editorconfig` + `Directory.Build.props` → Tasks 2-3 ✓
- API client hardening → Tasks 7-8 ✓
- Test project + pure-logic tests → Task 4 + tests in 5,7,9,10,11,12,14 ✓
- Browser sign-in (plugin side) → Tasks 14-16 ✓
- CI hardening → Task 17 ✓
- Docs (HANDOFF/README/workspace CLAUDE.md) → Task 18 ✓
- Web app companion → explicitly out of scope (separate plan) ✓

**Type consistency:** `ApiResult<T>` (`Ok`/`Fail`/`IsSuccess`/`Value`/`Error`), `ApiError`, `PartyMatcher.Match`→`PartyMatchResult`, `ItemSourceClassifier.Classify`/`SourceMatchesBis`, `GearSyncService.Diff`→`GearDiff`, `LootLogCoordinator.BuildSlotToFloorMapping`, `PkceCodes.Generate`→`Verifier`/`Challenge`/`State`, `BrowserAuthService.SignInAsync`, `RaidPlannerClient.ExchangePluginAuthCodeAsync`+`PluginAuthExchangeResponse.ApiKey` — names used consistently across tasks.

**Known DTO-verification points (flagged inline):** `PriorityResponse.Priority` value type (Task 11) and `GearSlotStatusDto` property names (Tasks 9, 12) must be confirmed against `Api/Models.cs` before writing those tests.
