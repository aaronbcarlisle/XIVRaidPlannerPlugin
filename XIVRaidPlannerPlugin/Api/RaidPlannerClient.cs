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
        var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        return client;
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
        var gid = groupId ?? _config.DefaultGroupId;
        var tid = tierId ?? _config.DefaultTierId;
        var url = $"/api/static-groups/{gid}/tiers/{tid}/priority";
        if (floor.HasValue)
            url += $"?floor={floor.Value}";
        return await GetAsync<PriorityResponse>(url);
    }

    // ==================== Current Week ====================

    public async Task<CurrentWeekResponse?> GetCurrentWeekAsync()
    {
        return await GetAsync<CurrentWeekResponse>(
            $"/api/static-groups/{_config.DefaultGroupId}/tiers/{_config.DefaultTierId}/current-week");
    }

    // ==================== Loot Logging ====================

    public async Task<bool> CreateLootLogEntryAsync(LootLogCreateRequest request)
    {
        return await PostAsync(
            $"/api/static-groups/{_config.DefaultGroupId}/tiers/{_config.DefaultTierId}/loot-log",
            request);
    }

    public async Task<bool> CreateMaterialLogEntryAsync(MaterialLogCreateRequest request)
    {
        return await PostAsync(
            $"/api/static-groups/{_config.DefaultGroupId}/tiers/{_config.DefaultTierId}/material-log",
            request);
    }

    public async Task<bool> MarkFloorClearedAsync(MarkFloorClearedRequest request)
    {
        return await PostAsync(
            $"/api/static-groups/{_config.DefaultGroupId}/tiers/{_config.DefaultTierId}/mark-floor-cleared",
            request);
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
