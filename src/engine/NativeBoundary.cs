using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Azure.Identity;

namespace Eventhouse.Engine.Spike;

internal static unsafe class NativeBoundary
{
    internal static uint AbiVersion() => SpikeContract.AbiVersion;

    internal static SpikeStatus Open(
        byte* queryUri, nuint queryUriLength,
        byte* database, nuint databaseLength,
        byte* tenantId, nuint tenantIdLength,
        byte* clientId, nuint clientIdLength,
        byte* clientSecret, nuint clientSecretLength,
        uint timeoutMilliseconds,
        ulong* connectionHandle)
    {
        Diagnostics.Clear();
        try
        {
            if (connectionHandle is null)
            {
                return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output handle is required.");
            }

            *connectionHandle = 0;
            var queryUriText = ReadUtf8(queryUri, queryUriLength, 512);
            var databaseText = ReadUtf8(database, databaseLength, 256);
            var tenantText = ReadUtf8(tenantId, tenantIdLength, 64);
            var clientText = ReadUtf8(clientId, clientIdLength, 64);
            var secretText = ReadUtf8(clientSecret, clientSecretLength, 4096);
            if (!FabricEndpoint.TryParse(queryUriText, out var queryOrigin) || string.IsNullOrWhiteSpace(databaseText) ||
                !Guid.TryParse(tenantText, out var tenant) || tenant == Guid.Empty ||
                !Guid.TryParse(clientText, out var client) || client == Guid.Empty ||
                string.IsNullOrEmpty(secretText) || timeoutMilliseconds > 600_000)
            {
                return Diagnostics.Set(SpikeStatus.InvalidArgument, "Configuration is invalid.");
            }

            var connection = new EngineConnection(queryOrigin!, databaseText, tenantText, clientText, secretText, timeoutMilliseconds);
            try { *connectionHandle = Handles.AddConnection(connection); }
            catch { connection.Dispose(); throw; }
            return SpikeStatus.Ok;
        }
        catch (ArgumentException)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Configuration is invalid.");
        }
        catch
        {
            return Diagnostics.Set(SpikeStatus.InternalError, "Connection creation failed.");
        }
    }

    internal static SpikeStatus Execute(ulong connectionHandle, byte* sql, nuint sqlLength, ulong* resultHandle)
    {
#if EVENTHOUSE_PRODUCT
        Diagnostics.Clear();
        if (resultHandle is null) return OperationStatus(SpikeStatus.InvalidArgument);
        *resultHandle = 0;
        var admission = Handles.ReserveConnection(connectionHandle, out var connection);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        var operation = new EngineOperation(connection!, connection!.DefaultTimeout, () => Handles.ReleaseConnection(connectionHandle));
        try
        {
            operation.Begin();
            var status = ExecuteCore(connection, sql, sqlLength, operation, out var result, privateOperation: true);
            if (status == SpikeStatus.Ok)
            {
                status = operation.Publish((StreamingResult)result!, out var published);
                *resultHandle = published;
            }
            if (*resultHandle == 0) operation.Dispose();
            return status == SpikeStatus.Timeout ? OperationStatus(SpikeStatus.Cancelled) : status;
        }
        catch { operation.Dispose(); return OperationStatus(SpikeStatus.InternalError); }
#else
        Diagnostics.Clear();
        if (resultHandle is null)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output handle is required.");
        }

        *resultHandle = 0;
        var admission = Handles.ReserveConnection(connectionHandle, out var connection);
        if (admission != SpikeStatus.Ok)
        {
            return OperationStatus(admission);
        }

        try
        {
            var status = ExecuteCore(connection!, sql, sqlLength, null, out var result);
            if (status == SpikeStatus.Ok)
            {
                try { *resultHandle = Handles.AddResult(result!); }
                catch { result!.Dispose(); throw; }
            }
            return status;
        }
        finally { Handles.ReleaseConnection(connectionHandle); }
