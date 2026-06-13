using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Floating sync button panel anchored to the right side of the native Character window.
/// Drawn via UiBuilder.Draw; visible only while the Character addon is open.
/// </summary>
public sealed class CharacterSyncOverlay : IDisposable
{
    private readonly GearSyncService _gearSync;
    private readonly Configuration _config;
    private readonly IGameGui _gameGui;

    private bool _isSyncingCurrent;
    private bool _isSyncingAll;
    private string _resultMessage = string.Empty;
    private Vector4 _resultColor = Theme.White;

    public CharacterSyncOverlay(GearSyncService gearSync, Configuration config, IGameGui gameGui)
    {
        _gearSync = gearSync;
        _config = config;
        _gameGui = gameGui;
        _gearSync.SyncCompleted += OnSyncCompleted;
    }

    public void Draw()
    {
        var addon = _gameGui.GetAddonByName("Character", 1);
        if (addon.IsNull || !addon.IsVisible) return;

        var pos = addon.Position;
        // Anchor inside the Character window, bottom-right corner.
        // Pivot (1, 1) means the set point is the bottom-right corner of the overlay,
        // so it extends leftward and upward — sits over the portrait background
        // without touching the GearSetList panel outside the window.
        ImGui.SetNextWindowPos(
            new Vector2(pos.X + addon.ScaledWidth - 8, pos.Y + addon.ScaledHeight - 8),
            ImGuiCond.Always,
            new Vector2(1f, 1f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(160, 0), new Vector2(190, float.MaxValue));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.12f, 0.93f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, 0.6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));

        var open = ImGui.Begin(
            "##XRPCharSync",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings);

        if (open) DrawContent();

        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private void DrawContent()
    {
        var isSyncing = _isSyncingCurrent || _isSyncingAll;

        ImGui.TextColored(Theme.Accent, "Gear Sync");
        ImGui.Separator();
        ImGui.Spacing();

        if (isSyncing) ImGui.BeginDisabled();

        if (ImGui.Button("Sync Current Job", new Vector2(-1, 0)))
        {
            _isSyncingCurrent = true;
            _resultMessage = string.Empty;
            _gearSync.SyncProfileGear();
        }

        ImGui.Spacing();

        if (ImGui.Button("Sync All Gearsets", new Vector2(-1, 0)))
        {
            _isSyncingAll = true;
            _resultMessage = string.Empty;
            _gearSync.SyncSavedGearsets();
        }

        if (isSyncing) ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(_resultMessage))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(_resultColor, _resultMessage);
            ImGui.PopTextWrapPos();
        }

        if (!string.IsNullOrEmpty(_config.LastGearSyncAt))
        {
            ImGui.Spacing();
            var age = FormatSyncAge(_config.LastGearSyncAt);
            if (!string.IsNullOrEmpty(_config.LastGearSyncError))
                ImGui.TextColored(Theme.Muted, $"Failed {age}");
            else
                ImGui.TextColored(Theme.Muted, $"{_config.LastGearSyncJobCount} jobs, {age}");
        }
    }

    private void OnSyncCompleted(bool success, string message)
    {
        _isSyncingCurrent = false;
        _isSyncingAll = false;
        _resultMessage = message;
        _resultColor = success ? Theme.Success : Theme.Error;
    }

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
        _gearSync.SyncCompleted -= OnSyncCompleted;
    }
}
