using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Compact FFXIV-native sync tray for the Character window.
///
/// Locked (default): anchors to the bottom-right corner of the Character addon,
///   auto-sizes, not movable or resizable.
/// Unlocked: free-floating at a saved screen position, draggable, resizable.
///   Position is saved to Configuration when the user locks again or closes
///   the Character window.
/// </summary>
public sealed class CharacterSyncOverlay : IDisposable
{
    private readonly GearSyncService _gearSync;
    private readonly MountFarmService _mountFarm;
    private readonly CollectionSyncService _collectionSync;
    private readonly Configuration _config;
    private readonly IGameGui _gameGui;
    private readonly Action _openSettings;

    // ── State machine ──────────────────────────────────────────────────
    private enum TrayState { Idle, SyncingCurrent, SyncingAll, SyncingMounts, SyncingCollections, Success, Error }
    private TrayState _state = TrayState.Idle;
    private DateTime _stateChangedAt = DateTime.MinValue;
    private string _statusDetail = string.Empty;

    // ── Fade-in animation ──────────────────────────────────────────────
    private bool _wasAddonVisible;
    private float _fadeAlpha = 1f;
    private DateTime _fadeStartTime;
    private const float FadeDurationMs = 150f;

    // ── Free-float position tracking ──────────────────────────────────
    // Updated every frame while unlocked; flushed to config on lock or close.
    private Vector2 _livePos;
    private float _liveW;

    // ── FFXIV-native palette ───────────────────────────────────────────
    private static readonly Vector4 BgColor = new(0.06f, 0.06f, 0.09f, 0.90f);
    private static readonly Vector4 BorderColor = new(0.58f, 0.52f, 0.38f, 0.72f);
    private static readonly Vector4 BtnNormal = new(0.14f, 0.14f, 0.19f, 1.00f);
    private static readonly Vector4 BtnHover = new(0.22f, 0.22f, 0.29f, 1.00f);
    private static readonly Vector4 BtnPress = new(0.07f, 0.07f, 0.11f, 1.00f);
    private static readonly Vector4 BtnPrimary = new(0.08f, 0.20f, 0.28f, 1.00f);
    private static readonly Vector4 BtnPrimaryHov = new(0.12f, 0.28f, 0.38f, 1.00f);
    private static readonly Vector4 StatusColor = new(0.50f, 0.50f, 0.56f, 1.00f);
    private static readonly Vector4 DragBarColor = new(0.38f, 0.38f, 0.44f, 1.00f);

    public CharacterSyncOverlay(
        GearSyncService gearSync,
        MountFarmService mountFarm,
        CollectionSyncService collectionSync,
        Configuration config,
        IGameGui gameGui,
        Action openSettings)
    {
        _gearSync = gearSync;
        _mountFarm = mountFarm;
        _collectionSync = collectionSync;
        _config = config;
        _gameGui = gameGui;
        _openSettings = openSettings;
        _liveW = _config.SyncTrayW;
        _gearSync.SyncCompleted += OnGearSyncCompleted;
        _mountFarm.SyncCompleted += OnMountSyncCompleted;
        _collectionSync.SyncCompleted += OnCollectionSyncCompleted;
    }

