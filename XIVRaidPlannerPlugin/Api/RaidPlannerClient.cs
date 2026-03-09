using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin.Api;

/// <summary>
/// HTTP client wrapper for the FFXIV Raid Planner API.
/// All calls use the configured API key for authentication.
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
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        return client;
    }

    /// <summary>
    /// Resolve the active tier ID for a group when no tier is explicitly configured.
    /// Caches the result per group to avoid repeated /tiers network calls in Auto mode.
    /// </summary>
    private async Task<string?> ResolveActiveTierIdAsync(string groupId)
    {
        // Return cached result if same group
        if (_cachedResolvedTierId != null && _cachedResolvedGroupId == groupId)
            return _cachedResolvedTierId;

        var tiers = await GetTiersAsync(groupId);
        var active = tiers.Find(t => t.IsActive);
        if (active != null)
        {
            _log.Information($"Resolved active tier: {active.TierId} ({active.Id})");
            _cachedResolvedTierId = active.Id;
            _cachedResolvedGroupId = groupId;
            return active.Id;
        }

        _log.Warning("No active tier found for group");
        return null;
    }

    /// <summary>Clear the cached auto-resolved tier (e.g., on group change or instance exit).</summary>
    public void InvalidateResolvedTier()
    {
        _cachedResolvedTierId = null;
        _cachedResolvedGroupId = null;
    }

    /// <summary>Resolve the active tier for a group, returning the full TierInfo for display purposes.</summary>
    public async Task<TierInfo?> ResolveActiveTierAsync(string groupId)
    {
        var tiers = await GetTiersAsync(groupId);
        var active = tiers.Find(t => t.IsActive);
        if (active != null)
        {
            _cachedResolvedTierId = active.Id;
            _cachedResolvedGroupId = groupId;
        }
        return active;
    }

    /// <summary>Resolve group and tier IDs, auto-detecting active tier when in Auto mode.</summary>
    private async Task<(string? GroupId, string? TierId)> ResolveIdsAsync(string? groupId = null, string? tierId = null)
    {
        var gid = groupId ?? _config.DefaultGroupId;
        var tid = tierId ?? _config.DefaultTierId;

        if (string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(gid))
            tid = await ResolveActiveTierIdAsync(gid);

        return (gid, tid);
    }

    // ==================== Health ====================

    public async Task<HealthResponse?> TestConnectionAsync()
    {
        try
        {
            var health = await GetAsync<HealthResponse>("/health");
            if (health?.Status != "healthy")
                return null;

            // Also verify the API key works
            await GetAsync<UserInfo>("/api/auth/me");
            return health;
        }
        catch (Exception ex)
        {
            _log.Error($"Connection test failed: {ex.Message}");
            return null;
        }
    }

    // ==================== Static Groups ====================

    public async Task<List<StaticGroupInfo>> GetStaticGroupsAsync()
    {
        return await GetAsync<List<StaticGroupInfo>>("/api/static-groups") ?? new();
    }

    // ==================== Tiers ====================

    public async Task<List<TierInfo>> GetTiersAsync(string groupId)
    {
        return await GetAsync<List<TierInfo>>($"/api/static-groups/{groupId}/tiers") ?? new();
    }

    // ==================== Priority ====================

    public async Task<PriorityResponse?> GetPriorityAsync(int? floor = null, string? groupId = null, string? tierId = null)
    {
        var (gid, tid) = await ResolveIdsAsync(groupId, tierId);
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return null;

        var url = $"/api/static-groups/{gid}/tiers/{tid}/priority";
        if (floor.HasValue)
            url += $"?floor={floor.Value}";
        return await GetAsync<PriorityResponse>(url);
    }

    // ==================== Current Week ====================

    public async Task<CurrentWeekResponse?> GetCurrentWeekAsync()
    {
        var (gid, tid) = await ResolveIdsAsync();
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return null;
        return await GetAsync<CurrentWeekResponse>(
            $"/api/static-groups/{gid}/tiers/{tid}/current-week");
    }

    // ==================== Loot Logging ====================

    public async Task<bool> CreateLootLogEntryAsync(LootLogCreateRequest request)
    {
        var (gid, tid) = await ResolveIdsAsync();
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return false;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/loot-log", request);
    }

    public async Task<bool> CreateMaterialLogEntryAsync(MaterialLogCreateRequest request)
    {
        var (gid, tid) = await ResolveIdsAsync();
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return false;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/material-log", request);
    }

    public async Task<bool> MarkFloorClearedAsync(MarkFloorClearedRequest request)
    {
        var (gid, tid) = await ResolveIdsAsync();
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return false;
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/mark-floor-cleared", request);
    }

    // ==================== Player Gear (BiS Tracking) ====================

    public async Task<PlayerGearResponse?> GetPlayerGearAsync(string playerId, string? groupId = null, string? tierId = null)
    {
        var (gid, tid) = await ResolveIdsAsync(groupId, tierId);
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return null;
        return await GetAsync<PlayerGearResponse>(
            $"/api/static-groups/{gid}/tiers/{tid}/players/{playerId}/gear");
    }

    /// <summary>Sync player gear by updating their current equipment state.</summary>
    public async Task<bool> SyncPlayerGearAsync(string playerId, SnapshotPlayerUpdateRequest request, string? groupId = null, string? tierId = null)
    {
        var (gid, tid) = await ResolveIdsAsync(groupId, tierId);
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return false;
        return await PutAsync(
            $"/api/static-groups/{gid}/tiers/{tid}/players/{playerId}",
            request);
    }

    /// <summary>Log a vendor purchase (self-log for members).</summary>
    public async Task<bool> CreatePurchaseLogEntryAsync(LootLogCreateRequest request)
    {
        var (gid, tid) = await ResolveIdsAsync();
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(tid))
            return false;
        request.Method = "purchase";
        request.Notes ??= "Auto-logged via Dalamud plugin";
        return await PostAsync($"/api/static-groups/{gid}/tiers/{tid}/loot-log", request);
    }

    // ==================== HTTP Helpers ====================

    private async Task<T?> GetAsync<T>(string endpoint) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _log.Error($"GET {endpoint} failed: {ex.StatusCode} - {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"GET {endpoint} error: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> PostAsync<T>(string endpoint, T body)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (HttpRequestException ex)
        {
            _log.Error($"POST {endpoint} failed: {ex.StatusCode} - {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"POST {endpoint} error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PutAsync<T>(string endpoint, T body)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(endpoint, content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (HttpRequestException ex)
        {
            _log.Error($"PUT {endpoint} failed: {ex.StatusCode} - {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"PUT {endpoint} error: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
