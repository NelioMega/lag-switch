using System.Collections.Concurrent;

namespace LagSwitch.Core;

/// <summary>Etat du pare-feu Windows, profil par profil.</summary>
public sealed record FirewallHealth(bool? Domain, bool? Private, bool? Public, int CurrentProfiles)
{
    /// <summary>Vrai si le pare-feu est actif sur au moins un des profils actuellement en vigueur.</summary>
    public bool? ActiveProfileProtected
    {
        get
        {
            bool? result = null;
            foreach (var (bit, state) in new[] { (1, Domain), (2, Private), (4, Public) })
            {
                if ((CurrentProfiles & bit) == 0) continue;
                if (state is null) return null;
                result = (result ?? false) || state.Value;
            }
            return result;
        }
    }

    public static FirewallHealth Unknown => new(null, null, null, 0);
}

/// <summary>
/// Pose deux regles de blocage dans le pare-feu Windows (une entrante, une sortante) et se
/// contente ensuite de les activer ou desactiver. Basculer un booleen sur une regle deja
/// existante prend quelques millisecondes, la ou creer puis supprimer les regles a chaque
/// coupure en prendrait des centaines.
///
/// Tous les appels COM sont serialises sur un thread STA dedie : l'API du pare-feu n'aime pas
/// etre appelee depuis plusieurs threads, et cela garde le thread d'interface libre.
/// </summary>
public sealed class FirewallEngine : IBlockBackend
{
    public string Name => "Pare-feu Windows";

    /// <summary>Les regles ne bloquent rien si le service Pare-feu est eteint.</summary>
    public bool NeedsWindowsFirewall => true;

    /// <summary>Lu au nettoyage : faut-il rendre au pare-feu l'etat ou on l'a trouve.</summary>
    public bool RestoreFirewallStateOnExit { get; set; } = true;

    public Task CleanupAsync() => CleanupAsync(RestoreFirewallStateOnExit);

    public void CleanupBlocking(TimeSpan timeout) => CleanupBlocking(RestoreFirewallStateOnExit, timeout);

    /// <summary>Prefixe de nom qui identifie nos regles, y compris celles laissees par un plantage.</summary>
    public const string RulePrefix = "LagSwitch";

    private const string OutboundRuleName = "LagSwitch - blocage sortant";
    private const string InboundRuleName = "LagSwitch - blocage entrant";
    private const string RuleDescription =
        "Regle temporaire posee par LagSwitch. Elle est supprimee a la fermeture de l'application.";

    // Constantes de l'API du pare-feu Windows (NET_FW_*).
    private const int ActionBlock = 0;
    private const int DirectionIn = 1;
    private const int DirectionOut = 2;
    private const int ProtocolAny = 256;
    private const int ProfileAll = 0x7FFFFFFF;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _pump;

    private dynamic? _policy;
    private dynamic? _outboundRule;
    private dynamic? _inboundRule;

    /// <summary>Etat du pare-feu avant que l'application n'y touche, pour pouvoir le rendre tel quel.</summary>
    private FirewallHealth? _stateBeforeUs;

