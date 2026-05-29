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
    private readonly BiSDataService _bisData;

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
    private string? _rosterFetchError;
    private string? _autoDetectStatus;
    private Vector4 _autoDetectStatusColor = Theme.White;

    public ConfigWindow(Configuration config, RaidPlannerClient apiClient, PartyMatchingService partyMatching, IPartyList partyList, IPlayerState playerState, PluginThread thread, BrowserAuthService browserAuth, BiSDataService bisData)
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
        _bisData = bisData;

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
        // Auto-fetch statics on first draw if credentials are already configured.
        // Mirrors the sign-in flow: test connection, then run the full auto-config
        // chain (statics → tier list → roster → auto-detect → BiS) so plugin reloads
        // restore full state without manual clicks.
        if (!_autoConnectAttempted && !_isTesting
            && !string.IsNullOrEmpty(_config.ApiKey))
        {
            _autoConnectAttempted = true;
            _isTesting = true;
            _connectionStatus = "Connecting...";
            _connectionStatusColor = Theme.Warning;

            _thread.RunBackground(async () =>
            {
                var testResult = await _apiClient.TestConnectionAsync();
                await _thread.RunOnUiAsync(() =>
                {
                    if (testResult.IsSuccess)
                    {
                        _connectionStatus = $"Connected (API v{testResult.Value!.Version})";
                        _connectionStatusColor = Theme.Success;
                    }
                    else if (testResult.Error == ApiError.Unauthorized)
                    {
                        _connectionStatus = "API key rejected. Sign in again from the Connection tab.";
                        _connectionStatusColor = Theme.Error;
                    }
                    else
                    {
                        _connectionStatus = "";
                    }
                    _isTesting = false;
                });

                // Only chain the full setup if auth succeeded. PostSignInAutoConfigAsync
                // also fetches statics, so we don't duplicate that work here.
                if (testResult.IsSuccess)
                    await PostSignInAutoConfigAsync();
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

                // On success, fetch statics and (if exactly one) chain the full auto-config.
                if (result.IsSuccess)
                    await PostSignInAutoConfigAsync();
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

                    _thread.RunBackground(async () =>
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
                _config.DefaultTierName = string.Empty;
                _tiers = null;
                _selectedTierIndex = -1;
                _staticPlayers = null; // Force re-fetch — old roster belongs to the previous static.
                _config.Save();
                _apiClient.InvalidateResolvedTier();
                // Force=true: overrides are character-keyed (not static-scoped), so the prior
                // value may belong to a different static and must be replaced.
                _thread.RunBackground(async () => await ReloadRosterAndAutoDetectAsync(force: true));
            }
        }

        // Auto-fetch tiers when group is selected
        if (!string.IsNullOrEmpty(_config.DefaultGroupId) && _tiers == null && !_isFetchingTiers)
        {
            _isFetchingTiers = true;
            var groupId = _config.DefaultGroupId;
            _thread.RunBackground(async () =>
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
                    _apiClient.InvalidateResolvedTier();
                }
                else if (_selectedTierIndex > 0 && _selectedTierIndex <= _tiers.Count)
                {
                    var tier = _tiers[_selectedTierIndex - 1];
                    _config.DefaultTierId = tier.Id;
                    _config.DefaultTierName = tier.TierId;
                    _config.Save();
                }
                _staticPlayers = null; // Roster is per-snapshot — old data is stale on tier change.
                // Force=true: player IDs are per-snapshot, so the prior override is now stale.
                _thread.RunBackground(async () => await ReloadRosterAndAutoDetectAsync(force: true));
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
                    _thread.RunBackground(async () => await ReloadRosterAndAutoDetectAsync(force: false));
                }
                ImGui.SameLine();
                ImGui.TextDisabled("Requires connection + static configured.");
            }
            else
            {
                ImGui.TextDisabled("Loading...");
            }

            if (!string.IsNullOrEmpty(_rosterFetchError))
            {
                ImGui.Spacing();
                ImGui.TextColored(Theme.Error, _rosterFetchError);
            }
            if (!string.IsNullOrEmpty(_autoDetectStatus))
            {
                ImGui.Spacing();
                ImGui.TextColored(_autoDetectStatusColor, _autoDetectStatus);
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
                    // Route through PartyMatchingService so OnOverrideChanged fires (BiS window refresh, etc.)
                    if (selectedIndex == 0)
                        _partyMatching.RemoveOverride(partyName, _staticPlayers);
                    else
                        _partyMatching.SetOverride(partyName, _staticPlayers[selectedIndex - 1].Id, _staticPlayers);
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
                _partyMatching.RemoveOverride(keyToRemove, _staticPlayers);
        }

        ImGui.Spacing();
        if (ImGui.Button("Refresh"))
        {
            _thread.RunBackground(async () => await ReloadRosterAndAutoDetectAsync(force: false));
        }
        ImGui.SameLine();
        if (ImGui.Button("Auto-detect my player"))
        {
            _autoDetectStatus = null;
            _thread.RunBackground(async () =>
            {
                if (_staticPlayers != null)
                    await TryAutoDetectPlayerAsync(_staticPlayers, force: true);
            });
        }

        if (!string.IsNullOrEmpty(_rosterFetchError) && _staticPlayers != null && _staticPlayers.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Error, _rosterFetchError);
        }
        if (!string.IsNullOrEmpty(_autoDetectStatus))
        {
            ImGui.Spacing();
            ImGui.TextColored(_autoDetectStatusColor, _autoDetectStatus);
        }
    }

    /// <summary>
    /// Full post-auth chain — runs after both fresh sign-in and plugin-reload auto-connect.
    /// Fetches statics, picks the right one (prefer existing DefaultGroupId, else
    /// auto-pick when exactly one accessible), prefetches the tier list, loads the
    /// roster, runs auto-detect, and loads BiS gear data — so every tab and vendor
    /// highlighting work without further clicks. If no group can be picked
    /// automatically, surfaces the list and lets the user choose; the Static dropdown
    /// handler then triggers the same chain via its own ReloadRosterAndAutoDetectAsync.
    /// </summary>
    private async Task PostSignInAutoConfigAsync()
    {
        var groupsResult = await _apiClient.GetStaticGroupsAsync();
        if (!groupsResult.IsSuccess)
        {
            await _thread.RunOnUiAsync(() =>
            {
                _connectionStatus = "Signed in, but couldn't load your statics. Try again from Test Connection.";
                _connectionStatusColor = Theme.Warning;
            });
            return;
        }

        var groups = groupsResult.Value!;
        await _thread.RunOnUiAsync(() =>
        {
            _staticGroups = groups;
            // Prevent the on-Draw auto-connect path from also firing GetStaticGroupsAsync.
            _autoConnectAttempted = true;
        });

        // Pick the static to use:
        //   1. If DefaultGroupId from previous session matches an accessible group, use it.
        //   2. Otherwise, if exactly one accessible group, auto-pick it.
        //   3. Otherwise, let the user pick from the dropdown.
        StaticGroupInfo? selected = null;
        if (!string.IsNullOrEmpty(_config.DefaultGroupId))
            selected = groups.Find(g => g.Id == _config.DefaultGroupId);
        if (selected == null && groups.Count == 1)
            selected = groups[0];
        if (selected == null) return;

        var groupId = selected.Id;
        var groupName = selected.Name;
        var groupShareCode = selected.ShareCode;
        var groupIndex = groups.IndexOf(selected);
        var reusingExistingGroup = _config.DefaultGroupId == groupId;

        // Await the config write so the subsequent API call sees the new DefaultGroupId.
        await _thread.RunOnUiAsync(() =>
        {
            _config.DefaultGroupId = groupId;
            _config.DefaultGroupName = groupName;
            _config.DefaultGroupShareCode = groupShareCode;
            // Preserve a previously chosen tier when reusing the existing group;
            // otherwise reset to Auto so the backend picks the active tier.
            if (!reusingExistingGroup)
            {
                _config.DefaultTierId = string.Empty;
                _config.DefaultTierName = string.Empty;
            }
            _config.Save();
            _selectedGroupIndex = groupIndex;
            _selectedTierIndex = -1;
            _tiers = null;
            if (!reusingExistingGroup)
                _apiClient.InvalidateResolvedTier();
        });

        // Prefetch the tier list so the Static tab is populated when first opened.
        // Runs in parallel with the roster/auto-detect chain — they don't depend on
        // the tier list (priority uses Auto-resolution server-side).
        var tiersTask = _apiClient.GetTiersAsync(groupId);

        await ReloadRosterAndAutoDetectAsync(force: false);

        var tiersResult = await tiersTask;
        await _thread.RunOnUiAsync(() =>
        {
            _tiers = tiersResult.IsSuccess ? tiersResult.Value : new List<TierInfo>();
            _isFetchingTiers = false;
        });

        // Ensure BiS data is loaded so vendor highlighting works without the user
        // having to open the BiS window first. Auto-detect only re-fetches BiS when
        // the override actually CHANGES — if a stale-but-correct override was already
        // present, OnOverrideChanged doesn't fire, so we load BiS explicitly here.
        var charName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
        if (!string.IsNullOrEmpty(charName) && _bisData.CurrentPlayerGear == null)
            await _bisData.FetchCurrentPlayerGearAsync(charName);
    }

    /// <summary>
    /// Fetch the roster for the configured static/tier, then run auto-detect. Centralizes the
    /// "context changed → re-link my player" chain triggered from Load/Refresh and from the
    /// Static and Tier dropdown changes. Set <paramref name="force"/> to overwrite an existing
    /// override (use for context changes; leave false for initial loads that should respect manual links).
    /// </summary>
    private async Task ReloadRosterAndAutoDetectAsync(bool force)
    {
        _thread.RunOnUi(() =>
        {
            _isFetchingRoster = true;
            _rosterFetchError = null;
            _autoDetectStatus = null;
        });

        var priorityResult = await _apiClient.GetPriorityAsync();
        _thread.RunOnUi(() =>
        {
            if (priorityResult.IsSuccess)
            {
                _staticPlayers = priorityResult.Value!.Players;
                if (_staticPlayers.Count == 0)
                    _rosterFetchError = "Tier loaded but has no players. Add players in the web app, or pick a different tier in the Static tab.";
                // Surface "no tier is marked active" when the resolver fell back to most-recent —
                // otherwise the user is silently pointed at an arbitrary tier and the actionable
                // error message ("pick a specific tier or mark one active") never fires.
                else if (!string.IsNullOrEmpty(_apiClient.LastTierResolutionWarning))
                {
                    _autoDetectStatus = _apiClient.LastTierResolutionWarning;
                    _autoDetectStatusColor = Theme.Warning;
                }
            }
            else
            {
                _rosterFetchError = priorityResult.Error switch
                {
                    ApiError.NotFound => "Couldn't resolve a tier for the selected static. Pick a specific tier in the Static tab, or check that one is marked active in the web app.",
                    ApiError.Unauthorized => "Not signed in or API key was revoked. Re-authorize in the Connection tab.",
                    ApiError.Network => "Network error while fetching the roster. Check your connection.",
                    ApiError.Server => "Server error while fetching the roster. Try again in a moment.",
                    _ => "Failed to load static roster. See plugin log for details.",
                };
            }
            _isFetchingRoster = false;
        });

        if (priorityResult.IsSuccess && priorityResult.Value!.Players.Count > 0)
            await TryAutoDetectPlayerAsync(priorityResult.Value!.Players, force: force);
    }

    /// <summary>
    /// External hook used by Plugin.cs to surface BiS-fetch failures on the Players
    /// tab when the local character's link changes outside of the auto-detect chain
    /// (e.g., dropdown change). Without this, fetch errors only became visible when
    /// the user next opened the BiS window — long after the action that triggered them.
    /// </summary>
    public void ShowPlayerLinkStatus(string message, Vector4 color)
    {
        _thread.RunOnUi(() =>
        {
            _autoDetectStatus = message;
            _autoDetectStatusColor = color;
        });
    }

    /// <summary>
    /// Look up the signed-in user's player card in the active tier and auto-populate
    /// the character→player override. Silent on no match (user may not be a member yet);
    /// shows a status when a link is created or when manual disambiguation is needed.
    /// Set <paramref name="force"/> to overwrite an existing override.
    /// </summary>
    private async Task TryAutoDetectPlayerAsync(List<PlayerInfo> roster, bool force = false)
    {
        var characterName = _playerState.IsLoaded ? _playerState.CharacterName?.ToString() : null;
        if (string.IsNullOrEmpty(characterName))
        {
            _thread.RunOnUi(() =>
            {
                _autoDetectStatus = "Character not loaded — can't auto-detect.";
                _autoDetectStatusColor = Theme.Warning;
            });
            return;
        }

        if (!force && _config.PlayerNameOverrides.ContainsKey(characterName))
            return; // Respect existing manual link.

        var meResult = await _apiClient.GetCurrentUserAsync();
        if (!meResult.IsSuccess)
        {
            _thread.RunOnUi(() =>
            {
                _autoDetectStatus = "Couldn't fetch your account info — try Refresh.";
                _autoDetectStatusColor = Theme.Warning;
            });
            return;
        }

        var playersResult = await _apiClient.GetSnapshotPlayersAsync();
        if (!playersResult.IsSuccess)
        {
            // The manual "Auto-detect my player" button bypasses ReloadRosterAndAutoDetectAsync,
            // so this is the only chance to tell the user why the click did nothing.
            _thread.RunOnUi(() =>
            {
                _autoDetectStatus = playersResult.Error switch
                {
                    ApiError.NotFound => "Couldn't load the player list. Pick a specific tier in the Static tab and try again.",
                    ApiError.Unauthorized => "Not signed in or API key was revoked. Re-authorize in the Connection tab.",
                    ApiError.Network => "Network error while looking up players. Check your connection.",
                    ApiError.Server => "Server error while looking up players. Try again in a moment.",
                    _ => "Couldn't look up players. See plugin log for details.",
                };
                _autoDetectStatusColor = Theme.Warning;
            });
            return;
        }

        var matches = playersResult.Value!.FindAll(p => p.UserId == meResult.Value!.Id);
        _thread.RunOnUi(() =>
        {
            switch (matches.Count)
            {
                case 1:
                    _partyMatching.SetOverride(characterName, matches[0].Id, roster);
                    _autoDetectStatus = $"Auto-linked {characterName} -> {matches[0].Name}";
                    _autoDetectStatusColor = Theme.Success;
                    break;
                case 0:
                    if (force)
                    {
                        _autoDetectStatus = "No player in this static is claimed by your account. Ask the owner to assign you, or pick manually below.";
                        _autoDetectStatusColor = Theme.Warning;
                    }
                    break;
                default:
                    _autoDetectStatus = $"Your account claims {matches.Count} players in this static. Pick the right one manually below.";
                    _autoDetectStatusColor = Theme.Warning;
                    break;
            }
        });
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
