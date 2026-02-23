using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Warning overlay shown when the player tries to leave a savage instance
/// while they have high priority for unclaimed loot.
/// Rendered as a borderless tooltip-style panel anchored above the game's
/// "Abandon duty?" dialog with a downward-pointing arrow connector.
/// </summary>
public class LeaveWarningWindow : Window, IDisposable
{
    private readonly LeaveWarningService _leaveWarning;
    private readonly IGameGui _gameGui;

    // Cached position for the arrow drawing
    private Vector2 _windowBottomCenter;
    private float _addonTopCenter;
    private bool _hasAddonAnchor;

    public LeaveWarningWindow(LeaveWarningService leaveWarning, IGameGui gameGui)
        : base("##LeaveWarning",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar)
    {
        _leaveWarning = leaveWarning;
        _gameGui = gameGui;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 80),
            MaximumSize = new Vector2(500, 400),
        };

        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        _hasAddonAnchor = false;

        // Try to anchor above the game's SelectYesno ("Abandon duty?") dialog
        var addon = _gameGui.GetAddonByName("SelectYesno", 1);
        if (!addon.IsNull && addon.IsVisible)
        {
            var addonPos = addon.Position;
            var addonWidth = addon.ScaledWidth;

            // Center horizontally over the dialog, place above with gap for arrow
            var x = addonPos.X + addonWidth * 0.5f;
            var y = addonPos.Y - 14; // gap for the arrow

            ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always, new Vector2(0.5f, 1f));

            _addonTopCenter = addonPos.Y;
            _hasAddonAnchor = true;
        }
        else
        {
            // Fallback: center on screen
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f,
                            viewport.WorkPos.Y + viewport.WorkSize.Y * 0.4f),
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f));
        }

        // Semi-transparent dark background
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.12f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.9f, 0.25f, 0.25f, 0.8f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

        ImGui.SetNextWindowFocus();
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);

        // Draw the arrow connector from our window down to the game dialog
        if (_hasAddonAnchor)
        {
            var drawList = ImGui.GetForegroundDrawList();
            var arrowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.25f, 0.25f, 0.8f));

            var tipX = _windowBottomCenter.X;
            var tipY = _addonTopCenter - 2;
            var baseY = _windowBottomCenter.Y + 2;

            // Triangle pointing down
            drawList.AddTriangleFilled(
                new Vector2(tipX - 8, baseY),
                new Vector2(tipX + 8, baseY),
                new Vector2(tipX, tipY),
                arrowColor);
        }
    }

    public override void Draw()
    {
        if (!_leaveWarning.ShouldShowWarning || _leaveWarning.WarningItems.Count == 0)
        {
            IsOpen = false;
            return;
        }

        // Warning icon + header
        ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "!! Unclaimed Priority Loot !!");
        ImGui.Spacing();

        foreach (var item in _leaveWarning.WarningItems)
        {
            var rankColor = item.Rank switch
            {
                1 => new Vector4(1, 0.843f, 0, 1),   // Gold
                2 => new Vector4(0.753f, 0.753f, 0.753f, 1), // Silver
                3 => new Vector4(0.804f, 0.498f, 0.196f, 1), // Bronze
                _ => new Vector4(1, 1, 1, 1),
            };

            ImGui.TextColored(rankColor, $"  #{item.Rank}");
            ImGui.SameLine();
            ImGui.Text(FormatDropName(item.DropType));
        }

        ImGui.Spacing();

        // Capture the bottom-center of the window content for the arrow
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        _windowBottomCenter = new Vector2(windowPos.X + windowSize.X * 0.5f, windowPos.Y + windowSize.Y);
    }

    private static string FormatDropName(string drop)
    {
        return drop switch
        {
            "ring" or "ring1" or "ring2" => "Ring",
            "universal_tomestone" => "Universal Tomestone",
            _ => char.ToUpper(drop[0]) + drop[1..],
        };
    }

    public void Dispose() { }
}
