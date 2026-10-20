using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Eventhouse.Engine.Spike;

internal sealed record ResultColumn(byte[] Name, SpikeCellType Type, bool Nullable);

internal sealed record ResultCell(SpikeCellType Type, bool IsNull, byte[] Value);

internal class QueryResult(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<ResultCell>> rows) : IDisposable
{
    private int _disposed;
    internal IReadOnlyList<ResultColumn> Columns { get; } = columns;
    internal IReadOnlyList<IReadOnlyList<ResultCell>> Rows { get; } = rows;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public virtual void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

internal static class RestProtocol
{
    internal static readonly string[][] MetadataColumnSets =
    [
        ["Value"],
        ["Timestamp", "Severity", "SeverityName", "StatusCode", "StatusDescription", "Count", "RequestId", "ActivityId", "SubActivityId", "ClientActivityId"],
        ["Ordinal", "Kind", "Name", "Id", "PrettyName"]
    ];

    internal static byte[] BuildRequest(string database, ReadOnlySpan<byte> sql)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("db", database);
        writer.WriteString("csl", sql);
        // Query limits: notruncation is ignored when conflicting truncation
        // options are supplied. Keep the SQL untouched and local parser guards.
        // https://learn.microsoft.com/en-us/kusto/concepts/query-limits
        writer.WriteString("properties", "{\"Options\":{\"query_language\":\"sql\",\"request_readonly\":true,\"request_readonly_hardline\":true,\"notruncation\":true}}");
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    internal static QueryResult ParseResponse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new ProtocolException(ProtocolReason.EmptyResponse);
        }

        if (utf8Json.Length > SpikeContract.MaximumResponseBytes)
        {
            throw new ProtocolException(ProtocolReason.SizeParse);
        }

        using var document = ParseJson(utf8Json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("Tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
        {
            throw new ProtocolException(ProtocolReason.Root);
        }

        JsonElement? primary = null;
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object || table.EnumerateObject().Count() != 3 ||
                !table.TryGetProperty("TableName", out var tableName) || tableName.ValueKind != JsonValueKind.String ||
                !table.TryGetProperty("Columns", out var columns) || columns.ValueKind != JsonValueKind.Array ||
                !table.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException(ProtocolReason.Table);
            }

            ValidateColumns(columns);
            ValidateRows(columns, rows);
            var name = tableName.GetString();
            if (name == "Table_0")
            {
                if (primary is not null)
                {
                    throw new ProtocolException(ProtocolReason.DuplicatePrimary);
                }

                primary = table;
            }
            else
            {
                if (!IsMetadataTableName(name))
                {
                    throw new ProtocolException(ProtocolReason.UnknownMetadataName);
                }

                var metadataKind = GetMetadataKind(columns);
                if (metadataKind < 0)
                {
                    throw new ProtocolException(ProtocolReason.UnknownMetadataSchema);
                }

                if (metadataKind == 1)
                {
                    ValidateQueryStatus(columns, rows);
                }
            }
        }

