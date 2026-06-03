using XIVRaidPlannerPlugin.Services;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class ItemSourceClassifierTests
{
    [Theory]
    [InlineData("Augmented Quetzalli Helm", 790, "tome_up")]
    [InlineData("Heavyweight Cuirass", 795, "savage")]
    [InlineData("Quetzalli Mail", 780, "tome")]
    [InlineData("Claro Walnut Ring", 770, "crafted")]
    [InlineData("Manderville Blade", 770, "relic")]
    [InlineData("Some Normal Chest", 760, "normal")]
    [InlineData("Junk", 100, "unknown")]
    public void Classify_NameAndLevel(string name, int ilv, string expected)
        => Assert.Equal(expected, ItemSourceClassifier.Classify(name, ilv));

    [Theory]
    [InlineData("savage", "raid", true)]
    [InlineData("tome_up", "tome", true)]
    [InlineData("crafted", "raid", false)]
    public void SourceMatchesBis(string equipped, string bis, bool expected)
        => Assert.Equal(expected, ItemSourceClassifier.SourceMatchesBis(equipped, bis));
}
