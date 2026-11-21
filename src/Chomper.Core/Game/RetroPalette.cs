using Microsoft.Xna.Framework;

namespace Chomper.Core.Game;

/// <summary>Small fixed palette inspired by 16-bit arcade hardware.</summary>
internal static class RetroPalette
{
    public static readonly Color Ink = new(8, 8, 24);
    public static readonly Color DeepNavy = new(16, 24, 64);
    public static readonly Color Wall = new(32, 80, 144);
    public static readonly Color WallHighlight = new(48, 176, 176);
    public static readonly Color Cyan = new(48, 176, 176);
    public static readonly Color White = new(248, 240, 208);
    public static readonly Color Gray = new(120, 128, 144);
    public static readonly Color Green = new(64, 184, 96);
    public static readonly Color Yellow = new(248, 216, 72);
    public static readonly Color Blue = new(56, 136, 216);
    public static readonly Color Red = new(224, 48, 56);
    public static readonly Color Orange = new(232, 120, 48);
    public static readonly Color Pink = new(216, 72, 136);
    public static readonly Color Purple = new(128, 64, 168);
    public static readonly Color PowerBlue = new(80, 112, 192);
    public static readonly Color Black = new(0, 0, 0);
}
