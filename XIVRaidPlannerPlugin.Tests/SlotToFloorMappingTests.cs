using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class SlotToFloorMappingTests
{
    [Fact]
    public void LaterFloorOverridesEarlier_ForSharedSlot()
    {
        var priority = new PriorityResponse
        {
            TierFloors = new List<string> { "M9S", "M10S" },
            Priority = new Dictionary<string, Dictionary<string, List<PriorityEntry>>>
            {
                ["floor1"] = new() { ["earring"] = new() },
                ["floor2"] = new() { ["earring"] = new(), ["body"] = new() },
            },
        };
        var map = LootLogCoordinator.BuildSlotToFloorMapping(priority);
        Assert.Equal("M10S", map["earring"]); // later floor wins
        Assert.Equal("M10S", map["body"]);
    }

    [Fact]
    public void UniqueSlot_MapsToCorrectFloor()
    {
        var priority = new PriorityResponse
        {
            TierFloors = new List<string> { "M9S", "M10S", "M11S", "M12S" },
            Priority = new Dictionary<string, Dictionary<string, List<PriorityEntry>>>
            {
                ["floor1"] = new() { ["head"] = new() },
                ["floor2"] = new() { ["body"] = new() },
                ["floor3"] = new() { ["legs"] = new() },
                ["floor4"] = new() { ["weapon"] = new() },
            },
        };
        var map = LootLogCoordinator.BuildSlotToFloorMapping(priority);
        Assert.Equal("M9S", map["head"]);
        Assert.Equal("M10S", map["body"]);
        Assert.Equal("M11S", map["legs"]);
        Assert.Equal("M12S", map["weapon"]);
    }

    [Fact]
    public void EmptyPriority_ReturnsEmptyMapping()
    {
        var priority = new PriorityResponse
        {
            TierFloors = new List<string> { "M9S" },
            Priority = new Dictionary<string, Dictionary<string, List<PriorityEntry>>>(),
        };
        var map = LootLogCoordinator.BuildSlotToFloorMapping(priority);
        Assert.Empty(map);
    }
}
