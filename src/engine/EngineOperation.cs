using System.Diagnostics;

namespace Eventhouse.Engine.Spike;

// Registry -> operation is the only nested lock order. Never enter the registry
// or invoke cancellation/disposal while holding the operation lock.
internal sealed class EngineOperation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Timer _timer;
    private readonly Action _releaseReservation;
    private readonly TimeSpan _timeout;
    private readonly bool _scheduleDeadlineTimer;
    private int _leases = 1; // Registry ownership, dropped by Close.
    private bool _closed;
    private bool _ownershipReleased;
    private bool _started;
    private SpikeStatus? _terminal;
    private ProtocolReason _terminalReason;
    private System.Text.Json.JsonTokenType? _terminalToken;
    private long _startedAt;

    internal EngineOperation(EngineConnection connection, TimeSpan timeout, Action releaseReservation,
        bool scheduleDeadlineTimer = true)
    {
        Connection = connection;
        _timeout = timeout;
        _releaseReservation = releaseReservation;
        // Internal deterministic test seam. Native callers always use timer delivery.
        _scheduleDeadlineTimer = scheduleDeadlineTimer;
        _timer = new Timer(_ => DeadlineElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal EngineConnection Connection { get; }
    internal CancellationToken Token => _cancellation.Token;

    internal bool Acquire()
    {
        lock (_gate)
        {
            if (_closed) return false;
            _leases++;
            return true;
        }
    }

    internal void Release()
    {
        bool dispose;
        lock (_gate) dispose = --_leases == 0;
        if (!dispose) return;
        try
        {
            _timer.Dispose();
            _cancellation.Dispose();
        }
        finally { _releaseReservation(); }
    }

    internal SpikeStatus Begin()
    {
        lock (_gate)
        {
            if (_started) return SpikeStatus.InvalidState;
            _started = true;
            _startedAt = Stopwatch.GetTimestamp();
            if (_terminal.HasValue) return _terminal.Value;
            if (_scheduleDeadlineTimer) _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            return SpikeStatus.Ok;
        }
    }

    private void CheckDeadline()
    {
        if (!_terminal.HasValue && _started && Stopwatch.GetElapsedTime(_startedAt) >= _timeout)
            _terminal = SpikeStatus.Timeout;
    }

    internal void Cancel()
    {
        bool signal;
        lock (_gate)
        {
            CheckDeadline();
            _terminal ??= SpikeStatus.Cancelled;
            signal = _terminal is SpikeStatus.Cancelled or SpikeStatus.Timeout;
        }
        if (signal) Signal();
    }

    private void Signal()
    {
        // CancelAsync marks the token synchronously but runs user/transport callbacks
        // without blocking close. Keep the CTS and reservation alive until they drain.
        lock (_gate) _leases++;
        _ = SignalAsync();
    }

    private async Task SignalAsync()
    {
        try { await _cancellation.CancelAsync().ConfigureAwait(false); }
        catch { /* Callback failures cannot escape the ABI or a timer callback. */ }
        finally { Release(); }
    }

    private void DeadlineElapsed()
    {
        if (!Acquire()) return;
        try
        {
            bool signal;
            lock (_gate)
            {
                CheckDeadline();
                signal = _terminal == SpikeStatus.Timeout;
                // Timer delivery may be early due to clock/timer granularity.
                if (!_terminal.HasValue)
                    _timer.Change(TimeSpan.FromMilliseconds(Math.Max(1,
                        (_timeout - Stopwatch.GetElapsedTime(_startedAt)).TotalMilliseconds)), Timeout.InfiniteTimeSpan);
            }
            if (signal) Signal();
        }
        catch { /* Timer callbacks must never escape onto the runtime thread pool. */ }
        finally { Release(); }
    }

    internal SpikeStatus Complete(SpikeStatus status, QueryResult? result, out ulong resultHandle)
    {
        resultHandle = 0;
        ulong candidate = 0;
        try
        {
            if (result is not null) candidate = Handles.AddResult(result);
            lock (_gate)
            {
                CheckDeadline();
                _terminal ??= status;
                if (_terminal == SpikeStatus.Ok)
                {
                    resultHandle = candidate;
                    candidate = 0; // Ownership transfers exactly once, under the decision lock.
                }
                return _terminal.Value;
            }
        }
        finally
        {
            if (candidate != 0) Handles.RemoveResult(candidate);
            else if (resultHandle == 0) result?.Dispose();
        }
    }

    // Streaming publication is not terminal. The cursor already owns a lease.
    internal SpikeStatus Publish(StreamingResult result, out ulong resultHandle)
    {
        resultHandle = 0;
        ulong candidate;
        try { candidate = Handles.AddResult(result); }
        catch { result.Dispose(); throw; }
        SpikeStatus status;
        lock (_gate)
        {
            CheckDeadline();
            status = _terminal ?? SpikeStatus.Ok;
            if (!_closed && !_terminal.HasValue) { resultHandle = candidate; candidate = 0; }
            else if (status == SpikeStatus.Ok) status = SpikeStatus.Cancelled;
        }
        if (candidate != 0) Handles.RemoveResult(candidate);
        return status;
    }

    internal SpikeStatus Decide(SpikeStatus status, ProtocolReason reason, out ProtocolReason terminalReason,
        System.Text.Json.JsonTokenType? unexpectedToken, out System.Text.Json.JsonTokenType? terminalToken)
    {
        lock (_gate)
        {
            CheckDeadline();
            if (!_terminal.HasValue) { _terminal = status; _terminalReason = reason; _terminalToken = unexpectedToken; }
            terminalReason = _terminalReason;
            terminalToken = _terminalToken;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return _terminal.Value;
        }
    }

    internal SpikeStatus CommitRow(Action commit)
    {
        lock (_gate)
        {
            CheckDeadline();
            if (_terminal.HasValue) return _terminal.Value;
            commit();
            return SpikeStatus.Ok;
        }
    }

    internal void MarkClosed()
    {
        lock (_gate)
        {
            _closed = true;
            CheckDeadline();
            _terminal ??= SpikeStatus.Cancelled;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownershipReleased) return;
            _ownershipReleased = true;
            MarkClosed();
        }
        try { Cancel(); }
        finally { Release(); }
    }
}
