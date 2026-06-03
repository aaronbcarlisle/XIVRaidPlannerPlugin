using System.Runtime.CompilerServices;

// Allow the unit-test assembly to call internal test seams (e.g. RaidPlannerClient.SendForTest).
// Keeps those seams out of the production API surface so plugin consumers can't accidentally
// take a dependency on them.
[assembly: InternalsVisibleTo("XIVRaidPlannerPlugin.Tests")]
