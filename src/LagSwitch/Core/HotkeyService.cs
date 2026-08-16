using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LagSwitch.Core;

/// <summary>
/// Raccourci global : il fonctionne meme quand le jeu est au premier plan, et il consomme la
/// touche — l'application au premier plan ne la verra pas.
///
/// <c>RegisterHotKey</c> ne signale que l'appui. Pour le mode maintien, un petit fil interroge
/// l'etat physique de la touche jusqu'au relachement.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB0DE;

    private HwndSource? _source;
    private uint _virtualKey;
    private bool _registered;
    private Thread? _releaseWatcher;
    private volatile bool _watching;

    /// <summary>Quand il est vrai, <see cref="Released"/> est leve au relachement de la touche.</summary>
    public bool WatchRelease { get; set; }

    public event Action? Pressed;
    public event Action? Released;

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    /// <summary>Enregistre le raccourci. Rend false si une autre application le detient deja.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        if (_source is null) return false;

        _virtualKey = virtualKey;
        _registered = Native.RegisterHotKey(
            _source.Handle, HotkeyId, modifiers | (uint)Native.Mod.NoRepeat, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            Native.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        _watching = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        handled = true;
        Pressed?.Invoke();
        if (WatchRelease) StartReleaseWatcher();
        return IntPtr.Zero;
    }

    private void StartReleaseWatcher()
    {
        if (_watching) return;
        _watching = true;

        var key = _virtualKey;
        _releaseWatcher = new Thread(() =>
        {
            // La touche peut ne pas etre encore vue comme enfoncee au moment ou WM_HOTKEY arrive.
            Thread.Sleep(20);
            while (_watching && Native.IsKeyDown(key)) Thread.Sleep(10);

            if (_watching)
            {
                _watching = false;
                Released?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "LagSwitch.KeyRelease",
        };
        _releaseWatcher.Start();
    }

    public void Dispose()
    {
        _watching = false;
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    // ------------------------------------------------------------- affichage

    /// <summary>Rend un libelle lisible du type « Ctrl + Alt + L ».</summary>
    public static string Describe(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & (uint)Native.Mod.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & (uint)Native.Mod.Alt) != 0) parts.Add("Alt");
        if ((modifiers & (uint)Native.Mod.Shift) != 0) parts.Add("Maj");
        if ((modifiers & (uint)Native.Mod.Win) != 0) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        parts.Add(key == Key.None ? $"0x{virtualKey:X2}" : key.ToString());
        return string.Join(" + ", parts);
    }

    /// <summary>Vrai si la touche ne sert qu'a composer un raccourci.</summary>
    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    public static uint CurrentModifiers()
    {
        uint modifiers = 0;
        var keyboard = Keyboard.Modifiers;
        if (keyboard.HasFlag(ModifierKeys.Control)) modifiers |= (uint)Native.Mod.Control;
        if (keyboard.HasFlag(ModifierKeys.Alt)) modifiers |= (uint)Native.Mod.Alt;
        if (keyboard.HasFlag(ModifierKeys.Shift)) modifiers |= (uint)Native.Mod.Shift;
        if (keyboard.HasFlag(ModifierKeys.Windows)) modifiers |= (uint)Native.Mod.Win;
        return modifiers;
    }
}
