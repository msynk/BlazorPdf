namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// Supplies real glyph outlines for non-embedded simple fonts (the standard-14 families
/// such as Helvetica) so text renders as filled letterforms instead of the monoline
/// placeholder. A host may register font bytes explicitly (works everywhere, including
/// Blazor WebAssembly); otherwise a common sans-serif system font is auto-discovered at
/// runtime when the platform allows file access (server / desktop). Nothing is bundled
/// in the package, so there is no font redistribution.
/// </summary>
internal static class FallbackFont
{
    private static readonly object Gate = new();
    private static bool _resolvedRegular;
    private static bool _resolvedBold;
    private static TrueTypeFont? _regular;
    private static TrueTypeFont? _bold;
    private static byte[]? _customRegular;
    private static byte[]? _customBold;

    /// <summary>Registers explicit fallback font bytes, overriding auto-discovery.</summary>
    public static void Register(byte[] regular, byte[]? bold)
    {
        lock (Gate)
        {
            _customRegular = regular;
            _customBold = bold;
            _regular = _bold = null;
            _resolvedRegular = _resolvedBold = false;
        }
    }

    /// <summary>The best available outline source for the requested weight, or null.</summary>
    public static TrueTypeFont? Get(bool bold)
    {
        if (bold)
        {
            var b = GetBold();
            if (b is not null) return b;
        }
        return GetRegular();
    }

    private static TrueTypeFont? GetRegular()
    {
        lock (Gate)
        {
            if (_resolvedRegular) return _regular;
            _resolvedRegular = true;
            _regular = _customRegular is not null
                ? TrueTypeFont.Parse(_customRegular)
                : LoadFirst(RegularCandidates());
            return _regular;
        }
    }

    private static TrueTypeFont? GetBold()
    {
        lock (Gate)
        {
            if (_resolvedBold) return _bold;
            _resolvedBold = true;
            _bold = _customBold is not null
                ? TrueTypeFont.Parse(_customBold)
                : LoadFirst(BoldCandidates());
            return _bold;
        }
    }

    private static TrueTypeFont? LoadFirst(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var font = TrueTypeFont.Parse(File.ReadAllBytes(path));
                if (font is not null) return font;
            }
            catch
            {
                // Unreadable/unsupported file; try the next candidate.
            }
        }
        return null;
    }

    private static IEnumerable<string> RegularCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var fonts = Path.Combine(
                Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
            yield return Path.Combine(fonts, "arial.ttf");
            yield return Path.Combine(fonts, "segoeui.ttf");
            yield return Path.Combine(fonts, "tahoma.ttf");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
            yield return "/Library/Fonts/Arial.ttf";
        }
        else
        {
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
            yield return "/usr/share/fonts/liberation/LiberationSans-Regular.ttf";
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            yield return "/usr/share/fonts/TTF/DejaVuSans.ttf";
        }
    }

    private static IEnumerable<string> BoldCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var fonts = Path.Combine(
                Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
            yield return Path.Combine(fonts, "arialbd.ttf");
            yield return Path.Combine(fonts, "segoeuib.ttf");
            yield return Path.Combine(fonts, "tahomabd.ttf");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/Supplemental/Arial Bold.ttf";
            yield return "/Library/Fonts/Arial Bold.ttf";
        }
        else
        {
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf";
            yield return "/usr/share/fonts/liberation/LiberationSans-Bold.ttf";
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
            yield return "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf";
        }
    }
}
