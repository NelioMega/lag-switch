using System.Runtime.InteropServices;

namespace LagSwitch.Core.Wfp;

public sealed class WfpException(string what, uint code)
    : Exception($"{what} : {WfpNative.Explain(code)}")
{
    public uint Code { get; } = code;
}

/// <summary>
/// Coupe en posant des filtres de blocage directement dans WFP, sous le pare-feu Windows.
///
/// Trois differences avec le moteur a base de regles de pare-feu :
/// le service Pare-feu peut etre eteint, ca marche quand meme ; le trafic de bouclage est
/// atteint, donc les tests locaux dans Studio le sont aussi ; et la session etant
/// <b>dynamique</b>, tous les filtres disparaissent d'eux-memes a la mort du processus — meme
/// tue depuis le gestionnaire des taches, on ne peut pas rester coupe.
///
/// Le choix des couches depend de la cible : globale, on filtre au niveau transport, ou tout
/// paquet passe (y compris ICMP et le bouclage) ; par application, il faut les couches ALE,
/// seules a connaitre l'executable a l'origine du trafic.
/// </summary>
public sealed class WfpEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ulong> _filters = new();

    private IntPtr _engine;
    private Guid _subLayerKey;
    private IntPtr _appId;      // FWP_BYTE_BLOB* rendu par WFP, a liberer
    private string? _targetPath;

    public bool IsOpen => _engine != IntPtr.Zero;

    /// <summary>Vrai quand les filtres sont poses, donc que le trafic est effectivement bloque.</summary>
    public bool IsBlocking
    {
        get { lock (_gate) return _filters.Count > 0; }
    }

    // ------------------------------------------------------------------ session

    public void Open()
    {
        lock (_gate)
        {
            if (_engine != IntPtr.Zero) return;

            var name = Marshal.StringToHGlobalUni("LagSwitch");
            try
            {
                var session = new WfpNative.FWPM_SESSION0
                {
                    displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = name },
                    flags = WfpNative.FWPM_SESSION_FLAG_DYNAMIC,
                };

                var status = WfpNative.FwpmEngineOpen0(
                    null, WfpNative.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out _engine);
                if (status != WfpNative.ERROR_SUCCESS)
                {
                    _engine = IntPtr.Zero;
                    throw new WfpException("ouverture du moteur WFP", status);
                }

                AddSubLayer(name);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    /// <summary>
    /// Un sous-calque a nous : l'arbitrage de WFP se fait calque par calque, et un blocage dans
    /// n'importe lequel suffit a jeter le paquet. Nos filtres n'ont donc pas a lutter de poids
    /// contre ceux du pare-feu Windows.
    /// </summary>
    private void AddSubLayer(IntPtr name)
    {
        _subLayerKey = Guid.NewGuid();

        var subLayer = new WfpNative.FWPM_SUBLAYER0
        {
            subLayerKey = _subLayerKey,
            displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = name },
            weight = 0xFFFF,
        };

        var status = WfpNative.FwpmSubLayerAdd0(_engine, ref subLayer, IntPtr.Zero);
        if (status != WfpNative.ERROR_SUCCESS) throw new WfpException("ajout du sous-calque", status);
    }

    // ------------------------------------------------------------------- cible

    /// <summary>Null pour tout le trafic, sinon le chemin complet de l'executable a couper.</summary>
    public void SetTarget(string? applicationPath)
    {
        lock (_gate)
        {
            RemoveFiltersCore();
            FreeAppId();
            _targetPath = applicationPath;

            if (string.IsNullOrWhiteSpace(applicationPath)) return;

            var status = WfpNative.FwpmGetAppIdFromFileName0(applicationPath, out _appId);
            if (status != WfpNative.ERROR_SUCCESS)
            {
                _appId = IntPtr.Zero;
                throw new WfpException($"identification de {applicationPath}", status);
            }
        }
    }

    private void FreeAppId()
    {
        if (_appId == IntPtr.Zero) return;
        WfpNative.FwpmFreeMemory0(ref _appId);
        _appId = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- blocage

    public void SetBlocked(bool blocked, bool outbound, bool inbound)
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) throw new InvalidOperationException("moteur WFP non ouvert");

            RemoveFiltersCore();
            if (!blocked || (!outbound && !inbound)) return;

            var perApp = _appId != IntPtr.Zero;

            var layers = new List<Guid>();
            if (outbound)
            {
                if (perApp) layers.AddRange([WfpNative.LayerAleAuthConnectV4, WfpNative.LayerAleAuthConnectV6]);
                else layers.AddRange([WfpNative.LayerOutboundTransportV4, WfpNative.LayerOutboundTransportV6]);
            }
            if (inbound)
            {
                if (perApp) layers.AddRange([WfpNative.LayerAleAuthRecvAcceptV4, WfpNative.LayerAleAuthRecvAcceptV6]);
                else layers.AddRange([WfpNative.LayerInboundTransportV4, WfpNative.LayerInboundTransportV6]);
            }

            // Transaction : les filtres apparaissent tous ensemble, sinon il existe un instant
            // ou seul un sens est coupe.
            var status = WfpNative.FwpmTransactionBegin0(_engine, 0);
            if (status != WfpNative.ERROR_SUCCESS) throw new WfpException("ouverture de la transaction", status);

            try
            {
                foreach (var layer in layers) _filters.Add(AddBlockFilter(layer));

                status = WfpNative.FwpmTransactionCommit0(_engine);
                if (status != WfpNative.ERROR_SUCCESS) throw new WfpException("validation de la transaction", status);
            }
            catch
            {
                WfpNative.FwpmTransactionAbort0(_engine);
                _filters.Clear();
                throw;
            }
        }
    }

    private ulong AddBlockFilter(Guid layer)
    {
        var name = Marshal.StringToHGlobalUni("LagSwitch - blocage");
        var conditions = IntPtr.Zero;

        try
        {
            uint conditionCount = 0;

            if (_appId != IntPtr.Zero)
            {
                var condition = new WfpNative.FWPM_FILTER_CONDITION0
                {
                    fieldKey = WfpNative.ConditionAleAppId,
                    matchType = WfpNative.FWP_MATCH_EQUAL,
                    conditionValue = new WfpNative.FWP_VALUE0
                    {
                        type = WfpNative.FWP_BYTE_BLOB_TYPE,
                        value = (ulong)_appId.ToInt64(),
                    },
                };

                conditions = Marshal.AllocHGlobal(Marshal.SizeOf<WfpNative.FWPM_FILTER_CONDITION0>());
                Marshal.StructureToPtr(condition, conditions, false);
                conditionCount = 1;
            }

            var filter = new WfpNative.FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new WfpNative.FWPM_DISPLAY_DATA0 { name = name },
                layerKey = layer,
                subLayerKey = _subLayerKey,

                // Poids laisse vide : WFP en attribue un, et le blocage l'emporte de toute facon.
                weight = new WfpNative.FWP_VALUE0 { type = WfpNative.FWP_EMPTY },

                numFilterConditions = conditionCount,
                filterCondition = conditions,
                action = new WfpNative.FWPM_ACTION0 { type = WfpNative.FWP_ACTION_BLOCK },
            };

            var status = WfpNative.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id);
            if (status != WfpNative.ERROR_SUCCESS)
                throw new WfpException($"ajout du filtre sur la couche {layer}", status);

            return id;
        }
        finally
        {
            if (conditions != IntPtr.Zero) Marshal.FreeHGlobal(conditions);
            Marshal.FreeHGlobal(name);
        }
    }

    private void RemoveFiltersCore()
    {
        if (_engine == IntPtr.Zero || _filters.Count == 0) return;

        foreach (var id in _filters)
        {
            try { WfpNative.FwpmFilterDeleteById0(_engine, id); }
            catch { /* la session dynamique nettoiera de toute facon */ }
        }

        _filters.Clear();
    }

    // ----------------------------------------------------------------- sortie

    public void Close()
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) return;

            RemoveFiltersCore();
            FreeAppId();

            WfpNative.FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }

    public void Dispose() => Close();

    public override string ToString() =>
        $"WFP[{(IsOpen ? "ouvert" : "ferme")}, cible={_targetPath ?? "tout le trafic"}, filtres={_filters.Count}]";
}
