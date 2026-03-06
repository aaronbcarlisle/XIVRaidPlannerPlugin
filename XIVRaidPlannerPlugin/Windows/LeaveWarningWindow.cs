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
    private bool _stylesPushed;

    public LeaveWarningWindow(LeaveWarningService leaveWarning, IGameGui gameGui)
        : base("##LeaveWarning",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav)
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
        _stylesPushed = false;

        // If warning was already dismissed, close immediately to prevent flash
        if (!_leaveWarning.ShouldShowWarning)
        {
            IsOpen = false;
            return;
        }

        // Only show when the game dialog is visible — hide when it's gone
        // Don't dismiss the warning state here so it re-appears if the dialog reopens
        var addon = _gameGui.GetAddonByName("SelectYesno", 1);
        if (addon.IsNull || !addon.IsVisible)
        {
            IsOpen = false;
            return;
        }

        // Anchor above the game dialog
        var addonPos = addon.Position;
        var addonWidth = addon.ScaledWidth;
        var x = addonPos.X + addonWidth * 0.5f;
        var y = addonPos.Y - 14;

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always, new Vector2(0.5f, 1f));
        _addonTopCenter = addonPos.Y;
        _hasAddonAnchor = true;

        // Semi-transparent dark background with red accent border
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.12f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.9f, 0.25f, 0.25f, 0.8f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));
        _stylesPushed = true;
    }

    public override void PostDraw()
    {
        if (_stylesPushed)
        {
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

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

        // Warning header
        ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "!! Unclaimed Priority Loot !!");
        ImGui.Spacing();

        foreach (var item in _leaveWarning.WarningItems)
        {
            var rankColor = item.Rank switch
            {
                1 => new Vector4(1, 0.843f, 0, 1),
                2 => new Vector4(0.753f, 0.753f, 0.753f, 1),
                3 => new Vector4(0.804f, 0.498f, 0.196f, 1),
                _ => new Vector4(1, 1, 1, 1),
            };

            ImGui.TextColored(rankColor, $"  #{item.Rank}");
            ImGui.SameLine();
            ImGui.Text(FormatDropName(item.DropType));
        }

        ImGui.Spacing();

        // Capture the bottom-center of the window for the arrow
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
