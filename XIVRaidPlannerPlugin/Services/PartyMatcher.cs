using System;
using System.Collections.Generic;
using System.Linq;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Services;

public sealed class PartyMatchResult
{
    public Dictionary<string, string> Matches { get; } = new();
    public List<PlayerInfo> UnmatchedPlayers { get; } = new();
    public List<string> UnmatchedPartyMembers { get; } = new();
}

/// <summary>Pure party-name → planner-player matching algorithm.</summary>
public static class PartyMatcher
{
    public static PartyMatchResult Match(
        IEnumerable<string> partyNames,
        IReadOnlyList<PlayerInfo> plannerPlayers,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new PartyMatchResult();
        result.UnmatchedPlayers.AddRange(plannerPlayers);

        foreach (var partyName in partyNames)
        {
            if (overrides.TryGetValue(partyName, out var overrideId))
            {
                var op = result.UnmatchedPlayers.FirstOrDefault(p => p.Id == overrideId);
                if (op != null)
                {
                    result.Matches[partyName] = overrideId;
                    result.UnmatchedPlayers.Remove(op);
                    continue;
                }
            }

            var exact = result.UnmatchedPlayers.FirstOrDefault(
                p => string.Equals(p.Name.Trim(), partyName, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                result.Matches[partyName] = exact.Id;
                result.UnmatchedPlayers.Remove(exact);
                continue;
            }

            result.UnmatchedPartyMembers.Add(partyName);
        }

        return result;
    }
}
