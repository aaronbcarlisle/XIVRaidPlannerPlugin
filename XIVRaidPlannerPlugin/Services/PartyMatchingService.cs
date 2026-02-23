using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

/// <summary>
/// Matches in-game party members to FFXIV Raid Planner player entries.
/// Uses name matching with manual override support.
/// </summary>
public class PartyMatchingService
{
    private readonly IPartyList _partyList;
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    /// <summary>Cached matches: in-game name -> planner player ID.</summary>
    public Dictionary<string, string> CurrentMatches { get; private set; } = new();

    /// <summary>Players from the API that couldn't be matched to party members.</summary>
    public List<PlayerInfo> UnmatchedPlayers { get; private set; } = new();

    /// <summary>Party member names that couldn't be matched to planner players.</summary>
    public List<string> UnmatchedPartyMembers { get; private set; } = new();

    public PartyMatchingService(IPartyList partyList, Configuration config, IPluginLog log)
    {
        _partyList = partyList;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Match current party members against planner players.
    /// Call this on zone entry or when party composition changes.
    /// </summary>
    public void MatchParty(List<PlayerInfo> plannerPlayers)
    {
        CurrentMatches.Clear();
        UnmatchedPlayers = new List<PlayerInfo>(plannerPlayers);
        UnmatchedPartyMembers = new List<string>();

        // Get party member names
        var partyNames = new List<string>();
        foreach (var member in _partyList)
        {
            var name = member.Name.ToString().Trim();
            if (!string.IsNullOrEmpty(name))
                partyNames.Add(name);
        }

        foreach (var partyName in partyNames)
        {
            // 1. Check manual overrides first
            if (_config.PlayerNameOverrides.TryGetValue(partyName, out var overrideId))
            {
                var overridePlayer = UnmatchedPlayers.FirstOrDefault(p => p.Id == overrideId);
                if (overridePlayer != null)
                {
                    CurrentMatches[partyName] = overrideId;
                    UnmatchedPlayers.Remove(overridePlayer);
                    continue;
                }
            }

            // 2. Exact name match (case-insensitive)
            var exactMatch = UnmatchedPlayers.FirstOrDefault(
                p => string.Equals(p.Name.Trim(), partyName, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                CurrentMatches[partyName] = exactMatch.Id;
                UnmatchedPlayers.Remove(exactMatch);
                continue;
            }

            // 3. No match found
            UnmatchedPartyMembers.Add(partyName);
        }

        _log.Information(
            $"Party matching: {CurrentMatches.Count} matched, " +
            $"{UnmatchedPartyMembers.Count} unmatched party members, " +
            $"{UnmatchedPlayers.Count} unmatched planner players");
    }

    /// <summary>Get the planner player ID for a given in-game character name.</summary>
    public string? GetPlayerIdForName(string characterName)
    {
        var trimmed = characterName.Trim();

        // Check matched party members first
        if (CurrentMatches.TryGetValue(trimmed, out var playerId))
            return playerId;

        // Fallback: check manual overrides directly (handles solo/unmatched cases)
        if (_config.PlayerNameOverrides.TryGetValue(trimmed, out var overrideId))
            return overrideId;

        return null;
    }

    /// <summary>Add a manual override and re-match.</summary>
    public void SetOverride(string characterName, string playerId, List<PlayerInfo> plannerPlayers)
    {
        _config.PlayerNameOverrides[characterName] = playerId;
        _config.Save();
        MatchParty(plannerPlayers);
    }
}
