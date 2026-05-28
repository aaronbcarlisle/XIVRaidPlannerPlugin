using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XIVRaidPlannerPlugin.Api;
using XIVRaidPlannerPlugin.Auth;
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
    private readonly PluginThread _thread;
    private readonly BrowserAuthService _browserAuth;

    // UI state
    private string _apiKeyInput = "";
    private string _apiUrlInput = "";
    private string _frontendUrlInput = "";
    private bool _useCustomUrls;
    private string _connectionStatus = "";
    private Vector4 _connectionStatusColor = Theme.White;
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

    public ConfigWindow(Configuration config, RaidPlannerClient apiClient, PartyMatchingService partyMatching, IPartyList partyList, IPlayerState playerState, PluginThread thread, BrowserAuthService browserAuth)
        : base("XIV Raid Planner - Settings",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _apiClient = apiClient;
        _partyMatching = partyMatching;
        _partyList = partyList;
        _playerState = playerState;
        _thread = thread;
        _browserAuth = browserAuth;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(600, 800),
        };

        _apiKeyInput = _config.ApiKey;
        _apiUrlInput = _config.ApiBaseUrl;
        _frontendUrlInput = _config.FrontendBaseUrl;
        _useCustomUrls = _config.UseCustomUrls;
        _selectedAutoLogMode = (int)_config.AutoLogMode;
    }

    /// <summary>Update the cached static player list (called when priority data is fetched).</summary>
    public void SetStaticPlayers(List<PlayerInfo> players) => _staticPlayers = players;

    public override void Draw()
    {
        // Auto-fetch statics on first draw if credentials are already configured
        if (!_autoConnectAttempted && !_isTesting
            && !string.IsNullOrEmpty(_config.ApiKey))
        {
            _autoConnectAttempted = true;
            _isTesting = true;
            _connectionStatus = "Connecting...";
            _connectionStatusColor = Theme.Warning;

            Task.Run(async () =>
            {
                var testResult = await _apiClient.TestConnectionAsync();
                List<StaticGroupInfo>? groups = null;
                if (testResult.IsSuccess)
                {
                    var groupsResult = await _apiClient.GetStaticGroupsAsync();
                    groups = groupsResult.IsSuccess ? groupsResult.Value : null;
                }
                // Marshal UI state updates back to the framework thread
                _thread.RunOnUi(() =>
                {
                    if (testResult.IsSuccess)
                    {
                        _connectionStatus = $"Connected (API v{testResult.Value!.Version})";
                        _connectionStatusColor = Theme.Success;
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
        ImGui.TextWrapped("Sign in with your FFXIV Raid Planner account to authorize the plugin. Your browser will open and you'll authenticate with Discord (same as the web app).");
        ImGui.Spacing();

        if (ImGui.Button("Sign in with browser##browser-auth"))
        {
            _connectionStatus = "Waiting for browser sign-in...";
            _connectionStatusColor = Theme.Warning;
            _thread.RunBackground(async () =>
            {
                var result = await _browserAuth.SignInAsync();
                _thread.RunOnUi(() =>
                {
                    if (result.IsSuccess)
                    {
                        _connectionStatus = "Signed in!";
                        _connectionStatusColor = Theme.Success;
                    }
                    else if (result.Error == ApiError.Unauthorized)
                    {
                        _connectionStatus = "Sign-in rejected. Try again or use Advanced to paste a key.";
                        _connectionStatusColor = Theme.Error;
                    }
                    else
                    {
                        _connectionStatus = "Sign-in failed or timed out. Check your network or use Advanced.";
                        _connectionStatusColor = Theme.Error;
                    }
                });
            });
        }

        ImGui.Spacing();

        if (!string.IsNullOrEmpty(_connectionStatus))
        {
            ImGui.TextColored(_connectionStatusColor, _connectionStatus);
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Advanced (manual API key / custom server)##advanced"))
        {
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
                    _connectionStatusColor = Theme.Warning;

                    Task.Run(async () =>
                    {
                        var testResult = await _apiClient.TestConnectionAsync();
                        List<StaticGroupInfo>? groups = null;
                        if (testResult.IsSuccess)
                        {
                            var groupsResult = await _apiClient.GetStaticGroupsAsync();
                            groups = groupsResult.IsSuccess ? groupsResult.Value : null;
                        }
                        _thread.RunOnUi(() =>
                        {
                            if (testResult.IsSuccess)
                            {
                                _connectionStatus = $"Connected (API v{testResult.Value!.Version})";
                                _connectionStatusColor = Theme.Success;
                                _staticGroups = groups;
                            }
                            else
                            {
                                _connectionStatus = testResult.Error == ApiError.Unauthorized
                                    ? "API key rejected — re-authorize via /xrp config"
                                    : _config.UseCustomUrls
                                        ? "Connection failed. Check API key and custom URLs."
                                        : "Connection failed. Check API key.";
                                _connectionStatusColor = Theme.Error;
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

            ImGui.Spacing();
            ImGui.TextDisabled("Override default server URLs for development/testing.");
            ImGui.Spacing();

            if (ImGui.Checkbox("Use custom URLs", ref _useCustomUrls))
            {
                _config.UseCustomUrls = _useCustomUrls;
                _config.Save();
                _apiClient.UpdateAuth();
            }

            ImGui.Spacing();

            if (!_useCustomUrls) ImGui.BeginDisabled();

            ImGui.Text("API URL");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##apiurl", ref _apiUrlInput, 256))
            {
                if (string.IsNullOrWhiteSpace(_apiUrlInput) || Uri.TryCreate(_apiUrlInput, UriKind.Absolute, out _))
                {
                    _config.ApiBaseUrl = _apiUrlInput;
                    _config.Save();
                    _apiClient.UpdateAuth();
                }
            }
            if (!string.IsNullOrWhiteSpace(_apiUrlInput) && !Uri.TryCreate(_apiUrlInput, UriKind.Absolute, out _))
                ImGui.TextColored(Theme.Error, "Invalid URL. Use an absolute URL, e.g. https://localhost:5000");
            else if (!_useCustomUrls)
                ImGui.TextDisabled($"Default: {Configuration.DefaultApiBaseUrl}");

            ImGui.Spacing();
            ImGui.Text("Frontend URL");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##frontendurl", ref _frontendUrlInput, 256))
            {
                _config.FrontendBaseUrl = _frontendUrlInput;
                _config.Save();
            }
            if (!_useCustomUrls)
                ImGui.TextDisabled($"Default: {Configuration.DefaultFrontendBaseUrl}");

            if (!_useCustomUrls) ImGui.EndDisabled();
        }
    }

    private void DrawStaticTab()
    {
        ImGui.Text("Select your static group and tier.");
        ImGui.Spacing();

        if (_staticGroups == null || _staticGroups.Count == 0)
        {
            ImGui.TextColored(Theme.Warning,
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
                _apiClient.InvalidateResolvedTier();
            }
        }

        // Auto-fetch tiers when group is selected
        if (!string.IsNullOrEmpty(_config.DefaultGroupId) && _tiers == null && !_isFetchingTiers)
        {
            _isFetchingTiers = true;
            var groupId = _config.DefaultGroupId;
            Task.Run(async () =>
            {
                var tiersResult = await _apiClient.GetTiersAsync(groupId);
                _thread.RunOnUi(() =>
                {
                    _tiers = tiersResult.IsSuccess ? tiersResult.Value : new List<TierInfo>();
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
                        var priorityResult = await _apiClient.GetPriorityAsync();
                        _thread.RunOnUi(() =>
                        {
                            if (priorityResult.IsSuccess)
                                _staticPlayers = priorityResult.Value!.Players;
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
            ImGui.TextColored(Theme.Accent, "Current Party");
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
            ImGui.TextColored(Theme.Warning, "Join a party to assign players.");
        }

        // Show saved overrides (for players not currently in party)
        var savedOverrides = _config.PlayerNameOverrides
            .Where(kv => !partyNames.Contains(kv.Key))
            .ToList();

        if (savedOverrides.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Saved Associations (not in party)");
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
                var priorityResult = await _apiClient.GetPriorityAsync();
                _thread.RunOnUi(() =>
                {
                    if (priorityResult.IsSuccess)
                        _staticPlayers = priorityResult.Value!.Players;
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // BiS highlighting section
        ImGui.TextColored(Theme.Accent, "BiS Highlighting");
        ImGui.TextDisabled("Tints BiS items in game UI windows with a teal highlight.");
        ImGui.Spacing();

        var bisHighlight = _config.EnableBisHighlighting;
        if (ImGui.Checkbox("Highlight BiS in Loot Window (Need/Greed)", ref bisHighlight))
        {
            _config.EnableBisHighlighting = bisHighlight;
            _config.Save();
        }

        var shopHighlight = _config.EnableShopHighlighting;
        if (ImGui.Checkbox("Highlight BiS in Tome Vendor Shops", ref shopHighlight))
        {
            _config.EnableShopHighlighting = shopHighlight;
            _config.Save();
        }
    }

    public void Dispose() { }
}