#endif
    }

    private static SpikeStatus ExecuteCore(EngineConnection connection, byte* sql, nuint sqlLength,
        EngineOperation? operation, out QueryResult? result, bool privateOperation = false)
    {
        result = null;
        try
        {
            var query = ReadBytes(sql, sqlLength, SpikeContract.MaximumQueryBytes);
            if (query.IsEmpty)
            {
                return Diagnostics.Set(SpikeStatus.InvalidArgument, "Query is invalid.");
            }

#if EVENTHOUSE_PRODUCT
            result = connection.ExecuteStreaming(query, operation!, privateOperation);
#else
            result = operation is null ? connection.Execute(query) : connection.Execute(query, operation.Token);
#endif
            return SpikeStatus.Ok;
        }
        catch (OperationCanceledException)
        {
            return Diagnostics.Set(SpikeStatus.Cancelled, "Query was cancelled or timed out.");
        }
        catch (AuthenticationFailedException)
        {
            return Diagnostics.Set(SpikeStatus.AuthenticationFailed, "Authentication failed.");
        }
        catch (AuthenticationException)
        {
            return Diagnostics.Set(SpikeStatus.AuthenticationFailed, "Authentication metadata was rejected.");
        }
        catch (EngineTransportException exception)
        {
            return Diagnostics.Set(SpikeStatus.TransportFailed, string.Concat(
                "transport_stage=", TransportStageToken(exception.Stage),
                ";transport_reason=", TransportReasonToken(exception.Reason)));
        }
        catch (HttpRequestException)
        {
            return Diagnostics.Set(SpikeStatus.TransportFailed, "transport_stage=unknown;transport_reason=unknown");
        }
        catch (QueryHttpException exception)
        {
            return exception.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest => Diagnostics.Set(SpikeStatus.QueryRejected, "Eventhouse rejected the query request."),
                System.Net.HttpStatusCode.Unauthorized => Diagnostics.Set(SpikeStatus.AuthenticationFailed, "Query authentication failed."),
                System.Net.HttpStatusCode.Forbidden => Diagnostics.Set(SpikeStatus.PermissionDenied, "Query permission was denied."),
                _ => Diagnostics.Set(SpikeStatus.HttpFailed, "Eventhouse query HTTP request failed.")
            };
        }
        catch (ProtocolException exception)
        {
            return ProtocolFailure(exception.Reason, exception.UnexpectedToken);
        }
        catch (JsonException)
        {
            return ProtocolFailure(ProtocolReason.InvalidJson);
        }
        catch (ArgumentException)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Query is invalid.");
        }
        catch
        {
            return Diagnostics.Set(SpikeStatus.InternalError, "Query execution failed.");
        }
    }

    internal static SpikeStatus PrepareOperation(ulong connectionHandle, uint timeoutMilliseconds, ulong* operationHandle)
    {
        Diagnostics.Clear();
        if (operationHandle is null) return OperationStatus(SpikeStatus.InvalidArgument);
        *operationHandle = 0;
        if (timeoutMilliseconds > 600_000) return OperationStatus(SpikeStatus.InvalidArgument);
        var admission = Handles.ReserveConnection(connectionHandle, out var connection);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        EngineOperation? operation = null;
        try
        {
            operation = new EngineOperation(connection!, timeoutMilliseconds == 0 ? connection!.DefaultTimeout :
                TimeSpan.FromMilliseconds(timeoutMilliseconds), () => Handles.ReleaseConnection(connectionHandle));
            *operationHandle = Handles.AddOperation(operation);
            return SpikeStatus.Ok;
        }
        catch
        {
            if (operation is null) Handles.ReleaseConnection(connectionHandle);
            else operation.Dispose();
            return OperationStatus(SpikeStatus.InternalError);
        }
    }

    internal static SpikeStatus ExecuteOperation(ulong operationHandle, byte* sql, nuint sqlLength, ulong* resultHandle)
    {
        Diagnostics.Clear();
        if (resultHandle is null) return OperationStatus(SpikeStatus.InvalidArgument);
        *resultHandle = 0;
        var operation = Handles.AcquireOperation(operationHandle);
        if (operation is null) return OperationStatus(SpikeStatus.InvalidHandle);
        try
        {
            var admission = operation.Begin();
            if (admission != SpikeStatus.Ok) return OperationStatus(admission);
            var status = ExecuteCore(operation.Connection, sql, sqlLength, operation, out var result);
            ulong published;
            status = result is StreamingResult cursor ? operation.Publish(cursor, out published) : operation.Complete(status, result, out published);
            *resultHandle = published;
            // Preserve detailed sanitised execution diagnostics unless a competing
            // terminal decision changed the outcome.
            return status == Diagnostics.Code ? status : OperationStatus(status);
        }
        catch { return OperationStatus(operation.Complete(SpikeStatus.InternalError, null, out _)); }
        finally { operation.Release(); }
    }

    internal static SpikeStatus CancelOperation(ulong handle)
    {
        Diagnostics.Clear();
        var operation = Handles.AcquireOperation(handle);
        if (operation is null) return OperationStatus(SpikeStatus.InvalidHandle);
        try { operation.Cancel(); return SpikeStatus.Ok; }
        finally { operation.Release(); }
    }

    internal static SpikeStatus CloseOperation(ulong handle)
    {
        Diagnostics.Clear();
        var operation = Handles.AcquireOperation(handle, remove: true);
        if (operation is null) return OperationStatus(SpikeStatus.InvalidHandle);
        try { operation.Dispose(); return SpikeStatus.Ok; }
        finally { operation.Release(); }
    }

    private static SpikeStatus OperationStatus(SpikeStatus status) => status == SpikeStatus.Ok
        ? ClearOperationStatus() : Diagnostics.Set(status, status switch
        {
            SpikeStatus.InvalidArgument => "Operation argument is invalid.",
            SpikeStatus.InvalidHandle => "Handle is invalid.",
            SpikeStatus.Cancelled => "Operation was cancelled.",
            SpikeStatus.Timeout => "Operation timed out.",
            SpikeStatus.InvalidState => "Operation has already executed.",
            SpikeStatus.Busy => "Connection is busy.",
            _ => "Operation failed."
        });

    private static SpikeStatus ClearOperationStatus() { Diagnostics.Clear(); return SpikeStatus.Ok; }

    private static SpikeStatus ProtocolFailure(ProtocolReason reason, JsonTokenType? unexpectedToken = null) =>
        Diagnostics.Set(SpikeStatus.ProtocolFailed, string.Concat("protocol_reason=", reason switch
        {
            ProtocolReason.ContentType => "content_type",
            ProtocolReason.EmptyResponse => "empty_response",
            ProtocolReason.SizeHeader => "size_header",
            ProtocolReason.SizeRead => "size_read",
            ProtocolReason.SizeParse => "size_parse",
            ProtocolReason.InvalidJson => "invalid_json",
            ProtocolReason.Root => "root",
            ProtocolReason.Table => "table",
            ProtocolReason.Columns => "columns",
            ProtocolReason.Rows => "rows",
            ProtocolReason.DuplicatePrimary => "duplicate_primary",
            ProtocolReason.MissingPrimary => "missing_primary",
            ProtocolReason.UnknownMetadataName => "unknown_metadata_name",
            ProtocolReason.UnknownMetadataSchema => "unknown_metadata_schema",
            ProtocolReason.EmptyQueryStatus => "empty_query_status",
            ProtocolReason.FailedQueryStatus => "failed_query_status",
            ProtocolReason.HeaderOrder => "header_order",
            ProtocolReason.CellLimit => "cell_limit",
            ProtocolReason.RowLimit => "row_limit",
            ProtocolReason.MetadataLimit => "metadata_limit",
            ProtocolReason.ColumnLimit => "column_limit",
            ProtocolReason.DepthLimit => "depth_limit",
            ProtocolReason.CountLimit => "count_limit",
            ProtocolReason.PrimaryStart => "primary_start",
            ProtocolReason.RowWidthExcess => "row_width_excess",
            ProtocolReason.RowWidthShort => "row_width_short",
            ProtocolReason.AuxiliaryRowStart => "aux_row_start",
            ProtocolReason.CellToken => "cell_token",
            ProtocolReason.UnknownRowObject => "unknown_row_object",
            ProtocolReason.InlineServiceError => "inline_service_error;service_category=other",
            ProtocolReason.InlineServiceTimeout => "inline_service_error;service_category=service_timeout",
            ProtocolReason.InlineResourceLimit => "inline_service_error;service_category=resource_limit",
            ProtocolReason.InlineResultTruncated => "inline_service_error;service_category=result_truncated",
            _ => "unknown"
        }, unexpectedToken.HasValue ? string.Concat(";unexpected_token=", unexpectedToken.Value switch
        {
            JsonTokenType.None => "none",
            JsonTokenType.StartObject => "start_object",
            JsonTokenType.EndObject => "end_object",
            JsonTokenType.StartArray => "start_array",
            JsonTokenType.EndArray => "end_array",
            JsonTokenType.PropertyName => "property_name",
            JsonTokenType.Comment => "comment",
            JsonTokenType.String => "string",
            JsonTokenType.Number => "number",
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => "null",
            _ => "unknown"
        }) : string.Empty));

    private static string TransportStageToken(TransportStage stage) => stage switch
    {
        TransportStage.Metadata => "metadata",
        TransportStage.Token => "token",
        TransportStage.Query => "query",
        _ => "unknown"
    };

    private static string TransportReasonToken(TransportReason reason) => reason switch
    {
        TransportReason.Configuration => "configuration",
        TransportReason.NameResolution => "name_resolution",
        TransportReason.Connection => "connection",
        TransportReason.SecureConnection => "secure_connection",
        TransportReason.HttpProtocol => "http_protocol",
        TransportReason.ExtendedConnect => "extended_connect",
        TransportReason.VersionNegotiation => "version_negotiation",
        TransportReason.UserAuthentication => "user_authentication",
        TransportReason.ProxyTunnel => "proxy_tunnel",
        TransportReason.InvalidResponse => "invalid_response",
        TransportReason.ResponseEnded => "response_ended",
        _ => "unknown"
    };

    internal static SpikeStatus ResultShape(ulong resultHandle, nuint* columnCount, nuint* rowCount)
    {
        Diagnostics.Clear();
        if (columnCount is null || rowCount is null)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output values are required.");
        }

        *columnCount = 0;
        *rowCount = 0;
        var admission = Handles.AcquireResult(resultHandle, out var result);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        try
        {
            *columnCount = (nuint)result!.Columns.Count;
            *rowCount = result is StreamingResult cursor ? cursor.RowCount : (nuint)result.Rows.Count;
            return SpikeStatus.Ok;
        }
        finally { (result as StreamingResult)?.ReleaseData(); }
    }

    internal static SpikeStatus Column(
        ulong resultHandle, nuint columnIndex, uint* type, byte* nullable,
        byte* nameBuffer, nuint nameCapacity, nuint* nameRequired)
    {
        Diagnostics.Clear();
        if (type is null || nullable is null || nameRequired is null)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output values are required.");
        }

        *type = 0;
        *nullable = 0;
        *nameRequired = 0;
        var admission = Handles.AcquireResult(resultHandle, out var result);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        try
        {
            if (columnIndex >= (nuint)result!.Columns.Count)
                return Diagnostics.Set(SpikeStatus.InvalidHandle, "Result or column is invalid.");
            var column = result.Columns[(int)columnIndex];
            *type = (uint)column.Type;
            *nullable = column.Nullable ? (byte)1 : (byte)0;
            return Copy(column.Name, nameBuffer, nameCapacity, nameRequired);
        }
        finally { (result as StreamingResult)?.ReleaseData(); }
    }

    internal static SpikeStatus Cell(
        ulong resultHandle, nuint rowIndex, nuint columnIndex, uint* type, byte* isNull,
        byte* valueBuffer, nuint valueCapacity, nuint* valueRequired)
    {
        Diagnostics.Clear();
        if (type is null || isNull is null || valueRequired is null)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output values are required.");
        }

        *type = 0;
        *isNull = 0;
        *valueRequired = 0;
        var admission = Handles.AcquireResult(resultHandle, out var result);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        try
        {
            if (result is StreamingResult) return OperationStatus(SpikeStatus.InvalidState);
            if (rowIndex >= (nuint)result!.Rows.Count || columnIndex >= (nuint)result.Columns.Count)
                return Diagnostics.Set(SpikeStatus.InvalidHandle, "Result row or column is invalid.");

            var cell = result.Rows[(int)rowIndex][(int)columnIndex];
            *type = (uint)cell.Type;
            *isNull = cell.IsNull ? (byte)1 : (byte)0;
            return Copy(cell.Value, valueBuffer, valueCapacity, valueRequired);
        }
        finally { (result as StreamingResult)?.ReleaseData(); }
    }

    internal static SpikeStatus Next(ulong handle, byte* hasRow)
    {
        Diagnostics.Clear();
        if (hasRow is null) return OperationStatus(SpikeStatus.InvalidArgument);
        *hasRow = 0;
        var admission = Handles.AcquireResult(handle, out var result);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        try
        {
            if (result is not StreamingResult cursor) return OperationStatus(SpikeStatus.InvalidState);
            var status = cursor.Next(out var row);
            *hasRow = row;
            return status == SpikeStatus.ProtocolFailed ? ProtocolFailure(cursor.Reason, cursor.UnexpectedToken) : OperationStatus(status);
        }
        finally { (result as StreamingResult)?.ReleaseData(); }
    }

    internal static SpikeStatus CurrentCell(ulong handle, nuint column, uint* type, byte* isNull,
        byte* buffer, nuint capacity, nuint* required)
    {
        Diagnostics.Clear();
        if (type is null || isNull is null || required is null) return OperationStatus(SpikeStatus.InvalidArgument);
        *type = 0; *isNull = 0; *required = 0;
        var admission = Handles.AcquireResult(handle, out var result);
        if (admission != SpikeStatus.Ok) return OperationStatus(admission);
        try
        {
            if (result is not StreamingResult cursor) return OperationStatus(SpikeStatus.InvalidState);
            if (column >= (nuint)result.Columns.Count) return OperationStatus(SpikeStatus.InvalidHandle);
            var cell = cursor.Current(column);
            if (cell is null)
            {
                // Current and terminal state are read in this order so an idle
                // cancellation which invalidates the row cannot become state11.
                var status = cursor.GetTerminalFailure() ?? SpikeStatus.InvalidState;
                return status == SpikeStatus.ProtocolFailed ? ProtocolFailure(cursor.Reason, cursor.UnexpectedToken) : OperationStatus(status);
            }
            *type = (uint)cell.Type; *isNull = cell.IsNull ? (byte)1 : (byte)0;
            return Copy(cell.Value, buffer, capacity, required);
        }
        finally { (result as StreamingResult)?.ReleaseData(); }
    }

    internal static SpikeStatus CloseConnection(ulong handle)
    {
        Diagnostics.Clear();
        var status = Handles.CloseConnection(handle);
        return status == SpikeStatus.Ok ? status : Diagnostics.Set(status,
            status == SpikeStatus.Busy ? "Connection is busy." : "Connection handle is invalid.");
    }

    internal static SpikeStatus CloseResult(ulong handle)
    {
        Diagnostics.Clear();
        return Handles.RemoveResult(handle)
            ? SpikeStatus.Ok
            : Diagnostics.Set(SpikeStatus.InvalidHandle, "Result handle is invalid.");
    }

    internal static SpikeStatus CopyLastError(byte* buffer, nuint capacity, nuint* required) =>
        Copy(Diagnostics.Message, buffer, capacity, required, preserveDiagnostic: true);

