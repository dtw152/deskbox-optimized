namespace DeskBox.Services;

internal readonly record struct WidgetMaterialOpacityProfile(
    double TintOpacity,
    double LuminosityOpacity);

internal readonly record struct WidgetMaterialGradientProfile(
    Windows.UI.Color StartColor,
    Windows.UI.Color EndColor);

/// <summary>
/// Centralizes the visual parameters shared by native widget backdrops and
/// embedded material surfaces such as the Glance calendar panel.
/// </summary>
internal static class WidgetMaterialVisualCalculator
{
    public static WidgetMaterialOpacityProfile CalculateAcrylic(
        bool isDark,
        bool useBase,
        double surfaceOpacity,
        double materialIntensity)
    {
        double intensity = NormalizeMaterialIntensity(materialIntensity);
        double surfaceStrength = Lerp(0.08, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = useBase
            ? Lerp(isDark ? 0.18 : 0.12, isDark ? 0.72 : 0.62, intensity)
            : Lerp(isDark ? 0.04 : 0.02, isDark ? 0.42 : 0.34, intensity);
        double luminosityOpacity = useBase
            ? Lerp(isDark ? 0.38 : 0.46, isDark ? 0.82 : 0.90, intensity)
            : Lerp(isDark ? 0.16 : 0.22, isDark ? 0.56 : 0.64, intensity);

        return new WidgetMaterialOpacityProfile(
            Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0),
            Math.Clamp(luminosityOpacity * surfaceStrength, 0.0, 1.0));
    }

    /// <summary>
    /// Milky matte frost ("磨砂"): Desktop Acrylic Base pushed toward a dense,
    /// opaque tint so it reads as frosted glass rather than transparent blur.
    /// </summary>
    public static WidgetMaterialOpacityProfile CalculateFrosted(
        bool isDark,
        double surfaceOpacity,
        double materialIntensity)
    {
        double intensity = NormalizeMaterialIntensity(materialIntensity);
        // Frost never fades to invisible: the opacity slider still works but
        // keeps a high floor so the matte character survives at 0%.
        double surfaceStrength = Lerp(0.55, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = Lerp(isDark ? 0.58 : 0.66, isDark ? 0.84 : 0.90, intensity);
        double luminosityOpacity = Lerp(isDark ? 0.62 : 0.72, isDark ? 0.80 : 0.86, intensity);
        return new WidgetMaterialOpacityProfile(
            Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0),
            Math.Clamp(luminosityOpacity * surfaceStrength, 0.0, 1.0));
    }

    /// <summary>
    /// Liquid glass ("液态玻璃"): Desktop Acrylic Thin with a very low tint and
    /// a bright luminosity layer, so the desktop behind stays legible through
    /// a glossy sheet while highlights stay crisp.
    /// </summary>
    public static WidgetMaterialOpacityProfile CalculateLiquidGlass(
        bool isDark,
        double surfaceOpacity,
        double materialIntensity)
    {
        double intensity = NormalizeMaterialIntensity(materialIntensity);
        double surfaceStrength = Lerp(0.25, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = Lerp(isDark ? 0.06 : 0.04, isDark ? 0.20 : 0.16, intensity);
        double luminosityOpacity = Lerp(isDark ? 0.34 : 0.42, isDark ? 0.62 : 0.72, intensity);
        return new WidgetMaterialOpacityProfile(
            Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0),
            Math.Clamp(luminosityOpacity * surfaceStrength, 0.0, 1.0));
    }

    public static double CalculateLegacyAcrylicOpacity(
        bool useBase,
        double surfaceOpacity,
        double materialIntensity)
    {
        double surface = Math.Clamp(surfaceOpacity, 0.0, 1.0);
        double intensity = NormalizeMaterialIntensity(materialIntensity);
        double maximumOpacity = useBase ? 0.90 : 0.72;
        double opacity = Lerp(0.01, maximumOpacity, surface);

        // Win10's accent policy exposes one tint-alpha control rather than the
        // independent tint/luminosity controls available to Desktop Acrylic.
        // Keep both settings effective by letting intensity tune the final
        // material concentration without flattening the opacity slider range.
        return Math.Clamp(opacity * Lerp(0.58, 1.0, intensity), 0.0, 1.0);
    }

