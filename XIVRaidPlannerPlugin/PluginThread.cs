using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XIVRaidPlannerPlugin;

/// <summary>Centralizes background/UI thread marshaling with exception logging.</summary>
public sealed class PluginThread
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public PluginThread(IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;
    }

    /// <summary>Run async work off the game thread; logs unhandled exceptions.</summary>
    public void RunBackground(Func<Task> work)
    {
        Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { _log.Error($"[Background] {ex}"); }
        });
    }

    /// <summary>Marshal an action onto the framework (game) thread (fire-and-forget).</summary>
    public void RunOnUi(Action action) => _framework.RunOnFrameworkThread(action);

    /// <summary>
    /// Marshal an action onto the framework thread and return a Task that completes when
    /// the action has run. Use this when subsequent background work depends on UI-thread
    /// state writes (e.g., Configuration field assignments) having already taken effect.
    /// </summary>
    public Task RunOnUiAsync(Action action) => _framework.RunOnFrameworkThread(action);
}