#if WP003_SPIKE_TEST_HOOKS
    internal static SpikeStatus CreateTestResult(ulong* resultHandle)
    {
        Diagnostics.Clear();
        if (resultHandle is null)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Output handle is required.");
        }

        *resultHandle = Handles.AddResult(new QueryResult(
            [
                new ResultColumn("integer"u8.ToArray(), SpikeCellType.Int32, true),
                new ResultColumn("null_text"u8.ToArray(), SpikeCellType.String, true),
                new ResultColumn("empty_text"u8.ToArray(), SpikeCellType.String, true)
            ],
            [
                new List<ResultCell>
                {
                    new(SpikeCellType.Int32, false, "1"u8.ToArray()),
                    new(SpikeCellType.String, true, []),
                    new(SpikeCellType.String, false, [])
                }
            ]));
        return SpikeStatus.Ok;
    }

    internal static SpikeStatus ValidateTestUtf8(uint kind, byte* value, nuint length)
    {
        Diagnostics.Clear();
        try
        {
            var maximum = kind switch
            {
                1 => SpikeContract.MaximumQueryBytes,
                2 => 4096,
                _ => throw new ArgumentException()
            };
            _ = ReadUtf8(value, length, maximum);
            return SpikeStatus.Ok;
        }
        catch (DecoderFallbackException)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Test input is invalid.");
        }
        catch (ArgumentException)
        {
            return Diagnostics.Set(SpikeStatus.InvalidArgument, "Test input is invalid.");
        }
    }

    internal static SpikeStatus ThrowTestException() => throw new TestBoundaryException();