    public static Windows.UI.Color BuildLegacyAcrylicSurfaceOverlayColor(
        bool isDark,
        Windows.UI.Color accentColor,
        bool useBase,
        double surfaceOpacity,
        double materialIntensity,
        string? materialType = null)
    {
        Windows.UI.Color tintColor = BuildContentTintColor(isDark, accentColor);
        double legacyOpacity = CalculateLegacyAcrylicOpacity(
            useBase,
            surfaceOpacity,
            materialIntensity);

        // Accent policy and Desktop Acrylic can report success on Win10 while
        // DWM (especially under a VM/RDP display driver) presents no blur or
        // tint. This lightweight XAML tint keeps both sliders visibly effective
        // without replacing the real window-level acrylic attempt.
        double overlayOpacity = legacyOpacity * (useBase ? 0.72 : 0.62);
        if (materialType == Contracts.WidgetMaterialKinds.LiquidGlass)
        {
            // Liquid glass stays a thin sheet: keep the Win10 fallback tint
            // light so it does not collapse into a flat acrylic slab.
            overlayOpacity *= 0.45;
        }

        return ApplySurfaceOpacity(tintColor, overlayOpacity);
    }

    public static WidgetMaterialOpacityProfile CalculateMica(
        bool isDark,
        bool useAlt,
        double materialIntensity)
    {
        double intensity = NormalizeMaterialIntensity(materialIntensity);
        double tintOpacity = useAlt
            ? Lerp(0.28, 0.82, intensity)
            : Lerp(0.04, 0.46, intensity);
        double luminosityOpacity = useAlt
            ? Lerp(isDark ? 0.34 : 0.42, isDark ? 0.72 : 0.76, intensity)
            : Lerp(isDark ? 0.78 : 0.82, isDark ? 0.94 : 0.96, intensity);

        return new WidgetMaterialOpacityProfile(tintOpacity, luminosityOpacity);
    }

    public static Windows.UI.Color BuildContentTintColor(
        bool isDark,
        Windows.UI.Color accentColor)
    {
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BuildAccentSurfaceColor(
            isDark,
            accentColor,
            baseColor,
            accentMix: isDark ? 0.08 : 0.16,
            overlayMix: isDark ? 0.04 : 0.08);
    }

    public static Windows.UI.Color BuildMicaFallbackColor(bool isDark, bool useAlt)
    {
        return useAlt
            ? isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1D)
                : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xEA, 0xEF)
            : isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
                : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    public static Windows.UI.Color BuildEmbeddedMicaTintOverlayColor(
        bool isDark,
        Windows.UI.Color accentColor,
        bool useAlt,
        double materialIntensity)
    {
        WidgetMaterialOpacityProfile profile = CalculateMica(
            isDark,
            useAlt,
            materialIntensity);
        Windows.UI.Color tintColor = BuildContentTintColor(isDark, accentColor);
        return ApplySurfaceOpacity(tintColor, profile.TintOpacity);
    }

    public static Windows.UI.Color BuildContentSolidSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity)
    {
        return ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? Windows.UI.Color.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: 0.18,
                overlayMix: isDark ? 0.15 : 0.04),
            Math.Clamp(surfaceOpacity, 0.0, 1.0));
    }

    public static WidgetMaterialGradientProfile BuildImagePaletteGradient(
        bool isDark,
        GlanceImagePalette palette)
    {
        var startBase = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var endBase = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1C)
            : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        var startColor = BlendColors(
            startBase,
            palette.Primary,
            isDark ? 0.44 : 0.34);
        var endColor = BlendColors(
            endBase,
            palette.Secondary,
            isDark ? 0.34 : 0.24);
        return new WidgetMaterialGradientProfile(startColor, endColor);
    }

    private static double NormalizeMaterialIntensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                value,
                SettingsService.MinWidgetMaterialIntensity,
                SettingsService.MaxWidgetMaterialIntensity)
            : SettingsService.DefaultWidgetMaterialIntensity;

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var mixed = BlendColors(baseColor, accentColor, accentMix);
        var overlay = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x2F, 0x36)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(mixed, overlay, overlayMix);
    }

    private static Windows.UI.Color ApplySurfaceOpacity(
        Windows.UI.Color color,
        double opacity) =>
        Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            color.R,
            color.G,
            color.B);

    private static Windows.UI.Color BlendColors(
        Windows.UI.Color from,
        Windows.UI.Color to,
        double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }
}
