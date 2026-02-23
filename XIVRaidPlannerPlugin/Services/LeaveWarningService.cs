using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Warns the player if they try to leave a savage instance while unclaimed priority loot exists.
/// Checks if the current player is in the top 3 priority for any pending drop.
/// </summary>
public class LeaveWarningService
{
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    /// <summary>Whether the leave warning popup should be shown.</summary>
    public bool ShouldShowWarning { get; private set; }

    /// <summary>Items the player has high priority for but hasn't claimed.</summary>
    public List<PriorityWarningItem> WarningItems { get; private set; } = new();

    public LeaveWarningService(Configuration config, IPluginLog log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Check if the player should be warned about leaving.
    /// Call this when the leave duty dialog appears.
    /// </summary>
    /// <param name="currentPlayerId">The planner player ID of the current user.</param>
    /// <param name="distributedLoot">Loot that has already been distributed this session.</param>
    /// <param name="floorPriority">Priority data for the current floor.</param>
    public void CheckLeaveWarning(
        string? currentPlayerId,
        List<LootEvent> distributedLoot,
        Dictionary<string, List<PriorityEntry>>? floorPriority)
    {
        ShouldShowWarning = false;
        WarningItems.Clear();

        if (!_config.EnableLeaveWarning || string.IsNullOrEmpty(currentPlayerId) || floorPriority == null)
            return;

        // Check each drop type in the floor priority
        foreach (var (dropType, priorityList) in floorPriority)
        {
            // Check if this item has already been distributed
            var isDistributed = distributedLoot.Any(l =>
                (l.GearSlot == dropType) ||
                (l.MaterialType == dropType) ||
                (dropType == "ring" && (l.GearSlot == "ring1" || l.GearSlot == "ring2")));

            if (isDistributed) continue;

            // Check if current player is in top 3 priority
            var playerEntry = priorityList
                .Take(3)
                .FirstOrDefault(e => e.PlayerId == currentPlayerId);

            if (playerEntry != null)
            {
                var rank = priorityList.IndexOf(playerEntry) + 1;
                WarningItems.Add(new PriorityWarningItem
                {
                    DropType = dropType,
                    Rank = rank,
                    Score = playerEntry.Score,
                });
            }
        }

        ShouldShowWarning = WarningItems.Count > 0;

        if (ShouldShowWarning)
        {
            _log.Information($"Leave warning: player has priority for {WarningItems.Count} unclaimed drops");
        }
    }

    /// <summary>Dismiss the warning (player chose "Leave Anyway").</summary>
    public void Dismiss()
    {
        ShouldShowWarning = false;
        WarningItems.Clear();
    }
}

public class PriorityWarningItem
{
    /// <summary>The drop type (gear slot or material name).</summary>
    public string DropType { get; set; } = string.Empty;

    /// <summary>Player's rank for this drop (1-3).</summary>
    public int Rank { get; set; }

    /// <summary>Player's priority score.</summary>
    public int Score { get; set; }
}
