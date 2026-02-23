using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using ImGuiNET;
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

    // UI state
    private string _apiKeyInput = "";
    private string _apiUrlInput = "";
    private string _connectionStatus = "";
    private Vector4 _connectionStatusColor = new(1, 1, 1, 1);
    private bool _isTesting;
    private List<StaticGroupInfo>? _staticGroups;
    private int _selectedGroupIndex = -1;
    private int _selectedAutoLogMode;

    public ConfigWindow(Configuration config, RaidPlannerClient apiClient, PartyMatchingService partyMatching)
        : base("XIV Raid Planner - Settings",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _apiClient = apiClient;
        _partyMatching = partyMatching;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(600, 800),
        };

        _apiKeyInput = _config.ApiKey;
        _apiUrlInput = _config.ApiBaseUrl;
        _selectedAutoLogMode = (int)_config.AutoLogMode;
    }

    public override void Draw()
    {
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
                    if (result != null)
                    {
                        _connectionStatus = $"Connected (API v{result.Version})";
                        _connectionStatusColor = new Vector4(0, 1, 0, 1);

                        // Also fetch statics on successful connection
                        _staticGroups = await _apiClient.GetStaticGroupsAsync();
                    }
                    else
                    {
                        _connectionStatus = "Connection failed. Check URL and API key.";
                        _connectionStatusColor = new Vector4(1, 0, 0, 1);
                    }
                    _isTesting = false;
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

        if (ImGui.Combo("##group", ref _selectedGroupIndex, groupNames, groupNames.Length))
        {
            if (_selectedGroupIndex >= 0 && _selectedGroupIndex < _staticGroups.Count)
            {
                _config.DefaultGroupId = _staticGroups[_selectedGroupIndex].Id;
                _config.Save();
            }
        }

        // Tier selector placeholder
        ImGui.Spacing();
        ImGui.Text("Tier ID");
        var tierInput = _config.DefaultTierId;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##tierid", ref tierInput, 100))
        {
            _config.DefaultTierId = tierInput;
            _config.Save();
        }
        ImGui.TextDisabled("e.g., aac-heavyweight");
    }

    private void DrawPlayersTab()
    {
        ImGui.Text("Party Member Matching");
        ImGui.Spacing();

        if (_partyMatching.CurrentMatches.Count == 0 && _partyMatching.UnmatchedPartyMembers.Count == 0)
        {
            ImGui.TextDisabled("Enter a savage instance to see party matching.");
            return;
        }

        // Matched players
        if (_partyMatching.CurrentMatches.Count > 0)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Matched:");
            foreach (var (name, playerId) in _partyMatching.CurrentMatches)
            {
                ImGui.BulletText($"{name} -> {playerId[..8]}...");
            }
        }

        // Unmatched party members
        if (_partyMatching.UnmatchedPartyMembers.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "Unmatched Party Members:");
            foreach (var name in _partyMatching.UnmatchedPartyMembers)
            {
                ImGui.BulletText(name);
            }
            ImGui.TextDisabled("Use manual overrides in the config to link these players.");
        }

        // Unmatched planner players
        if (_partyMatching.UnmatchedPlayers.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Unmatched Planner Players:");
            foreach (var player in _partyMatching.UnmatchedPlayers)
            {
                ImGui.BulletText($"{player.Name} ({player.Job})");
            }
        }
    }

    private void DrawSettingsTab()
    {
        // Auto-log mode
        ImGui.Text("Auto-Log Mode");
        var modes = new[] { "Confirm", "Auto", "Manual" };
        if (ImGui.Combo("##autolog", ref _selectedAutoLogMode, modes, modes.Length))
        {
            _config.AutoLogMode = (AutoLogMode)_selectedAutoLogMode;
            _config.Save();
        }

        ImGui.Spacing();

        // Show overlay toggle
        var showOverlay = _config.ShowOverlay;
        if (ImGui.Checkbox("Show Priority Overlay", ref showOverlay))
        {
            _config.ShowOverlay = showOverlay;
            _config.Save();
        }

        // Leave warning toggle
        var leaveWarning = _config.EnableLeaveWarning;
        if (ImGui.Checkbox("Warn When Leaving with Unclaimed Loot", ref leaveWarning))
        {
            _config.EnableLeaveWarning = leaveWarning;
            _config.Save();
        }

        // Overlay scale
        ImGui.Spacing();
        var scale = _config.OverlayScale;
        if (ImGui.SliderFloat("Overlay Scale", ref scale, 0.5f, 2.0f, "%.1f"))
        {
            _config.OverlayScale = scale;
            _config.Save();
        }
    }

    public void Dispose() { }
}