        return primary is JsonElement primaryTable ? ParsePrimary(primaryTable) : throw new ProtocolException(ProtocolReason.MissingPrimary);
    }

    private static JsonDocument ParseJson(ReadOnlySpan<byte> utf8Json)
    {
        try { return JsonDocument.Parse(utf8Json.ToArray()); }
        catch (JsonException)
        {
            // Do not retain parser messages, paths or response bytes in an inner exception.
            throw new ProtocolException(ProtocolReason.InvalidJson);
        }
    }

    private static QueryResult ParsePrimary(JsonElement table)
    {
        var columnsElement = table.GetProperty("Columns");
        var columns = new List<ResultColumn>(columnsElement.GetArrayLength());
        foreach (var column in columnsElement.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object ||
                !column.TryGetProperty("ColumnName", out var name) || name.ValueKind != JsonValueKind.String ||
                !column.TryGetProperty("DataType", out var dataType) || dataType.ValueKind != JsonValueKind.String ||
                !column.TryGetProperty("ColumnType", out var columnType) || columnType.ValueKind != JsonValueKind.String)
            {
                throw new ProtocolException(ProtocolReason.Columns);
            }

            var nameText = name.GetString();
            var dataTypeText = dataType.GetString();
            var columnTypeText = columnType.GetString();
            if (string.IsNullOrWhiteSpace(nameText) || string.IsNullOrWhiteSpace(dataTypeText) || string.IsNullOrWhiteSpace(columnTypeText))
            {
                throw new ProtocolException(ProtocolReason.Columns);
            }

            columns.Add(new ResultColumn(Encoding.UTF8.GetBytes(nameText), MapType(dataTypeText, columnTypeText), true));
        }

        var rows = new List<IReadOnlyList<ResultCell>>();
        foreach (var row in table.GetProperty("Rows").EnumerateArray())
        {
            var cells = new List<ResultCell>(columns.Count);
            var index = 0;
            foreach (var value in row.EnumerateArray())
            {
                cells.Add(ParseCell(columns[index++].Type, value));
            }

            rows.Add(cells);
        }

        return new QueryResult(columns, rows);
    }

    private static ResultCell ParseCell(SpikeCellType type, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new ResultCell(type, true, []);
        }

        // Dynamic strings retain their JSON quotes/escapes, just like every other
        // dynamic kind retains its lexical representation. Other strings decode.
        byte[] bytes = type != SpikeCellType.Dynamic && value.ValueKind == JsonValueKind.String
            ? Encoding.UTF8.GetBytes(value.GetString() ?? string.Empty)
            : Encoding.UTF8.GetBytes(value.GetRawText());
        return new ResultCell(type, false, bytes);
    }

    private static void ValidateRows(JsonElement columns, JsonElement rows)
    {
        var width = columns.GetArrayLength();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != width)
            {
                throw new ProtocolException(ProtocolReason.Rows);
            }
        }
    }

    private static void ValidateColumns(JsonElement columns)
    {
        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object || column.EnumerateObject().Count() != 3 ||
                !column.TryGetProperty("ColumnName", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !column.TryGetProperty("DataType", out var dataType) || dataType.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dataType.GetString()) ||
                !column.TryGetProperty("ColumnType", out var columnType) || columnType.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(columnType.GetString()))
            {
                throw new ProtocolException(ProtocolReason.Columns);
            }
        }
    }

    private static int GetMetadataKind(JsonElement columns)
    {
        var names = columns.EnumerateArray().Select(static column => column.GetProperty("ColumnName").GetString()).ToArray();
        for (var index = 0; index < MetadataColumnSets.Length; index++)
        {
            if (names.SequenceEqual(MetadataColumnSets[index], StringComparer.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal static bool IsMetadataTableName(string? name)
    {
        const string prefix = "Table_";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name.AsSpan(prefix.Length);
        if (suffix.IsEmpty || suffix[0] == '0')
        {
            return false;
        }

        foreach (var character in suffix)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateQueryStatus(JsonElement columns, JsonElement rows)
    {
        var names = columns.EnumerateArray().Select(static column => column.GetProperty("ColumnName").GetString()).ToArray();
        var severityIndex = Array.IndexOf(names, "Severity");
        var statusIndex = Array.IndexOf(names, "StatusCode");
        if (rows.GetArrayLength() == 0)
        {
            throw new ProtocolException(ProtocolReason.EmptyQueryStatus);
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (!row[severityIndex].TryGetInt64(out var severity) || severity <= 2 ||
                !row[statusIndex].TryGetInt64(out var status) || status != 0)
            {
                throw new ProtocolException(ProtocolReason.FailedQueryStatus);
            }
        }
    }

    internal static SpikeCellType MapType(string dataType, string columnType)
    {
        var value = columnType.ToLowerInvariant();
        return value switch
        {
            "bool" or "boolean" => SpikeCellType.Boolean,
            "int" or "int32" => SpikeCellType.Int32,
            "long" or "int64" => SpikeCellType.Int64,
            "real" or "double" => SpikeCellType.Double,
            "decimal" => SpikeCellType.Decimal,
            "datetime" => SpikeCellType.DateTime,
            "timespan" => SpikeCellType.TimeSpan,
            "guid" => SpikeCellType.Guid,
            "string" => SpikeCellType.String,
            "dynamic" => SpikeCellType.Dynamic,
            _ => dataType.ToLowerInvariant() switch
            {
                "int32" => SpikeCellType.Int32,
                "int64" => SpikeCellType.Int64,
                "string" => SpikeCellType.String,
                _ => SpikeCellType.Unknown
            }
        };
    }
}

internal enum ProtocolReason
{
    Unknown,
    ContentType,
    EmptyResponse,
    SizeHeader,
    SizeRead,
    SizeParse,
    InvalidJson,
    Root,
    Table,
    Columns,
    Rows,
    DuplicatePrimary,
    MissingPrimary,
    UnknownMetadataName,
    UnknownMetadataSchema,
    EmptyQueryStatus,
    FailedQueryStatus,
    HeaderOrder,
    CellLimit,
    RowLimit,
    MetadataLimit,
    ColumnLimit,
    DepthLimit,
    CountLimit,
    PrimaryStart,
    RowWidthExcess,
    RowWidthShort,
    AuxiliaryRowStart,
    CellToken,
    UnknownRowObject,
    InlineServiceError,
    InlineServiceTimeout,
    InlineResourceLimit,
    InlineResultTruncated
}

internal sealed class ProtocolException(ProtocolReason reason = ProtocolReason.Unknown, JsonTokenType? unexpectedToken = null)
    : Exception("Eventhouse response contract was rejected.")
{
    internal ProtocolReason Reason { get; } = reason;
    internal JsonTokenType? UnexpectedToken { get; } = unexpectedToken;
}
