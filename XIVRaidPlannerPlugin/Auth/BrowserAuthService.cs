using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Auth;

/// <summary>One-click browser sign-in: loopback listener + PKCE code exchange → xrp_ key.</summary>
public sealed class BrowserAuthService
{
    private readonly Configuration _config;
    private readonly RaidPlannerClient _api;
    private readonly IPluginLog _log;

    public BrowserAuthService(Configuration config, RaidPlannerClient api, IPluginLog log)
    {
        _config = config;
        _api = api;
        _log = log;
    }

    /// <summary>Runs the full flow. Returns Ok with the minted key on success; otherwise an ApiError.</summary>
    public async Task<ApiResult<string>> SignInAsync(CancellationToken ct = default)
    {
        var pkce = PkceCodes.Generate();
        using var listener = new HttpListener();
        var port = GetFreeLoopbackPort();
        var redirect = $"http://127.0.0.1:{port}/callback/";
        listener.Prefixes.Add(redirect);
        listener.Start();

        var url = $"{_config.EffectiveFrontendBaseUrl.TrimEnd('/')}/plugin-auth" +
                  $"?redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&state={pkce.State}&code_challenge={pkce.Challenge}&code_challenge_method=S256";

        OpenUrl(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return ApiResult<string>.Fail(ApiError.Network);
        }

        var query = context.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];
        await WriteBrowserResponse(context, "You're signed in. Return to the game.");

        if (returnedState != pkce.State || string.IsNullOrEmpty(code))
        {
            _log.Error("[BrowserAuth] state mismatch or missing code");
            return ApiResult<string>.Fail(ApiError.Unauthorized);
        }

        var result = await _api.ExchangePluginAuthCodeAsync(code, pkce.Verifier, ct);
        if (result.IsSuccess)
        {
            _config.ApiKey = result.Value!;
            _config.Save();
            _api.UpdateAuth();
        }

        return result;
    }

    private void OpenUrl(string url)
    {
        try
        {
            Dalamud.Utility.Util.OpenLink(url);
        }
        catch (Exception ex)
        {
            _log.Warning($"[BrowserAuth] Dalamud.Utility.Util.OpenLink failed ({ex.Message}); falling back to Process.Start");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WriteBrowserResponse(HttpListenerContext ctx, string message)
    {
        var html = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif'>{message}</body></html>");
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = html.Length;
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
    }
}
