namespace Iutq.Waffle.Generator;

/// <summary>Lookup strategy chosen per track by the selector.</summary>
internal enum LookupStrategyKind : byte
{
    /// <summary>At most 4 clips: baked comparison chain, no array.</summary>
    Tiny = 1,

    /// <summary>Byte LUT of payload unit offsets (duration &lt;= 512, all offsets &lt;= 254).</summary>
    Dense8 = 2,

    /// <summary>Ushort LUT of payload unit offsets (duration &lt;= 2048).</summary>
    Dense16 = 3,

    /// <summary>Unrolled comparison tree over sorted clip bounds, no array.</summary>
    Binary = 4,

    /// <summary>Crossfade per-tick LUT with baked weightA (duration &lt;= 2048).</summary>
    BlendLut = 5
}

internal sealed class ClipInfo
{
    public int Start;
    public int Len;
    public int Offset;
}

/// <summary>An overlapping (laneA, laneB) clip pair, replicated from the builder's crossfade scan.</summary>
internal sealed class BlendPairInfo
{
    public int LaneAIndex;
    public int LaneBIndex;
    public int BlendStart;
    public int BlendEnd;
    public int OffsetA;
    public int OffsetB;
    public bool LaneBFirst;
}

internal sealed class TrackInfo
{
    public int Index;
    public int Binding;
    public int TemplateId;
    public bool CrossFade;
    public LookupStrategyKind Strategy;
    public List<ClipInfo> LaneA = new();
    public List<ClipInfo> LaneB = new();
    public List<BlendPairInfo> Pairs = new();

    /// <summary>Dense8/Dense16 rows: offset literal or sentinel.</summary>
    public string[] LutRows = Array.Empty<string>();

    /// <summary>BlendLut rows: pre-rendered "new BlendEntry(a, b, w)" literals.</summary>
    public string[] BlendEntries = Array.Empty<string>();

    /// <summary>Pre-rendered unrolled binary tree body (recursive emission; Waffle cannot recurse).</summary>
    public string BinaryBody = string.Empty;

    public string ModeName => CrossFade ? "CrossFade" : "Exclusive";

    public string StrategyName => Strategy switch
    {
        LookupStrategyKind.Tiny => "Tiny (baked comparison chain)",
        LookupStrategyKind.Dense8 => "Dense8 (byte payload-direct LUT)",
        LookupStrategyKind.Dense16 => "Dense16 (ushort payload-direct LUT)",
        LookupStrategyKind.Binary => "Binary (unrolled comparison tree)",
        LookupStrategyKind.BlendLut => "BlendLut (per-tick blend entries, baked weightA)",
        _ => "Unknown"
    };

    public int ClipCount => LaneA.Count + LaneB.Count;
}

internal sealed class PartitionInfo
{
    private readonly List<int> _evTrack = new();
    private readonly List<int> _evTick = new();
    private readonly List<int> _evOffA = new();
    private readonly List<int> _evOffB = new();
    private readonly List<int> _evKind = new();
    private readonly List<string> _evFactor = new();

    public string ClassName = string.Empty;
    public int TimelineIndex;
    public int TypeSlot;
    public ulong TimelineKey;
    public int Duration;
    public bool Loop;
    public ulong TypeKey;
    public int PayloadSize;
    public List<TrackInfo> Tracks = new();

    /// <summary>Merged transition event stream, sorted by (tick, track ordinal).</summary>
    public int[] EvTrack = Array.Empty<int>();
    public int[] EvTick = Array.Empty<int>();
    public int[] EvOffA = Array.Empty<int>();
    public int[] EvOffB = Array.Empty<int>();
    public int[] EvKind = Array.Empty<int>();
    public string[] EvFactor = Array.Empty<string>();

    /// <summary>Prefix table with duration + 1 entries: first event index with Tick &gt;= t.</summary>
    public ushort[] TickStart = Array.Empty<ushort>();

    public int EventCount => EvTick.Length;
    public bool SingleTrack => Tracks.Count == 1;
    public bool HasBlendLut => Tracks.Any(t => t.Strategy == LookupStrategyKind.BlendLut);

    public void AddEvent(int trackOrdinal, int tick, int offsetA, int offsetB, int kind, string factorText)
    {
        _evTrack.Add(trackOrdinal);
        _evTick.Add(tick);
        _evOffA.Add(offsetA);
        _evOffB.Add(offsetB);
        _evKind.Add(kind);
        _evFactor.Add(factorText);
    }

    public void FreezeEvents()
    {
        EvTrack = _evTrack.ToArray();
        EvTick = _evTick.ToArray();
        EvOffA = _evOffA.ToArray();
        EvOffB = _evOffB.ToArray();
        EvKind = _evKind.ToArray();
        EvFactor = _evFactor.ToArray();
    }
}

internal sealed class SourceInfo
{
    public string OriginalName = string.Empty;
    public string SanitizedName = string.Empty;
    public string Base64 = string.Empty;
    public int ArenaOffset;
    public int ArenaUnits;
    public uint PayloadHash;
    public List<PartitionInfo> Partitions = new();
}
