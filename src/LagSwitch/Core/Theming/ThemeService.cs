using System.Windows;
using System.Windows.Media;

namespace LagSwitch.Core.Theming;

/// <summary>
/// Injecte la palette dans les ressources de l'application, puis la fait varier a chaud.
///
/// Le point important : les pinceaux sont crees ICI, en C#, et ne sont jamais remplaces —
/// seule leur <c>Color</c> change. Un pinceau declare en XAML dans un dictionnaire de
/// ressources est gele par WPF et refuserait toute modification ; un pinceau qu'on
/// remplacerait laisserait derriere lui toutes les couleurs deja posees depuis le code.
/// En mutant la couleur d'une instance partagee, l'interface entiere suit dans la meme frame,
/// y compris ce que le code-behind a affecte a la main.
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static ResourceDictionary? _target;

    public static Theme Current { get; private set; } = ThemeCatalog.Terminal;

    /// <summary>Leve apres chaque changement, pour ce que le code doit repeindre lui-meme.</summary>
    public static event Action<Theme>? Changed;

    /// <summary>
    /// A appeler une seule fois, avant que la moindre fenetre ne soit creee : les styles
    /// resolvent la palette au chargement.
    /// </summary>
    public static void Install(ResourceDictionary target, string? themeName)
    {
        _target = target;
        Apply(ThemeCatalog.ByName(themeName));
    }

    public static void Apply(string? themeName) => Apply(ThemeCatalog.ByName(themeName));

    public static void Apply(Theme theme)
    {
        if (_target is null) return;

        Current = theme;

        var bg = theme.Background;

        Set("Background", bg);
        Set("Card", theme.Card);
        Set("CardBorder", theme.CardBorder);
        Set("CardBorderLit", theme.CardBorderLit);
        Set("Field", theme.Field);
        Set("Text", theme.Text);
        Set("Muted", theme.Muted);
        Set("Dim", theme.Dim);

        Set("Accent", theme.Accent);
        Set("Online", theme.Accent);
        Set("Cut", theme.Cut);
        Set("Warn", theme.Warn);

        // Fonds neutres : le texte melange au fond, tres legerement.
        Set("ButtonFace", Mix(bg, theme.Text, 0.06));
        Set("ButtonFaceHover", Mix(bg, theme.Text, 0.12));
        Set("ButtonFacePressed", Mix(bg, theme.Text, 0.18));
        Set("ListHover", Mix(bg, theme.Text, 0.05));

        // Fonds et bordures teintes : la couleur d'etat melangee au fond.
        Set("AccentFace", Mix(bg, theme.Accent, 0.12));
        Set("AccentEdge", Mix(bg, theme.Accent, 0.45));
        Set("ListSelected", Mix(bg, theme.Accent, 0.14));
        Set("CutFace", Mix(bg, theme.Cut, 0.12));
        Set("CutEdge", Mix(bg, theme.Cut, 0.45));
        Set("WarnFace", Mix(bg, theme.Warn, 0.12));
        Set("WarnEdge", Mix(bg, theme.Warn, 0.45));

        // La pastille doit rester lisible par-dessus n'importe quel jeu : presque opaque.
        Set("OverlayFace", WithAlpha(theme.Card, 0xE6));

        _target["GrainOpacity"] = theme.GrainOpacity;

        Changed?.Invoke(theme);
    }

    private static void Set(string key, Color color)
    {
        if (Brushes.TryGetValue(key, out var brush))
        {
            brush.Color = color;
            return;
        }

        brush = new SolidColorBrush(color);
        Brushes[key] = brush;
        _target![key] = brush;
    }

    /// <summary>Melange <paramref name="over"/> dans <paramref name="under"/> a hauteur de <paramref name="amount"/>.</summary>
    private static Color Mix(Color under, Color over, double amount) => Color.FromArgb(
        0xFF,
        (byte)(under.R + (over.R - under.R) * amount),
        (byte)(under.G + (over.G - under.G) * amount),
        (byte)(under.B + (over.B - under.B) * amount));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
