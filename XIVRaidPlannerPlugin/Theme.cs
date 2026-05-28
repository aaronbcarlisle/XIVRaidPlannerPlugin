using System.Numerics;

namespace XIVRaidPlannerPlugin;

/// <summary>Single source of truth for plugin colors. Mirrors the web app design tokens.</summary>
public static class Theme
{
    // Role colors (web app design system)
    public static readonly Vector4 Tank   = new(0.353f, 0.624f, 0.831f, 1f);  // #5a9fd4
    public static readonly Vector4 Healer = new(0.353f, 0.831f, 0.565f, 1f);  // #5ad490
    public static readonly Vector4 Melee  = new(0.831f, 0.353f, 0.353f, 1f);  // #d45a5a
    public static readonly Vector4 Ranged = new(0.831f, 0.627f, 0.353f, 1f);  // #d4a05a
    public static readonly Vector4 Caster = new(0.706f, 0.353f, 0.831f, 1f);  // #b45ad4

    // Semantic colors
    public static readonly Vector4 Accent  = new(0.078f, 0.722f, 0.651f, 1f);  // #14b8a6
    public static readonly Vector4 Success = new(0.133f, 0.773f, 0.369f, 1f);  // #22c55e
    public static readonly Vector4 Warning = new(1f, 1f, 0f, 1f);              // #ffff00
    public static readonly Vector4 Error   = new(1f, 0.3f, 0.3f, 1f);         // #ff4d4d
    public static readonly Vector4 Muted   = new(0.6f, 0.6f, 0.6f, 1f);       // #999999
    public static readonly Vector4 White   = new(1f, 1f, 1f, 1f);             // #ffffff

    // Floor accent colors (1-4)
    public static readonly Vector4 Floor1 = new(0.133f, 0.773f, 0.369f, 1f);  // #22c55e
    public static readonly Vector4 Floor2 = new(0.231f, 0.510f, 0.965f, 1f);  // #3b82f6
    public static readonly Vector4 Floor3 = new(0.659f, 0.333f, 0.969f, 1f);  // #a855f7
    public static readonly Vector4 Floor4 = new(0.961f, 0.620f, 0.043f, 1f);  // #f59e0b

    public static Vector4 RoleColor(string role) => role switch
    {
        "tank"   => Tank,
        "healer" => Healer,
        "melee"  => Melee,
        "ranged" => Ranged,
        "caster" => Caster,
        _        => White,
    };

    public static Vector4 FloorColor(int floor) => floor switch
    {
        1 => Floor1,
        2 => Floor2,
        3 => Floor3,
        4 => Floor4,
        _ => Muted,
    };
}
