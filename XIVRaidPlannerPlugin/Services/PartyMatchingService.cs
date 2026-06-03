using System;
using System.Collections.Generic;
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
        var partyNames = new List<string>();
        foreach (var member in _partyList)
        {
            var name = member.Name.ToString().Trim();
            if (!string.IsNullOrEmpty(name))
                partyNames.Add(name);
        }

        var matchResult = PartyMatcher.Match(partyNames, plannerPlayers, _config.PlayerNameOverrides);
        CurrentMatches = matchResult.Matches;
        UnmatchedPlayers = matchResult.UnmatchedPlayers;
        UnmatchedPartyMembers = matchResult.UnmatchedPartyMembers;

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

    /// <summary>
    /// Fires when a manual override is added, changed, or removed.
    /// Args: (characterName, newPlayerId-or-null-on-remove).
    /// Subscribers (e.g., BiSDataService re-fetch) must filter on whether the change affects them.
    /// </summary>
    public event Action<string, string?>? OnOverrideChanged;

    /// <summary>Add or change a manual override and re-match.</summary>
    public void SetOverride(string characterName, string playerId, List<PlayerInfo> plannerPlayers)
    {
        _config.PlayerNameOverrides[characterName] = playerId;
        _config.Save();
        MatchParty(plannerPlayers);
        OnOverrideChanged?.Invoke(characterName, playerId);
    }

    /// <summary>Remove a manual override and re-match.</summary>
    public void RemoveOverride(string characterName, List<PlayerInfo> plannerPlayers)
    {
        if (!_config.PlayerNameOverrides.Remove(characterName)) return;
        _config.Save();
        MatchParty(plannerPlayers);
        OnOverrideChanged?.Invoke(characterName, null);
    }
}
