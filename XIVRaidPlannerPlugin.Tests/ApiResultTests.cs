using XIVRaidPlannerPlugin.Api;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class ApiResultTests
{
    [Fact]
    public void Ok_IsSuccess_WithValue()
    {
        var r = ApiResult<int>.Ok(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
        Assert.Equal(ApiError.None, r.Error);
    }

    [Fact]
    public void Fail_IsNotSuccess_WithError()
    {
        var r = ApiResult<int>.Fail(ApiError.Unauthorized);
        Assert.False(r.IsSuccess);
        Assert.Equal(ApiError.Unauthorized, r.Error);
    }
}
