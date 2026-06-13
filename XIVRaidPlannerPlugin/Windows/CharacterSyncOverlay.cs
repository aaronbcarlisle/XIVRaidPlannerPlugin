using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Compact FFXIV-native sync tray anchored to the bottom-right of the Character window.
/// Layout: [ ↻ Sync Job ] [ All ] [ ⋯ ] / status line
/// Sync Mounts lives in the ⋯ overflow menu — not a primary action on the Character window.
/// </summary>
public sealed class CharacterSyncOverlay : IDisposable
{
    private readonly GearSyncService _gearSync;
    private readonly MountFarmService _mountFarm;
    private readonly Configuration _config;
    private readonly IGameGui _gameGui;
    private readonly Action _openSettings;

    // ── State machine ──────────────────────────────────────────────────
    private enum TrayState { Idle, SyncingCurrent, SyncingAll, SyncingMounts, Success, Error }
    private TrayState _state = TrayState.Idle;
    private DateTime _stateChangedAt = DateTime.MinValue;
    private string _statusDetail = string.Empty;

    // ── Fade-in animation ──────────────────────────────────────────────
    private bool _wasAddonVisible;
    private float _fadeAlpha = 1f;
    private DateTime _fadeStartTime;
    private const float FadeDurationMs = 150f;

    // ── FFXIV-native palette ───────────────────────────────────────────
    // Deep charcoal background — close to FFXIV tooltip/panel chrome
    private static readonly Vector4 BgColor       = new(0.06f, 0.06f, 0.09f, 0.90f);
    // Gold-silver bevel border (matches FFXIV window frame language)
    private static readonly Vector4 BorderColor   = new(0.58f, 0.52f, 0.38f, 0.72f);
    // Inset button surface — slightly raised from bg
    private static readonly Vector4 BtnNormal     = new(0.14f, 0.14f, 0.19f, 1.00f);
    private static readonly Vector4 BtnHover      = new(0.22f, 0.22f, 0.29f, 1.00f);
    // Pressed surface goes darker to simulate inset
    private static readonly Vector4 BtnPress      = new(0.07f, 0.07f, 0.11f, 1.00f);
    // Primary button: teal-tinted dark to signal priority without being loud
    private static readonly Vector4 BtnPrimary    = new(0.08f, 0.20f, 0.28f, 1.00f);
    private static readonly Vector4 BtnPrimaryHov = new(0.12f, 0.28f, 0.38f, 1.00f);
    // Subdued status text — should not compete with buttons
    private static readonly Vector4 StatusColor   = new(0.50f, 0.50f, 0.56f, 1.00f);

    public CharacterSyncOverlay(
        GearSyncService gearSync,
        MountFarmService mountFarm,
        Configuration config,
        IGameGui gameGui,
        Action openSettings)
    {
        _gearSync = gearSync;
        _mountFarm = mountFarm;
        _config = config;
        _gameGui = gameGui;
        _openSettings = openSettings;
        _gearSync.SyncCompleted += OnGearSyncCompleted;
        _mountFarm.SyncCompleted += OnMountSyncCompleted;
    }

