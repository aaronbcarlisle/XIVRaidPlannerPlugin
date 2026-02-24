using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// ImGui overlay showing a player's full BiS gear set — a mirror of the web app player card.
/// Members see their own gear; Leads/Owners get a dropdown to view any static member.
/// </summary>
public class BiSViewerWindow : Window, IDisposable
{
    // Role colors matching the web app
    private static readonly Dictionary<string, Vector4> RoleColors = new()
    {
        ["tank"] = new Vector4(0.353f, 0.624f, 0.831f, 1f),
        ["healer"] = new Vector4(0.353f, 0.831f, 0.565f, 1f),
        ["melee"] = new Vector4(0.831f, 0.353f, 0.353f, 1f),
        ["ranged"] = new Vector4(0.831f, 0.627f, 0.353f, 1f),
        ["caster"] = new Vector4(0.706f, 0.353f, 0.831f, 1f),
    };

    // Gear slot display names
    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["weapon"] = "Weapon",
        ["head"] = "Head",
        ["body"] = "Body",
        ["hands"] = "Hands",
        ["legs"] = "Legs",
        ["feet"] = "Feet",
        ["earring"] = "Ears",
        ["necklace"] = "Neck",
        ["bracelet"] = "Wrists",
        ["ring1"] = "R. Ring",
        ["ring2"] = "L. Ring",
    };

    // Gear slot icon file names (shared with PriorityOverlayWindow)
    private static readonly Dictionary<string, string> SlotIconFileNames = new()
    {
        ["weapon"] = "weapon",
        ["head"] = "head",
        ["body"] = "body",
        ["hands"] = "hands",
        ["legs"] = "legs",
        ["feet"] = "feet",
        ["earring"] = "earring",
        ["necklace"] = "necklace",
        ["bracelet"] = "bracelet",
        ["ring1"] = "ring",
        ["ring2"] = "ring",
    };

    // Job abbreviation -> embedded PNG filename
    private static readonly Dictionary<string, string> JobIconFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "pld", ["WAR"] = "war", ["DRK"] = "drk", ["GNB"] = "gnb",
        ["WHM"] = "whm", ["SCH"] = "sch", ["AST"] = "ast", ["SGE"] = "sge",
        ["MNK"] = "mnk", ["DRG"] = "drg", ["NIN"] = "nin", ["SAM"] = "sam", ["RPR"] = "rpr", ["VPR"] = "vpr",
        ["BRD"] = "brd", ["MCH"] = "mch", ["DNC"] = "dnc",
        ["BLM"] = "blm", ["SMN"] = "smn", ["RDM"] = "rdm", ["PCT"] = "pct",
    };

    // BiS source display names
    private static readonly Dictionary<string, string> BisSourceNames = new()
    {
        ["raid"] = "Raid",
        ["tome"] = "Tome",
        ["base_tome"] = "Base Tome",
        ["crafted"] = "Crafted",
    };

    // BiS source colors
    private static readonly Dictionary<string, Vector4> BisSourceColors = new()
    {
        ["raid"] = new Vector4(0.133f, 0.773f, 0.369f, 1f),     // Green
        ["tome"] = new Vector4(0.298f, 0.722f, 0.659f, 1f),     // Teal
        ["base_tome"] = new Vector4(0.4f, 0.7f, 1.0f, 1f),      // Blue
        ["crafted"] = new Vector4(0.961f, 0.620f, 0.043f, 1f),   // Orange
    };

    private static readonly Vector4 ColorSuccess = new(0.133f, 0.773f, 0.369f, 1f);
    private static readonly Vector4 ColorWarning = new(0.961f, 0.843f, 0.043f, 1f);
    private static readonly Vector4 ColorMissing = new(0.937f, 0.267f, 0.267f, 1f);
    private static readonly Vector4 ColorMuted = new(0.4f, 0.4f, 0.45f, 1f);
    private static readonly Vector4 ColorLink = new(0.4f, 0.7f, 1.0f, 1f);

    private readonly BiSDataService _bisData;
    private readonly Configuration _config;

    // Cached textures
    private readonly Dictionary<string, ISharedImmediateTexture?> _slotIcons = new();
    private readonly Dictionary<string, ISharedImmediateTexture?> _jobIcons = new();

    // Player dropdown state
    private int _selectedPlayerIndex;

    public BiSViewerWindow(
        BiSDataService bisData,
        Configuration config)
        : base("XIV Raid Planner — BiS###XRPBiSViewer",
            ImGuiWindowFlags.NoCollapse)
    {
        _bisData = bisData;
        _config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(700, 600),
        };
    }

    public override void Draw()
    {
        var gear = _bisData.ViewedPlayerGear;

        // No data state
        if (gear == null)
        {
            if (_bisData.IsFetching)
            {
                ImGui.TextColored(ColorMuted, "Fetching gear data...");
            }
            else if (_bisData.LastError != null)
            {
                ImGui.TextColored(ColorMissing, $"Error: {_bisData.LastError}");
                if (ImGui.Button("Retry"))
                {
                    // Re-fetch current player gear
                    if (_bisData.CurrentPlayerGear != null)
                    {
                        _bisData.InvalidatePlayer(_bisData.CurrentPlayerGear.PlayerId);
                        _ = _bisData.FetchPlayerGearAsync(_bisData.CurrentPlayerGear.PlayerId, isCurrentPlayer: true);
                    }
                }
            }
            else
            {
                ImGui.TextColored(ColorMuted, "No gear data loaded.");
                ImGui.TextColored(ColorMuted, "Enter a savage instance or use /xrp bis.");
            }
            return;
        }

        // Player dropdown (Lead/Owner only)
        if (_bisData.CanViewOtherPlayers && _bisData.AvailablePlayers is { Count: > 0 })
        {
            DrawPlayerDropdown(gear);
        }

        // Player header
        DrawPlayerHeader(gear);

        ImGui.Separator();

        // Gear table
        DrawGearTable(gear);

        ImGui.Separator();

        // Progress summary
        DrawProgressSummary(gear);

        // Ctrl+Click hint
        ImGui.TextColored(ColorMuted, "Ctrl+Click player name to open in browser");
    }

    private void DrawPlayerDropdown(PlayerGearResponse gear)
    {
        var players = _bisData.AvailablePlayers!;

        // Build combo items
        var currentIdx = _selectedPlayerIndex;
        if (currentIdx >= players.Count) currentIdx = 0;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##PlayerSelect", $"{players[currentIdx].Name} ({players[currentIdx].Job})"))
        {
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var isSelected = i == currentIdx;
                var roleColor = RoleColors.GetValueOrDefault(p.Role, ColorMuted);

                ImGui.PushStyleColor(ImGuiCol.Text, roleColor);
                if (ImGui.Selectable($"{p.Name} ({p.Job})##{p.Id}", isSelected))
                {
                    _selectedPlayerIndex = i;
                    _ = _bisData.FetchPlayerGearAsync(p.Id);
                }
                ImGui.PopStyleColor();

                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
    }

    private void DrawPlayerHeader(PlayerGearResponse gear)
    {
        // Job icon
        var jobIcon = GetJobIcon(gear.Job);
        if (jobIcon != null)
        {
            var wrap = jobIcon.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(24, 24));
                ImGui.SameLine();
            }
        }

        // Player name (Ctrl+Click to open in browser)
        var isCtrlHeld = ImGui.GetIO().KeyCtrl;
        var nameColor = isCtrlHeld ? ColorLink : new Vector4(1f, 1f, 1f, 1f);
        ImGui.TextColored(nameColor, $"{gear.PlayerName} ({gear.Job})");
        if (ImGui.IsItemClicked() && isCtrlHeld)
        {
            OpenPlayerInBrowser(gear.PlayerId);
        }

        // BiS link
        if (gear.BisLink != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, " | ");
            ImGui.SameLine();
            ImGui.TextColored(ColorLink, "BiS");
            if (ImGui.IsItemClicked() && isCtrlHeld)
            {
                OpenBisLinkInBrowser(gear.BisLink);
            }
        }
    }

    private void DrawGearTable(PlayerGearResponse gear)
    {
        if (!ImGui.BeginTable("GearTable", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("BiS", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("iLv", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableHeadersRow();

        foreach (var slot in gear.Gear)
        {
            ImGui.TableNextRow();

            // Slot name with icon
            ImGui.TableNextColumn();
            var slotIcon = GetSlotIcon(slot.Slot);
            if (slotIcon != null)
            {
                var wrap = slotIcon.GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, new Vector2(16, 16));
                    ImGui.SameLine();
                }
            }
            ImGui.Text(SlotNames.GetValueOrDefault(slot.Slot, slot.Slot));

            // BiS source
            ImGui.TableNextColumn();
            var bisSource = slot.BisSource ?? "--";
            var bisColor = BisSourceColors.GetValueOrDefault(bisSource, ColorMuted);
            var bisName = BisSourceNames.GetValueOrDefault(bisSource, "--");
            ImGui.TextColored(bisColor, bisName);

            // Status indicator
            ImGui.TableNextColumn();
            if (slot.HasItem)
            {
                if (NeedsAugmentation(slot))
                {
                    ImGui.TextColored(ColorWarning, "~");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Needs augmentation");
                }
                else
                {
                    ImGui.TextColored(ColorSuccess, "\u2713");
                }
            }
            else
            {
                ImGui.TextColored(ColorMissing, "\u2717");
            }

            // Item name
            ImGui.TableNextColumn();
            var itemName = slot.ItemName ?? "--";
            if (itemName.Length > 22)
                itemName = itemName[..19] + "...";
            ImGui.Text(itemName);
            if (slot.ItemName != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(slot.ItemName);

            // Item level
            ImGui.TableNextColumn();
            if (slot.ItemLevel.HasValue)
                ImGui.Text(slot.ItemLevel.Value.ToString());
            else
                ImGui.TextColored(ColorMuted, "--");
        }

        ImGui.EndTable();
    }

    private void DrawProgressSummary(PlayerGearResponse gear)
    {
        var total = gear.Gear.Count;
        var acquired = 0;
        var needsAug = 0;

        foreach (var slot in gear.Gear)
        {
            if (slot.HasItem)
            {
                if (NeedsAugmentation(slot))
                    needsAug++;
                else
                    acquired++;
            }
        }

        var progressColor = acquired == total ? ColorSuccess : ColorMuted;
        ImGui.TextColored(progressColor, $"Progress: {acquired}/{total} slots complete");

        if (needsAug > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorWarning, $" ({needsAug} need augmentation)");
        }

        // Tome weapon status
        if (gear.TomeWeapon.Pursuing)
        {
            var twStatus = gear.TomeWeapon.HasItem
                ? (gear.TomeWeapon.IsAugmented ? "Augmented" : "Have (needs aug)")
                : "Pursuing";
            var twColor = gear.TomeWeapon.HasItem
                ? (gear.TomeWeapon.IsAugmented ? ColorSuccess : ColorWarning)
                : ColorMuted;
            ImGui.TextColored(twColor, $"Tome Weapon: {twStatus}");
        }
    }

    private static bool NeedsAugmentation(GearSlotStatusDto slot)
    {
        // Tome BiS source items need augmentation if they have the item but it's not augmented
        return slot.HasItem && !slot.IsAugmented && slot.BisSource == "tome";
    }

    private void OpenPlayerInBrowser(string playerId)
    {
        var baseUrl = !string.IsNullOrEmpty(_config.FrontendBaseUrl) ? _config.FrontendBaseUrl : _config.ApiBaseUrl;
        if (string.IsNullOrEmpty(_config.DefaultGroupShareCode)) return;

        var url = $"{baseUrl.TrimEnd('/')}/group/{_config.DefaultGroupShareCode}";
        if (!string.IsNullOrEmpty(_config.DefaultTierId))
            url += $"?tier={_config.DefaultTierId}&player={playerId}";

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Plugin.Log.Error($"Failed to open browser: {ex.Message}"); }
    }

    private void OpenBisLinkInBrowser(string bisLink)
    {
        // Don't open internal preset format links
        if (bisLink.StartsWith("bis|") || bisLink.StartsWith("sl|"))
            return;

        try { Process.Start(new ProcessStartInfo(bisLink) { UseShellExecute = true }); }
        catch (Exception ex) { Plugin.Log.Error($"Failed to open browser: {ex.Message}"); }
    }

    private ISharedImmediateTexture? GetSlotIcon(string slot)
    {
        // Normalize ring1/ring2 to ring
        var iconKey = SlotIconFileNames.GetValueOrDefault(slot, slot);
        if (_slotIcons.TryGetValue(iconKey, out var cached))
            return cached;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"XIVRaidPlannerPlugin.Images.slots.{iconKey}.png";
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, bytes.Length);
                stream.Dispose();
                var texture = Plugin.TextureProvider.GetFromManifestResource(assembly, resourceName);
                _slotIcons[iconKey] = texture;
                return texture;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to load slot icon '{iconKey}': {ex.Message}");
        }

        _slotIcons[iconKey] = null;
        return null;
    }

    private ISharedImmediateTexture? GetJobIcon(string job)
    {
        if (_jobIcons.TryGetValue(job, out var cached))
            return cached;

        if (!JobIconFileNames.TryGetValue(job, out var fileName))
        {
            _jobIcons[job] = null;
            return null;
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"XIVRaidPlannerPlugin.Images.jobs.{fileName}.png";
            var texture = Plugin.TextureProvider.GetFromManifestResource(assembly, resourceName);
            _jobIcons[job] = texture;
            return texture;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Failed to load job icon '{job}': {ex.Message}");
        }

        _jobIcons[job] = null;
        return null;
    }

    public void Dispose() { }
}