    public void Draw()
    {
        var addon = _gameGui.GetAddonByName("Character", 1);
        var isVisible = !addon.IsNull && addon.IsVisible;

        if (isVisible && !_wasAddonVisible)
        {
            _fadeStartTime = DateTime.UtcNow;
            _fadeAlpha = 0f;
        }

        // Auto-save position when Character window closes while unlocked
        if (!isVisible && _wasAddonVisible && !_config.SyncTrayLocked)
            PersistPosition();

        _wasAddonVisible = isVisible;
        if (!isVisible) return;

        var elapsedMs = (float)(DateTime.UtcNow - _fadeStartTime).TotalMilliseconds;
        _fadeAlpha = Math.Min(1f, elapsedMs / FadeDurationMs);

        var pos = addon.Position;
        var slideY = (1f - _fadeAlpha) * 8f;

        // Anchor position (Character window bottom-right)
        var anchorX = pos.X + addon.ScaledWidth - 8;
        var anchorY = pos.Y + addon.ScaledHeight - 8 + slideY;

        // ── Window position / size ─────────────────────────────────────
        if (_config.SyncTrayLocked)
        {
            ImGui.SetNextWindowPos(new Vector2(anchorX, anchorY), ImGuiCond.Always, new Vector2(1f, 1f));
            ImGui.SetNextWindowSizeConstraints(new Vector2(180, 0), new Vector2(220, float.MaxValue));
        }
        else
        {
            // Free-floating: position on first appear each time the Character window opens.
            // ImGuiCond.Appearing fires whenever the window transitions from hidden to visible.
            if (_config.SyncTrayX >= 0)
                ImGui.SetNextWindowPos(new Vector2(_config.SyncTrayX, _config.SyncTrayY), ImGuiCond.Appearing);
            else
                // No saved position yet — start at the anchor so it doesn't jump to 0,0
                ImGui.SetNextWindowPos(new Vector2(anchorX, anchorY), ImGuiCond.Appearing, new Vector2(1f, 1f));

            ImGui.SetNextWindowSize(new Vector2(_liveW, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowSizeConstraints(new Vector2(160, 60), new Vector2(350, 280));
        }

        // ── Style push ────────────────────────────────────────────────
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _fadeAlpha);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BgColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleColor(ImGuiCol.Button, BtnNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BtnPress);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 3f));

        var flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings;

        if (_config.SyncTrayLocked)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;

        var open = ImGui.Begin("##XRPCharSync", flags);

        if (open)
        {
            // Track live position/size every frame when unlocked (no disk write)
            if (!_config.SyncTrayLocked)
            {
                _livePos = ImGui.GetWindowPos();
                _liveW = ImGui.GetWindowSize().X;
            }

            DrawContent();
        }