    public void Draw()
    {
        var addon = _gameGui.GetAddonByName("Character", 1);
        var isVisible = !addon.IsNull && addon.IsVisible;

        // Trigger fade-in each time the Character window opens
        if (isVisible && !_wasAddonVisible)
        {
            _fadeStartTime = DateTime.UtcNow;
            _fadeAlpha = 0f;
        }
        _wasAddonVisible = isVisible;
        if (!isVisible) return;

        var elapsedMs = (float)(DateTime.UtcNow - _fadeStartTime).TotalMilliseconds;
        _fadeAlpha = Math.Min(1f, elapsedMs / FadeDurationMs);

        var pos = addon.Position;
        // Slide 8 px downward at alpha=0, reaching target at alpha=1
        var slideY = (1f - _fadeAlpha) * 8f;

        ImGui.SetNextWindowPos(
            new Vector2(pos.X + addon.ScaledWidth - 8, pos.Y + addon.ScaledHeight - 8 + slideY),
            ImGuiCond.Always,
            new Vector2(1f, 1f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(168, 0), new Vector2(208, float.MaxValue));

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _fadeAlpha);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,       BgColor);
        ImGui.PushStyleColor(ImGuiCol.Border,         BorderColor);
        ImGui.PushStyleColor(ImGuiCol.Button,         BtnNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,  BtnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,   BtnPress);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,    2f);
        // FrameBorderSize 0.5 gives the subtle bevel/inset look on each button
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize,  0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,      new Vector2(4f, 3f));

        var open = ImGui.Begin(
            "##XRPCharSync",
            ImGuiWindowFlags.NoTitleBar      |
            ImGuiWindowFlags.AlwaysAutoResize|
            ImGuiWindowFlags.NoScrollbar     |
            ImGuiWindowFlags.NoMove          |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav           |
            ImGuiWindowFlags.NoSavedSettings);

        if (open) DrawContent();

        ImGui.End();
        ImGui.PopStyleVar(7);   // Alpha + 6 style vars
        ImGui.PopStyleColor(5); // WindowBg + Border + Button + ButtonHovered + ButtonActive
    }

    private void DrawContent()
    {
        // Auto-revert success display after 3 s
        if (_state == TrayState.Success &&
            (DateTime.UtcNow - _stateChangedAt).TotalMilliseconds > 3000)
            _state = TrayState.Idle;

        var gearSyncing  = _state is TrayState.SyncingCurrent or TrayState.SyncingAll;
        var mountSyncing = _state == TrayState.SyncingMounts;

        // ── Button row ──────────────────────────────────────────────
        var avail   = ImGui.GetContentRegionAvail().X;
        const float overflowW = 22f;
        const float allW      = 38f;
        var   gap   = ImGui.GetStyle().ItemSpacing.X;
        var   jobW  = avail - overflowW - allW - gap * 2f;

        // Primary: ↻ Sync Job
        if (gearSyncing) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button,        BtnPrimary);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnPrimaryHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  BtnPress);
        var jobLabel = _state == TrayState.SyncingCurrent ? "↻ Syncing…" : "↻ Sync Job";
        if (ImGui.Button(jobLabel, new Vector2(jobW, 0)))
            TriggerSyncCurrentJob();
        ImGui.PopStyleColor(3);
        if (gearSyncing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync currently equipped job gear to your profile");

        ImGui.SameLine();

        // Secondary: All
        if (gearSyncing) ImGui.BeginDisabled();
        var allLabel = _state == TrayState.SyncingAll ? "…" : "All";
        if (ImGui.Button(allLabel, new Vector2(allW, 0)))
            TriggerSyncAll();
        if (gearSyncing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync all saved gearsets to your profile");

        ImGui.SameLine();

        // Tertiary: ⋯ overflow — never disabled (settings always accessible)
        if (ImGui.Button("⋯", new Vector2(overflowW, 0)))
            ImGui.OpenPopup("##xrp_overflow");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("More options");

        // ── Overflow popup ───────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(BgColor.X, BgColor.Y, BgColor.Z, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border,  BorderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        if (ImGui.BeginPopup("##xrp_overflow"))
        {
            if (mountSyncing) ImGui.BeginDisabled();
            if (ImGui.Selectable("↺  Sync Mounts"))
                TriggerSyncMounts();
            if (mountSyncing) ImGui.EndDisabled();

            ImGui.Separator();

            if (ImGui.Selectable("⚙  Plugin Settings"))
                _openSettings();

            ImGui.EndPopup();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        // ── Status line ──────────────────────────────────────────────
        ImGui.Spacing();
        var (statusText, statusCol) = BuildStatus();
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(statusCol, statusText);
        ImGui.PopTextWrapPos();
    }

    private void TriggerSyncCurrentJob()
    {
        if (_state is TrayState.SyncingCurrent or TrayState.SyncingAll) return;
        _state = TrayState.SyncingCurrent;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = string.Empty;
        _gearSync.SyncProfileGear();
    }

    private void TriggerSyncAll()
    {
        if (_state is TrayState.SyncingCurrent or TrayState.SyncingAll) return;
        _state = TrayState.SyncingAll;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = string.Empty;
        _gearSync.SyncSavedGearsets();
    }

    private void TriggerSyncMounts()
    {
        if (_state == TrayState.SyncingMounts) return;
        _state = TrayState.SyncingMounts;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = string.Empty;
        _mountFarm.Sync();
    }

    private (string text, Vector4 color) BuildStatus()
    {
        return _state switch
        {
            TrayState.SyncingCurrent => ("Syncing current job…", StatusColor),
            TrayState.SyncingAll     => ("Syncing all gearsets…", StatusColor),
            TrayState.SyncingMounts  => ("Syncing mounts…", StatusColor),
            TrayState.Success        => ("✓  " + Truncate(_statusDetail, 30), Theme.Success),
            TrayState.Error          => ("✕  " + Truncate(_statusDetail, 30), Theme.Error),
            _                        => BuildIdleStatus(),
        };
    }

    private (string, Vector4) BuildIdleStatus()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            return ("Not configured · /xrp config", new Vector4(Theme.Error.X, Theme.Error.Y, Theme.Error.Z, 0.80f));

        if (string.IsNullOrEmpty(_config.LastGearSyncAt))
            return ("Connected · Not synced yet", StatusColor);

        var age = FormatSyncAge(_config.LastGearSyncAt);
        if (!string.IsNullOrEmpty(_config.LastGearSyncError))
            return ($"Failed · {age}", new Vector4(Theme.Error.X, Theme.Error.Y, Theme.Error.Z, 0.85f));

        var n = _config.LastGearSyncJobCount;
        var jobStr = n > 0 ? $"{n} job{(n == 1 ? "" : "s")}" : "synced";
        return ($"Connected · {jobStr} · {age}", StatusColor);
    }

    private void OnGearSyncCompleted(bool success, string message)
    {
        _state = success ? TrayState.Success : TrayState.Error;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = message;
    }

    private void OnMountSyncCompleted(bool success, string message)
    {
        _state = success ? TrayState.Success : TrayState.Error;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = message;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string FormatSyncAge(string isoTimestamp)
    {
        if (!DateTime.TryParse(isoTimestamp, null, DateTimeStyles.RoundtripKind, out var dt))
            return string.Empty;
        var age = DateTime.UtcNow - dt;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours  < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays   < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    public void Dispose()
    {
        _gearSync.SyncCompleted -= OnGearSyncCompleted;
        _mountFarm.SyncCompleted -= OnMountSyncCompleted;
    }
}