#endif

    private static SpikeStatus Copy(ReadOnlySpan<byte> source, byte* buffer, nuint capacity, nuint* required, bool preserveDiagnostic = false)
    {
        if (required is null)
        {
            return preserveDiagnostic ? SpikeStatus.InvalidArgument : Diagnostics.Set(SpikeStatus.InvalidArgument, "Required length output is missing.");
        }

        *required = (nuint)source.Length;
        if (source.IsEmpty)
        {
            return SpikeStatus.Ok;
        }

        if (buffer is null || capacity < (nuint)source.Length)
        {
            return SpikeStatus.BufferTooSmall;
        }

        source.CopyTo(new Span<byte>(buffer, source.Length));
        return SpikeStatus.Ok;
    }

    private static ReadOnlySpan<byte> ReadBytes(byte* pointer, nuint length, int maximumLength)
    {
        if (pointer is null || length == 0 || length > (nuint)maximumLength || length > int.MaxValue)
        {
            throw new ArgumentException();
        }

        return new ReadOnlySpan<byte>(pointer, (int)length);
    }

    private static string ReadUtf8(byte* pointer, nuint length, int maximumLength)
    {
        var bytes = ReadBytes(pointer, length, maximumLength);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

}

#if WP003_SPIKE_TEST_HOOKS
internal sealed class TestBoundaryException : Exception;
#endif
