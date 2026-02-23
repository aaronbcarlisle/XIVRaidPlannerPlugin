using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Detects when the player enters/leaves a savage raid instance.
/// Resolves territory IDs to floor numbers using Lumina data.
/// </summary>
public class TerritoryService : IDisposable
{
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    /// <summary>Currently detected floor number (1-4), or null if not in a savage instance.</summary>
    public int? CurrentFloor { get; private set; }

    /// <summary>Current floor display name (e.g., "M10S"), or null.</summary>
    public string? CurrentFloorName { get; private set; }

    /// <summary>Fired when entering a savage instance. Arg = floor number (1-4).</summary>
    public event Action<int>? OnSavageEntered;

    /// <summary>Fired when leaving a savage instance.</summary>
    public event Action? OnSavageExited;

    // Territory ID -> (floor number, floor name) mapping.
    // Populated at init from Lumina data; fallback hardcoded values for known tiers.
    private readonly Dictionary<uint, (int Floor, string Name)> _savageTerritories = new();

    public TerritoryService(IClientState clientState, IDataManager dataManager, IPluginLog log)
    {
        _clientState = clientState;
        _dataManager = dataManager;
        _log = log;

        BuildTerritoryMap();

        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    private void BuildTerritoryMap()
    {
        // Try to resolve territory IDs from Lumina's TerritoryType sheet.
        // Each savage duty has a unique TerritoryType row.
        try
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            {
                foreach (var row in sheet)
                {
                    var cfcId = row.ContentFinderCondition.RowId;
                    if (cfcId == 0) continue;

                    var cfc = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>().GetRowOrDefault(cfcId);
                    if (cfc == null) continue;

                    var name = cfc.Value.Name.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    // Match savage duties by name pattern
                    if (!name.Contains("Savage", StringComparison.OrdinalIgnoreCase)) continue;

                    // Try to extract floor number from the duty name
                    var floorInfo = ParseSavageDutyName(name);
                    if (floorInfo.HasValue)
                    {
                        _savageTerritories[row.RowId] = floorInfo.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to build territory map from Lumina: {ex.Message}");
        }

        // Hardcoded fallbacks for known savage territories
        // Anabaseios (Pandaemonium tier 3)
        AddFallback(1148, 1, "P9S");
        AddFallback(1150, 2, "P10S");
        AddFallback(1152, 3, "P11S");
        AddFallback(1154, 4, "P12S");
        // AAC Heavyweight (Dawntrail 7.4) - placeholder IDs
        AddFallback(1243, 1, "M9S");
        AddFallback(1244, 2, "M10S");
        AddFallback(1245, 3, "M11S");
        AddFallback(1246, 4, "M12S");

        _log.Information($"Territory map built with {_savageTerritories.Count} savage instances");
    }

    private void AddFallback(uint territoryId, int floor, string name)
    {
        _savageTerritories.TryAdd(territoryId, (floor, name));
    }

    // Ordinal words to numbers for duty names like "The Ninth Circle (Savage)"
    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["First"] = 1, ["Second"] = 2, ["Third"] = 3, ["Fourth"] = 4,
        ["Fifth"] = 5, ["Sixth"] = 6, ["Seventh"] = 7, ["Eighth"] = 8,
        ["Ninth"] = 9, ["Tenth"] = 10, ["Eleventh"] = 11, ["Twelfth"] = 12,
    };

    private static (int Floor, string Name)? ParseSavageDutyName(string dutyName)
    {
        // Pattern 1: "AAC Heavyweight M1 (Savage)" -> M1S, floor 1
        for (var i = 0; i < dutyName.Length - 1; i++)
        {
            if (dutyName[i] == 'M' && char.IsDigit(dutyName[i + 1]))
            {
                var numStr = "";
                for (var j = i + 1; j < dutyName.Length && char.IsDigit(dutyName[j]); j++)
                    numStr += dutyName[j];

                if (int.TryParse(numStr, out var num))
                {
                    var floor = (num - 1) % 4 + 1;
                    var floorName = $"M{num}S";
                    return (floor, floorName);
                }
            }
        }

        // Pattern 2: "... The Ninth Circle (Savage)" -> P9S, floor 1
        // Covers Pandaemonium (Asphodelos, Abyssos, Anabaseios)
        foreach (var (word, num) in Ordinals)
        {
            if (dutyName.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                dutyName.Contains("Circle", StringComparison.OrdinalIgnoreCase))
            {
                var floor = (num - 1) % 4 + 1;
                var floorName = $"P{num}S";
                return (floor, floorName);
            }
        }

        // Pattern 3: "Eden's ... (Savage)" with ordinals -> E1S-E12S
        foreach (var (word, num) in Ordinals)
        {
            if (dutyName.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                dutyName.Contains("Eden", StringComparison.OrdinalIgnoreCase))
            {
                var floor = (num - 1) % 4 + 1;
                var floorName = $"E{num}S";
                return (floor, floorName);
            }
        }

        // Pattern 4: Generic savage duty — still register it as floor 1 so detection works
        if (dutyName.Contains("Savage", StringComparison.OrdinalIgnoreCase))
        {
            // Use the duty name as-is for display
            return (1, dutyName.Replace("(Savage)", "").Trim());
        }

        return null;
    }

    private void OnTerritoryChanged(ushort territoryId)
    {
        if (_savageTerritories.TryGetValue(territoryId, out var info))
        {
            if (CurrentFloor == null)
            {
                _log.Information($"Entered savage instance: {info.Name} (floor {info.Floor}, territory {territoryId})");
                CurrentFloor = info.Floor;
                CurrentFloorName = info.Name;
                OnSavageEntered?.Invoke(info.Floor);
            }
        }
        else
        {
            if (CurrentFloor != null)
            {
                _log.Information($"Left savage instance (territory {territoryId})");
                CurrentFloor = null;
                CurrentFloorName = null;
                OnSavageExited?.Invoke();
            }
        }
    }

    /// <summary>Check current territory on demand (e.g., when plugin loads while already in an instance).</summary>
    public void CheckCurrentTerritory()
    {
        var territoryId = _clientState.TerritoryType;
        if (territoryId != 0)
            OnTerritoryChanged(territoryId);
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
    }
}
