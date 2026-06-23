using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Resolves catalog mount and token entries against Lumina game data sheets.
///
/// Used by the /xrp resolve-ids admin command to produce a verified ID mapping
/// that can be imported into the backend catalog via POST /api/admin/collection-catalog/import-verified-ids.
///
/// Lumina data access runs on the framework/main thread (same pattern as ItemMappingService).
/// API calls (catalog fetch + import POST) run on background thread.
/// </summary>
public class CatalogIdResolverService
{
    private readonly RaidPlannerClient _client;
    private readonly PluginThread _thread;
    private readonly IDataManager _dataManager;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    // Lazily-built lookup: mount name (lower) → row ID
    private Dictionary<string, uint>? _mountNameIndex;

    // Lazily-built lookup: item name (lower) → row ID
    private Dictionary<string, uint>? _itemNameIndex;

    // Catalog reward name → Lumina Mount sheet Singular name.
    // Required when the catalog display name differs from the Lumina singular
    // (e.g. "Felyne Support Team Cart Horn" is the catalog display name
    //  but Lumina Mount.Singular is "Felyne Support Team Cart").
    private static readonly Dictionary<string, string> _mountNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Felyne Support Team Cart Horn"] = "Felyne Support Team Cart",
    };

    public CatalogIdResolverService(
        RaidPlannerClient client,
        PluginThread thread,
        IDataManager dataManager,
        IChatGui chat,
        IPluginLog log)
    {
        _client = client;
        _thread = thread;
        _dataManager = dataManager;
        _chat = chat;
        _log = log;
    }

    /// <summary>
    /// Entry point from /xrp resolve-ids command (framework/main thread).
    /// Builds Lumina indexes, fetches catalog, resolves IDs, then optionally POSTs to backend.
    /// </summary>
    public void ResolveAndReport(bool postToBackend = true)
    {
        // Build Lumina indexes synchronously (must be on framework thread)
        BuildIndexes();

        _chat.Print("[XIV Raid Planner] Resolving catalog IDs from Lumina — fetching catalog...");

        _thread.RunBackground(async () =>
        {
            try
            {
                var catalogResult = await _client.GetMountFarmCatalogAsync();
                if (!catalogResult.IsSuccess || catalogResult.Value == null)
                {
                    _thread.RunOnUi(() =>
                        _chat.PrintError($"[XIV Raid Planner] Failed to fetch catalog: {catalogResult.Error}"));
                    return;
                }

                var catalog = catalogResult.Value.Entries;
                var results = Resolve(catalog);

                var exactCount = results.Count(r => r.Confidence == "exact");
                var noneCount = results.Count(r => r.Confidence == "none");

                _thread.RunOnUi(() =>
                    _chat.Print($"[XIV Raid Planner] Resolved {exactCount}/{catalog.Count} entries with exact confidence ({noneCount} no match)."));

                // Write results to file alongside plugin data
                var outputPath = WriteResultsToFile(results);
                _thread.RunOnUi(() =>
                    _chat.Print($"[XIV Raid Planner] Results written to: {outputPath}"));

                if (postToBackend && exactCount > 0)
                {
                    var exactResults = results.Where(r => r.Confidence == "exact").ToList();
                    _thread.RunOnUi(() =>
                        _chat.Print($"[XIV Raid Planner] Posting {exactResults.Count} exact matches to backend..."));

                    var importResult = await _client.PostVerifiedIdsAsync(exactResults);
                    if (importResult.IsSuccess && importResult.Value != null)
                    {
                        var v = importResult.Value;
                        _thread.RunOnUi(() =>
                            _chat.Print($"[XIV Raid Planner] Import: {v.Updated} updated, {v.AlreadySet} already set, {v.Skipped} skipped."));
                    }
                    else
                    {
                        _thread.RunOnUi(() =>
                            _chat.PrintError($"[XIV Raid Planner] Backend import failed: {importResult.Error}"));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[CatalogIdResolver] Resolve failed: {ex.Message}");
                _thread.RunOnUi(() =>
                    _chat.PrintError("[XIV Raid Planner] ID resolution failed. Check plugin log."));
            }
        });
    }

    /// <summary>
    /// Resolve all catalog entries against Lumina indexes.
    /// Must be called after BuildIndexes().
    /// </summary>
    private List<CatalogIdResolutionResult> Resolve(List<MountFarmCatalogEntry> catalog)
    {
        var results = new List<CatalogIdResolutionResult>();
        var now = DateTime.UtcNow.ToString("o");

        foreach (var entry in catalog)
        {
            var result = new CatalogIdResolutionResult
            {
                SourceDutyKey = entry.TrialId ?? string.Empty,
                RewardName = entry.MountName,
                TokenName = entry.TotemName,
                VerifiedBy = "plugin_lumina",
                VerifiedAt = now,
            };

            // Resolve mount ID
            if (!string.IsNullOrEmpty(entry.MountName))
            {
                var mountResolution = ResolveMountByName(entry.MountName);
                result.GameMountId = mountResolution.RowId;
                result.Confidence = mountResolution.Confidence;
                result.Reason = mountResolution.Reason;
            }
            else
            {
                result.Confidence = "none";
                result.Reason = "No mount name in catalog entry";
            }

            // Resolve token item ID (independent of mount confidence)
            if (!string.IsNullOrEmpty(entry.TotemName))
            {
                var tokenResolution = ResolveItemByName(entry.TotemName);
                result.TokenItemId = tokenResolution.RowId;
                if (result.TokenItemId != null)
                {
                    // Upgrade confidence to "exact" if both resolved
                    if (result.Confidence != "exact") result.TokenReason = tokenResolution.Reason;
                }
                else
                {
                    result.TokenReason = tokenResolution.Reason;
                }
            }

            results.Add(result);
        }

        return results;
    }

    private LuminaResolution ResolveMountByName(string name)
    {
        if (_mountNameIndex == null)
            return new LuminaResolution(null, "none", "Mount index not built");

        // Apply alias: catalog display name → Lumina Singular name
        var lookupName = _mountNameAliases.TryGetValue(name, out var aliased) ? aliased : name;
        var aliasNote = aliased != null ? $" (alias from '{name}')" : string.Empty;

        var key = lookupName.ToLowerInvariant().Trim();
        if (!_mountNameIndex.TryGetValue(key, out var rowId))
            return new LuminaResolution(null, "none", $"'{lookupName}' not found in Mount sheet{aliasNote}");

        return new LuminaResolution(rowId, "exact", $"Exact match in Mount sheet row {rowId}{aliasNote}");
    }

    private LuminaResolution ResolveItemByName(string name)
    {
        if (_itemNameIndex == null)
            return new LuminaResolution(null, "none", "Item index not built");

        var key = name.ToLowerInvariant().Trim();
        if (!_itemNameIndex.TryGetValue(key, out var rowId))
            return new LuminaResolution(null, "none", $"'{name}' not found in Item sheet");

        return new LuminaResolution(rowId, "exact", $"Exact match in Item sheet row {rowId}");
    }

    /// <summary>Build name → row ID lookup tables from Lumina sheets. Must run on framework thread.</summary>
    private void BuildIndexes()
    {
        BuildMountIndex();
        BuildItemIndex();
    }

    private void BuildMountIndex()
    {
        if (_mountNameIndex != null) return;
        _mountNameIndex = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var mountSheet = _dataManager.GetExcelSheet<Mount>();
            if (mountSheet == null)
            {
                _log.Warning("[CatalogIdResolver] Could not load Mount sheet from Lumina");
                return;
            }

            var conflicts = new HashSet<string>();
            foreach (var row in mountSheet)
            {
                var name = row.Singular.ToString().Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var key = name.ToLowerInvariant();
                if (_mountNameIndex.ContainsKey(key))
                {
                    // Ambiguous — remove so we don't use a wrong ID
                    _mountNameIndex.Remove(key);
                    conflicts.Add(key);
                }
                else if (!conflicts.Contains(key))
                {
                    _mountNameIndex[key] = row.RowId;
                }
            }

            _log.Info($"[CatalogIdResolver] Built mount index: {_mountNameIndex.Count} unique names ({conflicts.Count} ambiguous removed)");
        }
        catch (Exception ex)
        {
            _log.Error($"[CatalogIdResolver] Failed to build mount index: {ex.Message}");
            _mountNameIndex = null;
        }
    }

    private void BuildItemIndex()
    {
        if (_itemNameIndex != null) return;
        _itemNameIndex = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var itemSheet = _dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                _log.Warning("[CatalogIdResolver] Could not load Item sheet from Lumina");
                return;
            }

            var conflicts = new HashSet<string>();
            foreach (var item in itemSheet)
            {
                var name = item.Name.ToString().Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var key = name.ToLowerInvariant();
                if (_itemNameIndex.ContainsKey(key))
                {
                    _itemNameIndex.Remove(key);
                    conflicts.Add(key);
                }
                else if (!conflicts.Contains(key))
                {
                    _itemNameIndex[key] = item.RowId;
                }
            }

            _log.Info($"[CatalogIdResolver] Built item index: {_itemNameIndex.Count} unique names ({conflicts.Count} ambiguous removed)");
        }
        catch (Exception ex)
        {
            _log.Error($"[CatalogIdResolver] Failed to build item index: {ex.Message}");
            _itemNameIndex = null;
        }
    }

    private static string WriteResultsToFile(List<CatalogIdResolutionResult> results)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "XIVRaidPlannerPlugin");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "collection_resolved_ids.json");
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private record LuminaResolution(uint? RowId, string Confidence, string Reason);
}

// ── DTOs for resolver output / import request ─────────────────────────────────

public class CatalogIdResolutionResult
{
    [JsonPropertyName("sourceDutyKey")] public string SourceDutyKey { get; set; } = string.Empty;
    [JsonPropertyName("rewardName")] public string RewardName { get; set; } = string.Empty;
    [JsonPropertyName("gameMountId")] public uint? GameMountId { get; set; }
    [JsonPropertyName("tokenName")] public string? TokenName { get; set; }
    [JsonPropertyName("tokenItemId")] public uint? TokenItemId { get; set; }
    [JsonPropertyName("confidence")] public string Confidence { get; set; } = "none";
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("tokenReason")] public string? TokenReason { get; set; }
    [JsonPropertyName("verifiedBy")] public string VerifiedBy { get; set; } = "plugin_lumina";
    [JsonPropertyName("verifiedAt")] public string VerifiedAt { get; set; } = string.Empty;
}

public class CatalogIdImportResult
{
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("alreadySet")] public int AlreadySet { get; set; }
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
}
