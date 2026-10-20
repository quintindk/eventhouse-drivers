using System.Net.Http;

namespace Eventhouse.Engine.Spike;

internal sealed class StreamingResult : QueryResult
{
    private readonly object _gate = new();
    private EngineOperation? _operation;
    private readonly bool _privateOperation;
    private readonly CancellationTokenRegistration _registration;
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private StreamingParser? _parser;
    private bool _closed, _dataBusy, _detached;
    private int _calls, _finishing;
    private SpikeStatus? _terminal;
    private ProtocolReason _reason;
    private System.Text.Json.JsonTokenType? _unexpectedToken;
    private IReadOnlyList<ResultCell>? _row;
    private nuint _delivered;

    private StreamingResult(HttpResponseMessage response, Stream stream, StreamingParser parser,
        EngineOperation operation, bool privateOperation) : base(parser.Columns, [])
    {
        _response = response;
        _stream = stream;
        _parser = parser;
        _operation = operation;
        _privateOperation = privateOperation;
        if (!operation.Acquire()) throw new OperationCanceledException();
        _registration = operation.Token.UnsafeRegister(static state => ((StreamingResult)state!).Cancelled(), this);
        if (_detached) _registration.Unregister(); // Registration can invoke synchronously.
    }

    internal static async Task<StreamingResult> CreateAsync(HttpResponseMessage response, EngineOperation operation, bool privateOperation)
    {
        Stream? stream = null;
        StreamingParser? parser = null;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(operation.Token).ConfigureAwait(false);
            parser = new StreamingParser(stream);
            await parser.InitialiseAsync(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return new StreamingResult(response, stream, parser, operation, privateOperation);
        }
        catch { parser?.Dispose(); stream?.Dispose(); response.Dispose(); throw; }
    }

    // Invoked under the registry lock. No callbacks, I/O or disposal here.
    internal SpikeStatus AcquireData()
    {
        lock (_gate)
        {
            if (_closed) return SpikeStatus.InvalidHandle;
            if (_dataBusy) return SpikeStatus.Busy;
            _dataBusy = true;
            _calls++;
            return SpikeStatus.Ok;
        }
    }

    internal void ReleaseData()
    {
        lock (_gate) { _dataBusy = false; _calls--; }
        DetachIfReady();
    }

    internal nuint RowCount { get { lock (_gate) return _terminal == SpikeStatus.Ok ? _delivered : nuint.MaxValue; } }
    internal ProtocolReason Reason { get { lock (_gate) return _reason; } }
    internal System.Text.Json.JsonTokenType? UnexpectedToken { get { lock (_gate) return _unexpectedToken; } }
    internal SpikeStatus? GetTerminalFailure()
    {
        lock (_gate) return _terminal.HasValue && _terminal != SpikeStatus.Ok ? _terminal : null;
    }
    internal ResultCell? Current(nuint column)
    {
        lock (_gate) return !_closed && !_terminal.HasValue && _row is not null && column < (nuint)_row.Count ? _row[(int)column] : null;
    }

    internal SpikeStatus Next(out byte hasRow)
    {
        hasRow = 0;
        StreamingParser? parser;
        lock (_gate)
        {
            _row = null;
            if (_terminal.HasValue) return _terminal.Value;
            parser = _parser;
        }
        try
        {
            var row = parser!.NextAsync(_operation!.Token).GetAwaiter().GetResult();
            if (row is null) return Finish(SpikeStatus.Ok);
            if (_delivered == nuint.MaxValue - 1) return Finish(SpikeStatus.ProtocolFailed, ProtocolReason.CountLimit);
            var status = _operation.CommitRow(() =>
            {
                lock (_gate)
                {
                    if (_closed) return;
                    _row = row;
                    _delivered++;
                }
            });
            if (status != SpikeStatus.Ok) return Finish(status);
            lock (_gate)
            {
                if (_closed) return SpikeStatus.Cancelled;
                if (_terminal.HasValue) return _terminal.Value;
                hasRow = 1;
            }
            return SpikeStatus.Ok;
        }
        catch (ProtocolException exception) { return Finish(SpikeStatus.ProtocolFailed, exception.Reason, exception.UnexpectedToken); }
        catch (OperationCanceledException) { return Finish(SpikeStatus.Cancelled); }
        catch (HttpRequestException) { return Finish(SpikeStatus.TransportFailed); }
        catch (IOException) { return Finish(SpikeStatus.TransportFailed); }
        catch (ObjectDisposedException) { return Finish(SpikeStatus.Cancelled); }
        catch { return Finish(SpikeStatus.InternalError); }
    }

    private void Cancelled() => Finish(SpikeStatus.Cancelled);

    private SpikeStatus Finish(SpikeStatus status, ProtocolReason reason = ProtocolReason.Unknown,
        System.Text.Json.JsonTokenType? unexpectedToken = null)
    {
        EngineOperation operation;
        lock (_gate)
        {
            if (_terminal.HasValue) return _terminal.Value;
            _finishing++;
            operation = _operation!;
        }
        status = operation.Decide(status, reason, out reason, unexpectedToken, out unexpectedToken);
        lock (_gate)
        {
            if (!_terminal.HasValue)
            {
                _terminal = status;
                _reason = status == SpikeStatus.ProtocolFailed ? reason : ProtocolReason.Unknown;
                _unexpectedToken = status == SpikeStatus.ProtocolFailed ? unexpectedToken : null;
            }
            _row = null;
            status = _terminal.Value;
            _finishing--;
        }
        DetachIfReady();
        return status;
    }

    private void DetachIfReady()
    {
        HttpResponseMessage? response;
        Stream? stream;
        StreamingParser? parser;
        EngineOperation? operation;
        lock (_gate)
        {
            if (_detached || _calls != 0 || _finishing != 0 || (!_terminal.HasValue && !_closed)) return;
            _detached = true;
            response = _response; _response = null;
            stream = _stream; _stream = null;
            parser = _parser; _parser = null;
            operation = _operation; _operation = null;
            _row = null;
        }
        // Unregister does not wait for a callback which may be this call itself.
        _registration.Unregister();
        try { parser?.Dispose(); }
        finally
        {
            try { stream?.Dispose(); }
            finally
            {
                try { response?.Dispose(); }
                finally
                {
                    try { if (_privateOperation) operation!.Dispose(); }
                    finally { operation!.Release(); }
                }
            }
        }
    }

    internal void MarkRemoved() { lock (_gate) _closed = true; }

    public override void Dispose()
    {
        MarkRemoved();
        EngineOperation? operation;
        lock (_gate) { operation = _operation; if (operation is not null) _finishing++; }
        if (operation is not null)
        {
            try { operation.Cancel(); }
            finally { lock (_gate) _finishing--; }
        }
        Finish(SpikeStatus.Cancelled);
        DetachIfReady();
        base.Dispose();
    }
}
