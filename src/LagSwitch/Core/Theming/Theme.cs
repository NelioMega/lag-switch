using System.Windows.Media;

namespace LagSwitch.Core.Theming;

/// <summary>
/// Une palette complete. Seules les douze couleurs de base sont ecrites : les fonds de boutons,
/// les bordures teintees et les etats de survol sont <b>calcules</b> par melange avec le fond,
/// pour qu'un theme clair reste coherent sans qu'on ait a redecliner trente valeurs a la main.
/// </summary>
public sealed record Theme(
    string Name,
    string Tagline,
    Color Background,
    Color Card,
    Color CardBorder,
    Color CardBorderLit,
    Color Field,
    Color Text,
    Color Muted,
    Color Dim,
    Color Accent,
    Color Cut,
    Color Warn,
    double GrainOpacity)
{
    /// <summary>Vrai pour les palettes claires, ou le grain d'ecran doit se faire oublier.</summary>
    public bool IsLight => (0.299 * Background.R + 0.587 * Background.G + 0.114 * Background.B) > 128;
}

public static class ThemeCatalog
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    public static readonly Theme Terminal = new(
        "Terminal", "noir, ardoise et rouge d'alerte — la palette du logo",
        Background: C("#FF04060A"), Card: C("#FF080C12"),
        CardBorder: C("#FF1B2532"), CardBorderLit: C("#FF2C3B4E"), Field: C("#FF020407"),
        Text: C("#FFC9D6E4"), Muted: C("#FF7E8CA0"), Dim: C("#FF4E5C71"),
        Accent: C("#FF2FE08A"), Cut: C("#FFFF2A2A"), Warn: C("#FFE8A33D"),
        GrainOpacity: 1.0);

    public static readonly Theme Ambre = new(
        "Ambre", "un moniteur monochrome ambre des annees 80",
        Background: C("#FF0B0703"), Card: C("#FF140D05"),
        CardBorder: C("#FF3A2A10"), CardBorderLit: C("#FF5A421A"), Field: C("#FF070402"),
        Text: C("#FFFFC15E"), Muted: C("#FFB8823A"), Dim: C("#FF6E4E22"),
        Accent: C("#FFFFB000"), Cut: C("#FFFF4B1F"), Warn: C("#FFFFD980"),
        GrainOpacity: 1.0);

    public static readonly Theme Phosphore = new(
        "Phosphore", "le vert des tubes cathodiques, rien d'autre",
        Background: C("#FF020A04"), Card: C("#FF04120A"),
        CardBorder: C("#FF10422A"), CardBorderLit: C("#FF1A6B44"), Field: C("#FF010805"),
        Text: C("#FF7DFFB0"), Muted: C("#FF46B87A"), Dim: C("#FF2A7048"),
        Accent: C("#FF39FF88"), Cut: C("#FFFF3B3B"), Warn: C("#FFE8E23D"),
        GrainOpacity: 1.0);

    public static readonly Theme Neon = new(
        "Neon", "violet profond, cyan et magenta",
        Background: C("#FF0B0418"), Card: C("#FF140A24"),
        CardBorder: C("#FF33194F"), CardBorderLit: C("#FF552C80"), Field: C("#FF070210"),
        Text: C("#FFE6D9FF"), Muted: C("#FFA88CD0"), Dim: C("#FF6B5490"),
        Accent: C("#FF2BE8E8"), Cut: C("#FFFF2D95"), Warn: C("#FFFFD23F"),
        GrainOpacity: 0.8);

    public static readonly Theme Nord = new(
        "Nord", "bleu-gris froid, desature, reposant",
        Background: C("#FF10151C"), Card: C("#FF171E27"),
        CardBorder: C("#FF2A3543"), CardBorderLit: C("#FF3C4A5C"), Field: C("#FF0C1117"),
        Text: C("#FFD8E0EA"), Muted: C("#FF8B9AAC"), Dim: C("#FF5A6A7C"),
        Accent: C("#FFA3BE8C"), Cut: C("#FFBF616A"), Warn: C("#FFEBCB8B"),
        GrainOpacity: 0.4);

    public static readonly Theme Papier = new(
        "Papier", "clair, pour travailler en plein jour",
        Background: C("#FFF2F0EA"), Card: C("#FFFBFAF6"),
        CardBorder: C("#FFD8D3C6"), CardBorderLit: C("#FFB9B2A1"), Field: C("#FFFFFFFF"),
        Text: C("#FF23201A"), Muted: C("#FF6B655A"), Dim: C("#FF97907F"),
        Accent: C("#FF1E7A4B"), Cut: C("#FFC0281F"), Warn: C("#FF9A6200"),
        GrainOpacity: 0.25);

    public static readonly IReadOnlyList<Theme> All = [Terminal, Ambre, Phosphore, Neon, Nord, Papier];

    public static Theme ByName(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Terminal;
}
