using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Overlay shown during savage raids when the static has split-clear mode enabled.
/// Displays the current player's run assignment (Run A / Run B), their character,
/// their loot target, and teammates in the same run.
/// </summary>
public class SplitClearOverlayWindow : Window, IDisposable
{
    private readonly Configuration _config;

    private string _staticName = "";
    private SplitRunInfo? _playerRun;
    private bool _isMarkingCleared;

    private string _statusMessage = "";
    private Vector4 _statusColor;
    private DateTime _statusExpiry;

    /// <summary>Fired when the user clicks "Mark Run Cleared". Arg = "A" or "B".</summary>
    public event Action<string>? OnMarkRunCleared;

    /// <summary>Fired when the user clicks the refresh button.</summary>
    public event Action? OnRefresh;

    public bool HasData => _playerRun != null;

    public SplitClearOverlayWindow(Configuration config)
        : base("Split Clear##XRPSplitClear", ImGuiWindowFlags.NoCollapse)
    {
        _config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 180),
            MaximumSize = new Vector2(600, 600),
        };
        BgAlpha = 0.95f;
    }

    /// <summary>
    /// Populate the window from fresh split-clear API data.
    /// Resolves which run the local character belongs to by name-matching.
    /// </summary>
    public void SetData(SplitClearDataResponse data, string? localCharName, string staticName)
    {
        if (!string.IsNullOrEmpty(staticName))
            _staticName = staticName;
        WindowName = string.IsNullOrEmpty(_staticName)
            ? "Split Clear##XRPSplitClear"
            : $"Split Clear  |  {_staticName}##XRPSplitClear";

        _playerRun = string.IsNullOrEmpty(localCharName)
            ? null
            : ResolvePlayerRun(data, localCharName, data.PlayerCharacters);
        _isMarkingCleared = false;
    }

    public void ClearData()
    {
        _playerRun = null;
        _staticName = "";
        _statusMessage = "";
        _isMarkingCleared = false;
        WindowName = "Split Clear##XRPSplitClear";
    }

    public void MarkRunClearedSuccess(string run)
    {
        _isMarkingCleared = false;
        if (_playerRun != null && _playerRun.Run == run)
        {
            _playerRun.RunCleared = true;
            foreach (var t in _playerRun.Teammates)
                t.Cleared = true;
        }
        ShowStatus($"Run {run} marked as cleared!", Theme.Success);
    }

    public void MarkRunClearedFailed()
    {
        _isMarkingCleared = false;
        ShowStatus("Failed to mark run cleared — check connection.", Theme.Error);
    }

    public void ShowStatus(string message, Vector4 color)
    {
        _statusMessage = message;
        _statusColor = color;
        _statusExpiry = DateTime.UtcNow.AddSeconds(5);
    }

    public override void Draw()
    {
        if (_playerRun == null)
        {
            ImGui.TextColored(Theme.Muted, "Not assigned to a split run for this static.");
            ImGui.Spacing();
            if (ImGui.SmallButton("Refresh"))
                OnRefresh?.Invoke();
            return;
        }

        var run = _playerRun;

        // ── Run label ──
        var runColor = run.Run == "A" ? Theme.Accent : Theme.Warning;
        ImGui.TextColored(runColor, $"Run {run.Run}");
        ImGui.SameLine();
        var clearLabel = run.RunCleared ? "  ✓ Cleared" : "";
        if (run.RunCleared) ImGui.TextColored(Theme.Success, clearLabel);

        ImGui.Separator();

        // ── Character ──
        ImGui.TextColored(Theme.Muted, "Character");
        ImGui.SameLine();
        var charDisplay = string.IsNullOrEmpty(run.CharacterWorld)
            ? (run.CharacterName ?? "—")
            : $"{run.CharacterName}  @{run.CharacterWorld}";
        ImGui.Text(charDisplay);

        // ── Loot target ──
        ImGui.TextColored(Theme.Muted, "Loot target");
        ImGui.SameLine();
        var lootLabel = FormatLootTarget(run.LootTarget, run.LootTargetJob);
        ImGui.TextColored(Theme.White, lootLabel);

        // ── Teammates ──
        if (run.Teammates.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Same run:");
            foreach (var teammate in run.Teammates)
            {
                var clearMark = teammate.Cleared ? " ✓" : "";
                ImGui.Text($"  {teammate.CharacterName ?? teammate.PlayerName}{clearMark}");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Actions ──
        if (!run.RunCleared)
        {
            if (_isMarkingCleared)
            {
                ImGui.TextColored(Theme.Muted, "Marking cleared...");
            }
            else if (ImGui.Button($"Mark Run {run.Run} Cleared"))
            {
                _isMarkingCleared = true;
                OnMarkRunCleared?.Invoke(run.Run);
            }
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Refresh"))
            OnRefresh?.Invoke();

        // ── Status message ──
        if (!string.IsNullOrEmpty(_statusMessage) && DateTime.UtcNow < _statusExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(_statusColor, _statusMessage);
        }
    }

    public void Dispose() { }

    // ==================== Run Resolution ====================

    private static SplitRunInfo? ResolvePlayerRun(
        SplitClearDataResponse data,
        string localCharName,
        Dictionary<string, List<SplitClearCharacter>> playerChars)
    {
        var needle = localCharName.Trim().ToLowerInvariant();

        foreach (var a in data.Assignments)
        {
            // Resolve character names for each run slot
            var (nameA, worldA) = GetRunCharacterNameWorld(a, "A", playerChars.GetValueOrDefault(a.SnapshotPlayerId));
            var (nameB, worldB) = GetRunCharacterNameWorld(a, "B", playerChars.GetValueOrDefault(a.SnapshotPlayerId));

            if (nameA?.Trim().ToLowerInvariant() == needle)
                return BuildRunInfo(a, "A", nameA, worldA, data.Assignments, playerChars);
            if (nameB?.Trim().ToLowerInvariant() == needle)
                return BuildRunInfo(a, "B", nameB, worldB, data.Assignments, playerChars);
        }

        return null;
    }

    private static (string? Name, string? World) GetRunCharacterNameWorld(
        SplitClearAssignmentDto a, string run, List<SplitClearCharacter>? chars)
    {
        var slot = run == "A" ? a.RunACharacter : a.RunBCharacter;
        var linkId = run == "A" ? a.RunACharacterLinkId : a.RunBCharacterLinkId;

        // Prefer linked Player Hub character
        if (!string.IsNullOrEmpty(linkId) && chars != null)
        {
            var linked = chars.FirstOrDefault(c => c.Id == linkId);
            if (linked != null) return (linked.Name, linked.Server);
        }

        // Fall back to legacy text fields
        if (slot == "main") return (a.MainCharacterName, a.MainCharacterWorld);
        if (slot == "alt")  return (a.AltCharacterName, a.AltCharacterWorld);

        return (null, null);
    }

    private static SplitRunInfo BuildRunInfo(
        SplitClearAssignmentDto self, string run,
        string? charName, string? charWorld,
        List<SplitClearAssignmentDto> allAssignments,
        Dictionary<string, List<SplitClearCharacter>> playerChars)
    {
        var cleared = run == "A" ? self.RunACleared : self.RunBCleared;

        // Build teammate list: all other assignments that have a character in the same run slot pattern
        // We look for assignments where run A or run B has a character name (not the current player).
        var teammates = new List<RunTeammate>();
        foreach (var other in allAssignments)
        {
            if (other.SnapshotPlayerId == self.SnapshotPlayerId) continue;

            var (oName, _) = GetRunCharacterNameWorld(other, run, playerChars.GetValueOrDefault(other.SnapshotPlayerId));
            if (string.IsNullOrEmpty(oName)) continue;

            teammates.Add(new RunTeammate
            {
                PlayerName = other.SnapshotPlayerId,
                CharacterName = oName,
                Cleared = run == "A" ? other.RunACleared : other.RunBCleared,
            });
        }

        return new SplitRunInfo
        {
            Run = run,
            CharacterName = charName,
            CharacterWorld = charWorld,
            LootTarget = self.LootTarget,
            LootTargetJob = self.LootTargetJob,
            RunCleared = cleared,
            Teammates = teammates,
        };
    }

    private static string FormatLootTarget(string? lootTarget, string? lootTargetJob) => lootTarget switch
    {
        "funnel_main" => "Funnel Main",
        "funnel_job"  => string.IsNullOrEmpty(lootTargetJob) ? "Funnel Job" : $"Funnel {lootTargetJob}",
        _             => "Normal",
    };
}

/// <summary>Resolved split-run context for the local player.</summary>
internal sealed class SplitRunInfo
{
    public string Run { get; init; } = "A";
    public string? CharacterName { get; init; }
    public string? CharacterWorld { get; init; }
    public string? LootTarget { get; init; }
    public string? LootTargetJob { get; init; }
    public bool RunCleared { get; set; }
    public List<RunTeammate> Teammates { get; init; } = new();
}

internal sealed class RunTeammate
{
    public string PlayerName { get; init; } = string.Empty;
    public string? CharacterName { get; init; }
    public bool Cleared { get; set; }
}
