using System.Numerics;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class ThemeTests
{
    [Fact]
    public void RoleColor_KnownRole_ReturnsExpected()
    {
        Assert.Equal(new Vector4(0.353f, 0.624f, 0.831f, 1f), Theme.RoleColor("tank"));
    }

    [Fact]
    public void RoleColor_UnknownRole_ReturnsWhite()
    {
        Assert.Equal(new Vector4(1, 1, 1, 1), Theme.RoleColor("nonsense"));
    }

    [Fact]
    public void FloorColor_OutOfRange_ReturnsMuted()
    {
        Assert.Equal(Theme.Muted, Theme.FloorColor(99));
    }
}
