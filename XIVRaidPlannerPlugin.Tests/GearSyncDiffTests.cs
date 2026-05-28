using System.Collections.Generic;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class GearSyncDiffTests
{
    [Fact]
    public void CountsNewlyAcquiredSlots()
    {
        var fresh = new List<GearSlotStatusDto>
        {
            new() { Slot = "head", HasItem = false, CurrentSource = "none" },
            new() { Slot = "body", HasItem = true,  CurrentSource = "savage" },
        };
        var updated = new List<GearSlotStatusDto>
        {
            new() { Slot = "head", HasItem = true,  CurrentSource = "savage" },
            new() { Slot = "body", HasItem = true,  CurrentSource = "savage" },
        };
        var diff = GearSyncService.Diff(updated, fresh);
        Assert.Equal(1, diff.ChangeCount);
        Assert.Equal(new[] { "head" }, diff.NewlyAcquired.ToArray());
    }
}
