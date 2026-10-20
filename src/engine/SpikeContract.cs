namespace Eventhouse.Engine.Spike;

internal static class SpikeContract
{
#if EVENTHOUSE_PRODUCT
    internal const uint AbiVersion = 0x0001_0003;
    internal const string ExportPrefix = "eh_";
#else
    internal const uint AbiVersion = 0x0001_0000;
    internal const string ExportPrefix = "eh_spike_";
#endif
    internal const string ApprovedQueryUri = "https://example.z2.kusto.fabric.microsoft.com";
    internal const string ApprovedResource = "https://kusto.kusto.windows.net";
    internal const string Scope = ApprovedResource + "/.default";
    internal const int MaximumQueryBytes = 16 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int InputWindowBytes = 128 * 1024;
    internal const int MaximumCellBytes = 4 * 1024 * 1024;
    internal const int MaximumRowBytes = 8 * 1024 * 1024;
    internal const int MaximumMetadataBytes = 256 * 1024;
    internal const int MaximumColumns = 4096;
}

internal enum SpikeStatus : uint
{
    Ok = 0,
    InvalidArgument = 1,
    BufferTooSmall = 2,
    InvalidHandle = 3,
    AuthenticationFailed = 4,
    TransportFailed = 5,
    HttpFailed = 6,
    ProtocolFailed = 7,
    Cancelled = 8,
    InternalError = 9,
    Timeout = 10,
    InvalidState = 11,
    Busy = 12,
    PermissionDenied = 13,
    QueryRejected = 14
}

internal enum SpikeCellType : uint
{
    Unknown = 0,
    Boolean = 1,
    Int32 = 2,
    Int64 = 3,
    Double = 4,
    Decimal = 5,
    DateTime = 6,
    TimeSpan = 7,
    Guid = 8,
    String = 9,
    Dynamic = 10
}
