using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LagSwitch.Core;

namespace LagSwitch;

/// <summary>
/// Pastille toujours au-dessus, en haut de l'ecran. Elle n'accepte ni le focus ni les clics :
/// pendant une partie, une fenetre qui vole la souris ou le premier plan est pire que pas de
/// fenetre du tout.
///
/// Un jeu en plein ecran EXCLUSIF la masquera quand meme — c'est une limite de Windows, pas un
/// reglage. En fenetre sans bordure, qui est le mode par defaut de la plupart des jeux, elle s'affiche.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Recentre();
        Loaded += (_, _) => Recentre();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
            style | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    private void Recentre()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 14;
    }

    /// <summary>
    /// Met la pastille au diapason. La couleur est passee par cle de ressource plutot que
    /// deduite d'un booleen : il y a trois etats, pas deux — en ligne, coupe, et inactif.
    /// </summary>
    public void SetState(string brushKey, string text)
    {
        var brush = (Brush)FindResource(brushKey);
        Shell.BorderBrush = brush;
        Dot.Fill = brush;
        Label.Foreground = brush;
        Label.Text = text;

        // Toujours remettre au premier plan : une autre fenetre topmost peut etre passee devant.
        Topmost = false;
        Topmost = true;
    }
}
