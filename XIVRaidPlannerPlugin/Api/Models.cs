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
    [JsonPropertyName("augmentableSlots")] public Dictionary<string, List<string>>? AugmentableSlots { get; set; }
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

// ==================== Player Gear (BiS Tracking) ====================

public class MateriaSlotInfo
{
    [JsonPropertyName("itemId")] public int ItemId { get; set; }
    [JsonPropertyName("itemName")] public string ItemName { get; set; } = string.Empty;
    [JsonPropertyName("stat")] public string? Stat { get; set; }
    [JsonPropertyName("tier")] public int? Tier { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

public class GearSlotStatusDto
{
    [JsonPropertyName("slot")] public string Slot { get; set; } = string.Empty;
    [JsonPropertyName("bisSource")] public string? BisSource { get; set; }
    [JsonPropertyName("currentSource")] public string CurrentSource { get; set; } = "unknown";
    [JsonPropertyName("hasItem")] public bool HasItem { get; set; }
    [JsonPropertyName("isAugmented")] public bool IsAugmented { get; set; }
    [JsonPropertyName("itemId")] public int? ItemId { get; set; }
    [JsonPropertyName("itemName")] public string? ItemName { get; set; }
    [JsonPropertyName("itemLevel")] public int? ItemLevel { get; set; }
    [JsonPropertyName("itemIcon")] public string? ItemIcon { get; set; }
    [JsonPropertyName("materia")] public List<MateriaSlotInfo> Materia { get; set; } = new();
}

public class TomeWeaponInfo
{
    [JsonPropertyName("pursuing")] public bool Pursuing { get; set; }
    [JsonPropertyName("hasItem")] public bool HasItem { get; set; }
    [JsonPropertyName("isAugmented")] public bool IsAugmented { get; set; }
}

public class PlayerGearResponse
{
    [JsonPropertyName("playerId")] public string PlayerId { get; set; } = string.Empty;
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = string.Empty;
    [JsonPropertyName("job")] public string Job { get; set; } = string.Empty;
    [JsonPropertyName("bisLink")] public string? BisLink { get; set; }
    [JsonPropertyName("gear")] public List<GearSlotStatusDto> Gear { get; set; } = new();
    [JsonPropertyName("tomeWeapon")] public TomeWeaponInfo TomeWeapon { get; set; } = new();
}

// ==================== Player Update (for gear sync) ====================

public class SnapshotPlayerUpdateRequest
{
    [JsonPropertyName("gear")] public List<GearSlotStatusDto>? Gear { get; set; }
    [JsonPropertyName("tomeWeapon")] public TomeWeaponInfo? TomeWeapon { get; set; }
}

// ==================== Health ====================

public class HealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}

// ==================== Plugin Auth (PKCE exchange) ====================

public class PluginAuthExchangeResponse
{
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = string.Empty;
}

// ==================== Snapshot Player (for auto-detect) ====================

/// <summary>Subset of the snapshot player record — only the fields needed to map the signed-in user to their player card.</summary>
public class SnapshotPlayerSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string? UserId { get; set; }
}

// ==================== Mount Farm Sync ====================

public class MountSyncItem
{
    [JsonPropertyName("mountId")] public int MountId { get; set; }
    [JsonPropertyName("trialId")] public string? TrialId { get; set; }
    [JsonPropertyName("owned")] public bool Owned { get; set; }
}

public class TotemSyncItem
{
    [JsonPropertyName("itemId")] public int ItemId { get; set; }
    [JsonPropertyName("trialId")] public string? TrialId { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("totemName")] public string? TotemName { get; set; }
    [JsonPropertyName("foundIn")] public List<string>? FoundIn { get; set; }
}

