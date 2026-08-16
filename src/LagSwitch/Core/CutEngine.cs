using System.Diagnostics;

namespace LagSwitch.Core;

/// <summary>
/// Joue les motifs de coupure sur un fil dedie. Les quatre modes se ramenent au meme
/// automate : couper pendant <c>cut</c>, rendre la ligne pendant <c>gap</c>, recommencer —
/// avec <c>cut = 0</c> pour « rester coupe » et <c>gap = 0</c> pour « ne pas recommencer ».
///
/// Quoi qu'il arrive — annulation, plafond de duree atteint, exception — le <c>finally</c>
/// remet la connexion. Une coupure ne peut pas survivre a la fin du motif.
/// </summary>
public sealed class CutEngine : IDisposable
{
    private readonly FirewallEngine _firewall;
    private readonly object _gate = new();

    private Thread? _worker;
    private CancellationTokenSource? _cancel;
    private bool _blocked;

    public CutEngine(FirewallEngine firewall) => _firewall = firewall;

    /// <summary>Vrai quand la connexion est coupee a l'instant present.</summary>
    public bool IsBlocked
    {
        get { lock (_gate) return _blocked; }
    }

    /// <summary>Vrai tant qu'un motif est en cours (y compris pendant ses phases en ligne).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _worker is { IsAlive: true }; }
    }

    /// <summary>Leve a chaque bascule coupe / en ligne. N'est pas sur le thread d'interface.</summary>
    public event Action<bool>? BlockedChanged;

    /// <summary>Leve au demarrage et a l'arret d'un motif.</summary>
    public event Action<bool>? RunningChanged;

    public event Action<string>? Logged;

    /// <summary>
    /// Declenche le raccourci. En bascule et en instable, une seconde pression arrete ;
    /// en impulsion et en maintien, une pression pendant qu'un motif tourne est ignoree.
    /// </summary>
    public void Trigger(Settings settings)
    {
        lock (_gate)
        {
            if (_worker is { IsAlive: true })
            {
                if (settings.Mode is CutMode.Toggle or CutMode.Flicker)
                {
                    StopCore("arret manuel");
                }
                return;
            }

            var (cut, gap) = settings.Mode switch
            {
                CutMode.Toggle => (0, 0),
                CutMode.Hold => (0, 0),
                CutMode.Burst => (settings.BurstMilliseconds, 0),
                CutMode.Flicker => (settings.FlickerCutMilliseconds, settings.FlickerGapMilliseconds),
                _ => (0, 0),
            };

            var capMs = settings.MaxCutSeconds * 1000;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;

            var worker = new Thread(() => Run(cut, gap, capMs, cancel.Token))
            {
                IsBackground = true,
                Name = "LagSwitch.Pattern",
                Priority = ThreadPriority.AboveNormal,
            };
            _worker = worker;
            worker.Start();
            RunningChanged?.Invoke(true);

            Logged?.Invoke(settings.Mode switch
            {
                CutMode.Toggle => "Coupure (bascule)",
                CutMode.Hold => "Coupure (maintien)",
                CutMode.Burst => $"Impulsion de {settings.BurstMilliseconds} ms",
                CutMode.Flicker => $"Instable : {settings.FlickerCutMilliseconds} ms coupe / {settings.FlickerGapMilliseconds} ms en ligne",
                _ => "Coupure",
            });
        }
    }

    /// <summary>Relachement de la touche en mode maintien.</summary>
    public void Release() => Stop("touche relachee");

    /// <summary>Arret immediat, quel que soit le mode.</summary>
    public void Stop(string reason)
    {
        lock (_gate) StopCore(reason);
    }

    private void StopCore(string reason)
    {
        if (_cancel is null) return;
        try { _cancel.Cancel(); } catch { }
        Logged?.Invoke($"Retabli ({reason})");
    }

    /// <summary>Filet de securite : retablit la ligne sans rien demander a personne.</summary>
    public void PanicRestore()
    {
        Thread? worker;
        lock (_gate)
        {
            StopCore("panique");
            worker = _worker;
        }

        worker?.Join(TimeSpan.FromSeconds(2));
        _firewall.SetBlocked(false);
        SetBlockedFlag(false);
    }

    private void Run(int cutMs, int gapMs, int capMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var hitCap = false;

        try
        {
            while (!token.IsCancellationRequested && clock.ElapsedMilliseconds < capMs)
            {
                Block(true);

                var slice = cutMs > 0 ? cutMs : capMs;
                if (WaitOrDone(slice, clock, capMs, token, ref hitCap)) break;

                // gap <= 0 : le motif ne comporte qu'une seule coupure.
                if (gapMs <= 0) break;

                Block(false);
                if (WaitOrDone(gapMs, clock, capMs, token, ref hitCap)) break;
            }
        }
        finally
        {
            Block(false);

            if (hitCap)
                Logged?.Invoke($"Retabli (plafond de {capMs / 1000} s atteint)");

            lock (_gate)
            {
                _cancel?.Dispose();
                _cancel = null;
                _worker = null;
            }
            RunningChanged?.Invoke(false);
        }
    }

    /// <summary>Attend <paramref name="ms"/>, sans jamais depasser le plafond. Rend true s'il faut sortir.</summary>
    private static bool WaitOrDone(int ms, Stopwatch clock, int capMs, CancellationToken token, ref bool hitCap)
    {
        var remaining = capMs - (int)clock.ElapsedMilliseconds;
        if (remaining <= 0)
        {
            hitCap = true;
            return true;
        }

        var slice = Math.Min(ms, remaining);
        if (token.WaitHandle.WaitOne(slice)) return true; // annule

        if (clock.ElapsedMilliseconds >= capMs)
        {
            hitCap = true;
            return true;
        }
        return false;
    }

    private void Block(bool blocked)
    {
        if (IsBlocked == blocked) return;
        _firewall.SetBlocked(blocked);
        SetBlockedFlag(blocked);
    }

    private void SetBlockedFlag(bool blocked)
    {
        lock (_gate)
        {
            if (_blocked == blocked) return;
            _blocked = blocked;
        }
        BlockedChanged?.Invoke(blocked);
    }

    public void Dispose() => PanicRestore();
}
