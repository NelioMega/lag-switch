using System.Net.NetworkInformation;

namespace LagSwitch.Core;

public sealed record LinkSample(bool Reachable, long RoundTripMs);

/// <summary>
/// Mesure l'etat REEL du lien au lieu de le supposer. C'est le garde-fou contre le pire defaut
/// possible pour un instrument de test : afficher « coupe » alors que le trafic passe encore.
///
/// La sonde vit dans le processus de LagSwitch. En cible globale, elle est donc coupee comme le
/// reste et dit la verite. En cible par application, elle reste verte par construction : ce n'est
/// pas son trafic qui est vise, et l'interface doit le dire plutot que de laisser croire a une preuve.
/// </summary>
public sealed class LinkProbe : IDisposable
{
    private readonly Ping _ping = new();
    private CancellationTokenSource? _cancel;
    private Task? _loop;

    public string Host { get; set; } = "1.1.1.1";

    /// <summary>Delai entre deux mesures.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Tant que c'est faux, la sonde n'a jamais joint l'hote : sur un reseau qui filtre l'ICMP,
    /// son silence ne prouve rien. L'interface doit alors s'abstenir de conclure.
    /// </summary>
    public bool HasEverSucceeded { get; private set; }

    public LinkSample? Last { get; private set; }

    public event Action<LinkSample>? Measured;

    public void Start()
    {
        if (_loop is not null) return;
        _cancel = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancel.Token));
    }

    public void Stop()
    {
        try { _cancel?.Cancel(); } catch { }
        _loop = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            LinkSample sample;
            try
            {
                var reply = await _ping.SendPingAsync(Host, 900).ConfigureAwait(false);
                var ok = reply.Status == IPStatus.Success;
                if (ok) HasEverSucceeded = true;
                sample = new LinkSample(ok, ok ? reply.RoundtripTime : -1);
            }
            catch
            {
                // Hote introuvable, pile reseau absente, ping deja en cours : tout compte comme injoignable.
                sample = new LinkSample(false, -1);
            }

            Last = sample;
            Measured?.Invoke(sample);

            try { await Task.Delay(Interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _ping.Dispose(); } catch { }
    }
}
