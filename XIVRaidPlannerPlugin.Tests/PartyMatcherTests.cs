using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class PartyMatcherTests
{
    private static PlayerInfo P(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void ExactNameMatch_CaseInsensitive()
    {
        var players = new List<PlayerInfo> { P("1", "Cloud Strife"), P("2", "Tifa Lockhart") };
        var result = PartyMatcher.Match(new[] { "cloud strife" }, players, new Dictionary<string, string>());
        Assert.Equal("1", result.Matches["cloud strife"]);
        Assert.Single(result.UnmatchedPlayers);
    }

    [Fact]
    public void Override_TakesPrecedence()
    {
        var players = new List<PlayerInfo> { P("1", "Cloud Strife") };
        var overrides = new Dictionary<string, string> { ["Cloudy McCloud"] = "1" };
        var result = PartyMatcher.Match(new[] { "Cloudy McCloud" }, players, overrides);
        Assert.Equal("1", result.Matches["Cloudy McCloud"]);
    }

    [Fact]
    public void NoMatch_GoesToUnmatchedPartyMembers()
    {
        var result = PartyMatcher.Match(new[] { "Random Person" }, new List<PlayerInfo>(), new Dictionary<string, string>());
        Assert.Contains("Random Person", result.UnmatchedPartyMembers);
    }
}
