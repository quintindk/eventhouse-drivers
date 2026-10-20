namespace Eventhouse.Engine.Spike;

internal static class Handles
{
    private const ulong ConnectionTag = 0xC011_0000_0000_0000;
    private const ulong ResultTag = 0xAE50_0000_0000_0000;
    private const ulong OperationTag = 0x0F00_0000_0000_0000;
    private const ulong SequenceMask = 0x0000_FFFF_FFFF_FFFF;
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, IDisposable> Items = new();
    private static readonly HashSet<ulong> Reserved = new();
    private static ulong s_sequence;

    internal static ulong AddConnection(EngineConnection connection) => Add(ConnectionTag, connection);
    internal static ulong AddResult(QueryResult result) => Add(ResultTag, result);
    internal static ulong AddOperation(EngineOperation operation) => Add(OperationTag, operation);

    internal static bool TryGetConnection(ulong handle, out EngineConnection? connection) => TryGet(handle, ConnectionTag, out connection);
    internal static bool TryGetResult(ulong handle, out QueryResult? result) => TryGet(handle, ResultTag, out result);

    internal static SpikeStatus AcquireResult(ulong handle, out QueryResult? result)
    {
        lock (Gate)
        {
            if (!TryGet(handle, ResultTag, out result)) return SpikeStatus.InvalidHandle;
            return result is StreamingResult cursor ? cursor.AcquireData() : SpikeStatus.Ok;
        }
    }

    internal static SpikeStatus ReserveConnection(ulong handle, out EngineConnection? connection)
    {
        lock (Gate)
        {
            if (!TryGet(handle, ConnectionTag, out connection)) return SpikeStatus.InvalidHandle;
            return Reserved.Add(handle) ? SpikeStatus.Ok : SpikeStatus.Busy;
        }
    }

    internal static void ReleaseConnection(ulong handle)
    {
        lock (Gate) Reserved.Remove(handle);
    }

    internal static EngineOperation? AcquireOperation(ulong handle, bool remove = false)
    {
        lock (Gate)
        {
            if (!TryGet<EngineOperation>(handle, OperationTag, out var operation) || !operation!.Acquire()) return null;
            if (remove)
            {
                operation.MarkClosed();
                Items.Remove(handle);
            }
            return operation;
        }
    }

    internal static SpikeStatus CloseConnection(ulong handle)
    {
        EngineConnection? connection;
        lock (Gate)
        {
            if (!TryGet(handle, ConnectionTag, out connection)) return SpikeStatus.InvalidHandle;
            if (Reserved.Contains(handle)) return SpikeStatus.Busy;
            Items.Remove(handle);
        }
        connection!.Dispose();
        return SpikeStatus.Ok;
    }

    internal static bool RemoveConnection(ulong handle) => CloseConnection(handle) == SpikeStatus.Ok;

    internal static bool RemoveResult(ulong handle)
    {
        QueryResult? result;
        lock (Gate)
        {
            if (!TryGet(handle, ResultTag, out result)) return false;
            if (result is StreamingResult cursor) cursor.MarkRemoved();
            Items.Remove(handle);
        }
        result!.Dispose();
        return true;
    }

    private static bool TryGet<T>(ulong handle, ulong tag, out T? value) where T : class
    {
        lock (Gate)
        {
            value = null;
            if ((handle & ~SequenceMask) != tag || !Items.TryGetValue(handle, out var item)) return false;
            value = item as T;
            return value is not null;
        }
    }

    private static ulong Add(ulong tag, IDisposable value)
    {
        lock (Gate)
        {
            if (s_sequence == SequenceMask) throw new InvalidOperationException();
            var handle = tag | ++s_sequence;
            Items.Add(handle, value);
            return handle;
        }
    }
}
