using System.Runtime.InteropServices;

namespace LagSwitch.Core.Wfp;

/// <summary>
/// Interop avec la plateforme de filtrage Windows (WFP), la couche sur laquelle le pare-feu
/// Windows est lui-meme construit. Poser nos filtres directement ici a trois consequences :
/// ils s'appliquent meme quand le service Pare-feu est eteint, ils touchent le trafic de
/// bouclage, et une session dynamique les efface toute seule si le processus meurt.
///
/// Les dispositions memoire sont ecrites pour x64. Les unions du C sont representees par des
/// champs de la bonne taille ET du bon alignement : un <c>Guid</c> s'aligne sur 4 la ou une
/// union contenant un UINT64 s'aligne sur 8, et se tromper la decale tout ce qui suit.
/// </summary>
internal static class WfpNative
{
    private const string Dll = "fwpuclnt.dll";

    public const uint ERROR_SUCCESS = 0;

    /// <summary>Les objets crees disparaissent avec la session. C'est notre filet anti-plantage.</summary>
    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

    public const uint RPC_C_AUTHN_WINNT = 10;

    public const uint FWP_ACTION_FLAG_TERMINATING = 0x00001000;
    public const uint FWP_ACTION_BLOCK = 0x00000001 | FWP_ACTION_FLAG_TERMINATING;

    // FWP_DATA_TYPE
    public const uint FWP_EMPTY = 0;
    public const uint FWP_BYTE_BLOB_TYPE = 12;

    // FWP_MATCH_TYPE
    public const uint FWP_MATCH_EQUAL = 0;

    // ------------------------------------------------------------------ couches

    /// <summary>
    /// Couches « transport » : tout paquet y passe, y compris ICMP et le bouclage, et les flux
    /// deja etablis sont touches immediatement. En revanche l'identite de l'application n'y est
    /// pas disponible : elles ne servent qu'a la cible globale.
    /// </summary>
    public static readonly Guid LayerOutboundTransportV4 = new("09e61aea-d214-46e2-9b21-b26b0b2f28c8");
    public static readonly Guid LayerOutboundTransportV6 = new("e1735bde-013f-4655-b351-a49e15762df0");
    public static readonly Guid LayerInboundTransportV4 = new("5926dfc8-e3cf-4426-a283-dc393f5d0f9d");
    public static readonly Guid LayerInboundTransportV6 = new("634a869f-fc23-4b90-b0c1-bf620a36ae6f");

    /// <summary>
    /// Couches « ALE » : evaluees a l'etablissement d'une connexion, et reevaluees quand la
    /// politique change — c'est ce qui coupe aussi les connexions deja ouvertes. Seules ces
    /// couches connaissent l'application a l'origine du trafic.
    /// </summary>
    public static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerAleAuthRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    public static readonly Guid LayerAleAuthRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    public static readonly Guid ConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    // --------------------------------------------------------------- structures

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        [MarshalAs(UnmanagedType.Bool)] public bool kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    /// <summary>
    /// FWP_VALUE0 et FWP_CONDITION_VALUE0 ont la meme forme : un type, puis une union dont le
    /// plus grand membre fait 8 octets et impose un alignement de 8.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_VALUE0
    {
        public uint type;
        private uint _padding;
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        private uint _padding;
        public FWP_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_ACTION0
    {
        public uint type;
        public Guid filterTypeOrCalloutKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        private uint _pad0;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        private uint _pad1;
        public IntPtr filterCondition;
        public FWPM_ACTION0 action;
        private uint _pad2;

        // union { UINT64 rawContext; GUID providerContextKey; } : 16 octets, aligne sur 8.
        // Deux ulong plutot qu'un Guid, sinon l'alignement de 4 du Guid decalerait la suite.
        public ulong providerContextLow;
        public ulong providerContextHigh;

        public IntPtr reserved;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    // ------------------------------------------------------------------ appels

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FWPM_SESSION0 session,
        out IntPtr engineHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FwpmGetAppIdFromFileName0(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IntPtr appId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FwpmFreeMemory0(ref IntPtr p);

    /// <summary>Rend un message lisible pour les codes que l'on croise vraiment.</summary>
    public static string Explain(uint code) => code switch
    {
        0 => "succes",
        0x80320001 => "FWP_E_CALLOUT_NOT_FOUND",
        0x80320003 => "FWP_E_PROVIDER_NOT_FOUND",
        0x80320005 => "FWP_E_SUBLAYER_NOT_FOUND",
        0x80320007 => "FWP_E_LAYER_NOT_FOUND",
        0x8032000D => "FWP_E_ALREADY_EXISTS",
        0x80320014 => "FWP_E_TIMEOUT",
        0x80320035 => "FWP_E_INVALID_PARAMETER",
        0x80320009 => "FWP_E_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED (elevation requise)",
        87 => "ERROR_INVALID_PARAMETER",
        _ => $"0x{code:X8}",
    };
}
