namespace LagSwitch.Core;

/// <summary>Quel mecanisme pose le blocage.</summary>
public enum BlockBackend
{
    /// <summary>Filtres poses directement dans la plateforme de filtrage Windows.</summary>
    Wfp,

    /// <summary>Regles du pare-feu Windows, qui doit donc etre actif.</summary>
    Firewall,
}

/// <summary>
/// Ce que <see cref="CutEngine"/> a besoin de savoir faire, quel que soit le mecanisme dessous.
/// Les deux implementations sont interchangeables a chaud.
/// </summary>
public interface IBlockBackend : IDisposable
{
    string Name { get; }

    /// <summary>Vrai si le blocage est prepare et n'attend plus qu'une bascule.</summary>
    bool IsArmed { get; }

    /// <summary>Vrai si ce mecanisme ne peut rien bloquer sans le service Pare-feu Windows.</summary>
    bool NeedsWindowsFirewall { get; }

    /// <summary>Prepare le blocage pour la cible donnee.</summary>
    Task ArmAsync(TargetKind target, string? applicationPath);

    void SetBlocked(bool blocked, CutDirection direction);

    Task CleanupAsync();

    void CleanupBlocking(TimeSpan timeout);
}