public class PluginMountFarmSyncRequest
{
    [JsonPropertyName("characterName")] public string? CharacterName { get; set; }
    [JsonPropertyName("characterWorld")] public string? CharacterWorld { get; set; }
    [JsonPropertyName("mounts")] public List<MountSyncItem> Mounts { get; set; } = new();
    [JsonPropertyName("totems")] public List<TotemSyncItem> Totems { get; set; } = new();
    [JsonPropertyName("source")] public string Source { get; set; } = "plugin";
    [JsonPropertyName("pluginVersion")] public string? PluginVersion { get; set; }
    [JsonPropertyName("syncedAt")] public string? SyncedAt { get; set; }
}

public class MountFarmCatalogEntry
{
    [JsonPropertyName("trialId")] public string TrialId { get; set; } = string.Empty;
    [JsonPropertyName("expansion")] public string Expansion { get; set; } = string.Empty;
    [JsonPropertyName("dutyName")] public string DutyName { get; set; } = string.Empty;
    [JsonPropertyName("mountName")] public string MountName { get; set; } = string.Empty;
    [JsonPropertyName("mountId")] public int? MountId { get; set; }
    [JsonPropertyName("totemName")] public string? TotemName { get; set; }
    [JsonPropertyName("totemItemId")] public int? TotemItemId { get; set; }
    [JsonPropertyName("totemTarget")] public int TotemTarget { get; set; } = 99;
}

public class MountFarmCatalogResponse
{
    [JsonPropertyName("entries")] public List<MountFarmCatalogEntry> Entries { get; set; } = new();
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}

// ==================== Batch Gearset Sync ====================

public class PluginGearsetSyncGearSlot
{
    [JsonPropertyName("slot")] public string Slot { get; set; } = string.Empty;
    [JsonPropertyName("hasItem")] public bool HasItem { get; set; }
    [JsonPropertyName("currentSource")] public string CurrentSource { get; set; } = "unknown";
    [JsonPropertyName("isAugmented")] public bool IsAugmented { get; set; }
    [JsonPropertyName("itemId")] public int? ItemId { get; set; }
    [JsonPropertyName("itemName")] public string? ItemName { get; set; }
    [JsonPropertyName("itemLevel")] public int? ItemLevel { get; set; }
    [JsonPropertyName("itemIcon")] public string? ItemIcon { get; set; }
    [JsonPropertyName("materia")] public List<MateriaSlotInfo>? Materia { get; set; }
}

public class PluginGearsetEntry
{
    [JsonPropertyName("gearsetIndex")] public int GearsetIndex { get; set; }
    [JsonPropertyName("gearsetName")] public string GearsetName { get; set; } = string.Empty;
    [JsonPropertyName("job")] public string Job { get; set; } = string.Empty;
    [JsonPropertyName("classJobId")] public int ClassJobId { get; set; }
    [JsonPropertyName("gear")] public List<PluginGearsetSyncGearSlot> Gear { get; set; } = new();
}

public class PluginBatchGearsetSyncRequest
{
    [JsonPropertyName("characterName")] public string CharacterName { get; set; } = string.Empty;
    [JsonPropertyName("characterWorld")] public string CharacterWorld { get; set; } = string.Empty;
    [JsonPropertyName("gearsets")] public List<PluginGearsetEntry> Gearsets { get; set; } = new();
    [JsonPropertyName("source")] public string Source { get; set; } = "plugin";
    [JsonPropertyName("pluginVersion")] public string? PluginVersion { get; set; }
}

public class PluginBatchGearsetSyncResult
{
    [JsonPropertyName("characterId")] public string CharacterId { get; set; } = string.Empty;
    [JsonPropertyName("syncedJobs")] public List<PluginBatchGearsetSyncJobResult> SyncedJobs { get; set; } = new();
    [JsonPropertyName("totalSynced")] public int TotalSynced { get; set; }
    [JsonPropertyName("totalUnchanged")] public int TotalUnchanged { get; set; }
}

public class PluginBatchGearsetSyncJobResult
{
    [JsonPropertyName("job")] public string Job { get; set; } = string.Empty;
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = string.Empty;
    [JsonPropertyName("gearChanged")] public bool GearChanged { get; set; }
    [JsonPropertyName("avgItemLevel")] public int AvgItemLevel { get; set; }
}
