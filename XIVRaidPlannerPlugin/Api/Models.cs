using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XIVRaidPlannerPlugin.Api;

/// <summary>
/// C# DTOs matching the FFXIV Raid Planner API responses.
/// Property names use JsonPropertyName to match the camelCase API.
/// </summary>

// ==================== Auth ====================

public class UserInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("discordUsername")] public string DiscordUsername { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

// ==================== Static Groups ====================

public class StaticGroupInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("shareCode")] public string ShareCode { get; set; } = string.Empty;
    [JsonPropertyName("userRole")] public string? UserRole { get; set; }
}

public class TierInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tierId")] public string TierId { get; set; } = string.Empty;
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("currentWeek")] public int CurrentWeek { get; set; }
}

// ==================== Priority ====================

public class PriorityResponse
{
    [JsonPropertyName("currentWeek")] public int CurrentWeek { get; set; }
    [JsonPropertyName("tierFloors")] public List<string> TierFloors { get; set; } = new();
    [JsonPropertyName("players")] public List<PlayerInfo> Players { get; set; } = new();
    [JsonPropertyName("priority")] public Dictionary<string, Dictionary<string, List<PriorityEntry>>> Priority { get; set; } = new();
}

public class PlayerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("job")] public string Job { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
}

public class PriorityEntry
{
    [JsonPropertyName("playerId")] public string PlayerId { get; set; } = string.Empty;
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = string.Empty;
    [JsonPropertyName("job")] public string Job { get; set; } = string.Empty;
    [JsonPropertyName("score")] public int Score { get; set; }
}

// ==================== Loot Logging ====================

public class LootLogCreateRequest
{
    [JsonPropertyName("weekNumber")] public int WeekNumber { get; set; }
    [JsonPropertyName("floor")] public string Floor { get; set; } = string.Empty;
    [JsonPropertyName("itemSlot")] public string ItemSlot { get; set; } = string.Empty;
    [JsonPropertyName("recipientPlayerId")] public string RecipientPlayerId { get; set; } = string.Empty;
    [JsonPropertyName("method")] public string Method { get; set; } = "drop";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("weaponJob")] public string? WeaponJob { get; set; }
    [JsonPropertyName("isExtra")] public bool IsExtra { get; set; }
    [JsonPropertyName("markAcquired")] public bool MarkAcquired { get; set; }
}

public class MaterialLogCreateRequest
{
    [JsonPropertyName("weekNumber")] public int WeekNumber { get; set; }
    [JsonPropertyName("floor")] public string Floor { get; set; } = string.Empty;
    [JsonPropertyName("materialType")] public string MaterialType { get; set; } = string.Empty;
    [JsonPropertyName("recipientPlayerId")] public string RecipientPlayerId { get; set; } = string.Empty;
    [JsonPropertyName("method")] public string Method { get; set; } = "drop";
    [JsonPropertyName("slotAugmented")] public string? SlotAugmented { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("markAugmented")] public bool MarkAugmented { get; set; }
}

public class MarkFloorClearedRequest
{
    [JsonPropertyName("weekNumber")] public int WeekNumber { get; set; }
    [JsonPropertyName("floor")] public string Floor { get; set; } = string.Empty;
    [JsonPropertyName("playerIds")] public List<string> PlayerIds { get; set; } = new();
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

// ==================== Current Week ====================

public class CurrentWeekResponse
{
    [JsonPropertyName("currentWeek")] public int CurrentWeek { get; set; }
    [JsonPropertyName("maxLoggedWeek")] public int MaxLoggedWeek { get; set; }
    [JsonPropertyName("maxWeek")] public int MaxWeek { get; set; }
}

// ==================== Health ====================

public class HealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}