    public FirewallEngine()
    {
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "LagSwitch.Firewall",
        };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
    }

    /// <summary>Vrai si les regles sont posees et pretes a etre basculees.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>Vrai si l'application a elle-meme allume le pare-feu.</summary>
    public bool TurnedFirewallOn => _stateBeforeUs is not null;

    // ---------------------------------------------------------------- pompe

    private void Pump()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); }
            catch { /* chaque job gere deja son resultat */ }
        }
    }

    private Task<T> OnPump<T>(Func<T> job)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try { completion.SetResult(job()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetCanceled(); // pompe deja fermee
        }
        return completion.Task;
    }

    private Task OnPump(Action job) => OnPump<bool>(() => { job(); return true; });

    private dynamic Policy
    {
        get
        {
            _policy ??= Activator.CreateInstance(
                Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new PlatformNotSupportedException(
                    "Le service Pare-feu Windows (HNetCfg.FwPolicy2) est introuvable sur ce systeme."))!;
            return _policy;
        }
    }

    // ------------------------------------------------------------- lecture

    public Task<FirewallHealth> ReadHealthAsync() => OnPump(ReadHealthCore);

    private FirewallHealth ReadHealthCore()
    {
        try
        {
            var policy = Policy;
            int current;
            try { current = (int)policy.CurrentProfileTypes; }
            catch { current = 0; }

            return new FirewallHealth(
                ReadProfile(policy, 1),
                ReadProfile(policy, 2),
                ReadProfile(policy, 4),
                current);
        }
        catch
        {
            return FirewallHealth.Unknown;
        }

        static bool? ReadProfile(dynamic policy, int profile)
        {
            try { return (bool)policy.FirewallEnabled[profile]; }
            catch { return null; }
        }
    }

    // ------------------------------------------------------- allumer le pare-feu

    /// <summary>
    /// Active le pare-feu sur les profils ou il est eteint. Appele uniquement sur action
    /// explicite de l'utilisateur : c'est un reglage de securite du systeme.
    /// </summary>
    public Task<bool> EnableFirewallAsync() => OnPump(() =>
    {
        var before = ReadHealthCore();
        var somethingWasOff = before.Domain is false || before.Private is false || before.Public is false;
        if (somethingWasOff) _stateBeforeUs ??= before;

        var allDone = true;
        foreach (var (profile, state) in new[] { (1, before.Domain), (2, before.Private), (4, before.Public) })
        {
            if (state is true) continue;
            try { Policy.FirewallEnabled[profile] = true; }
            catch { allDone = false; }
        }
        return allDone;
    });

    /// <summary>Remet le pare-feu dans l'etat ou l'application l'a trouve.</summary>
    private void RestoreFirewallStateCore()
    {
        if (_stateBeforeUs is null) return;

        foreach (var (profile, state) in new[]
                 {
                     (1, _stateBeforeUs.Domain),
                     (2, _stateBeforeUs.Private),
                     (4, _stateBeforeUs.Public),
                 })
        {
            if (state is not false) continue; // il etait deja allume : on n'y touche pas
            try { Policy.FirewallEnabled[profile] = false; }
            catch { /* best-effort */ }
        }

        _stateBeforeUs = null;
    }

    // --------------------------------------------------------------- regles

    /// <summary>
    /// Supprime d'eventuelles regles orphelines puis pose la paire de regles, desactivees.
    /// A rappeler chaque fois que la cible change.
    /// </summary>
    public Task ArmAsync(TargetKind target, string? applicationPath) => OnPump(() =>
    {
        IsArmed = false;
        _outboundRule = null;
        _inboundRule = null;

        RemoveOwnRulesCore();

        var path = target == TargetKind.Application ? applicationPath : null;
        if (target == TargetKind.Application && string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Aucune application ciblee.");

        _outboundRule = CreateRule(OutboundRuleName, DirectionOut, path);
        _inboundRule = CreateRule(InboundRuleName, DirectionIn, path);
        IsArmed = true;
    });

    private dynamic CreateRule(string name, int direction, string? applicationPath)
    {
        dynamic rule = Activator.CreateInstance(
            Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new PlatformNotSupportedException("HNetCfg.FWRule introuvable."))!;

        rule.Name = name;
        rule.Description = RuleDescription;
        rule.Grouping = RulePrefix;
        rule.Action = ActionBlock;
        rule.Direction = direction;
        rule.Protocol = ProtocolAny;
        rule.InterfaceTypes = "All";
        rule.Profiles = ProfileAll;
        rule.Enabled = false;
        if (!string.IsNullOrWhiteSpace(applicationPath))
            rule.ApplicationName = applicationPath;

        Policy.Rules.Add(rule);

        // On relit la regle depuis la collection : l'objet rendu est vivant, poser
        // Enabled dessus ecrit directement dans la base de regles.
        return Policy.Rules.Item(name);
    }

    /// <summary>
    /// Coupe (true) ou retablit (false). C'est le chemin chaud : deux ecritures COM.
    /// Les deux regles etant deja separees, choisir un sens ne coute rien de plus.
    /// </summary>
    public Task SetBlockedAsync(bool blocked, CutDirection direction) => OnPump(() =>
    {
        if (_outboundRule is null || _inboundRule is null) return;
        _outboundRule.Enabled = blocked && direction is CutDirection.Both or CutDirection.Outbound;
        _inboundRule.Enabled = blocked && direction is CutDirection.Both or CutDirection.Inbound;
    });

    /// <summary>Version synchrone, pour le fil qui joue les motifs de coupure.</summary>
    public void SetBlocked(bool blocked, CutDirection direction)
    {
        try { SetBlockedAsync(blocked, direction).GetAwaiter().GetResult(); }
        catch { /* la restauration finale et le nettoyage de sortie rattrapent */ }
    }

    private void RemoveOwnRulesCore()
    {
        var doomed = new List<string>();
        try
        {
            foreach (dynamic rule in Policy.Rules)
            {
                string? name = null;
                try { name = rule.Name as string; }
                catch { /* regle illisible */ }

                if (name is not null && name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                    doomed.Add(name);
            }
        }
        catch
        {
            // Enumeration impossible : on tente quand meme les deux noms connus.
            doomed.Add(OutboundRuleName);
            doomed.Add(InboundRuleName);
        }

        foreach (var name in doomed)
        {
            try { Policy.Rules.Remove(name); }
            catch { /* deja partie */ }
        }
    }

    /// <summary>Retire les regles et rend au pare-feu son etat d'origine.</summary>
    public Task CleanupAsync(bool restoreFirewallState) => OnPump(() =>
    {
        IsArmed = false;
        _outboundRule = null;
        _inboundRule = null;
        RemoveOwnRulesCore();
        if (restoreFirewallState) RestoreFirewallStateCore();
    });

    /// <summary>Nettoyage bloquant, pour la fermeture de l'application.</summary>
    public void CleanupBlocking(bool restoreFirewallState, TimeSpan timeout)
    {
        try { CleanupAsync(restoreFirewallState).Wait(timeout); }
        catch { /* on quitte de toute facon */ }
    }

    public void Dispose()
    {
        try { _work.CompleteAdding(); } catch { }
        try { _pump.Join(TimeSpan.FromSeconds(2)); } catch { }
        _work.Dispose();
    }
}
