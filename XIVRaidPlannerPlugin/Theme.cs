using System.Collections.Generic;
using System.Numerics;

namespace XIVRaidPlannerPlugin;

/// <summary>Single source of truth for plugin colors. Mirrors the web app design tokens.</summary>
public static class Theme
{
    // Role colors (web app design system)
    public static readonly Vector4 Tank = new(0.353f, 0.624f, 0.831f, 1f);
    public static readonly Vector4 Healer = new(0.353f, 0.831f, 0.565f, 1f);
    public static readonly Vector4 Melee = new(0.831f, 0.353f, 0.353f, 1f);
    public static readonly Vector4 Ranged = new(0.831f, 0.627f, 0.353f, 1f);
    public static readonly Vector4 Caster = new(0.706f, 0.353f, 0.831f, 1f);

    // Semantic colors
    public static readonly Vector4 Accent = new(0.298f, 0.722f, 0.659f, 1f);
    public static readonly Vector4 Success = new(0.133f, 0.773f, 0.369f, 1f);
    public static readonly Vector4 Warning = new(1f, 1f, 0f, 1f);
    public static readonly Vector4 Error = new(1f, 0.3f, 0.3f, 1f);
    public static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    private static readonly Dictionary<string, Vector4> Roles = new()
    {
        ["tank"] = Tank, ["healer"] = Healer, ["melee"] = Melee,
        ["ranged"] = Ranged, ["caster"] = Caster,
    };

    // Floor accent colors (1-4)
    private static readonly Dictionary<int, Vector4> Floors = new()
    {
        [1] = new(0.133f, 0.773f, 0.369f, 1f),
        [2] = new(0.231f, 0.510f, 0.965f, 1f),
        [3] = new(0.659f, 0.333f, 0.969f, 1f),
        [4] = new(0.961f, 0.620f, 0.043f, 1f),
    };

    public static Vector4 RoleColor(string role) => Roles.GetValueOrDefault(role, White);
    public static Vector4 FloorColor(int floor) => Floors.GetValueOrDefault(floor, Muted);
}
