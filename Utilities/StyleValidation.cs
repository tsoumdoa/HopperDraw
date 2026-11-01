using System;
using System.Drawing.Drawing2D;

namespace hopperdraw
{
    internal static class StyleValidation
    {
        internal const float MinWidth = 0.1f, MaxWidth = 1000f, MinMultiplier = 0.01f;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static bool ValidWidth(double value) => Finite(value) && value >= 0.1 && value <= MaxWidth;
        internal static bool ValidMultiplier(double value) => Finite(value) && value >= MinMultiplier && value <= 1000;
        internal static float Width(double value) => Finite(value) ? (float)Math.Max(MinWidth, Math.Min(MaxWidth, value)) : 8f;
        internal static float Multiplier(double value) => Finite(value) ? (float)Math.Max(MinMultiplier, Math.Min(1000, value)) : 1f;
        internal static float EffectiveWidth(double basis, double multiplier) => Width((double)Width(basis) * Multiplier(multiplier));
        internal static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        internal static DashStyle SafeDash(DashStyle value) => (int)value >= 0 && (int)value <= 4 ? value : DashStyle.Solid;
    }
}
