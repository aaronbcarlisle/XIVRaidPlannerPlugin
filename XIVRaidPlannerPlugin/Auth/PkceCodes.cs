using System;
using System.Security.Cryptography;
using System.Text;

namespace XIVRaidPlannerPlugin.Auth;

/// <summary>PKCE verifier/challenge + CSRF state for the browser sign-in flow.</summary>
public sealed class PkceCodes
{
    public required string Verifier { get; init; }
    public required string Challenge { get; init; }
    public required string State { get; init; }

    public static PkceCodes Generate()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var sha = SHA256.Create();
        var challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return new PkceCodes { Verifier = verifier, Challenge = challenge, State = state };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
