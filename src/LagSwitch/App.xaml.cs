using System.Windows;
using System.Windows.Threading;
using LagSwitch.Core;

namespace LagSwitch;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private int _shutdownDone;

    public static new App Current => (App)Application.Current;

    public Settings Settings { get; private set; } = new();
    public FirewallEngine Firewall { get; private set; } = null!;
    public CutEngine Cut { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Deux instances se marcheraient dessus : elles partagent le meme jeu de regles.
        _singleInstance = new Mutex(true, @"Global\LagSwitch.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "LagSwitch est deja lance.",
                "LagSwitch",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Settings = SettingsStore.Load();
        Firewall = new FirewallEngine();
        Cut = new CutEngine(Firewall);

        // Une instance precedente a pu mourir en laissant ses regles actives : on balaie.
        _ = Firewall.CleanupAsync(restoreFirewallState: false);

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Ne jamais laisser la connexion coupee a cause d'un plantage de l'interface.
        try { Cut?.PanicRestore(); } catch { }

        MessageBox.Show(
            $"LagSwitch a rencontre une erreur et a retabli la connexion.\n\n{e.Exception.Message}",
            "LagSwitch",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Teardown();
        base.OnExit(e);
    }

    private void Teardown()
    {
        if (Interlocked.Exchange(ref _shutdownDone, 1) == 1) return;

        try { Cut?.PanicRestore(); } catch { }
        try { Cut?.Dispose(); } catch { }
        try { Firewall?.CleanupBlocking(Settings.RestoreFirewallOnExit, TimeSpan.FromSeconds(5)); } catch { }
        try { Firewall?.Dispose(); } catch { }
        try { SettingsStore.Save(Settings); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
    }
}
