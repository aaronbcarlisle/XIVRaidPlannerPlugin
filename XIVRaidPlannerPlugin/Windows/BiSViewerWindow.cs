using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

public class BiSViewerWindow : Window, IDisposable
{
    // ==================== Design System Colors ====================

    private static readonly Vector4 ColorGearRaid = new(0.973f, 0.443f, 0.443f, 1f);
    private static readonly Vector4 ColorGearTome = new(0.176f, 0.831f, 0.749f, 1f);
    private static readonly Vector4 ColorGearBaseTome = new(0.376f, 0.647f, 0.980f, 1f);
    private static readonly Vector4 ColorGearCrafted = new(0.984f, 0.573f, 0.235f, 1f);
    private static readonly Vector4 ColorComplete = new(0.133f, 0.773f, 0.369f, 1f);
    private static readonly Vector4 ColorNeedsAug = new(0.918f, 0.702f, 0.031f, 1f);
    private static readonly Vector4 ColorMissing = new(0.322f, 0.322f, 0.357f, 1f);
    private static readonly Vector4 ColorTextPrimary = new(0.941f, 0.941f, 0.961f, 1f);
    private static readonly Vector4 ColorTextSecondary = new(0.631f, 0.631f, 0.667f, 1f);
    private static readonly Vector4 ColorTextMuted = new(0.322f, 0.322f, 0.357f, 1f);
    private static readonly Vector4 ColorAccent = new(0.078f, 0.722f, 0.651f, 1f);

    private static readonly Dictionary<string, Vector4> RoleColors = new()
    {
        ["tank"] = new Vector4(0.353f, 0.624f, 0.831f, 1f),
        ["healer"] = new Vector4(0.353f, 0.831f, 0.565f, 1f),
        ["melee"] = new Vector4(0.831f, 0.353f, 0.353f, 1f),
        ["ranged"] = new Vector4(0.831f, 0.627f, 0.353f, 1f),
        ["caster"] = new Vector4(0.706f, 0.353f, 0.831f, 1f),
    };

    private static readonly Dictionary<string, Vector4> EquippedSourceColors = new()
    {
        ["savage"] = ColorGearRaid, ["raid"] = ColorGearRaid,
        ["tome_up"] = ColorGearTome, ["tome"] = ColorGearTome, ["base_tome"] = ColorGearBaseTome,
        ["crafted"] = ColorGearCrafted,
        ["catchup"] = new Vector4(0.6f, 0.75f, 0.9f, 1f),
        ["normal"] = ColorTextSecondary, ["relic"] = new Vector4(0.8f, 0.4f, 0.8f, 1f),
        ["prep"] = ColorTextMuted, ["unknown"] = ColorTextMuted,
    };

