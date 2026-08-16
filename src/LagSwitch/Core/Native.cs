using System.Runtime.InteropServices;

namespace LagSwitch.Core;

/// <summary>Les quelques appels Win32 dont l'application a besoin.</summary>
internal static class Native
{
    public const int WM_HOTKEY = 0x0312;

    [Flags]
    public enum Mod : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // Styles etendus, pour une pastille qui ne prend jamais le focus et laisse
    // passer les clics : sinon elle volerait la souris au jeu.
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static bool IsKeyDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;
}
