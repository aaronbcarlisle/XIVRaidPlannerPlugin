using System;
using System.Security.Cryptography;
using System.Text;
using XIVRaidPlannerPlugin.Auth;
using Xunit;

namespace XIVRaidPlannerPlugin.Tests;

public class PkceCodesTests
{
    [Fact]
    public void Challenge_IsBase64UrlSha256OfVerifier()
    {
        var codes = PkceCodes.Generate();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(codes.Verifier));
        var expected = Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, codes.Challenge);
    }

    [Fact]
    public void Generate_ProducesDistinctVerifierAndState()
    {
        var a = PkceCodes.Generate();
        var b = PkceCodes.Generate();
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.State, b.State);
        Assert.True(a.Verifier.Length >= 43);
    }
}
