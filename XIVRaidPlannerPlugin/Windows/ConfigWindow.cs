using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Configuration window for the plugin.
/// Opened via /xrp config slash command.
/// </summary>
public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly RaidPlannerClient _apiClient;
    private readonly PartyMatchingService _partyMatching;
    private readonly IPartyList _partyList;
    private readonly IPlayerState _playerState;

    // UI state
    private string _apiKeyInput = "";
    private string _apiUrlInput = "";
    private string _frontendUrlInput = "";
    private string _connectionStatus = "";
    private Vector4 _connectionStatusColor = new(1, 1, 1, 1);
    private bool _isTesting;
    private List<StaticGroupInfo>? _staticGroups;
    private int _selectedGroupIndex = -1;
    private int _selectedAutoLogMode;
    private bool _autoConnectAttempted;

    // Static tab state
    private List<TierInfo>? _tiers;
    private int _selectedTierIndex = -1;
    private bool _isFetchingTiers;

    // Players tab state
    private List<PlayerInfo>? _staticPlayers;
    private bool _isFetchingRoster;

    public ConfigWindow(Configuration config, RaidPlannerClient apiClient, PartyMatchingService partyMatching, IPartyList partyList, IPlayerState playerState)
        : base("XIV Raid Planner - Settings",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _apiClient = apiClient;
        _partyMatching = partyMatching;
        _partyList = partyList;
        _playerState = playerState;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(600, 800),
        };

        _apiKeyInput = _config.ApiKey;
        _apiUrlInput = _config.ApiBaseUrl;
        _frontendUrlInput = _config.FrontendBaseUrl;
        _selectedAutoLogMode = (int)_config.AutoLogMode;
    }

    /// <summary>Update the cached static player list (called when priority data is fetched).</summary>
    public void SetStaticPlayers(List<PlayerInfo> players) => _staticPlayers = players;

    public override void Draw()
    {
        // Auto-fetch statics on first draw if credentials are already configured
        if (!_autoConnectAttempted && !_isTesting
            && !string.IsNullOrEmpty(_config.ApiKey) && !string.IsNullOrEmpty(_config.ApiBaseUrl))
        {
            _autoConnectAttempted = true;
            _isTesting = true;
            _connectionStatus = "Connecting...";
            _connectionStatusColor = new Vector4(1, 1, 0, 1);

            Task.Run(async () =>
            {
                var result = await _apiClient.TestConnectionAsync();
                var groups = result != null ? await _apiClient.GetStaticGroupsAsync() : null;
                // Marshal UI state updates back to the framework thread
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (result != null)
                    {
                        _connectionStatus = $"Connected (API v{result.Version})";
                        _connectionStatusColor = new Vector4(0, 1, 0, 1);
                        _staticGroups = groups;
                    }
                    else
                    {
                        _connectionStatus = "";
                    }
                    _isTesting = false;
                });
            });
        }

        if (ImGui.BeginTabBar("config_tabs"))
        {
            if (ImGui.BeginTabItem("Connection"))
            {
                DrawConnectionTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Static"))
            {
                DrawStaticTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Players"))
            {
                DrawPlayersTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawConnectionTab()
    {
        ImGui.Text("API URL");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##apiurl", ref _apiUrlInput, 256))
        {
            _config.ApiBaseUrl = _apiUrlInput;
            _config.Save();
            _apiClient.UpdateAuth();
        }

        ImGui.Spacing();
        ImGui.Text("Frontend URL (optional, for Ctrl+Click links)");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##frontendurl", ref _frontendUrlInput, 256))
        {
            _config.FrontendBaseUrl = _frontendUrlInput;
            _config.Save();
        }
        ImGui.TextDisabled("Leave blank if API and frontend share the same URL.");

        ImGui.Spacing();
        ImGui.Text("API Key");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##apikey", ref _apiKeyInput, 256, ImGuiInputTextFlags.Password))
        {
            _config.ApiKey = _apiKeyInput;
            _config.Save();
            _apiClient.UpdateAuth();
        }
        ImGui.TextDisabled("Get your API key from the web app (User Menu > API Keys)");

        ImGui.Spacing();
        if (!_isTesting)
        {
            if (ImGui.Button("Test Connection"))
            {
                _isTesting = true;
                _connectionStatus = "Testing...";
                _connectionStatusColor = new Vector4(1, 1, 0, 1);

                Task.Run(async () =>
                {
                    var result = await _apiClient.TestConnectionAsync();
                    var groups = result != null ? await _apiClient.GetStaticGroupsAsync() : null;
                    Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (result != null)
                        {
                            _connectionStatus = $"Connected (API v{result.Version})";
                            _connectionStatusColor = new Vector4(0, 1, 0, 1);
                            _staticGroups = groups;
                        }
                        else
                        {
                            _connectionStatus = "Connection failed. Check URL and API key.";
                            _connectionStatusColor = new Vector4(1, 0, 0, 1);
                        }
                        _isTesting = false;
                    });
                });
            }
        }
        else
        {
            ImGui.TextDisabled("Testing...");
        }

        if (!string.IsNullOrEmpty(_connectionStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(_connectionStatusColor, _connectionStatus);
        }
    }

    private void DrawStaticTab()
    {
        ImGui.Text("Select your static group and tier.");
        ImGui.Spacing();

        if (_staticGroups == null || _staticGroups.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1),
                "No statics loaded. Test your connection first.");
            return;
        }

        // Static group selector
        ImGui.Text("Static Group");
        var groupNames = new string[_staticGroups.Count];
        for (var i = 0; i < _staticGroups.Count; i++)
            groupNames[i] = _staticGroups[i].Name;

        // Sync selected index with saved config
        if (_selectedGroupIndex < 0 && !string.IsNullOrEmpty(_config.DefaultGroupId))
        {
            for (var i = 0; i < _staticGroups.Count; i++)
            {
                if (_staticGroups[i].Id == _config.DefaultGroupId)
                {
                    _selectedGroupIndex = i;
                    // Ensure display name and share code are populated from API data
                    var needsSave = false;
                    if (string.IsNullOrEmpty(_config.DefaultGroupName))
                    {
                        _config.DefaultGroupName = _staticGroups[i].Name;
                        needsSave = true;
                    }
                    if (string.IsNullOrEmpty(_config.DefaultGroupShareCode))
                    {
                        _config.DefaultGroupShareCode = _staticGroups[i].ShareCode;
                        needsSave = true;
                    }
                    if (needsSave)
                        _config.Save();
                    break;
                }
            }
        }

        if (ImGui.Combo("##group", ref _selectedGroupIndex, groupNames))
        {
            if (_selectedGroupIndex >= 0 && _selectedGroupIndex < _staticGroups.Count)
            {
                _config.DefaultGroupId = _staticGroups[_selectedGroupIndex].Id;
                _config.DefaultGroupName = _staticGroups[_selectedGroupIndex].Name;
                _config.DefaultGroupShareCode = _staticGroups[_selectedGroupIndex].ShareCode;
                _config.DefaultTierId = string.Empty;
                _tiers = null;
                _selectedTierIndex = -1;
                _config.Save();
            }
        }

        // Auto-fetch tiers when group is selected
        if (!string.IsNullOrEmpty(_config.DefaultGroupId) && _tiers == null && !_isFetchingTiers)
        {
            _isFetchingTiers = true;
            var groupId = _config.DefaultGroupId;
            Task.Run(async () =>
            {
                var tiers = await _apiClient.GetTiersAsync(groupId);
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    _tiers = tiers;
                    _isFetchingTiers = false;
                });
            });
        }

        // Tier selector
        ImGui.Spacing();
        ImGui.Text("Tier");

        if (_isFetchingTiers)
        {
            ImGui.TextDisabled("Loading tiers...");
        }
        else if (_tiers == null || _tiers.Count == 0)
        {
            ImGui.TextDisabled("No tiers found. Select a group first.");
        }
        else
        {
            // Build tier names with "Auto" as the first option
            var tierNames = new string[_tiers.Count + 1];
            tierNames[0] = "Auto (active tier)";
            for (var i = 0; i < _tiers.Count; i++)
            {
                var active = _tiers[i].IsActive ? " (active)" : "";
                tierNames[i + 1] = $"{_tiers[i].TierId}{active}";
            }

            // Sync selected index with saved config (0 = Auto, 1+ = specific tier)
            if (_selectedTierIndex < 0)
            {
                if (string.IsNullOrEmpty(_config.DefaultTierId))
                {
                    _selectedTierIndex = 0; // Auto
                }
                else
                {
                    for (var i = 0; i < _tiers.Count; i++)
                    {
                        if (_tiers[i].Id == _config.DefaultTierId)
                        {
                            _selectedTierIndex = i + 1;
                            // Ensure display name is populated from API data
                            if (string.IsNullOrEmpty(_config.DefaultTierName))
                            {
                                _config.DefaultTierName = _tiers[i].TierId;
                                _config.Save();
                            }
                            break;
                        }
                    }
                    // If saved tier not found in list, fall back to Auto
                    if (_selectedTierIndex < 0)
                        _selectedTierIndex = 0;
                }
            }

            if (ImGui.Combo("##tier", ref _selectedTierIndex, tierNames))
            {
                if (_selectedTierIndex == 0)
                {
                    // Auto — clear tier so auto-detection picks the active one
                    _config.DefaultTierId = string.Empty;
                    _config.DefaultTierName = string.Empty;
                    _config.Save();
                }
                else if (_selectedTierIndex > 0 && _selectedTierIndex <= _tiers.Count)
                {
                    var tier = _tiers[_selectedTierIndex - 1];
                    _config.DefaultTierId = tier.Id;
                    _config.DefaultTierName = tier.TierId;
                    _config.Save();
                }
            }
        }
    }

    private void DrawPlayersTab()
    {
        ImGui.Text("Link party members to your static roster.");
        ImGui.Spacing();

        // Fetch roster button
        if (_staticPlayers == null || _staticPlayers.Count == 0)
        {
            if (!_isFetchingRoster)
            {
                if (ImGui.Button("Load Static Roster"))
                {
                    _isFetchingRoster = true;
                    Task.Run(async () =>
                    {
                        var priority = await _apiClient.GetPriorityAsync();
                        Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (priority != null)
                                _staticPlayers = priority.Players;
                            _isFetchingRoster = false;
                        });
                    });
                }
                ImGui.SameLine();
                ImGui.TextDisabled("Requires connection + static/tier configured.");
            }
            else
            {
                ImGui.TextDisabled("Loading...");
            }

            if (_staticPlayers == null || _staticPlayers.Count == 0)
                return;
        }

        // Build party member name list (include local player when solo)
        var partyNames = new List<string>();
        foreach (var member in _partyList)
        {
            var name = member.Name.ToString().Trim();
            if (!string.IsNullOrEmpty(name))
                partyNames.Add(name);
        }

        if (partyNames.Count == 0 && _playerState.IsLoaded)
        {
            var localName = _playerState.CharacterName;
            if (!string.IsNullOrEmpty(localName))
                partyNames.Add(localName);
        }

        // Build combo options: ["(none)", "PlayerName (Job)", ...]
        var comboLabels = new string[_staticPlayers.Count + 1];
        comboLabels[0] = "(none)";
        for (var i = 0; i < _staticPlayers.Count; i++)
            comboLabels[i + 1] = $"{_staticPlayers[i].Name} ({_staticPlayers[i].Job})";

        // Current party assignment section
        if (partyNames.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.298f, 0.722f, 0.659f, 1f), "Current Party");
            ImGui.Separator();

            foreach (var partyName in partyNames)
            {
                ImGui.PushID(partyName);

                // Find current assignment
                _config.PlayerNameOverrides.TryGetValue(partyName, out var assignedId);
                var selectedIndex = 0;
                if (assignedId != null)
                {
                    for (var i = 0; i < _staticPlayers.Count; i++)
                    {
                        if (_staticPlayers[i].Id == assignedId)
                        {
                            selectedIndex = i + 1;
                            break;
                        }
                    }
                }

                ImGui.Text(partyName);
                ImGui.SameLine(200);
                ImGui.SetNextItemWidth(200);
                if (ImGui.Combo("##assign", ref selectedIndex, comboLabels))
                {
                    if (selectedIndex == 0)
                        _config.PlayerNameOverrides.Remove(partyName);
                    else
                        _config.PlayerNameOverrides[partyName] = _staticPlayers[selectedIndex - 1].Id;
                    _config.Save();

                    // Re-run matching with updated overrides
                    _partyMatching.MatchParty(_staticPlayers);
                }

                ImGui.PopID();
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Join a party to assign players.");
        }

        // Show saved overrides (for players not currently in party)
        var savedOverrides = _config.PlayerNameOverrides
            .Where(kv => !partyNames.Contains(kv.Key))
            .ToList();

        if (savedOverrides.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Saved Associations (not in party)");
            ImGui.Separator();

            string? keyToRemove = null;
            foreach (var (charName, playerId) in savedOverrides)
            {
                var playerName = _staticPlayers.FirstOrDefault(p => p.Id == playerId)?.Name ?? playerId[..8];
                ImGui.BulletText($"{charName} -> {playerName}");
                ImGui.SameLine();
                ImGui.PushID($"rm_{charName}");
                if (ImGui.SmallButton("X"))
                    keyToRemove = charName;
                ImGui.PopID();
            }

            if (keyToRemove != null)
            {
                _config.PlayerNameOverrides.Remove(keyToRemove);
                _config.Save();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Refresh"))
        {
            _isFetchingRoster = true;
            Task.Run(async () =>
            {
                var priority = await _apiClient.GetPriorityAsync();
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (priority != null)
                        _staticPlayers = priority.Players;
                    _isFetchingRoster = false;
                });
            });
        }
    }

    private void DrawSettingsTab()
    {
        // Auto-log mode
        ImGui.Text("Auto-Log Mode");
        var modes = new[] { "Confirm", "Auto", "Manual" };
        if (ImGui.Combo("##autolog", ref _selectedAutoLogMode, modes))
        {
            _config.AutoLogMode = (AutoLogMode)_selectedAutoLogMode;
            _config.Save();
        }

        ImGui.Spacing();

        // Show overlay toggle (master)
        var showOverlay = _config.ShowOverlay;
        if (ImGui.Checkbox("Show Priority Overlay", ref showOverlay))
        {
            _config.ShowOverlay = showOverlay;
            _config.Save();
        }

        // Sub-options (indented, disabled if master is off)
        if (!showOverlay) ImGui.BeginDisabled();
        ImGui.Indent(20);

        var onEntry = _config.ShowOverlayOnEntry;
        if (ImGui.Checkbox("Show when entering raid instance", ref onEntry))
        {
            _config.ShowOverlayOnEntry = onEntry;
            _config.Save();
        }

        var onDutyComplete = _config.ShowOverlayOnDutyComplete;
        if (ImGui.Checkbox("Show when duty completes", ref onDutyComplete))
        {
            _config.ShowOverlayOnDutyComplete = onDutyComplete;
            _config.Save();
        }

        var onLootWindow = _config.ShowOverlayOnLootWindow;
        if (ImGui.Checkbox("Show when loot window opens", ref onLootWindow))
        {
            _config.ShowOverlayOnLootWindow = onLootWindow;
            _config.Save();
        }

        ImGui.Unindent(20);
        if (!showOverlay) ImGui.EndDisabled();

        // Leave warning toggle
        var leaveWarning = _config.EnableLeaveWarning;
        if (ImGui.Checkbox("Warn When Leaving with Unclaimed Loot", ref leaveWarning))
        {
            _config.EnableLeaveWarning = leaveWarning;
            _config.Save();
        }
    }

    public void Dispose() { }
}
