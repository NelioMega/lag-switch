using System.Windows;
using System.Windows.Threading;
using LagSwitch.Core;
using LagSwitch.Core.Theming;
using LagSwitch.Core.Wfp;

namespace LagSwitch;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private int _shutdownDone;
    private WfpBackend? _wfp;

    public static new App Current => (App)Application.Current;

    public Settings Settings { get; private set; } = new();

    /// <summary>
    /// Toujours instancie, meme quand le blocage passe par WFP : c'est lui qui lit l'etat du
    /// pare-feu et qui balaie d'eventuelles regles laissees par une session precedente.
    /// </summary>
    public FirewallEngine Firewall { get; private set; } = null!;

    public IBlockBackend Backend { get; private set; } = null!;

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

        // Avant toute fenetre : les styles resolvent la palette au chargement.
        ThemeService.Install(Resources, Settings.ThemeName);

        Firewall = new FirewallEngine { RestoreFirewallStateOnExit = Settings.RestoreFirewallOnExit };
        _wfp = new WfpBackend();
        Backend = Settings.Backend == BlockBackend.Wfp ? _wfp : Firewall;
        Cut = new CutEngine(Backend);

        // Une instance precedente a pu mourir en laissant ses regles de pare-feu actives.
        // Les filtres WFP, eux, sont deja partis avec leur session.
        _ = Firewall.CleanupAsync(restoreFirewallState: false);

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();
    }

    /// <summary>Change de mecanisme de blocage sans redemarrer, en nettoyant le precedent.</summary>
    public async Task UseBackendAsync(BlockBackend which)
    {
        Cut.PanicRestore();
        try { await Backend.CleanupAsync(); } catch { }

        Backend = which == BlockBackend.Wfp ? _wfp! : Firewall;
        Cut.Backend = Backend;
        Settings.Backend = which;
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

        if (Firewall is not null) Firewall.RestoreFirewallStateOnExit = Settings.RestoreFirewallOnExit;
        try { _wfp?.CleanupBlocking(TimeSpan.FromSeconds(2)); } catch { }
        try { Firewall?.CleanupBlocking(TimeSpan.FromSeconds(5)); } catch { }

        try { _wfp?.Dispose(); } catch { }
        try { Firewall?.Dispose(); } catch { }
        try { SettingsStore.Save(Settings); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
    }
}
