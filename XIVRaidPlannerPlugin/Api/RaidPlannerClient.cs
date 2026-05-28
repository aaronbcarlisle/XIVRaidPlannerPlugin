using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Api;

/// <summary>
/// HTTP client wrapper for the FFXIV Raid Planner API.
/// All calls use the configured API key for authentication.
/// All public methods return <see cref="ApiResult{T}"/> with categorized errors.
/// </summary>
public class RaidPlannerClient : IDisposable
{
    private HttpClient _httpClient;
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    // Cached resolved tier ID per group (avoids repeated /tiers calls in Auto mode)
    private string? _cachedResolvedTierId;
    private string? _cachedResolvedGroupId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RaidPlannerClient(Configuration config, IPluginLog log)
    {
        _config = config;
        _log = log;
        _httpClient = CreateHttpClient();
    }

    /// <summary>Recreate the HttpClient with current config (BaseAddress can only be set once).</summary>
    public void UpdateAuth()
    {
        _httpClient.Dispose();
        _httpClient = CreateHttpClient();
    }

    private HttpClient CreateHttpClient()
    {
        var baseUrl = _config.EffectiveApiBaseUrl.TrimEnd('/');
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }
        return client;
    }

    // ==================== Status → Error Mapper ====================

    private static ApiError MapStatus(System.Net.HttpStatusCode code) => (int)code switch
    {
        401 or 403 => ApiError.Unauthorized,
        404 => ApiError.NotFound,
        >= 500 => ApiError.Server,
        _ => ApiError.Unknown,
    };

    // ==================== Tier Resolution ====================

    /// <summary>
    /// Resolve the active tier ID for a group when no tier is explicitly configured.
    /// Caches the result per group to avoid repeated /tiers network calls in Auto mode.
    /// </summary>
    private async Task<ApiResult<string>> ResolveActiveTierIdAsync(string groupId, CancellationToken ct = default)
    {
        // Return cached result if same group
        if (_cachedResolvedTierId != null && _cachedResolvedGroupId == groupId)
            return ApiResult<string>.Ok(_cachedResolvedTierId);

        var result = await GetTiersAsync(groupId, ct);
        if (!result.IsSuccess) return ApiResult<string>.Fail(result.Error);

        var active = result.Value!.Find(t => t.IsActive);
        if (active != null)
        {
            _log.Information($"Resolved active tier: {active.TierId} ({active.Id})");
            _cachedResolvedTierId = active.Id;
            _cachedResolvedGroupId = groupId;
            return ApiResult<string>.Ok(active.Id);
        }

        _log.Warning("No active tier found for group");
        return ApiResult<string>.Fail(ApiError.NotFound);
    }

    /// <summary>Clear the cached auto-resolved tier (e.g., on group change or instance exit).</summary>
    public void InvalidateResolvedTier()
    {
        _cachedResolvedTierId = null;
        _cachedResolvedGroupId = null;
    }

    /// <summary>Resolve the active tier for a group, returning the full TierInfo for display purposes.</summary>
    public async Task<ApiResult<TierInfo>> ResolveActiveTierAsync(string groupId, CancellationToken ct = default)
    {
        var result = await GetTiersAsync(groupId, ct);
        if (!result.IsSuccess) return ApiResult<TierInfo>.Fail(result.Error);

        var active = result.Value!.Find(t => t.IsActive);
        if (active != null)
        {
            _cachedResolvedTierId = active.Id;
            _cachedResolvedGroupId = groupId;
            return ApiResult<TierInfo>.Ok(active);
        }

        return ApiResult<TierInfo>.Fail(ApiError.NotFound);
    }

    /// <summary>Resolve group and tier IDs, auto-detecting active tier when in Auto mode.</summary>
    private async Task<ApiResult<(string GroupId, string TierId)>> ResolveIdsAsync(string? groupId = null, string? tierId = null, CancellationToken ct = default)
    {
        var gid = groupId ?? _config.DefaultGroupId;
        var tid = tierId ?? _config.DefaultTierId;

        if (string.IsNullOrEmpty(gid))
            return ApiResult<(string, string)>.Fail(ApiError.NotFound);

        if (string.IsNullOrEmpty(tid))
        {
            var resolved = await ResolveActiveTierIdAsync(gid, ct);
            if (!resolved.IsSuccess)
                return ApiResult<(string, string)>.Fail(resolved.Error);
            tid = resolved.Value!;
        }

        return ApiResult<(string, string)>.Ok((gid, tid));
    }

    // ==================== Health ====================

    public async Task<ApiResult<HealthResponse>> TestConnectionAsync(CancellationToken ct = default)
    {
        var healthResult = await GetAsync<HealthResponse>("/health", ct);
        if (!healthResult.IsSuccess) return healthResult;
        if (healthResult.Value!.Status != "healthy")
            return ApiResult<HealthResponse>.Fail(ApiError.Server);

        // Also verify the API key works
        var authResult = await GetAsync<UserInfo>("/api/auth/me", ct);
        if (!authResult.IsSuccess) return ApiResult<HealthResponse>.Fail(authResult.Error);

        return healthResult;
    }

    // ==================== Static Groups ====================

    public async Task<ApiResult<List<StaticGroupInfo>>> GetStaticGroupsAsync(CancellationToken ct = default)
    {
        return await GetAsync<List<StaticGroupInfo>>("/api/static-groups", ct);
    }

    // ==================== Tiers ====================

    public async Task<ApiResult<List<TierInfo>>> GetTiersAsync(string groupId, CancellationToken ct = default)
    {
        return await GetAsync<List<TierInfo>>($"/api/static-groups/{groupId}/tiers", ct);
    }

    // ==================== Priority ====================

    public async Task<ApiResult<PriorityResponse>> GetPriorityAsync(int? floor = null, string? groupId = null, string? tierId = null, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(groupId, tierId, ct);
        if (!ids.IsSuccess) return ApiResult<PriorityResponse>.Fail(ids.Error);
        var (gid, tid) = ids.Value;

        var url = $"/api/static-groups/{gid}/tiers/{tid}/priority";
        if (floor.HasValue)
            url += $"?floor={floor.Value}";
        return await GetAsync<PriorityResponse>(url, ct);
    }

    // ==================== Current Week ====================

    public async Task<ApiResult<CurrentWeekResponse>> GetCurrentWeekAsync(CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(ct: ct);
        if (!ids.IsSuccess) return ApiResult<CurrentWeekResponse>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await GetAsync<CurrentWeekResponse>(
            $"/api/static-groups/{gid}/tiers/{tid}/current-week", ct);
    }

    // ==================== Loot Logging ====================

    public async Task<ApiResult<bool>> CreateLootLogEntryAsync(LootLogCreateRequest request, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(ct: ct);
        if (!ids.IsSuccess) return ApiResult<bool>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/loot-log", request, ct);
    }

    public async Task<ApiResult<bool>> CreateMaterialLogEntryAsync(MaterialLogCreateRequest request, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(ct: ct);
        if (!ids.IsSuccess) return ApiResult<bool>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/material-log", request, ct);
    }

    public async Task<ApiResult<bool>> MarkFloorClearedAsync(MarkFloorClearedRequest request, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(ct: ct);
        if (!ids.IsSuccess) return ApiResult<bool>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/mark-floor-cleared", request, ct);
    }

    // ==================== Player Gear (BiS Tracking) ====================

    public async Task<ApiResult<PlayerGearResponse>> GetPlayerGearAsync(string playerId, string? groupId = null, string? tierId = null, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(groupId, tierId, ct);
        if (!ids.IsSuccess) return ApiResult<PlayerGearResponse>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await GetAsync<PlayerGearResponse>(
            $"/api/static-groups/{gid}/tiers/{tid}/players/{playerId}/gear", ct);
    }

    /// <summary>Sync player gear by updating their current equipment state.</summary>
    public async Task<ApiResult<bool>> SyncPlayerGearAsync(string playerId, SnapshotPlayerUpdateRequest request, string? groupId = null, string? tierId = null, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(groupId, tierId, ct);
        if (!ids.IsSuccess) return ApiResult<bool>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        return await PutAsync(
            $"/api/static-groups/{gid}/tiers/{tid}/players/{playerId}",
            request, ct);
    }

    // ==================== Plugin Auth (PKCE exchange) ====================

    /// <summary>
    /// Exchange a browser-issued auth code + PKCE verifier for an xrp_ API key.
    /// This endpoint is intentionally unauthenticated — the user is obtaining their first key.
    /// </summary>
    public async Task<ApiResult<string>> ExchangePluginAuthCodeAsync(string code, string codeVerifier, CancellationToken ct = default)
    {
        var body = new { code, code_verifier = codeVerifier };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            var resp = await _httpClient.PostAsync("/api/api-keys/plugin-auth/exchange", content, ct);
            if (!resp.IsSuccessStatusCode) return ApiResult<string>.Fail(MapStatus(resp.StatusCode));
            var payload = JsonSerializer.Deserialize<PluginAuthExchangeResponse>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
            return string.IsNullOrEmpty(payload?.ApiKey)
                ? ApiResult<string>.Fail(ApiError.Unknown)
                : ApiResult<string>.Ok(payload.ApiKey);
        }
        catch (TaskCanceledException) { return ApiResult<string>.Fail(ApiError.Network); }
        catch (HttpRequestException) { return ApiResult<string>.Fail(ApiError.Network); }
        catch (Exception ex)
        {
            _log.Error($"POST /api/api-keys/plugin-auth/exchange: {ex.Message}");
            return ApiResult<string>.Fail(ApiError.Unknown);
        }
    }

    /// <summary>Log a vendor purchase (self-log for members).</summary>
    public async Task<ApiResult<bool>> CreatePurchaseLogEntryAsync(LootLogCreateRequest request, CancellationToken ct = default)
    {
        var ids = await ResolveIdsAsync(ct: ct);
        if (!ids.IsSuccess) return ApiResult<bool>.Fail(ids.Error);
        var (gid, tid) = ids.Value;
        request.Method = "purchase";
        request.Notes ??= "Auto-logged via Dalamud plugin";
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/loot-log", request, ct);
    }

    // ==================== HTTP Helpers ====================

    private async Task<ApiResult<T>> GetAsync<T>(string endpoint, CancellationToken ct = default) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Error($"GET {endpoint} -> {(int)response.StatusCode}");
                return ApiResult<T>.Fail(MapStatus(response.StatusCode));
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is null ? ApiResult<T>.Fail(ApiError.Unknown) : ApiResult<T>.Ok(value);
        }
        catch (TaskCanceledException) { return ApiResult<T>.Fail(ApiError.Network); }
        catch (HttpRequestException) { return ApiResult<T>.Fail(ApiError.Network); }
        catch (Exception ex) { _log.Error($"GET {endpoint}: {ex.Message}"); return ApiResult<T>.Fail(ApiError.Unknown); }
    }

    private async Task<ApiResult<bool>> PostAsync<T>(string endpoint, T body, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Error($"POST {endpoint} -> {(int)response.StatusCode}");
                return ApiResult<bool>.Fail(MapStatus(response.StatusCode));
            }
            return ApiResult<bool>.Ok(true);
        }
        catch (TaskCanceledException) { return ApiResult<bool>.Fail(ApiError.Network); }
        catch (HttpRequestException) { return ApiResult<bool>.Fail(ApiError.Network); }
        catch (Exception ex) { _log.Error($"POST {endpoint}: {ex.Message}"); return ApiResult<bool>.Fail(ApiError.Unknown); }
    }

    private async Task<ApiResult<bool>> PutAsync<T>(string endpoint, T body, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(endpoint, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Error($"PUT {endpoint} -> {(int)response.StatusCode}");
                return ApiResult<bool>.Fail(MapStatus(response.StatusCode));
            }
            return ApiResult<bool>.Ok(true);
        }
        catch (TaskCanceledException) { return ApiResult<bool>.Fail(ApiError.Network); }
        catch (HttpRequestException) { return ApiResult<bool>.Fail(ApiError.Network); }
        catch (Exception ex) { _log.Error($"PUT {endpoint}: {ex.Message}"); return ApiResult<bool>.Fail(ApiError.Unknown); }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // ==================== Test Seam ====================

    /// <summary>
    /// Test-only entry point: exercises the status → ApiError mapping via a stub HttpMessageHandler.
    /// Not for production use.
    /// </summary>
    public static async Task<ApiResult<UserInfo>> SendForTest(HttpMessageHandler handler, string endpoint)
    {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var resp = await client.GetAsync(endpoint);
        if (!resp.IsSuccessStatusCode) return ApiResult<UserInfo>.Fail(MapStatus(resp.StatusCode));
        return ApiResult<UserInfo>.Ok(new UserInfo());
    }
}