        ImGui.End();
        ImGui.PopStyleVar(7);
        ImGui.PopStyleColor(5);
    }

    private void DrawContent()
    {
        // Auto-revert success display after 3 s
        if (_state == TrayState.Success &&
            (DateTime.UtcNow - _stateChangedAt).TotalMilliseconds > 3000)
            _state = TrayState.Idle;

        // ── Drag header (unlocked only) ────────────────────────────────
        if (!_config.SyncTrayLocked)
        {
            ImGui.TextColored(DragBarColor, "XRP Sync");
            ImGui.SameLine();
            // Right-align the "Lock" button
            var lockW = ImGui.CalcTextSize("Lock").X + ImGui.GetStyle().FramePadding.X * 2f;
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - lockW);
            if (ImGui.SmallButton("Lock"))
                DoLock();
            ImGui.Separator();
        }

        var gearSyncing = _state is TrayState.SyncingCurrent or TrayState.SyncingAll;
        var mountSyncing = _state == TrayState.SyncingMounts;

        // ── Button row ────────────────────────────────────────────────
        var avail = ImGui.GetContentRegionAvail().X;
        const float overflowW = 26f;
        const float allW = 38f;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var jobW = avail - overflowW - allW - gap * 2f;

        // Primary: Sync Job
        if (gearSyncing) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, BtnPrimary);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnPrimaryHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BtnPress);
        var jobLabel = _state == TrayState.SyncingCurrent ? "Syncing..." : "Sync Job";
        if (ImGui.Button(jobLabel, new Vector2(jobW, 0)))
            TriggerSyncCurrentJob();
        ImGui.PopStyleColor(3);
        if (gearSyncing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync currently equipped job gear to your profile");

        ImGui.SameLine();

        // Secondary: All
        if (gearSyncing) ImGui.BeginDisabled();
        var allLabel = _state == TrayState.SyncingAll ? "..." : "All";
        if (ImGui.Button(allLabel, new Vector2(allW, 0)))
            TriggerSyncAll();
        if (gearSyncing) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync all saved gearsets to your profile");

        ImGui.SameLine();

        // Overflow: ...
        if (ImGui.Button("...", new Vector2(overflowW, 0)))
            ImGui.OpenPopup("##xrp_overflow");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("More options");

        // ── Overflow popup ────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(BgColor.X, BgColor.Y, BgColor.Z, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        if (ImGui.BeginPopup("##xrp_overflow"))
        {
            if (mountSyncing) ImGui.BeginDisabled();
            if (ImGui.Selectable("Sync Mounts"))
                TriggerSyncMounts();
            if (mountSyncing) ImGui.EndDisabled();

            var collectionSyncing = _state == TrayState.SyncingCollections;
            if (collectionSyncing || !_config.EnableCollectionSync) ImGui.BeginDisabled();
            if (ImGui.Selectable("Sync Collections"))
                TriggerSyncCollections();
            if (collectionSyncing || !_config.EnableCollectionSync) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Update your collection goal states (mounts, tokens) from game data");

            ImGui.Separator();

            if (ImGui.Selectable("Refresh Status"))
                RefreshStatus();

            ImGui.Separator();

            if (ImGui.Selectable("Plugin Settings"))
                _openSettings();

            ImGui.Separator();

            if (_config.SyncTrayLocked)
            {
                if (ImGui.Selectable("Unlock Position"))
                    DoUnlock();
            }
            else
            {
                if (ImGui.Selectable("Lock Position"))
                    DoLock();
            }

            ImGui.EndPopup();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        // ── Status line ───────────────────────────────────────────────
        ImGui.Spacing();
        var (statusText, statusCol) = BuildStatus();
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(statusCol, statusText);
        ImGui.PopTextWrapPos();
    }

    private void DoLock()
    {
        PersistPosition();
        _config.SyncTrayLocked = true;
        _config.Save();
    }

    private void DoUnlock()
    {
        _config.SyncTrayLocked = false;
        // Don't call Save() here — position saves on re-lock or window close
    }

    // Clears any persisted error and resets to Idle so the tray shows current status.
    private void RefreshStatus()
    {
        if (_state is TrayState.SyncingCurrent or TrayState.SyncingAll or TrayState.SyncingMounts or TrayState.SyncingCollections)
            return;
        _config.LastGearSyncError = string.Empty;
        _config.Save();
        _state = TrayState.Idle;
        _statusDetail = string.Empty;
    }

    // Writes live position to config and flushes to disk.
    private void PersistPosition()
    {
        _config.SyncTrayX = _livePos.X;
        _config.SyncTrayY = _livePos.Y;
        _config.SyncTrayW = _liveW;
        _config.Save();
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

    private void TriggerSyncCollections()
    {
        if (_state == TrayState.SyncingCollections) return;
        _state = TrayState.SyncingCollections;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = string.Empty;
        _collectionSync.Sync();
    }

    private (string text, Vector4 color) BuildStatus()
    {
        return _state switch
        {
            TrayState.SyncingCurrent => ("Syncing current job...", StatusColor),
            TrayState.SyncingAll => ("Syncing all gearsets...", StatusColor),
            TrayState.SyncingMounts => ("Syncing mounts...", StatusColor),
            TrayState.SyncingCollections => ("Syncing collections...", StatusColor),
            TrayState.Success => ("OK  " + Truncate(_statusDetail, 46), Theme.Success),
            TrayState.Error => (Truncate(_statusDetail, 50), Theme.Error),
            _ => BuildIdleStatus(),
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
        return ($"Connected · {jobStr} · Synced {age}", StatusColor);
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

    private void OnCollectionSyncCompleted(bool success, string message)
    {
        _state = success ? TrayState.Success : TrayState.Error;
        _stateChangedAt = DateTime.UtcNow;
        _statusDetail = message;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static string FormatSyncAge(string isoTimestamp)
    {
        if (!DateTime.TryParse(isoTimestamp, null, DateTimeStyles.RoundtripKind, out var dt))
            return string.Empty;
        var age = DateTime.UtcNow - dt;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    public void Dispose()
    {
        _gearSync.SyncCompleted -= OnGearSyncCompleted;
        _mountFarm.SyncCompleted -= OnMountSyncCompleted;
        _collectionSync.SyncCompleted -= OnCollectionSyncCompleted;
    }
}
