using System.Text;
using System.Text.Json;

namespace Eventhouse.Engine.Spike;

// A pull parser for the v1 Tables response. Only the current token and cell can
// spill beyond the input window. No response DOM, row history or read-ahead task.
internal sealed class StreamingParser(Stream stream) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private byte[] _input = new byte[SpikeContract.InputWindowBytes];
    private int _length;
    private int _offset;
    private bool _eof;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 65 });
    private long _metadataBytes;
    private bool _metadata = true;
    private BoundedBytes? _capture;
    private JsonTokenType _kind;
    private byte[] _token = [];
    private bool _primarySeen;
    private bool _inPrimary;
    private bool _finished;
    private int _depth;
    internal List<ResultColumn> Columns { get; private set; } = [];
    internal int InputCapacity => _input.Length;
    internal int MaximumInputCapacity { get; private set; } = SpikeContract.InputWindowBytes;

    internal async Task InitialiseAsync(CancellationToken token)
    {
        await ExpectAsync(JsonTokenType.StartObject, token).ConfigureAwait(false);
        await PropertyAsync("Tables", ProtocolReason.Root, token).ConfigureAwait(false);
        await ExpectAsync(JsonTokenType.StartArray, token).ConfigureAwait(false);
        await FindPrimaryOrFinishAsync(token).ConfigureAwait(false);
    }

    private async Task FindPrimaryOrFinishAsync(CancellationToken token)
    {
        while (true)
        {
            await ReadAsync(token).ConfigureAwait(false);
            if (_kind == JsonTokenType.EndArray)
            {
                if (!_primarySeen) throw new ProtocolException(ProtocolReason.MissingPrimary);
                await ExpectAsync(JsonTokenType.EndObject, token).ConfigureAwait(false);
                if (await TryReadAsync(token).ConfigureAwait(false)) throw new ProtocolException(ProtocolReason.Root);
                _finished = true;
                return;
            }
            Require(JsonTokenType.StartObject, ProtocolReason.Table);
            string? name = null;
            List<ResultColumn>? columns = null;
            List<string>? names = null;
            var seen = 0;
            while (true)
            {
                await ReadAsync(token).ConfigureAwait(false);
                Require(JsonTokenType.PropertyName, ProtocolReason.Table);
                var property = Text();
                if (property == "TableName" && (seen & 1) == 0)
                {
                    seen |= 1;
                    await ExpectAsync(JsonTokenType.String, token).ConfigureAwait(false);
                    name = Text();
                }
                else if (property == "Columns" && (seen & 2) == 0)
                {
                    seen |= 2;
                    (columns, names) = await ReadColumnsAsync(token).ConfigureAwait(false);
                }
                else if (property == "Rows")
                {
                    if (seen != 3) throw new ProtocolException(ProtocolReason.HeaderOrder);
                    await ExpectAsync(JsonTokenType.StartArray, token).ConfigureAwait(false);
                    if (name == "Table_0")
                    {
                        if (_primarySeen) throw new ProtocolException(ProtocolReason.DuplicatePrimary);
                        _primarySeen = true;
                        _inPrimary = true;
                        Columns = columns!;
                        _metadata = false;
                        return;
                    }
                    if (!RestProtocol.IsMetadataTableName(name)) throw new ProtocolException(ProtocolReason.UnknownMetadataName);
                    var kind = Array.FindIndex(RestProtocol.MetadataColumnSets,
                        set => set.SequenceEqual(names!, StringComparer.Ordinal));
                    if (kind < 0) throw new ProtocolException(ProtocolReason.UnknownMetadataSchema);
                    await ReadAuxiliaryAsync(columns!, kind, token).ConfigureAwait(false);
                    await ExpectAsync(JsonTokenType.EndObject, token).ConfigureAwait(false);
                    break;
                }
                else throw new ProtocolException(ProtocolReason.Table);
            }
        }
    }

    private async Task<(List<ResultColumn>, List<string>)> ReadColumnsAsync(CancellationToken token)
    {
        await ExpectAsync(JsonTokenType.StartArray, token).ConfigureAwait(false);
        var columns = new List<ResultColumn>();
        var names = new List<string>();
        while (true)
        {
            await ReadAsync(token).ConfigureAwait(false);
            if (_kind == JsonTokenType.EndArray) return (columns, names);
            Require(JsonTokenType.StartObject, ProtocolReason.Columns);
            if (columns.Count == SpikeContract.MaximumColumns) throw new ProtocolException(ProtocolReason.ColumnLimit);
            string? name = null, dataType = null, columnType = null;
            var seen = 0;
            while (true)
            {
                await ReadAsync(token).ConfigureAwait(false);
                if (_kind == JsonTokenType.EndObject) break;
                Require(JsonTokenType.PropertyName, ProtocolReason.Columns);
                var property = Text();
                var bit = property switch { "ColumnName" => 1, "DataType" => 2, "ColumnType" => 4, _ => 0 };
                if (bit == 0 || (seen & bit) != 0) throw new ProtocolException(ProtocolReason.Columns);
                seen |= bit;
                await ExpectAsync(JsonTokenType.String, token).ConfigureAwait(false);
                var text = Text();
                if (string.IsNullOrWhiteSpace(text)) throw new ProtocolException(ProtocolReason.Columns);
                switch (bit) { case 1: name = text; break; case 2: dataType = text; break; case 4: columnType = text; break; }
            }
            if (seen != 7) throw new ProtocolException(ProtocolReason.Columns);
            columns.Add(new ResultColumn(Encoding.UTF8.GetBytes(name!), RestProtocol.MapType(dataType!, columnType!), true));
            names.Add(name!);
        }
    }

    internal async Task<IReadOnlyList<ResultCell>?> NextAsync(CancellationToken token)
    {
        if (_finished) return null;
        if (!_inPrimary) throw new ProtocolException(ProtocolReason.Table);
        await ReadAsync(token).ConfigureAwait(false);
        if (_kind == JsonTokenType.EndArray)
        {
            _inPrimary = false;
            _metadata = true;
            await ExpectAsync(JsonTokenType.EndObject, token).ConfigureAwait(false);
            await FindPrimaryOrFinishAsync(token).ConfigureAwait(false);
            // FindPrimary rejects a duplicate, so it can only finish here.
            return null;
        }
        if (_kind == JsonTokenType.StartObject)
        {
            // Error objects may occur inside Rows after a delivered prefix. Consume
            // only this bounded value, never accept it as a row or expose its text.
            var error = await ReadValueAsync(SpikeContract.MaximumCellBytes, token).ConfigureAwait(false);
            throw new ProtocolException(ClassifyRowObject(error), JsonTokenType.StartObject);
        }
        if (_kind != JsonTokenType.StartArray) throw new ProtocolException(ProtocolReason.PrimaryStart, _kind);
        return await ReadRowAsync(Columns, token).ConfigureAwait(false);
    }

    private static ProtocolReason ClassifyRowObject(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        reader.Read();
        return ClassifyErrorObject(ref reader, false);
    }

    private static ProtocolReason ClassifyErrorObject(ref Utf8JsonReader reader, bool recognised)
    {
        var category = ProtocolReason.InlineServiceError;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var marker = reader.ValueTextEquals("OneApiErrors"u8) || reader.ValueTextEquals("Exceptions"u8);
            var error = reader.ValueTextEquals("error"u8);
            var permanent = reader.ValueTextEquals("@permanent"u8);
            var code = reader.ValueTextEquals("code"u8) || reader.ValueTextEquals("Code"u8) || reader.ValueTextEquals("ErrorCode"u8);
            var message = reader.ValueTextEquals("@message"u8) || reader.ValueTextEquals("Message"u8) || reader.ValueTextEquals("message"u8);
            if (!reader.Read()) throw new ProtocolException(ProtocolReason.InvalidJson);
            var found = ProtocolReason.InlineServiceError;
            if (error && reader.TokenType == JsonTokenType.StartObject)
            {
                recognised = true;
                found = ClassifyErrorObject(ref reader, true);
            }
            else if (marker && reader.TokenType == JsonTokenType.StartArray)
            {
                recognised = true;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var child = ClassifyErrorObject(ref reader, true);
                        if (child > found) found = child;
                    }
                    else reader.Skip();
                }
            }
            else if (permanent && reader.TokenType is JsonTokenType.True or JsonTokenType.False) recognised = true;
            else if ((code || message) && reader.TokenType == JsonTokenType.String)
            {
                // Bounded transient decoding handles JSON escapes. No message or
                // unknown code is retained in terminal state or diagnostics.
                var scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
                try
                {
                    var length = reader.CopyString(scratch);
                    var value = scratch.AsSpan(0, length);
                    found = MatchesServiceToken(value, "E_QUERY_RESULT_SET_TOO_LARGE"u8, message) ? ProtocolReason.InlineResultTruncated :
                        MatchesServiceToken(value, "E_RUNAWAY_QUERY"u8, message) || MatchesServiceToken(value, "E_QUERY_COMPLEXITY"u8, message) ? ProtocolReason.InlineResourceLimit :
                        MatchesServiceToken(value, "E_QUERY_TIMEOUT"u8, message) || MatchesServiceToken(value, "E_TIMEOUT"u8, message) ? ProtocolReason.InlineServiceTimeout :
                        ProtocolReason.InlineServiceError;
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(scratch, clearArray: true); }
            }
            else reader.Skip(); // Unrelated objects cannot supply service codes.
            // Deterministic priority for multiple codes: truncation, resource,
            // timeout, other. This is diagnostic classification, not retry policy.
            if (found > category) category = found;
        }
        return recognised ? category : ProtocolReason.UnknownRowObject;
    }

    private static bool MatchesServiceToken(ReadOnlySpan<byte> value, ReadOnlySpan<byte> literal, bool message)
    {
        if (!message) return value.SequenceEqual(literal);
        var offset = 0;
        while (offset <= value.Length - literal.Length)
        {
            var match = value[offset..].IndexOf(literal);
            if (match < 0) return false;
            var start = offset + match;
            var end = start + literal.Length;
            if ((start == 0 || !MachineTokenByte(value[start - 1])) &&
                (end == value.Length || !MachineTokenByte(value[end]))) return true;
            offset = start + 1;
        }
        return false;
    }

    private static bool MachineTokenByte(byte value) => value is >= (byte)'A' and <= (byte)'Z' or
        >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or >= 0x80;

    private async Task<IReadOnlyList<ResultCell>> ReadRowAsync(IReadOnlyList<ResultColumn> columns, CancellationToken token, bool queryStatus = false)
    {
        var cells = new List<ResultCell>(columns.Count);
        var rawBytes = 0;
        var returnedBytes = 0;
        while (true)
        {
            await ReadAsync(token).ConfigureAwait(false);
            if (_kind == JsonTokenType.EndArray) break;
            if (cells.Count == columns.Count) throw new ProtocolException(ProtocolReason.RowWidthExcess, _kind);
            if (queryStatus && (cells.Count == 1 || cells.Count == 3) && _kind != JsonTokenType.Number)
                throw new ProtocolException(ProtocolReason.FailedQueryStatus);
            var type = columns[cells.Count].Type;
            var firstKind = _kind;
            var raw = await ReadValueAsync(SpikeContract.MaximumRowBytes - rawBytes, token).ConfigureAwait(false);
            rawBytes += raw.Length;
            byte[] value;
            var isNull = firstKind == JsonTokenType.Null;
            if (isNull) value = [];
            else if (type != SpikeCellType.Dynamic && firstKind == JsonTokenType.String)
                value = DecodeString(raw);
            else value = raw;
            if (value.Length > SpikeContract.MaximumCellBytes) throw new ProtocolException(ProtocolReason.CellLimit);
            if (value.Length > SpikeContract.MaximumRowBytes - returnedBytes) throw new ProtocolException(ProtocolReason.RowLimit);
            returnedBytes += value.Length;
            cells.Add(new ResultCell(type, isNull, value));
        }
        if (cells.Count != columns.Count) throw new ProtocolException(ProtocolReason.RowWidthShort, _kind);
        return cells;
    }

    private async Task ReadAuxiliaryAsync(List<ResultColumn> columns, int kind, CancellationToken token)
    {
        var rows = 0;
        while (true)
        {
            await ReadAsync(token).ConfigureAwait(false);
            if (_kind == JsonTokenType.EndArray) break;
            if (_kind != JsonTokenType.StartArray) throw new ProtocolException(ProtocolReason.AuxiliaryRowStart, _kind);
            var row = await ReadRowAsync(columns, token, kind == 1).ConfigureAwait(false);
            rows = 1; // Only presence matters. Never accumulate metadata rows.
            if (kind == 1 && (!Integer(row[1], out var severity) || severity <= 2 ||
                              !Integer(row[3], out var status) || status != 0))
                throw new ProtocolException(ProtocolReason.FailedQueryStatus);
        }
        if (kind == 1 && rows == 0) throw new ProtocolException(ProtocolReason.EmptyQueryStatus);
    }

    private static bool Integer(ResultCell cell, out long value)
    {
        var reader = new Utf8JsonReader(cell.Value);
        value = 0;
        return !cell.IsNull && reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out value);
    }

    private async Task<byte[]> ReadValueAsync(int rowRemaining, CancellationToken token)
    {
        if (_kind is not (JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or
            JsonTokenType.Null or JsonTokenType.StartObject or JsonTokenType.StartArray)) throw new ProtocolException(ProtocolReason.CellToken, _kind);
        var maximum = Math.Min(SpikeContract.MaximumCellBytes, rowRemaining);
        var reason = rowRemaining < SpikeContract.MaximumCellBytes ? ProtocolReason.RowLimit : ProtocolReason.CellLimit;
        var output = new BoundedBytes(maximum, reason);
        output.Append(_token);
        if (_kind is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            var depth = _depth - 1;
            _capture = output;
            try
            {
                do { await ReadAsync(token).ConfigureAwait(false); } while (_depth != depth);
            }
            finally { _capture = null; }
        }
        return output.ToArray();
    }

    private static byte[] DecodeString(byte[] raw)
    {
        var reader = new Utf8JsonReader(raw);
        reader.Read();
        var decoded = new byte[raw.Length - 2];
        var count = reader.CopyString(decoded);
        return decoded.AsSpan(0, count).ToArray();
    }

    private string Text()
    {
        // Property tokens include the colon. A string reader handles both forms.
        var reader = new Utf8JsonReader(_kind == JsonTokenType.PropertyName ? WrapProperty(_token) : _token);
        reader.Read();
        if (_kind == JsonTokenType.PropertyName) reader.Read();
        return reader.GetString() ?? string.Empty;
    }

    private static byte[] WrapProperty(byte[] raw)
    {
        var bytes = new byte[raw.Length + 2];
        bytes[0] = (byte)'{';
        raw.CopyTo(bytes, 1);
        bytes[^1] = (byte)'0';
        return bytes;
    }

    private void Require(JsonTokenType kind, ProtocolReason reason)
    {
        if (_kind != kind) throw new ProtocolException(reason);
    }

    private async Task PropertyAsync(string name, ProtocolReason reason, CancellationToken token)
    {
        await ReadAsync(token).ConfigureAwait(false);
        Require(JsonTokenType.PropertyName, reason);
        if (Text() != name) throw new ProtocolException(reason);
    }

    private async Task ExpectAsync(JsonTokenType kind, CancellationToken token)
    {
        await ReadAsync(token).ConfigureAwait(false);
        Require(kind, kind == JsonTokenType.EndObject ? ProtocolReason.Table : ProtocolReason.InvalidJson);
    }

    private async Task ReadAsync(CancellationToken token)
    {
        if (!await TryReadAsync(token).ConfigureAwait(false))
            throw new ProtocolException(_metadataBytes == 0 ? ProtocolReason.EmptyResponse : ProtocolReason.InvalidJson);
    }

    private async Task<bool> TryReadAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (ParseToken()) return true;
            if (_eof) return false;
            // A failed Read leaves only an incomplete token (preceding whitespace
            // has been consumed). Its bytes count against the active budget even
            // when the backing array was enlarged by an earlier primary cell.
            if (_metadata && _length > SpikeContract.MaximumMetadataBytes - _metadataBytes)
                throw new ProtocolException(ProtocolReason.MetadataLimit);
            var maximum = _metadata ? SpikeContract.MaximumMetadataBytes + SpikeContract.InputWindowBytes :
                SpikeContract.MaximumCellBytes + SpikeContract.InputWindowBytes;
            if (_length == _input.Length)
            {
                if (_input.Length >= maximum) throw new ProtocolException(_metadata ? ProtocolReason.MetadataLimit : ProtocolReason.CellLimit);
                Array.Resize(ref _input, Math.Min(maximum, checked(_input.Length * 2)));
                MaximumInputCapacity = Math.Max(MaximumInputCapacity, _input.Length);
            }
            if (_offset != 0)
            {
                _input.AsSpan(_offset, _length).CopyTo(_input);
                _offset = 0;
            }
            var available = Math.Min(SpikeContract.InputWindowBytes, _input.Length - _length);
            if (_metadata)
                available = (int)Math.Min(available, SpikeContract.MaximumMetadataBytes - _metadataBytes - _length + 1);
            var read = await stream.ReadAsync(_input.AsMemory(_length, available), token).ConfigureAwait(false);
            _length += read;
            _eof = read == 0;
        }
    }

    private bool ParseToken()
    {
        try
        {
            var reader = new Utf8JsonReader(_input.AsSpan(_offset, _length), _eof, _state);
            var success = reader.Read();
            var consumed = checked((int)reader.BytesConsumed);
            if (_metadata)
            {
                _metadataBytes += consumed;
                if (_metadataBytes > SpikeContract.MaximumMetadataBytes) throw new ProtocolException(ProtocolReason.MetadataLimit);
            }
            _capture?.Append(_input.AsSpan(_offset, consumed));
            if (success)
            {
                _kind = reader.TokenType;
                // Read validates JSON syntax, not the UTF-8 inside string tokens.
                // Validate every string/property, including nested dynamic values,
                // without decoding/re-serialising their preserved lexical bytes.
                if (_kind is JsonTokenType.String or JsonTokenType.PropertyName)
                    _ = StrictUtf8.GetCharCount(reader.ValueSpan);
                if (_kind is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    if (++_depth > 64) throw new ProtocolException(ProtocolReason.DepthLimit);
                }
                else if (_kind is JsonTokenType.EndArray or JsonTokenType.EndObject) _depth--;
                var start = checked((int)reader.TokenStartIndex);
                var size = consumed - start;
                if (!_metadata && size > SpikeContract.MaximumCellBytes) throw new ProtocolException(ProtocolReason.CellLimit);
                _token = _input.AsSpan(_offset + start, size).ToArray();
            }
            _state = reader.CurrentState;
            _offset += consumed;
            _length -= consumed;
            return success;
        }
        catch (JsonException) { throw new ProtocolException(ProtocolReason.InvalidJson); }
        catch (DecoderFallbackException) { throw new ProtocolException(ProtocolReason.InvalidJson); }
    }

    public void Dispose() { _input = []; _token = []; _capture = null; }

    private sealed class BoundedBytes(int maximum, ProtocolReason reason)
    {
        private byte[] _bytes = [];
        private int _count;
        internal void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > maximum - _count) throw new ProtocolException(reason);
            var required = _count + bytes.Length;
            if (required > _bytes.Length)
                Array.Resize(ref _bytes, Math.Min(maximum, Math.Max(required, Math.Max(256, _bytes.Length * 2))));
            bytes.CopyTo(_bytes.AsSpan(_count));
            _count = required;
        }
        internal byte[] ToArray() => _bytes.AsSpan(0, _count).ToArray();
    }
}