    // ==================== Slot Data ====================

    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["weapon"] = "Weapon", ["head"] = "Head", ["body"] = "Body",
        ["hands"] = "Hands", ["legs"] = "Legs", ["feet"] = "Feet",
        ["earring"] = "Ears", ["necklace"] = "Neck", ["bracelet"] = "Wrists",
        ["ring1"] = "R. Ring", ["ring2"] = "L. Ring",
    };

    private static readonly Dictionary<string, string> SlotIconFileNames = new()
    {
        ["weapon"] = "weapon", ["head"] = "head", ["body"] = "body",
        ["hands"] = "hands", ["legs"] = "legs", ["feet"] = "feet",
        ["earring"] = "earring", ["necklace"] = "necklace", ["bracelet"] = "bracelet",
        ["ring1"] = "ring", ["ring2"] = "ring",
    };

    private static readonly Dictionary<string, string> JobIconFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "pld", ["WAR"] = "war", ["DRK"] = "drk", ["GNB"] = "gnb",
        ["WHM"] = "whm", ["SCH"] = "sch", ["AST"] = "ast", ["SGE"] = "sge",
        ["MNK"] = "mnk", ["DRG"] = "drg", ["NIN"] = "nin", ["SAM"] = "sam", ["RPR"] = "rpr", ["VPR"] = "vpr",
        ["BRD"] = "brd", ["MCH"] = "mch", ["DNC"] = "dnc",
        ["BLM"] = "blm", ["SMN"] = "smn", ["RDM"] = "rdm", ["PCT"] = "pct",
    };

    // Full stat name mapping for materia display
    private static readonly Dictionary<string, string> StatFullNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRT"] = "Critical Hit", ["DH"] = "Direct Hit Rate", ["DET"] = "Determination",
        ["SKS"] = "Skill Speed", ["SPS"] = "Spell Speed", ["TEN"] = "Tenacity", ["PIE"] = "Piety",
    };

    // ==================== Fields ====================

    private readonly BiSDataService _bisData;
    private readonly InventoryService _inventoryService;
    private readonly Configuration _config;

    private readonly Dictionary<string, ISharedImmediateTexture?> _slotIcons = new();
    private readonly Dictionary<string, ISharedImmediateTexture?> _jobIcons = new();
    private readonly Dictionary<uint, ISharedImmediateTexture?> _itemIcons = new();
    private readonly Dictionary<uint, uint> _itemIconIds = new();

    private Dictionary<string, EquippedItemDetails>? _equippedGear;
    private bool _equippedGearStale = true;
    private int _selectedPlayerIndex;

    public event System.Action? OnSyncRequested;

    public BiSViewerWindow(BiSDataService bisData, InventoryService inventoryService, Configuration config)
        : base("XIV Raid Planner \u2014 BiS###XRPBiSViewer", ImGuiWindowFlags.NoCollapse)
    {
        _bisData = bisData;
        _inventoryService = inventoryService;
        _config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(650, 350),
            MaximumSize = new Vector2(1200, 700),
        };
    }

    public void InvalidateEquippedGear() => _equippedGearStale = true;

    // ==================== Main Draw ====================

    public override void Draw()
    {
        var gear = _bisData.ViewedPlayerGear;
        if (gear == null) { DrawEmptyState(); return; }

        if (_bisData.CanViewOtherPlayers && _bisData.AvailablePlayers is { Count: > 0 })
            DrawPlayerDropdown(gear);
        DrawPlayerHeader(gear);
        ImGui.Separator();

        if (_equippedGearStale && IsViewingSelf(gear))
        {
            try { _equippedGear = _inventoryService.ReadEquippedGearEnriched(); }
            catch { _equippedGear = null; }
            _equippedGearStale = false;
        }

        DrawGearTable(gear);
        ImGui.Separator();
        DrawProgressSummary(gear);
        ImGui.TextColored(ColorTextMuted, "Ctrl+Click player name or BiS to open in browser");
    }

    private void DrawEmptyState()
    {
        if (_bisData.IsFetching) { ImGui.TextColored(ColorTextMuted, "Fetching gear data..."); return; }
        if (_bisData.LastError != null)
        {
            ImGui.TextColored(ColorGearRaid, $"Error: {_bisData.LastError}");
            if (ImGui.Button("Retry") && _bisData.CurrentPlayerGear != null)
            { _bisData.InvalidatePlayer(_bisData.CurrentPlayerGear.PlayerId); _ = _bisData.FetchPlayerGearAsync(_bisData.CurrentPlayerGear.PlayerId, isCurrentPlayer: true); }
            return;
        }
        ImGui.TextColored(ColorTextMuted, "No gear data loaded.");
        ImGui.TextColored(ColorTextMuted, "Enter a savage instance or use /xrp bis.");
    }

    private void DrawPlayerDropdown(PlayerGearResponse gear)
    {
        var players = _bisData.AvailablePlayers!;
        var idx = _selectedPlayerIndex < players.Count ? _selectedPlayerIndex : 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##PlayerSelect", $"{players[idx].Name} ({players[idx].Job})"))
        {
            for (var i = 0; i < players.Count; i++)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, RoleColors.GetValueOrDefault(players[i].Role, ColorTextMuted));
                if (ImGui.Selectable($"{players[i].Name} ({players[i].Job})##{players[i].Id}", i == idx))
                { _selectedPlayerIndex = i; _ = _bisData.FetchPlayerGearAsync(players[i].Id); _equippedGearStale = true; }
                ImGui.PopStyleColor();
                if (i == idx) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();
    }

    private void DrawPlayerHeader(PlayerGearResponse gear)
    {
        var jobIcon = GetJobIcon(gear.Job);
        if (jobIcon != null) { var w = jobIcon.GetWrapOrDefault(); if (w != null) { ImGui.Image(w.Handle, new Vector2(24, 24)); ImGui.SameLine(); } }

        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.TextColored(ctrl ? ColorAccent : ColorTextPrimary, $"{gear.PlayerName} ({gear.Job})");
        if (ImGui.IsItemClicked() && ctrl) OpenPlayerInBrowser(gear.PlayerId);

        if (gear.BisLink != null)
        {
            ImGui.SameLine(); ImGui.TextColored(ColorTextMuted, " | "); ImGui.SameLine();
            ImGui.TextColored(ctrl ? ColorAccent : ColorGearTome, "BiS");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+Click to open BiS link");
            if (ImGui.IsItemClicked() && ctrl) OpenBisLinkInBrowser(gear.BisLink);
        }

        if (IsViewingSelf(gear))
        {
            ImGui.SameLine(); ImGui.TextColored(ColorTextMuted, " | "); ImGui.SameLine();
            if (ImGui.SmallButton("Sync Gear")) { OnSyncRequested?.Invoke(); _equippedGearStale = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sync equipped gear to web app");
        }
    }

    // ==================== Gear Table ====================

    private void DrawGearTable(PlayerGearResponse gear)
    {
        // 6 columns: [Slot icon+name] [BiS icon+name] [Src badge] [Status] [Equipped icon+name] [iLv]
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("GearTable", 6, flags)) return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 95);
        ImGui.TableSetupColumn("BiS", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("##Src", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 30);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 24);
        ImGui.TableSetupColumn("Equipped", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("iLv", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 30);
        ImGui.TableHeadersRow();

        foreach (var slot in gear.Gear)
        {
            ImGui.TableNextRow();

            // Col 1: Slot icon + name
            ImGui.TableNextColumn();
            DrawSlotCell(slot.Slot);

            // Col 2: BiS item icon + name (hover tooltip)
            ImGui.TableNextColumn();
            DrawBisItemCell(slot);

            // Col 3: Source badge
            ImGui.TableNextColumn();
            DrawSourceBadge(slot.BisSource);

            // Col 4: Status circle
            ImGui.TableNextColumn();
            DrawStatusCircle(slot);

            // Col 5: Equipped item icon + name
            ImGui.TableNextColumn();
            DrawEquippedCell(slot, gear);

            // Col 6: Equipped iLv
            ImGui.TableNextColumn();
            DrawEquippedLevelCell(slot, gear);
        }

        ImGui.EndTable();
    }

    // ==================== Slot Cell (gold-bright icons) ====================

    private void DrawSlotCell(string slot)
    {
        var slotIcon = GetSlotIcon(slot);
        if (slotIcon != null)
        {
            var wrap = slotIcon.GetWrapOrDefault();
            if (wrap != null) { ImGui.Image(wrap.Handle, new Vector2(20, 20)); ImGui.SameLine(); }
        }
        ImGui.Text(SlotNames.GetValueOrDefault(slot, slot));
    }

    // ==================== BiS Item Cell ====================

    private void DrawBisItemCell(GearSlotStatusDto slot)
    {
        if (slot.ItemId is > 0)
        {
            var iconId = GetItemIconId((uint)slot.ItemId.Value);
            if (iconId > 0)
            {
                var tex = GetItemIcon(iconId);
                var wrap = tex?.GetWrapOrDefault();
                if (wrap != null)
                {
                    var tint = slot.HasItem ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 0.6f);
                    ImGui.Image(wrap.Handle, new Vector2(22, 22), new Vector2(0, 0), new Vector2(1, 1), tint);
                    if (ImGui.IsItemHovered()) DrawBisTooltip(slot);
                    ImGui.SameLine();
                }
            }
        }

        var sourceColor = GetBisSourceColor(slot.BisSource);
        if (!string.IsNullOrEmpty(slot.ItemName))
        {
            ImGui.TextColored(sourceColor, slot.ItemName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot.ItemName);
        }
        else
        {
            ImGui.TextColored(ColorTextMuted, "\u2014");
        }
    }

    private void DrawSourceBadge(string? bisSource)
    {
        if (string.IsNullOrEmpty(bisSource)) { ImGui.TextColored(ColorTextMuted, "-"); return; }
        var (letter, color) = bisSource switch
        {
            "raid" => ("R", ColorGearRaid), "tome" => ("T", ColorGearTome),
            "base_tome" => ("BT", ColorGearBaseTome), "crafted" => ("C", ColorGearCrafted),
            _ => ("-", ColorTextMuted),
        };
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var ts = ImGui.CalcTextSize(letter);
        var pad = new Vector2(4, 1);
        var sz = new Vector2(ts.X + pad.X * 2, ts.Y + pad.Y * 2);
        dl.AddRectFilled(pos, new Vector2(pos.X + sz.X, pos.Y + sz.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.2f)), 3f);
        ImGui.InvisibleButton($"##src_{bisSource}", sz);
        dl.AddText(new Vector2(pos.X + pad.X, pos.Y + pad.Y), ImGui.ColorConvertFloat4ToU32(color), letter);
    }

    private void DrawStatusCircle(GearSlotStatusDto slot)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var lh = ImGui.GetTextLineHeightWithSpacing();
        var center = new Vector2(pos.X + 11, pos.Y + lh / 2);
        var sc = GetBisSourceColor(slot.BisSource);
        if (slot.HasItem && !NeedsAugmentation(slot))
        { dl.AddCircle(center, 6f, ImGui.ColorConvertFloat4ToU32(sc), 12, 2f); dl.AddCircleFilled(center, 3.5f, ImGui.ColorConvertFloat4ToU32(sc), 12); }
        else if (slot.HasItem)
        { dl.AddCircle(center, 6f, ImGui.ColorConvertFloat4ToU32(sc), 12, 2f); }
        else
        { dl.AddCircleFilled(center, 6f, ImGui.ColorConvertFloat4ToU32(new Vector4(ColorMissing.X, ColorMissing.Y, ColorMissing.Z, 0.5f)), 12); }
        ImGui.Dummy(new Vector2(22, lh));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(slot.HasItem ? (NeedsAugmentation(slot) ? "Needs augmentation" : "BiS acquired") : "Missing");
    }

    // ==================== Equipped Cells ====================

    private void DrawEquippedCell(GearSlotStatusDto slot, PlayerGearResponse gear)
    {
        if (IsViewingSelf(gear) && _equippedGear != null && _equippedGear.TryGetValue(slot.Slot, out var eq))
        {
            if (eq.IconId > 0)
            {
                var tex = GetItemIcon(eq.IconId);
                var wrap = tex?.GetWrapOrDefault();
                if (wrap != null) { ImGui.Image(wrap.Handle, new Vector2(22, 22)); if (ImGui.IsItemHovered()) DrawEquippedTooltip(eq); ImGui.SameLine(); }
            }
            var sc = EquippedSourceColors.GetValueOrDefault(eq.Source, ColorTextSecondary);
            ImGui.TextColored(sc, eq.ItemName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(eq.ItemName);
        }
        else
        {
            var source = slot.CurrentSource;
            if (!string.IsNullOrEmpty(source) && source != "unknown")
                ImGui.TextColored(EquippedSourceColors.GetValueOrDefault(source, ColorTextMuted), source);
            else ImGui.TextColored(ColorTextMuted, "\u2014");
        }
    }

    private void DrawEquippedLevelCell(GearSlotStatusDto slot, PlayerGearResponse gear)
    {
        if (IsViewingSelf(gear) && _equippedGear != null && _equippedGear.TryGetValue(slot.Slot, out var eq))
        {
            var c = ColorTextSecondary;
            if (slot.ItemLevel.HasValue)
            { if (eq.ItemLevel >= slot.ItemLevel.Value) c = ColorComplete; else if (eq.ItemLevel >= slot.ItemLevel.Value - 10) c = ColorNeedsAug; else c = ColorGearRaid; }
            ImGui.TextColored(c, eq.ItemLevel.ToString());
        }
        else ImGui.TextColored(ColorTextMuted, "\u2014");
    }

    // ==================== Tooltips ====================

    private void DrawBisTooltip(GearSlotStatusDto slot)
    {
        ImGui.BeginTooltip();

        // Icon + name + status on one line
        if (slot.ItemId is > 0)
        {
            var iconId = GetItemIconId((uint)slot.ItemId.Value);
            if (iconId > 0) { var tex = GetItemIcon(iconId); var w = tex?.GetWrapOrDefault(); if (w != null) { ImGui.Image(w.Handle, new Vector2(40, 40)); ImGui.SameLine(); } }
        }

        ImGui.BeginGroup();
        ImGui.TextColored(ColorTextPrimary, slot.ItemName ?? "Unknown");
        if (!slot.HasItem) { ImGui.SameLine(); ImGui.TextColored(ColorTextMuted, "(missing)"); }
        else if (NeedsAugmentation(slot)) { ImGui.SameLine(); ImGui.TextColored(ColorTextMuted, "(needs augment)"); }
        if (slot.ItemLevel.HasValue) ImGui.TextColored(ColorTextSecondary, $"Item Level {slot.ItemLevel.Value}");
        ImGui.EndGroup();

        // Materia with game icons — same format as equipped: "Name: +Value Stat"
        if (slot.Materia is { Count: > 0 })
        {
            ImGui.Separator();
            foreach (var mat in slot.Materia)
            {
                // Materia game icon
                if (mat.ItemId > 0)
                {
                    var mIconId = GetItemIconId((uint)mat.ItemId);
                    if (mIconId > 0) { var mTex = GetItemIcon(mIconId); var mW = mTex?.GetWrapOrDefault(); if (mW != null) { ImGui.Image(mW.Handle, new Vector2(20, 20)); ImGui.SameLine(); } }
                }
                // Resolve stat value + full name via Lumina for consistent format
                var (fullStatName, statValue) = ResolveBisMateriaStats(mat);
                var displayText = statValue > 0 && !string.IsNullOrEmpty(fullStatName)
                    ? $"{mat.ItemName}: +{statValue} {fullStatName}"
                    : !string.IsNullOrEmpty(fullStatName) ? $"{mat.ItemName} ({fullStatName})" : mat.ItemName;
                ImGui.TextColored(ColorTextSecondary, displayText);
            }
        }

        ImGui.Separator();
        var sourceColor = GetBisSourceColor(slot.BisSource);
        ImGui.TextColored(sourceColor, slot.BisSource switch
        { "raid" => "Savage", "tome" => "Tome (Aug.)", "base_tome" => "Base Tome", "crafted" => "Crafted", _ => "Unknown" });
        ImGui.EndTooltip();
    }

    private void DrawEquippedTooltip(EquippedItemDetails eq)
    {
        ImGui.BeginTooltip();

        if (eq.IconId > 0) { var tex = GetItemIcon(eq.IconId); var w = tex?.GetWrapOrDefault(); if (w != null) { ImGui.Image(w.Handle, new Vector2(40, 40)); ImGui.SameLine(); } }

        ImGui.BeginGroup();
        ImGui.TextColored(ColorTextPrimary, eq.ItemName);
        ImGui.TextColored(ColorTextSecondary, $"Item Level {eq.ItemLevel}");
        ImGui.EndGroup();

        // Equipped materia with full names and stat values
        if (eq.Materia.Count > 0)
        {
            ImGui.Separator();
            foreach (var mat in eq.Materia)
            {
                if (mat.IconId > 0) { var mTex = GetItemIcon(mat.IconId); var mW = mTex?.GetWrapOrDefault(); if (mW != null) { ImGui.Image(mW.Handle, new Vector2(20, 20)); ImGui.SameLine(); } }
                // "Heaven's Eye Materia XII: +54 Determination"
                var displayText = mat.StatValue > 0 && !string.IsNullOrEmpty(mat.FullStatName)
                    ? $"{mat.Name}: +{mat.StatValue} {mat.FullStatName}"
                    : !string.IsNullOrEmpty(mat.FullStatName) ? $"{mat.Name} ({mat.FullStatName})" : mat.Name;
                ImGui.TextColored(ColorTextSecondary, displayText);
            }
        }

        ImGui.Separator();
        var sc = EquippedSourceColors.GetValueOrDefault(eq.Source, ColorTextMuted);
        ImGui.TextColored(sc, eq.Source);
        ImGui.EndTooltip();
    }

    // ==================== Progress ====================

    private void DrawProgressSummary(PlayerGearResponse gear)
    {
        var total = gear.Gear.Count;
        var acquired = 0;
        var needsAug = 0;
        foreach (var s in gear.Gear) { if (s.HasItem) { if (NeedsAugmentation(s)) needsAug++; else acquired++; } }
        ImGui.TextColored(acquired == total ? ColorComplete : ColorTextSecondary, $"Progress: {acquired}/{total} slots complete");
        if (needsAug > 0) { ImGui.SameLine(); ImGui.TextColored(ColorNeedsAug, $" ({needsAug} need augmentation)"); }
        if (gear.TomeWeapon.Pursuing)
        {
            var tw = gear.TomeWeapon;
            var status = tw.HasItem ? (tw.IsAugmented ? "Augmented" : "Have (needs aug)") : "Pursuing";
            ImGui.TextColored(tw.HasItem ? (tw.IsAugmented ? ColorComplete : ColorNeedsAug) : ColorTextMuted, $"Tome Weapon: {status}");
        }
    }

    // ==================== Helpers ====================

    // Cached materia stats: itemId → (fullStatName, statValue)
    private readonly Dictionary<uint, (string FullStatName, int StatValue)> _materiaStatsCache = new();

    /// <summary>Look up materia stat value + full stat name from Lumina using the materia's item ID.</summary>
    private (string FullStatName, int StatValue) ResolveBisMateriaStats(MateriaSlotInfo mat)
    {
        var itemId = (uint)mat.ItemId;
        if (_materiaStatsCache.TryGetValue(itemId, out var cached)) return cached;

        // Fallback from API data
        var statAbbr = !string.IsNullOrEmpty(mat.Stat) ? mat.Stat.ToUpper() : "";
        var fallback = (StatFullNames.GetValueOrDefault(statAbbr, statAbbr), 0);

        try
        {
            // Search Lumina's Materia sheet for a row+grade that produces this item ID
            var materiaSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>();
            if (materiaSheet == null) { _materiaStatsCache[itemId] = fallback; return fallback; }

            foreach (var row in materiaSheet)
            {
                for (byte grade = 0; grade < 10; grade++)
                {
                    try
                    {
                        var ref_ = row.Item[grade];
                        if (ref_.RowId == itemId)
                        {
                            var fullName = row.BaseParam.Value.Name.ToString();
                            var value = row.Value[grade];
                            var result = (fullName, (int)value);
                            _materiaStatsCache[itemId] = result;
                            return result;
                        }
                    }
                    catch { break; }
                }
            }
        }
        catch { /* fall through to fallback */ }

        _materiaStatsCache[itemId] = fallback;
        return fallback;
    }

    private static bool NeedsAugmentation(GearSlotStatusDto slot) => slot.HasItem && !slot.IsAugmented && slot.BisSource == "tome";
    private bool IsViewingSelf(PlayerGearResponse gear) => _bisData.CurrentPlayerGear != null && gear.PlayerId == _bisData.CurrentPlayerGear.PlayerId;
    private static Vector4 GetBisSourceColor(string? s) => s switch { "raid" => ColorGearRaid, "tome" => ColorGearTome, "base_tome" => ColorGearBaseTome, "crafted" => ColorGearCrafted, _ => ColorTextMuted };

    private void OpenPlayerInBrowser(string playerId)
    {
        var baseUrl = !string.IsNullOrEmpty(_config.FrontendBaseUrl) ? _config.FrontendBaseUrl : _config.ApiBaseUrl;
        if (string.IsNullOrEmpty(_config.DefaultGroupShareCode)) return;
        var url = $"{baseUrl.TrimEnd('/')}/group/{_config.DefaultGroupShareCode}";
        if (!string.IsNullOrEmpty(_config.DefaultTierId)) url += $"?tier={_config.DefaultTierId}&player={playerId}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OpenBisLinkInBrowser(string bisLink)
    {
        if (bisLink.StartsWith("bis|") || bisLink.StartsWith("sl|")) return;
        try { Process.Start(new ProcessStartInfo(bisLink) { UseShellExecute = true }); } catch { }
    }

    // ==================== Textures ====================

    private uint GetItemIconId(uint itemId)
    {
        if (_itemIconIds.TryGetValue(itemId, out var c)) return c;
        try
        {
            var s = Plugin.DataManager.GetExcelSheet<Item>();
            if (s == null) { _itemIconIds[itemId] = 0; return 0; }
            var i = s.GetRowOrDefault(itemId);
            if (i == null) { _itemIconIds[itemId] = 0; return 0; }
            var iconId = (uint)i.Value.Icon;
            _itemIconIds[itemId] = iconId;
            return iconId;
        }
        catch { _itemIconIds[itemId] = 0; return 0; }
    }

    private ISharedImmediateTexture? GetItemIcon(uint iconId)
    {
        if (_itemIcons.TryGetValue(iconId, out var c)) return c;
        try { var t = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)); _itemIcons[iconId] = t; return t; }
        catch { _itemIcons[iconId] = null; return null; }
    }

    private ISharedImmediateTexture? GetSlotIcon(string slot)
    {
        var key = SlotIconFileNames.GetValueOrDefault(slot, slot);
        if (_slotIcons.TryGetValue(key, out var c)) return c;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var t = Plugin.TextureProvider.GetFromManifestResource(asm, $"XIVRaidPlannerPlugin.Images.slots.{key}.png");
            _slotIcons[key] = t;
            return t;
        }
        catch { _slotIcons[key] = null; return null; }
    }

    private ISharedImmediateTexture? GetJobIcon(string job)
    {
        if (_jobIcons.TryGetValue(job, out var c)) return c;
        if (!JobIconFileNames.TryGetValue(job, out var fn)) { _jobIcons[job] = null; return null; }
        try
        {
            var t = Plugin.TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), $"XIVRaidPlannerPlugin.Images.jobs.{fn}.png");
            _jobIcons[job] = t;
            return t;
        }
        catch { _jobIcons[job] = null; return null; }
    }

    public void Dispose() { }
}
