using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Eventhouse.Engine.Spike;

var tests = new (string Name, Action Run)[]
{
    ("profile ABI version is explicit", () => Equal(
        typeof(RestProtocol).Assembly.GetName().Name == "eventhouse-engine" ? 0x0001_0003U : 0x0001_0000U,
        NativeBoundary.AbiVersion())),
    ("request fixes SQL and read-only properties", RequestIsSqlAndReadOnly),
    ("typed/null fixture parses independently", TypedFixtureParses),
    ("draft representation preserves JSON lexical values", DraftRepresentationParses),
    ("one-api error fixture is rejected", () => Rejects("oneapi-error.json")),
    ("failed query-status fixture is rejected", () => Rejects("failed-query-status.json")),
    ("malformed fixture is rejected", () => Rejects("malformed.json")),
    ("wrong row width is rejected", () => Rejects("wrong-row-width.json")),
    ("extra root property is rejected", () => Rejects("extra-root-property.json")),
    ("extra table property is rejected", () => Rejects("extra-table-property.json")),
    ("extra column property is rejected", () => Rejects("extra-column-property.json")),
    ("unknown metadata table is rejected", () => Rejects("unknown-metadata-table.json")),
    ("invalid metadata table name is rejected", () => Rejects("invalid-metadata-table-name.json")),
    ("empty query status is rejected", () => Rejects("empty-query-status.json")),
    ("metadata row width is rejected", () => Rejects("metadata-row-width.json")),
    ("JWT audience must match exactly", JwtAudienceMustMatchExactly),
    ("both HTTP handlers disable redirects", RedirectsAreDisabled),
    ("metadata content type fails before token and query", MetadataContentTypeFailsClosed),
    ("malformed metadata fails before token and query", MalformedMetadataFailsClosed),
    ("metadata redirect fails before token and query", MetadataRedirectFailsClosed),
    ("metadata resource mismatch dispatches no token or query", MetadataMismatchStopsDispatch),
    ("token audience mismatch dispatches no query", AudienceMismatchStopsQuery),
    ("Eventhouse redirect is not followed", EventhouseRedirectIsNotFollowed),
    ("metadata transport failure is classified", () => TransportFailureIsClassified(
        new SequenceHandler(new HttpRequestException(HttpRequestError.NameResolutionError)),
        new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "metadata", "name_resolution")),
    ("nested token transport failure is classified", () => TransportFailureIsClassified(
        new SequenceHandler(MetadataResponse()),
        new FailingTokenProvider(new AuthenticationFailedException(string.Empty,
            new HttpRequestException(HttpRequestError.SecureConnectionError))), "token", "secure_connection")),
    ("aggregate nested token transport failure is classified", () => TransportFailureIsClassified(
        new SequenceHandler(MetadataResponse()),
        new FailingTokenProvider(new AuthenticationFailedException(string.Empty,
            new AggregateException(new InvalidOperationException(null,
                new HttpRequestException(HttpRequestError.ConnectionError))))), "token", "connection")),
    ("query transport failure is classified", () => TransportFailureIsClassified(
        new SequenceHandler(MetadataResponse(), new HttpRequestException(HttpRequestError.HttpProtocolError)),
        new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "query", "http_protocol")),
    ("unknown transport reason falls back safely", () => TransportFailureIsClassified(
        new SequenceHandler(new HttpRequestException((HttpRequestError)int.MaxValue)),
        new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "metadata", "unknown")),
    ("handle type confusion is rejected", HandleTypeConfusion),
    ("stale result handle is rejected", StaleResultHandle),
    ("double close is rejected safely", DoubleClose),
    ("buffer sizing does not overrun", BufferSizing),
    ("null native outputs are rejected", NullOutputs),
    ("hostile endpoint is rejected before network", HostileEndpointRejected),
    ("error text is fixed and bounded", ErrorTextIsBounded),
    ("operation prepare reserves connection and inherits timeout", OperationReservation),
    ("operation arguments and handle kinds reject safely", OperationArguments),
    ("prepared cancellation dispatches nothing and consumes attempt", PreparedCancellation),
    ("invalid operation SQL consumes attempt", InvalidOperationSql),
    ("cancel during metadata", () => CancelAtStage(1, false)),
    ("cancel during token", () => CancelAtStage(0, false)),
    ("cancel during response read", () => CancelAtStage(2, false)),
    ("timeout during metadata", () => CancelAtStage(1, true)),
    ("timeout during token", () => CancelAtStage(0, true)),
    ("timeout during response read", () => CancelAtStage(2, true)),
    ("operation close suppresses in-flight result and retains leases", CloseDuringExecution),
    ("operation success transfers result across close", SuccessfulOperation),
    ("cancel first remains immutable after deadline", CancelBeforeDeadline),
    ("deadline first remains timeout after cancel", DeadlineBeforeCancel),
    ("cancel and close before commit dispose unpublished result", UnpublishedResult),
    ("delayed cancel A cannot affect B", DelayedCancel),
    ("legacy execution reserves connection until finish", LegacyReservation),
    ("operation close before execution rejects stale token", CloseBeforeExecute),
    ("admitted signal lease prevents premature connection disposal", SignalLease),
    ("token removal already forbids result commit", RemovalForbidsCommit),
    ("commit checks expired deadline independently of timer delivery", CommitChecksDeadline),
    ("explicit operation timeout overrides connection ceiling", TimeoutOverride),
    ("success before cancellation remains immutable", SuccessBeforeCancel),
    ("Fabric raw origins validate before URI normalisation", FabricOrigins),
    ("native open retains configured normalised origin", ConfiguredNativeOrigin),
    ("configured origin carries metadata and query with fixed auth and SQL properties", ConfiguredRequests),
    ("metadata HTTP failures retain authentication status on both execute paths", MetadataHttpStatuses),
    ("query HTTP failures use safe stage-specific statuses on both execute paths", QueryHttpStatuses),
    ("wrong-resource metadata remains authentication failure on both paths", MetadataResourceBothPaths),
    ("token authentication failures remain sanitised on both paths", TokenAuthenticationBothPaths),
    ("malformed metadata object kinds reject authentication on both paths", MetadataObjectKindsBothPaths),
    ("non-dynamic JSON strings retain decoded typed cell bytes", TypedJsonKindBytes),
    ("dynamic objects retain lexical bytes and key order", () => DynamicLexicalBytes("""{ "b": 2, "a":1, "b":3 }""")),
    ("dynamic arrays retain lexical bytes and nested kinds", () => DynamicLexicalBytes("""[1, {"x":"y"}, true, null, [2,3]]""")),
    ("dynamic scalar strings retain quotes and escapes", () => DynamicLexicalBytes("\"quote:\\\" slash:\\/ backslash:\\\\ newline:\\n tab:\\t\"")),
    ("dynamic Unicode retains literal UTF8 and lexical escapes", () => DynamicLexicalBytes("\"é雪😀\\u00e9\\u96EA\\uD83D\\uDE00\\u0000\"")),
    ("dynamic empty string retains JSON quotes", () => DynamicLexicalBytes("\"\"")),
    ("dynamic numbers retain large precision and exponent spelling", DynamicNumericBytes),
    ("dynamic booleans retain JSON bytes", () => { DynamicLexicalBytes("true"); DynamicLexicalBytes("false"); }),
    ("dynamic SQL null differs from JSON string null", () => { DynamicLexicalBytes("null", true); DynamicLexicalBytes("\"null\""); }),
    ("other native types retain existing JSON kind representations", NonDynamicRepresentations),
    ("protocol fixture reasons are exact and sanitised", ProtocolFixtureReasons),
    ("protocol parse bounds and invalid JSON have fixed reasons", ProtocolParseReasons),
    ("protocol diagnostics survive both execute paths and native error reads", ProtocolDiagnosticsBothPaths),
    ("protocol default and unknown reasons fail closed", ProtocolReasonFallback)
};

if (args.Contains("--streaming", StringComparer.Ordinal))
    tests =
    [
        ("cursor split reads preserve dynamic bytes", CursorSplitReads),
        ("cursor consumes generated response beyond old cap", CursorLargeResponse),
        ("cursor late protocol failures never become EOF", CursorLateFailures),
        ("cursor chosen guards reject excess", CursorBounds),
        ("cursor EOF releases lease while result metadata remains", CursorEofOwnership),
        ("cursor idle cancellation detaches and recovers", () => CursorIdle(false)),
        ("cursor idle timeout detaches and recovers", () => CursorIdle(true)),
        ("cursor close races admitted fetch safely", CursorCloseFetch),
        ("cursor cancelled disposal retains reservation after token close until drain", CursorRetiredDisposalDrain),
        ("cursor row metadata and column boundaries", CursorAggregateBounds),
        ("cursor auxiliary schemas order and successful status", CursorAuxiliary),
        ("cursor fetch cancellation timeout and operation close", CursorFetchTermination),
        ("cursor physical EOF and transport interruption", CursorPhysicalEof),
        ("cursor parser capacities and nested spill remain bounded", CursorParserStorage),
        ("cursor rejects invalid UTF8 strings and nested property names", CursorInvalidUtf8),
        ("cursor metadata budget survives primary token spill", CursorMetadataAfterSpill),
        ("cursor terminal getters preserve cancel timeout and protocol status", CursorTerminalGetters),
        ("cursor row rejection diagnostics are closed and latched", CursorRowDiagnostics),
        ("cursor inline service objects fail with closed categories", CursorInlineServiceErrors),
        ("complete-result request preserves SQL and excludes overriding options", RequestCompleteResults),
        ("inline messages expose only closed machine-code categories", CursorInlineMessageErrors),
        ("product ABI cursor and private operation path", CursorProductBoundary),
        // Security checks execute against the same product assembly as the cursor
        // cases. Handler injection proves dispatch policy, not TLS handshakes.
        ("product security ABI version is explicit", () => Equal(0x00010003U, NativeBoundary.AbiVersion())),
        ("JWT audience must match exactly", JwtAudienceMustMatchExactly),
        ("both HTTP handlers disable redirects", RedirectsAreDisabled),
        ("metadata content type fails before token and query", MetadataContentTypeFailsClosed),
        ("malformed metadata fails before token and query", MalformedMetadataFailsClosed),
        ("metadata redirect fails before token and query", MetadataRedirectFailsClosed),
        ("wrong-resource metadata remains authentication failure on both paths", MetadataResourceBothPaths),
        ("token audience mismatch dispatches no query", AudienceMismatchStopsQuery),
        ("Eventhouse redirect is not followed", EventhouseRedirectIsNotFollowed),
        ("metadata transport failure is classified on both paths", () => TransportFailureBothPaths(
            () => new SequenceHandler(new HttpRequestException(HttpRequestError.NameResolutionError, "SECRET_SENTINEL")),
            () => new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "metadata", "name_resolution")),
        ("nested token transport failure is classified on both paths", () => TransportFailureBothPaths(
            () => new SequenceHandler(MetadataResponse()),
            () => new FailingTokenProvider(new AuthenticationFailedException("SECRET_SENTINEL",
                new HttpRequestException(HttpRequestError.SecureConnectionError))), "token", "secure_connection")),
        ("aggregate token transport failure is classified on both paths", () => TransportFailureBothPaths(
            () => new SequenceHandler(MetadataResponse()),
            () => new FailingTokenProvider(new AuthenticationFailedException("SECRET_SENTINEL",
                new AggregateException(new InvalidOperationException("SECRET_SENTINEL",
                    new HttpRequestException(HttpRequestError.ConnectionError))))), "token", "connection")),
        ("query transport failure is classified on both paths", () => TransportFailureBothPaths(
            () => new SequenceHandler(MetadataResponse(), new HttpRequestException(HttpRequestError.HttpProtocolError, "SECRET_SENTINEL")),
            () => new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "query", "http_protocol")),
        ("unknown transport reason falls back safely on both paths", () => TransportFailureBothPaths(
            () => new SequenceHandler(new HttpRequestException((HttpRequestError)int.MaxValue, "SECRET_SENTINEL")),
            () => new StubTokenProvider(Token(SpikeContract.ApprovedResource)), "metadata", "unknown")),
        ("hostile endpoint is rejected before network", HostileEndpointRejected),
        ("error text is fixed and bounded", ErrorTextIsBounded),
        ("Fabric raw origins validate before URI normalisation", FabricOrigins),
        ("native open retains configured normalised origin", ConfiguredNativeOrigin),
        ("metadata HTTP failures retain authentication status on both execute paths", MetadataHttpStatuses),
        ("query HTTP failures use safe stage-specific statuses on both execute paths", QueryHttpStatuses),
        ("token authentication failures remain sanitised on both paths", TokenAuthenticationBothPaths),
        ("malformed metadata object kinds reject authentication on both paths", MetadataObjectKindsBothPaths)
    ];

if (args.Contains("--streaming", StringComparer.Ordinal) && typeof(RestProtocol).Assembly.GetName().Name != "eventhouse-engine")
    tests = tests.Where(test => test.Name != "product ABI cursor and private operation path").ToArray();

var nativeIndex = Array.IndexOf(args, "--native-streaming");
if (nativeIndex >= 0)
    tests = [("published NativeAOT ABI10003 exports and hostile handle guards", () => NativeStreamingSmoke(args[nativeIndex + 1]))];

var passed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}");
        if (args.Contains("--streaming", StringComparer.Ordinal)) Console.Error.WriteLine(exception.StackTrace);
    }
}

Console.WriteLine($"RESULT pass={passed} fail={tests.Length - passed} skipped=0 blocked=0");
return passed == tests.Length ? 0 : 1;

static void RequestIsSqlAndReadOnly()
{
    var request = RestProtocol.BuildRequest("fixture-db", "SELECT 1"u8);
    using var document = JsonDocument.Parse(request);
    Equal("fixture-db", document.RootElement.GetProperty("db").GetString());
    Equal("SELECT 1", document.RootElement.GetProperty("csl").GetString());
    var properties = document.RootElement.GetProperty("properties").GetString();
    using var nested = JsonDocument.Parse(properties ?? throw new InvalidOperationException());
    var options = nested.RootElement.GetProperty("Options");
    Equal("sql", options.GetProperty("query_language").GetString());
    True(options.GetProperty("request_readonly").GetBoolean());
    True(options.GetProperty("request_readonly_hardline").GetBoolean());
    Equal(JsonValueKind.True, options.GetProperty("notruncation").ValueKind);
}

static void RequestCompleteResults()
{
    foreach (var sql in new[] { "SELECT * FROM transactions", " -- SQL_SENTINEL\nSELECT '雪\\\"', 'notruncation=false'\r\n", "SELECT 1; -- truncationmaxsize=1" })
    {
        var source = Encoding.UTF8.GetBytes(sql);
        var original = source.ToArray();
        using var request = JsonDocument.Parse(RestProtocol.BuildRequest("fixture-db", source));
        True(source.AsSpan().SequenceEqual(original));
        var root = request.RootElement;
        Equal(3, root.EnumerateObject().Count());
        Equal("fixture-db", root.GetProperty("db").GetString());
        Equal(sql, root.GetProperty("csl").GetString());
        Equal(JsonValueKind.String, root.GetProperty("properties").ValueKind);
        using var properties = JsonDocument.Parse(root.GetProperty("properties").GetString()!);
        Equal(1, properties.RootElement.EnumerateObject().Count());
        var options = properties.RootElement.GetProperty("Options");
        Equal(4, options.EnumerateObject().Count());
        Equal("sql", options.GetProperty("query_language").GetString());
        foreach (var name in new[] { "request_readonly", "request_readonly_hardline", "notruncation" })
            Equal(JsonValueKind.True, options.GetProperty(name).ValueKind);
        foreach (var name in new[] { "truncationmaxsize", "truncationmaxrecords", "query_take_max_records" })
            True(!options.TryGetProperty(name, out _));
    }
    ConfiguredRequests(); // Inspect actual dispatch on legacy and operation paths.
}

static void TypedFixtureParses()
{
    using var result = RestProtocol.ParseResponse(Fixture("typed-null-success.json"));
    Equal(4, result.Columns.Count);
    Equal(1, result.Rows.Count);
    Equal("int_value", Encoding.UTF8.GetString(result.Columns[0].Name));
    Equal(SpikeCellType.Int32, result.Columns[0].Type);
    Equal("42", Encoding.UTF8.GetString(result.Rows[0][0].Value));
    True(result.Rows[0][1].IsNull);
    Equal(SpikeCellType.String, result.Rows[0][2].Type);
    Equal("hello", Encoding.UTF8.GetString(result.Rows[0][2].Value));
    True(!result.Rows[0][3].IsNull);
    Equal(0, result.Rows[0][3].Value.Length);
}

static void TypedJsonKindBytes()
{
    using var result = RestProtocol.ParseResponse("""
        {"Tables":[{"TableName":"Table_0","Columns":[
          {"ColumnName":"integer","DataType":"Int32","ColumnType":"int"},
          {"ColumnName":"boolean","DataType":"Boolean","ColumnType":"bool"},
          {"ColumnName":"decimal","DataType":"Decimal","ColumnType":"decimal"}],
          "Rows":[["\u0034\u0032","tr\u0075e","1.23\u00300"],[42,true,1.2300]]}]}
        """u8);
    var expectedTypes = new[] { SpikeCellType.Int32, SpikeCellType.Boolean, SpikeCellType.Decimal };
    var expectedBytes = new[] { "42"u8.ToArray(), "true"u8.ToArray(), "1.2300"u8.ToArray() };
    Equal(2, result.Rows.Count);
    for (var column = 0; column < expectedTypes.Length; column++)
    {
        Equal(expectedTypes[column], result.Columns[column].Type);
        foreach (var row in result.Rows)
        {
            Equal(expectedTypes[column], row[column].Type);
            True(!row[column].IsNull);
            True(row[column].Value.AsSpan().SequenceEqual(expectedBytes[column]));
        }
    }
}

static void DraftRepresentationParses()
{
    using var result = RestProtocol.ParseResponse(Fixture("feasibility-representation.json"));
    Equal("hello", Encoding.UTF8.GetString(result.Rows[0][0].Value));
    Equal("1.2300", Encoding.UTF8.GetString(result.Rows[0][1].Value));
    Equal("{\"b\":2,\"a\":1}", Encoding.UTF8.GetString(result.Rows[0][2].Value));
    Equal("[1,\"x\"]", Encoding.UTF8.GetString(result.Rows[0][3].Value));
}

static void DynamicNumericBytes()
{
    foreach (var lexical in new[]
    {
        "12345678901234567890123456789012345678901234567890",
        "-123456789012345678901234567890.1234567890123456789000",
        "1.2300E+004", "1e400", "-0", "-0.0000"
    })
        DynamicLexicalBytes(lexical);
}

static void DynamicLexicalBytes(string lexical, bool isNull = false)
{
    var body = "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[" +
        "{\"ColumnName\":\"value\",\"DataType\":\"Dynamic\",\"ColumnType\":\"dynamic\"}],\"Rows\":[[" + lexical + "]]}]}";
    using var result = RestProtocol.ParseResponse(Encoding.UTF8.GetBytes(body));
    Equal(1, result.Columns.Count);
    Equal(1, result.Rows.Count);
    Equal(SpikeCellType.Dynamic, result.Columns[0].Type);
    var cell = result.Rows[0][0];
    Equal(SpikeCellType.Dynamic, cell.Type);
    Equal(isNull, cell.IsNull);
    var expected = isNull ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(lexical);
    True(cell.Value.AsSpan().SequenceEqual(expected));
    AssertNativeCellBytes(result, expected, isNull);
}

static unsafe void AssertNativeCellBytes(QueryResult result, byte[] expected, bool isNull)
{
    // Exercise the existing ABI copy path after the parser's JsonDocument is disposed.
    var handle = Handles.AddResult(result);
    try
    {
        uint type = 0;
        byte nullFlag = 99;
        nuint required = 99;
        Equal(expected.Length == 0 ? SpikeStatus.Ok : SpikeStatus.BufferTooSmall,
            NativeBoundary.Cell(handle, 0, 0, &type, &nullFlag, null, 0, &required));
        Equal(10U, type);
        Equal(isNull ? (byte)1 : (byte)0, nullFlag);
        Equal((nuint)expected.Length, required);
        var guarded = Enumerable.Repeat((byte)0xA5, expected.Length + 2).ToArray();
        fixed (byte* buffer = guarded)
        {
            if (expected.Length > 0)
            {
                Equal(SpikeStatus.BufferTooSmall,
                    NativeBoundary.Cell(handle, 0, 0, &type, &nullFlag, buffer + 1, required - 1, &required));
                True(guarded.All(value => value == 0xA5));
            }
            Equal(SpikeStatus.Ok,
                NativeBoundary.Cell(handle, 0, 0, &type, &nullFlag, buffer + 1, (nuint)expected.Length, &required));
        }
        Equal(10U, type);
        Equal(isNull ? (byte)1 : (byte)0, nullFlag);
        Equal((nuint)expected.Length, required);
        Equal((byte)0xA5, guarded[0]);
        Equal((byte)0xA5, guarded[^1]);
        True(guarded.AsSpan(1, expected.Length).SequenceEqual(expected));
    }
    finally { Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(handle)); }
}

static void NonDynamicRepresentations()
{
    foreach (var type in new[] { "unknown", "bool", "int", "long", "real", "decimal", "datetime", "timespan", "guid", "string" })
    {
        var body = "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[" +
            "{\"ColumnName\":\"value\",\"DataType\":\"Other\",\"ColumnType\":\"" + type + "\"}]," +
            "\"Rows\":[[\"\\u0034\\u0032\"],[1.2300E+004],[true],[null],[\"\"],[{ \"b\":2, \"a\":1 }],[[1, 2]]]}]}";
        using var result = RestProtocol.ParseResponse(Encoding.UTF8.GetBytes(body));
        string[] expected = ["42", "1.2300E+004", "true", "", "", "{ \"b\":2, \"a\":1 }", "[1, 2]"];
        Equal(expected.Length, result.Rows.Count);
        for (var row = 0; row < expected.Length; row++)
        {
            Equal(row == 3, result.Rows[row][0].IsNull);
            True(result.Rows[row][0].Value.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected[row])));
        }
    }
}

static void JwtAudienceMustMatchExactly()
{
    True(JwtAudienceValidator.HasApprovedAudience(Token(SpikeContract.ApprovedResource)));
    True(!JwtAudienceValidator.HasApprovedAudience(Token(SpikeContract.ApprovedResource + "/")));
    True(!JwtAudienceValidator.HasApprovedAudience(Token("https://example.test")));
    True(!JwtAudienceValidator.HasApprovedAudience("not-a-jwt"));
    True(!JwtAudienceValidator.HasApprovedAudience(TokenWithArrayAudience()));
}

static void RedirectsAreDisabled()
{
    using var tokenHandler = AzureTokenProvider.CreateHandler();
    using var eventhouseHandler = EngineConnection.CreateEventhouseHandler();
    True(!tokenHandler.AllowAutoRedirect);
    True(!eventhouseHandler.AllowAutoRedirect);
    // No permissive certificate callback is installed. This is configuration
    // evidence only, not proof of invalid-certificate or hostname rejection.
    True(tokenHandler.SslOptions.RemoteCertificateValidationCallback is null);
    True(eventhouseHandler.SslOptions.RemoteCertificateValidationCallback is null);
}

static void MetadataContentTypeFailsClosed()
{
    MetadataRejectedBothPaths(() => Response(HttpStatusCode.OK, "SECRET_SENTINEL", "text/plain"));
}

static void MetadataRedirectFailsClosed()
{
    MetadataRejectedBothPaths(() =>
    {
        var redirect = Response(HttpStatusCode.Redirect, "SECRET_SENTINEL", "text/plain");
        redirect.Headers.Location = new Uri("https://example.test/steal");
        return redirect;
    });
}

static void MalformedMetadataFailsClosed()
{
    MetadataRejectedBothPaths(() => Response(HttpStatusCode.OK, "{\"SECRET_SENTINEL\":", "application/json"));
}

static void MetadataMismatchStopsDispatch()
{
    var handler = new SequenceHandler(Response(HttpStatusCode.OK,
        "{\"AzureAD\":{\"KustoServiceResourceId\":\"https://example.test\"}}", "application/json"));
    var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
    using var connection = Connection(handler, token);
    Throws<AuthenticationException>(() => connection.Execute("SELECT 1"u8));
    Equal(0, token.CallCount);
    Equal(0, handler.QueryCount);
}

static void AudienceMismatchStopsQuery()
{
    foreach (var operationPath in new[] { false, true })
    foreach (var audience in new[] { Token("https://example.test/SECRET_SENTINEL"),
        Token(SpikeContract.ApprovedResource + "/"), "not-a-jwt", TokenWithArrayAudience() })
    {
        var handler = new SequenceHandler(MetadataResponse());
        var token = new StubTokenProvider(audience);
        var connection = AddOfflineConnection(handler, token);
        try
        {
            ExecutePath(connection, operationPath, outcome =>
            {
                Equal((SpikeStatus.AuthenticationFailed, 0UL), outcome);
                AssertError(SpikeStatus.AuthenticationFailed, "Authentication metadata was rejected.");
            });
            Equal(1, token.CallCount);
            Equal(0, handler.QueryCount);
            Equal(1, handler.CallCount);
        }
        finally { CloseAfterDrain(connection); }
        Equal(1, handler.DisposeCount);
        Equal(1, token.DisposeCount);
    }
}

static void EventhouseRedirectIsNotFollowed()
{
    foreach (var operationPath in new[] { false, true })
    {
        var redirect = Response(HttpStatusCode.Redirect, "SECRET_SENTINEL", "text/plain");
        redirect.Headers.Location = new Uri("https://example.test/steal");
        var handler = new SequenceHandler(MetadataResponse(), redirect);
        var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
        var connection = AddOfflineConnection(handler, token);
        try
        {
            ExecutePath(connection, operationPath, outcome =>
            {
                Equal((SpikeStatus.HttpFailed, 0UL), outcome);
                AssertError(SpikeStatus.HttpFailed, "Eventhouse query HTTP request failed.");
            });
            Equal(2, handler.CallCount);
            Equal(1, handler.QueryCount);
            Equal(1, token.CallCount);
        }
        finally { CloseAfterDrain(connection); }
        Equal(1, handler.DisposeCount);
        Equal(1, token.DisposeCount);
    }
}

static void TransportFailureBothPaths(Func<SequenceHandler> handler, Func<ITokenProvider> token,
    string stage, string reason)
{
    foreach (var operationPath in new[] { false, true })
        TransportFailureIsClassified(handler(), token(), stage, reason, operationPath);
}

static unsafe void TransportFailureIsClassified(
    SequenceHandler handler, ITokenProvider tokenProvider, string expectedStage, string expectedReason, bool operationPath = false)
{
    var connection = new EngineConnection("fixture-db", tokenProvider,
        new HttpClient(handler, disposeHandler: true), TimeSpan.FromSeconds(2));
    var connectionHandle = Handles.AddConnection(connection);
    try
    {
        ExecutePath(connectionHandle, operationPath, outcome =>
        {
            Equal((SpikeStatus.TransportFailed, 0UL), outcome);
            AssertError(SpikeStatus.TransportFailed, $"transport_stage={expectedStage};transport_reason={expectedReason}");
        });
        Equal(expectedStage == "query" ? 2 : 1, handler.CallCount);
        Equal(expectedStage == "query" ? 1 : 0, handler.QueryCount);
        if (tokenProvider is StubTokenProvider stub) Equal(expectedStage == "metadata" ? 0 : 1, stub.CallCount);
        if (tokenProvider is FailingTokenProvider failing) Equal(1, failing.CallCount);
    }
    finally
    {
        CloseAfterDrain(connectionHandle);
    }
    Equal(1, handler.DisposeCount);
}

static void Rejects(string file)
{
    try
    {
        using var ignored = RestProtocol.ParseResponse(Fixture(file));
    }
    catch (ProtocolException)
    {
        return;
    }
    catch (JsonException)
    {
        return;
    }

    throw new InvalidOperationException();
}

static unsafe void HandleTypeConfusion()
{
    var resultHandle = CreateResult();
    try
    {
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseConnection(resultHandle));
        nuint columns = 0;
        nuint rows = 0;
        Equal(SpikeStatus.Ok, NativeBoundary.ResultShape(resultHandle, &columns, &rows));
    }
    finally
    {
        _ = NativeBoundary.CloseResult(resultHandle);
    }
}

static unsafe void StaleResultHandle()
{
    var handle = CreateResult();
    Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(handle));
    nuint columns = 99;
    nuint rows = 99;
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.ResultShape(handle, &columns, &rows));
    Equal((nuint)0, columns);
    Equal((nuint)0, rows);
}

static void DoubleClose()
{
    var handle = CreateResult();
    Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(handle));
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseResult(handle));
}

static unsafe void BufferSizing()
{
    var handle = CreateResult();
    try
    {
        uint type = 0;
        byte nullable = 0;
        nuint required = 0;
        Equal(SpikeStatus.BufferTooSmall, NativeBoundary.Column(handle, 0, &type, &nullable, null, 0, &required));
        Equal((nuint)3, required);
        var guarded = stackalloc byte[5];
        guarded[0] = 0xAA;
        guarded[4] = 0xBB;
        Equal(SpikeStatus.Ok, NativeBoundary.Column(handle, 0, &type, &nullable, guarded + 1, 3, &required));
        Equal((byte)0xAA, guarded[0]);
        Equal((byte)0xBB, guarded[4]);
        Equal("col", Encoding.UTF8.GetString(new ReadOnlySpan<byte>(guarded + 1, 3)));
    }
    finally
    {
        _ = NativeBoundary.CloseResult(handle);
    }
}

static unsafe void NullOutputs()
{
    Equal(SpikeStatus.InvalidArgument, NativeBoundary.ResultShape(1, null, null));
    Equal(SpikeStatus.InvalidArgument, NativeBoundary.CopyLastError(null, 0, null));
}

static unsafe void HostileEndpointRejected()
{
    var endpoint = Encoding.UTF8.GetBytes("http://example.test/");
    var database = "db"u8.ToArray();
    var tenant = Encoding.UTF8.GetBytes("11111111-1111-1111-1111-111111111111");
    var client = Encoding.UTF8.GetBytes("22222222-2222-2222-2222-222222222222");
    var secret = "not-a-real-secret"u8.ToArray();
    ulong handle = 9;
    fixed (byte* endpointPointer = endpoint)
    fixed (byte* databasePointer = database)
    fixed (byte* tenantPointer = tenant)
    fixed (byte* clientPointer = client)
    fixed (byte* secretPointer = secret)
    {
        var status = NativeBoundary.Open(endpointPointer, (nuint)endpoint.Length, databasePointer, (nuint)database.Length,
            tenantPointer, (nuint)tenant.Length, clientPointer, (nuint)client.Length, secretPointer, (nuint)secret.Length, 1000, &handle);
        Equal(SpikeStatus.InvalidArgument, status);
        Equal(0UL, handle);
    }
}

static unsafe void ErrorTextIsBounded()
{
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseResult(ulong.MaxValue));
    Equal(SpikeStatus.InvalidHandle, Diagnostics.Code);
    nuint required = 0;
    Equal(SpikeStatus.BufferTooSmall, NativeBoundary.CopyLastError(null, 0, &required));
    True(required > 0 && required < 128);
    var bytes = new byte[(int)required];
    fixed (byte* pointer = bytes)
    {
        Equal(SpikeStatus.Ok, NativeBoundary.CopyLastError(pointer, required, &required));
    }
    Equal("Result handle is invalid.", Encoding.UTF8.GetString(bytes));
}

static ulong CreateResult()
{
    var result = new QueryResult(
        [new ResultColumn("col"u8.ToArray(), SpikeCellType.Int32, true)],
        [new List<ResultCell> { new(SpikeCellType.Int32, false, "1"u8.ToArray()) }]);
    return Handles.AddResult(result);
}

static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

static EngineConnection Connection(SequenceHandler handler, ITokenProvider token) =>
    new("fixture-db", token, new HttpClient(handler, disposeHandler: true), TimeSpan.FromSeconds(2));

static HttpResponseMessage MetadataResponse() => Response(HttpStatusCode.OK,
    "{\"AzureAD\":{\"KustoServiceResourceId\":\"https://kusto.kusto.windows.net\"}}", "application/json");

static HttpResponseMessage Response(HttpStatusCode status, string body, string contentType)
{
    var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType) { CharSet = "utf-8" };
    return response;
}

static string Token(string audience)
{
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { aud = audience })))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return "eyJhbGciOiJub25lIn0." + payload + ".signature";
}

static string TokenWithArrayAudience()
{
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"aud\":[\"https://kusto.kusto.windows.net\"]}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return "eyJhbGciOiJub25lIn0." + payload + ".signature";
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException();
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException();
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException();
}

static unsafe ulong Prepare(ulong connection, uint timeout = 0)
{
    ulong operation = 99;
    Equal(SpikeStatus.Ok, NativeBoundary.PrepareOperation(connection, timeout, &operation));
    True(operation != 0);
    return operation;
}

static unsafe (SpikeStatus Status, ulong Result) RunOperation(ulong operation, bool invalidSql = false)
{
    ulong result = 99;
    var sql = "SELECT 1"u8.ToArray();
    fixed (byte* pointer = sql)
    {
        var status = NativeBoundary.ExecuteOperation(operation, invalidSql ? null : pointer, (nuint)sql.Length, &result);
        return (status, result);
    }
}

static ulong AddOfflineConnection(HttpMessageHandler handler, ITokenProvider? token = null, int timeout = 2000) =>
    Handles.AddConnection(new EngineConnection("fixture-db", token ?? new StubTokenProvider(Token(SpikeContract.ApprovedResource)),
        new HttpClient(handler), TimeSpan.FromMilliseconds(timeout)));

static void Wait(ManualResetEventSlim gate) => True(gate.Wait(TimeSpan.FromSeconds(5)));
static T Finish<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
static void CloseAfterDrain(ulong connection)
{
    SpikeStatus status = SpikeStatus.Busy;
    True(SpinWait.SpinUntil(() => (status = NativeBoundary.CloseConnection(connection)) != SpikeStatus.Busy,
        TimeSpan.FromSeconds(5)));
    Equal(SpikeStatus.Ok, status);
}

static void OperationReservation()
{
    var handler = new SequenceHandler();
    var connection = AddOfflineConnection(handler);
    var operation = Prepare(connection);
    try
    {
        unsafe
        {
            ulong other = 99;
            Equal(SpikeStatus.Busy, NativeBoundary.PrepareOperation(connection, 0, &other));
            Equal(0UL, other);
            Equal(SpikeStatus.Busy, NativeBoundary.Execute(connection, null, 0, &other));
        }
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
        Equal(0, handler.CallCount);
    }
    finally { Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation)); }
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static unsafe void OperationArguments()
{
    Equal(SpikeStatus.InvalidArgument, NativeBoundary.PrepareOperation(0, 0, null));
    ulong output = 99;
    Equal(SpikeStatus.InvalidArgument, NativeBoundary.PrepareOperation(0, 600001, &output));
    Equal(0UL, output);
    var result = CreateResult();
    try
    {
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.PrepareOperation(result, 0, &output));
        Equal(0UL, output);
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(result));
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseOperation(result));
        Equal(SpikeStatus.InvalidArgument, NativeBoundary.ExecuteOperation(result, null, 0, null));
        Equal((SpikeStatus.InvalidHandle, 0UL), RunOperation(result));
    }
    finally { NativeBoundary.CloseResult(result); }
}

static void PreparedCancellation()
{
    var handler = new SequenceHandler();
    var connection = AddOfflineConnection(handler);
    var operation = Prepare(connection);
    Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(operation));
    Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(operation));
    Equal((SpikeStatus.Cancelled, 0UL), RunOperation(operation));
    Equal((SpikeStatus.InvalidState, 0UL), RunOperation(operation));
    Equal(0, handler.CallCount);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseOperation(operation));
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static void InvalidOperationSql()
{
    var handler = new SequenceHandler();
    var connection = AddOfflineConnection(handler);
    var operation = Prepare(connection);
    Equal((SpikeStatus.InvalidArgument, 0UL), RunOperation(operation, true));
    Equal((SpikeStatus.InvalidState, 0UL), RunOperation(operation));
    Equal(0, handler.CallCount);
    NativeBoundary.CloseOperation(operation);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static void CancelAtStage(int stage, bool timeout)
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var handler = new OperationHandler(stage, entered, release);
    var token = new OperationTokenProvider(stage == 0, entered, release, Token(SpikeContract.ApprovedResource));
    var connection = AddOfflineConnection(handler, token, timeout ? 100 : 2000);
    var operation = Prepare(connection); // Zero must inherit the connection timeout.
    var task = Task.Run(() => RunOperation(operation));
    try
    {
        Wait(entered);
        Equal((SpikeStatus.InvalidState, 0UL), RunOperation(operation));
        if (!timeout) Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(operation));
        var result = Finish(task);
        Equal((timeout ? SpikeStatus.Timeout : SpikeStatus.Cancelled, 0UL), result);
        Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(operation));
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
    }
    finally
    {
        release.Set();
        NativeBoundary.CloseOperation(operation);
        Finish(task);
        CloseAfterDrain(connection);
    }
}

static void CloseDuringExecution()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var handler = new OperationHandler(1, entered, release, ignoreCancellation: true);
    var connection = AddOfflineConnection(handler);
    var operation = Prepare(connection);
    var task = Task.Run(() => RunOperation(operation));
    try
    {
        Wait(entered);
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(operation));
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
    }
    finally { release.Set(); }
    Equal((SpikeStatus.Cancelled, 0UL), Finish(task));
    CloseAfterDrain(connection);
    Equal(1, handler.DisposeCount);
}

static void SuccessfulOperation()
{
    var connection = AddOfflineConnection(new SequenceHandler(MetadataResponse(),
        Response(HttpStatusCode.OK, Encoding.UTF8.GetString(Fixture("typed-null-success.json")), "application/json")));
    var operation = Prepare(connection, 2000);
    var outcome = RunOperation(operation);
    Equal(SpikeStatus.Ok, outcome.Status);
    True(outcome.Result != 0);
    Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(operation));
    Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
    unsafe
    {
        nuint columns = 0, rows = 0;
        Equal(SpikeStatus.Ok, NativeBoundary.ResultShape(outcome.Result, &columns, &rows));
        Equal((nuint)4, columns);
    }
    Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(outcome.Result));
}

static void CancelBeforeDeadline() => TerminalOrdering(false);
static void DeadlineBeforeCancel() => TerminalOrdering(true);
static void TerminalOrdering(bool deadlineFirst)
{
    var connectionHandle = AddOfflineConnection(new SequenceHandler());
    True(Handles.TryGetConnection(connectionHandle, out var connection));
    var timeout = TimeSpan.FromSeconds(1);
    using var operation = new EngineOperation(connection!, timeout, () => { });
    True(operation.Acquire());
    try
    {
        Equal(SpikeStatus.Ok, operation.Begin());
        var admittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!deadlineFirst)
        {
            operation.Cancel();
            True(operation.Token.IsCancellationRequested);
            // Keep execution admitted beyond the real deadline. Prepared cancellation
            // is covered separately and cannot establish this terminal ordering.
            True(SpinWait.SpinUntil(() => System.Diagnostics.Stopwatch.GetElapsedTime(admittedAt) > timeout,
                TimeSpan.FromSeconds(5)));
        }
        if (deadlineFirst) True(operation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        operation.Cancel();
        var unpublished = new QueryResult([], []);
        Equal(deadlineFirst ? SpikeStatus.Timeout : SpikeStatus.Cancelled,
            operation.Complete(SpikeStatus.Ok, unpublished, out var result));
        Equal(0UL, result);
        True(unpublished.IsDisposed);
    }
    finally { operation.Release(); NativeBoundary.CloseConnection(connectionHandle); }
}

static void UnpublishedResult()
{
    foreach (var close in new[] { false, true })
    {
        var connectionHandle = AddOfflineConnection(new SequenceHandler());
        True(Handles.TryGetConnection(connectionHandle, out var connection));
        var operation = new EngineOperation(connection!, TimeSpan.FromSeconds(2), () => { });
        True(operation.Acquire());
        Equal(SpikeStatus.Ok, operation.Begin());
        if (close) operation.Dispose(); else operation.Cancel();
        var result = new QueryResult([], []);
        Equal(SpikeStatus.Cancelled, operation.Complete(SpikeStatus.Ok, result, out var published));
        Equal(0UL, published);
        operation.Dispose();
        operation.Release();
        NativeBoundary.CloseConnection(connectionHandle);
    }
}

static void DelayedCancel()
{
    var handler = new SequenceHandler();
    var connection = AddOfflineConnection(handler);
    var a = Prepare(connection);
    var capturedA = Handles.AcquireOperation(a)!;
    NativeBoundary.CloseOperation(a);
    unsafe
    {
        ulong b = 99;
        Equal(SpikeStatus.Busy, NativeBoundary.PrepareOperation(connection, 0, &b));
    }
    capturedA.Cancel();
    capturedA.Release();
    var bHandle = Prepare(connection);
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(a));
    Equal((SpikeStatus.InvalidArgument, 0UL), RunOperation(bHandle, true));
    NativeBoundary.CloseOperation(bHandle);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static void LegacyReservation()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var connection = AddOfflineConnection(new OperationHandler(1, entered, release));
    var task = Task.Run(() => RunLegacy(connection));
    try
    {
        Wait(entered);
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
        unsafe
        {
            ulong op = 99;
            Equal(SpikeStatus.Busy, NativeBoundary.PrepareOperation(connection, 0, &op));
        }
    }
    finally { release.Set(); }
    var outcome = Finish(task);
    Equal(SpikeStatus.Ok, outcome.Status);
    NativeBoundary.CloseResult(outcome.Result);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static unsafe (SpikeStatus Status, ulong Result) RunLegacy(ulong connection)
{
    ulong result = 0;
    var bytes = "SELECT 1"u8.ToArray();
    fixed (byte* sql = bytes)
        return (NativeBoundary.Execute(connection, sql, (nuint)bytes.Length, &result), result);
}

static void CloseBeforeExecute()
{
    var handler = new SequenceHandler();
    var connection = AddOfflineConnection(handler);
    var operation = Prepare(connection);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
    Equal((SpikeStatus.InvalidHandle, 0UL), RunOperation(operation));
    Equal(0, handler.CallCount);
    Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
}

static void SignalLease()
{
    var connection = AddOfflineConnection(new SequenceHandler());
    var operation = Prepare(connection);
    var admitted = Handles.AcquireOperation(operation)!;
    using var callbackEntered = new ManualResetEventSlim();
    using var callbackRelease = new ManualResetEventSlim();
    using var registration = admitted.Token.Register(() =>
    {
        callbackEntered.Set();
        if (!callbackRelease.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException();
    });
    var task = Task.Run(() => NativeBoundary.CancelOperation(operation));
    try
    {
        Wait(callbackEntered);
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
        admitted.Release();
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
    }
    finally { callbackRelease.Set(); }
    Equal(SpikeStatus.Ok, Finish(task));
    CloseAfterDrain(connection);
}

static void RemovalForbidsCommit()
{
    var connection = AddOfflineConnection(new SequenceHandler());
    var handle = Prepare(connection);
    var execution = Handles.AcquireOperation(handle)!;
    Equal(SpikeStatus.Ok, execution.Begin());
    var closing = Handles.AcquireOperation(handle, remove: true)!;
    // Pause close before signalling/disposal. Removal itself must have committed
    // cancellation, otherwise an admitted execute could still publish success.
    Equal(SpikeStatus.Cancelled, execution.Complete(SpikeStatus.Ok, new QueryResult([], []), out var result));
    Equal(0UL, result);
    Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(handle));
    closing.Dispose();
    closing.Release();
    execution.Release();
    CloseAfterDrain(connection);
}

static void CommitChecksDeadline()
{
    var handle = AddOfflineConnection(new SequenceHandler());
    True(Handles.TryGetConnection(handle, out var connection));
    var timeout = TimeSpan.FromMilliseconds(50);
    using var operation = new EngineOperation(connection!, timeout, () => { }, scheduleDeadlineTimer: false);
    Equal(SpikeStatus.Ok, operation.Begin());
    var admittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    True(SpinWait.SpinUntil(() => System.Diagnostics.Stopwatch.GetElapsedTime(admittedAt) > timeout,
        TimeSpan.FromSeconds(5)));
    // No timer was scheduled and no cancel call occurred. Only Complete can decide
    // Timeout here. The token must remain unsignalled before and after commit.
    True(!operation.Token.IsCancellationRequested);
    var unpublished = new QueryResult([], []);
    Equal(SpikeStatus.Timeout, operation.Complete(SpikeStatus.Ok, unpublished, out var result));
    Equal(0UL, result);
    True(unpublished.IsDisposed);
    True(!operation.Token.IsCancellationRequested);
    NativeBoundary.CloseConnection(handle);
}

static void TimeoutOverride()
{
    var connection = AddOfflineConnection(new SequenceHandler(MetadataResponse(),
        Response(HttpStatusCode.OK, Encoding.UTF8.GetString(Fixture("typed-null-success.json")), "application/json")), timeout: 0);
    var operation = Prepare(connection, 2000);
    var outcome = RunOperation(operation);
    Equal(SpikeStatus.Ok, outcome.Status);
    NativeBoundary.CloseResult(outcome.Result);
    NativeBoundary.CloseOperation(operation);
    CloseAfterDrain(connection);
}

static void SuccessBeforeCancel()
{
    var handle = AddOfflineConnection(new SequenceHandler());
    True(Handles.TryGetConnection(handle, out var connection));
    using var operation = new EngineOperation(connection!, TimeSpan.FromSeconds(2), () => { });
    Equal(SpikeStatus.Ok, operation.Begin());
    Equal(SpikeStatus.Ok, operation.Complete(SpikeStatus.Ok, new QueryResult([], []), out var result));
    operation.Cancel();
    True(!operation.Token.IsCancellationRequested);
    True(Handles.TryGetResult(result, out _));
    NativeBoundary.CloseResult(result);
    NativeBoundary.CloseConnection(handle);
}

static void FabricOrigins()
{
    const string suffix = ".kusto.fabric.microsoft.com";
    var longestHost = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 34) + suffix;
    Equal(253, longestHost.Length);
    foreach (var host in new[] { "a" + suffix, "second-region.cluster-2" + suffix, new string('a', 63) + suffix, longestHost })
    {
        foreach (var ending in new[] { "", "/" })
        {
            var raw = "HTTPS://" + host.ToUpperInvariant() + ending;
            True(FabricEndpoint.TryParse(raw, out var parsed));
            Equal("https://" + host + "/", parsed!.AbsoluteUri);
        }
    }
    foreach (var raw in new[]
    {
        "", "http://a" + suffix, " https://a" + suffix, "https://a" + suffix + " ",
        "https://kusto.fabric.microsoft.com", "https://" + suffix, "https://a..b" + suffix,
        "https://-a" + suffix, "https://a-" + suffix, "https://a_b" + suffix,
        "https://" + new string('a', 64) + suffix, "https://x" + longestHost,
        "https://a" + suffix + ".", "https://a" + suffix + ".evil.example",
        "https://akusto.fabric.microsoft.com", "https://a.kusto.fabric.microsoft.comx",
        "https://127.0.0.1", "https://[::1]", "https://user@a" + suffix,
        "https://user:pass@a" + suffix, "https://a" + suffix + ":443",
        "https://a" + suffix + ":8443", "https://a" + suffix + ":",
        "https://a" + suffix + "?", "https://a" + suffix + "#",
        "https://a" + suffix + "/?", "https://a" + suffix + "/#",
        "https://a" + suffix + "?q=x", "https://a" + suffix + "#x",
        "https://a" + suffix + "/path", "https://a" + suffix + "//",
        "https://a" + suffix + "/./", "https://a" + suffix + "/%2f",
        "https://%61" + suffix, "https://a%2eb" + suffix, "https://a\\b" + suffix,
        "https://é" + suffix, "https://a\t" + suffix, "https://a\n" + suffix,
        "https://a\0" + suffix, "https://a" + suffix + "\\"
    })
    {
        True(!FabricEndpoint.TryParse(raw, out var parsed));
        True(parsed is null);
        var opened = OpenOrigin(raw);
        Equal(SpikeStatus.InvalidArgument, opened.Status);
        Equal(0UL, opened.Handle);
        Equal("Configuration is invalid.", Encoding.UTF8.GetString(Diagnostics.Message));
    }
}

static unsafe (SpikeStatus Status, ulong Handle) OpenOrigin(string raw)
{
    var origin = Encoding.UTF8.GetBytes(raw);
    var database = "fixture-db"u8.ToArray();
    var tenant = "11111111-1111-1111-1111-111111111111"u8.ToArray();
    var client = "22222222-2222-2222-2222-222222222222"u8.ToArray();
    var secret = "dummy-secret"u8.ToArray();
    ulong handle = 99;
    fixed (byte* o = origin, d = database, t = tenant, c = client, s = secret)
    {
        var status = NativeBoundary.Open(o, (nuint)origin.Length, d, (nuint)database.Length,
            t, (nuint)tenant.Length, c, (nuint)client.Length, s, (nuint)secret.Length, 2000, &handle);
        return (status, handle);
    }
}

static void ConfiguredNativeOrigin()
{
    foreach (var raw in new[] { SpikeContract.ApprovedQueryUri, "HTTPS://SECOND-REGION.CLUSTER-2.KUSTO.FABRIC.MICROSOFT.COM/" })
    {
        var outcome = OpenOrigin(raw);
        Equal(SpikeStatus.Ok, outcome.Status);
        try
        {
            True(Handles.TryGetConnection(outcome.Handle, out var connection));
            Equal(raw.TrimEnd('/').ToLowerInvariant() + "/", connection!.QueryOrigin.AbsoluteUri);
        }
        finally { Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(outcome.Handle)); }
    }
    Equal("https://kusto.kusto.windows.net/.default", SpikeContract.Scope);
    Equal("https://kusto.kusto.windows.net", SpikeContract.ApprovedResource);
}

static void ConfiguredRequests()
{
    const string origin = "https://second-region.cluster-2.kusto.fabric.microsoft.com/";
    foreach (var operationPath in new[] { false, true })
    {
        var handler = new InspectRequestsHandler(Fixture("typed-null-success.json"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri(origin) };
        var connection = Handles.AddConnection(new EngineConnection("fixture-db",
            new StubTokenProvider(Token(SpikeContract.ApprovedResource)), http, TimeSpan.FromSeconds(2)));
        try
        {
            ExecutePath(connection, operationPath, result =>
            {
                Equal(SpikeStatus.Ok, result.Status);
                True(result.Result != 0);
                try
                {
                    if (typeof(RestProtocol).Assembly.GetName().Name == "eventhouse-engine")
                    {
                        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
                        Equal(SpikeStatus.Ok, Fetch(result.Result, out var row)); Equal((byte)1, row);
                        Equal(SpikeStatus.Ok, Fetch(result.Result, out row)); Equal((byte)0, row);
                        Equal(SpikeStatus.Ok, Fetch(result.Result, out row)); Equal((byte)0, row);
                    }
                }
                finally { Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(result.Result)); }
            });
            Equal(2, handler.Requests.Count);
            Equal(origin + "v1/rest/auth/metadata", handler.Requests[0].Uri.AbsoluteUri);
            Equal(origin + "v1/rest/query", handler.Requests[1].Uri.AbsoluteUri);
            Equal("GET", handler.Requests[0].Method);
            Equal("POST", handler.Requests[1].Method);
            True(handler.Requests[0].Authorization is null);
            Equal("Bearer " + Token(SpikeContract.ApprovedResource), handler.Requests[1].Authorization);
            using var document = JsonDocument.Parse(handler.Requests[1].Body!);
            Equal("fixture-db", document.RootElement.GetProperty("db").GetString());
            Equal("SELECT 1", document.RootElement.GetProperty("csl").GetString());
            using var properties = JsonDocument.Parse(document.RootElement.GetProperty("properties").GetString()!);
            var options = properties.RootElement.GetProperty("Options");
            Equal("sql", options.GetProperty("query_language").GetString());
            True(options.GetProperty("request_readonly").GetBoolean());
            True(options.GetProperty("request_readonly_hardline").GetBoolean());
            Equal(JsonValueKind.True, options.GetProperty("notruncation").ValueKind);
            Equal(4, options.EnumerateObject().Count());
            foreach (var name in new[] { "truncationmaxsize", "truncationmaxrecords", "query_take_max_records" })
                True(!options.TryGetProperty(name, out _));
        }
        finally { CloseAfterDrain(connection); }
    }
}

static void ExecutePath(ulong connection, bool operationPath, Action<(SpikeStatus Status, ulong Result)> inspect)
{
    if (!operationPath) { inspect(RunLegacy(connection)); return; }
    var operation = Prepare(connection);
    // Inspect diagnostics and drain a successful cursor before close changes
    // either. Never manufacture an error by writing test values into Diagnostics.
    try { inspect(RunOperation(operation)); }
    finally { Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation)); }
}

static unsafe void AssertError(SpikeStatus expected, string message)
{
    Equal(expected, Diagnostics.Code);
    Equal(message, Encoding.UTF8.GetString(Diagnostics.Message));
    nuint required = 0;
    Equal(SpikeStatus.BufferTooSmall, NativeBoundary.CopyLastError(null, 0, &required));
    True(required > 0 && required < 128);
    var bytes = new byte[(int)required];
    fixed (byte* pointer = bytes)
        Equal(SpikeStatus.Ok, NativeBoundary.CopyLastError(pointer, required, &required));
    Equal(message, Encoding.UTF8.GetString(bytes));
    Equal(expected, Diagnostics.Code);
}

static void MetadataHttpStatuses() => HttpStatuses(true);
static void QueryHttpStatuses() => HttpStatuses(false);
static void HttpStatuses(bool metadata)
{
    foreach (var operationPath in new[] { false, true })
    foreach (var code in new[] { 400, 401, 403, 429, 500, 503, 302 })
    {
        var failure = Response((HttpStatusCode)code, "SECRET_SENTINEL SQL_SENTINEL BODY_SENTINEL", "text/plain");
        failure.Headers.Location = new Uri("https://evil.example/SECRET_SENTINEL");
        var handler = metadata ? new SequenceHandler(failure) : new SequenceHandler(MetadataResponse(), failure);
        var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
        var connection = AddOfflineConnection(handler, token);
        try
        {
            var expected = metadata ? SpikeStatus.AuthenticationFailed : code switch
            {
                400 => SpikeStatus.QueryRejected,
                401 => SpikeStatus.AuthenticationFailed,
                403 => SpikeStatus.PermissionDenied,
                _ => SpikeStatus.HttpFailed
            };
            var message = metadata ? "Authentication metadata was rejected." : code switch
            {
                400 => "Eventhouse rejected the query request.",
                401 => "Query authentication failed.",
                403 => "Query permission was denied.",
                _ => "Eventhouse query HTTP request failed."
            };
            ExecutePath(connection, operationPath, outcome =>
            {
                Equal((expected, 0UL), outcome);
                AssertError(expected, message);
            });
            Equal(metadata ? 0 : 1, token.CallCount);
            Equal(metadata ? 0 : 1, handler.QueryCount);
            Equal(metadata ? 1 : 2, handler.CallCount); // Neither retries nor redirect dispatches.
        }
        finally { CloseAfterDrain(connection); }
        Equal(1, handler.DisposeCount);
        Equal(1, token.DisposeCount);
    }
}

static void MetadataResourceBothPaths()
{
    foreach (var resource in new[] { "https://evil.example/SECRET_SENTINEL", SpikeContract.ApprovedResource + "/" })
        MetadataRejectedBothPaths(() => Response(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { AzureAD = new { KustoServiceResourceId = resource } }), "application/json"));
}

static void TokenAuthenticationBothPaths()
{
    foreach (var operationPath in new[] { false, true })
    {
        var handler = new SequenceHandler(MetadataResponse());
        var token = new FailingTokenProvider(new AuthenticationFailedException("SECRET_SENTINEL SQL_SENTINEL BODY_SENTINEL"));
        var connection = AddOfflineConnection(handler, token);
        try
        {
            ExecutePath(connection, operationPath, outcome =>
            {
                Equal((SpikeStatus.AuthenticationFailed, 0UL), outcome);
                AssertError(SpikeStatus.AuthenticationFailed, "Authentication failed.");
            });
            Equal(0, handler.QueryCount);
            Equal(1, handler.CallCount);
            Equal(1, token.CallCount);
        }
        finally { CloseAfterDrain(connection); }
        Equal(1, handler.DisposeCount);
        Equal(1, token.DisposeCount);
    }
}

static void MetadataObjectKindsBothPaths()
{
    foreach (var payload in new[] { "[]", "{}", "{\"AzureAD\":null}", "{\"AzureAD\":[]}",
        "{\"AzureAD\":{}}", "{\"AzureAD\":{\"KustoServiceResourceId\":null}}",
        "{\"AzureAD\":{\"KustoServiceResourceId\":[]}}", "{\"AzureAD\":{\"KustoServiceResourceId\":42}}" })
        MetadataRejectedBothPaths(() => Response(HttpStatusCode.OK, payload, "application/json"));
}

static void MetadataRejectedBothPaths(Func<HttpResponseMessage> response)
{
    foreach (var operationPath in new[] { false, true })
    {
        var handler = new SequenceHandler(response());
        var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
        var connection = AddOfflineConnection(handler, token);
        try
        {
            ExecutePath(connection, operationPath, outcome =>
            {
                Equal((SpikeStatus.AuthenticationFailed, 0UL), outcome);
                AssertError(SpikeStatus.AuthenticationFailed, "Authentication metadata was rejected.");
            });
            Equal(0, token.CallCount);
            Equal(0, handler.QueryCount);
            Equal(1, handler.CallCount);
        }
        finally { CloseAfterDrain(connection); }
        Equal(1, handler.DisposeCount);
        Equal(1, token.DisposeCount);
    }
}

static (string File, ProtocolReason Reason, string Token)[] ProtocolFailures() =>
[
    ("oneapi-error.json", ProtocolReason.Root, "root"),
    ("failed-query-status.json", ProtocolReason.FailedQueryStatus, "failed_query_status"),
    ("malformed.json", ProtocolReason.InvalidJson, "invalid_json"),
    ("wrong-row-width.json", ProtocolReason.Rows, "rows"),
    ("extra-root-property.json", ProtocolReason.Root, "root"),
    ("extra-table-property.json", ProtocolReason.Table, "table"),
    ("extra-column-property.json", ProtocolReason.Columns, "columns"),
    ("unknown-metadata-table.json", ProtocolReason.UnknownMetadataSchema, "unknown_metadata_schema"),
    ("invalid-metadata-table-name.json", ProtocolReason.UnknownMetadataName, "unknown_metadata_name"),
    ("empty-query-status.json", ProtocolReason.EmptyQueryStatus, "empty_query_status"),
    ("metadata-row-width.json", ProtocolReason.Rows, "rows")
];

static void ProtocolFixtureReasons()
{
    foreach (var failure in ProtocolFailures())
        AssertProtocolReason(Fixture(failure.File), failure.Reason);
}

static void AssertProtocolReason(byte[] body, ProtocolReason reason)
{
    try { using var ignored = RestProtocol.ParseResponse(body); }
    catch (ProtocolException exception)
    {
        Equal(reason, exception.Reason);
        Equal("Eventhouse response contract was rejected.", exception.Message);
        True(exception.InnerException is null);
        True(!exception.ToString().Contains("SENTINEL", StringComparison.Ordinal));
        return;
    }
    throw new InvalidOperationException();
}

static void ProtocolParseReasons()
{
    AssertProtocolReason([], ProtocolReason.EmptyResponse);
    AssertProtocolReason(new byte[SpikeContract.MaximumResponseBytes + 1], ProtocolReason.SizeParse);
    // At the unchanged cap, parsing is attempted rather than rejected as oversized.
    AssertProtocolReason(new byte[SpikeContract.MaximumResponseBytes], ProtocolReason.InvalidJson);
    AssertProtocolReason("{\"BODY_SENTINEL\": SECRET_SENTINEL"u8.ToArray(), ProtocolReason.InvalidJson);
    AssertProtocolReason("{\"Tables\":[]}"u8.ToArray(), ProtocolReason.MissingPrimary);
    AssertProtocolReason("{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[],\"Rows\":[]},{\"TableName\":\"Table_0\",\"Columns\":[],\"Rows\":[]}]}"u8.ToArray(),
        ProtocolReason.DuplicatePrimary);
}

static void ProtocolDiagnosticsBothPaths()
{
    foreach (var failure in ProtocolFailures())
        AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, Encoding.UTF8.GetString(Fixture(failure.File)), "application/json"), failure.Token);
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, "BODY_SENTINEL", "text/plain"), "content_type");
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, "", "application/json"), "empty_response");
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, "{\"BODY_SENTINEL\": SECRET_SENTINEL", "application/json"), "invalid_json");
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, "{\"Tables\":[],\"SECRET_SENTINEL\":\"BODY_SENTINEL\"}", "application/json"), "root");
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK, "{\"Tables\":[]}", "application/json"), "missing_primary");
    AssertProtocolDiagnostic(() => Response(HttpStatusCode.OK,
        "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[],\"Rows\":[]},{\"TableName\":\"Table_0\",\"Columns\":[],\"Rows\":[]}]}", "application/json"), "duplicate_primary");
    AssertProtocolDiagnostic(() =>
    {
        var response = Response(HttpStatusCode.OK, "BODY_SENTINEL", "application/json");
        response.Content.Headers.ContentLength = SpikeContract.MaximumResponseBytes + 1;
        return response;
    }, "size_header");
    AssertProtocolDiagnostic(() =>
    {
        var content = new StreamContent(new MemoryStream(new byte[SpikeContract.MaximumResponseBytes + 1]));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 0; // Header cannot bypass the independent read cap.
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }, "size_read");
}

static void ProtocolReasonFallback()
{
    var fallback = new ProtocolException();
    Equal(ProtocolReason.Unknown, fallback.Reason);
    Equal("Eventhouse response contract was rejected.", fallback.Message);
    AssertProtocolDiagnostic(() => fallback, "unknown");
    AssertProtocolDiagnostic(() => new ProtocolException((ProtocolReason)int.MaxValue), "unknown");
    AssertProtocolDiagnostic(() => new JsonException("SECRET_SENTINEL BODY_SENTINEL"), "invalid_json");
}

static unsafe void AssertProtocolDiagnostic(Func<object> failure, string reason)
{
    foreach (var operationPath in new[] { false, true })
    {
        var handler = new SequenceHandler(MetadataResponse(), failure());
        var connection = AddOfflineConnection(handler);
        var operation = operationPath ? Prepare(connection) : 0;
        try
        {
            ulong result = 99;
            var sql = "SELECT 'SQL_SENTINEL'"u8.ToArray();
            fixed (byte* pointer = sql)
            {
                Equal(SpikeStatus.ProtocolFailed, operationPath
                    ? NativeBoundary.ExecuteOperation(operation, pointer, (nuint)sql.Length, &result)
                    : NativeBoundary.Execute(connection, pointer, (nuint)sql.Length, &result));
            }
            Equal(0UL, result);
            Equal((SpikeStatus)7, Diagnostics.Code);
            // Read immediately on the calling thread, before any ordinary ABI call clears it.
            nuint required = 0;
            Equal(SpikeStatus.BufferTooSmall, NativeBoundary.CopyLastError(null, 0, &required));
            True(required > 0 && required < 128);
            var bytes = new byte[(int)required];
            fixed (byte* pointer = bytes)
            {
                Equal(SpikeStatus.Ok, NativeBoundary.CopyLastError(pointer, required, &required));
            }
            var message = Encoding.UTF8.GetString(bytes);
            Equal("protocol_reason=" + reason, message);
            True(!message.Contains("SENTINEL", StringComparison.Ordinal));
            Equal(SpikeStatus.ProtocolFailed, Diagnostics.Code);
            Equal(message, Encoding.UTF8.GetString(Diagnostics.Message));
            Equal(2, handler.CallCount);
            Equal(1, handler.QueryCount);
        }
        finally
        {
            if (operationPath) Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
            CloseAfterDrain(connection);
        }
    }
}

static string CursorHeader(string type = "dynamic") =>
    "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[{\"ColumnName\":\"value\",\"DataType\":\"Other\",\"ColumnType\":\"" + type + "\"}],\"Rows\":[";

static HttpResponseMessage CursorResponse(Stream stream)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    return response;
}

static (ulong Connection, ulong Operation, StreamingResult Cursor, ulong Result) OpenCursor(Stream stream, uint timeout = 5000)
{
    var connection = AddOfflineConnection(new SequenceHandler(MetadataResponse()));
    var operation = Prepare(connection, timeout);
    var owner = Handles.AcquireOperation(operation)!;
    var failed = false;
    try
    {
        Equal(SpikeStatus.Ok, owner.Begin());
        var cursor = StreamingResult.CreateAsync(CursorResponse(stream), owner, false).GetAwaiter().GetResult();
        Equal(SpikeStatus.Ok, owner.Publish(cursor, out var result));
        return (connection, operation, cursor, result);
    }
    catch { failed = true; NativeBoundary.CloseOperation(operation); throw; }
    finally { owner.Release(); if (failed) CloseAfterDrain(connection); }
}

static void CloseCursor((ulong Connection, ulong Operation, StreamingResult Cursor, ulong Result) item)
{
    NativeBoundary.CloseResult(item.Result);
    NativeBoundary.CloseOperation(item.Operation);
    if (Handles.TryGetConnection(item.Connection, out _)) CloseAfterDrain(item.Connection);
}

static unsafe SpikeStatus Fetch(ulong result, out byte row)
{
    byte value = 99;
    var status = NativeBoundary.Next(result, &value);
    row = value;
    return status;
}

static unsafe byte[] CurrentBytes(ulong result)
{
    uint type;
    byte isNull;
    nuint required;
    var status = NativeBoundary.CurrentCell(result, 0, &type, &isNull, null, 0, &required);
    Equal(required == 0 ? SpikeStatus.Ok : SpikeStatus.BufferTooSmall, status);
    var buffer = new byte[(int)required + 2];
    buffer[0] = 0xA5; buffer[^1] = 0xA5;
    fixed (byte* pointer = buffer)
        Equal(SpikeStatus.Ok, NativeBoundary.CurrentCell(result, 0, &type, &isNull, pointer + 1, required, &required));
    Equal((byte)0xA5, buffer[0]); Equal((byte)0xA5, buffer[^1]);
    return buffer.AsSpan(1, (int)required).ToArray();
}

static void CursorSplitReads()
{
    var lexical = "{ \"é雪😀\": [1.2300E+004, \"\\uD83D\\uDE00\\\\\\\"\", null], \"b\":true }";
    foreach (var chunk in new[] { 1, 2, 3, 7, 131072 })
    {
        var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[" + lexical + "]]}]}") , chunk);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            True(CurrentBytes(item.Result).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(lexical)));
            True(CurrentBytes(item.Result).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(lexical)));
            Equal(SpikeStatus.Ok, Fetch(item.Result, out row)); Equal((byte)0, row);
            True(stream.ReadCalls > 0);
        }
        finally { CloseCursor(item); }
    }
    var decoded = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader("string") + "[\"é\\u96ea\\uD83D\\uDE00\"]]}]}"), 1));
    try
    {
        Equal(SpikeStatus.Ok, Fetch(decoded.Result, out _));
        Equal("é雪😀", Encoding.UTF8.GetString(CurrentBytes(decoded.Result)));
    }
    finally { CloseCursor(decoded); }
}

static unsafe void CursorLargeResponse()
{
    var stream = new GeneratedRowsStream(CursorHeader(), "[1234567890]", 400000, "]}]}");
    var item = OpenCursor(stream, 600000);
    try
    {
        nuint columns, rows;
        Equal(SpikeStatus.Ok, NativeBoundary.ResultShape(item.Result, &columns, &rows));
        Equal(nuint.MaxValue, rows);
        var count = 0;
        while (true)
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row));
            if (row == 0) break;
            if (count == 0 || count == 399999) Equal("1234567890", Encoding.UTF8.GetString(CurrentBytes(item.Result)));
            count++;
        }
        Equal(400000, count);
        True(stream.BytesRead > SpikeContract.MaximumResponseBytes);
        Equal(SpikeStatus.Ok, NativeBoundary.ResultShape(item.Result, &columns, &rows));
        Equal((nuint)400000, rows);
        True(stream.Disposed);
        Equal(SpikeStatus.Ok, Fetch(item.Result, out var end)); Equal((byte)0, end);
    }
    finally { CloseCursor(item); }
}

static void CursorLateFailures()
{
    foreach (var file in new[] { "failed-query-status.json", "empty-query-status.json", "metadata-row-width.json" })
    {
        var item = OpenCursor(new ChunkedResponseStream(Fixture(file), 1));
        try
        {
            if (file == "failed-query-status.json")
            { Equal(SpikeStatus.Ok, Fetch(item.Result, out var first)); Equal((byte)1, first); }
            Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out var row)); Equal((byte)0, row);
            var message = Encoding.UTF8.GetString(Diagnostics.Message);
            Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row));
            Equal(message, Encoding.UTF8.GetString(Diagnostics.Message));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            CloseAfterDrain(item.Connection); // Result deliberately stays open.
        }
        finally { CloseCursor(item); }
    }
    foreach (var suffix in new[] { "]}]}x", "]}]", "]}],\"error\":{}}", "]},{\"TableName\":\"Table_0\",\"Columns\":[],\"Rows\":[]}]}" })
    {
        var item = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]" + suffix), 2));
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out _));
            Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out var row)); Equal((byte)0, row);
        }
        finally { CloseCursor(item); }
    }
}

static unsafe void CursorBounds()
{
    foreach (var operationPath in new[] { false, true })
    foreach (var size in new[] { SpikeContract.MaximumCellBytes, SpikeContract.MaximumCellBytes + 1 })
    {
        // A number is one token, generated in bounded pieces rather than one body allocation.
        var stream = new GeneratedRowsStream(CursorHeader() + "[", "1", size, "]]}]}", separator: "");
        var recoveryStream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[2]]}]}"), 1);
        var response = new CountedCursorResponse(stream);
        var recoveryResponse = new CountedCursorResponse(recoveryStream);
        var handler = new SequenceHandler(MetadataResponse(), response, MetadataResponse(), recoveryResponse);
        var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
        var connection = AddOfflineConnection(handler, token, timeout: 600000);
        ulong operation = 0, result = 0, recoveryOperation = 0, recoveryResult = 0;
        try
        {
            if (operationPath) operation = Prepare(connection, 600000);
            var outcome = operationPath ? RunOperation(operation) : RunLegacy(connection);
            Equal(SpikeStatus.Ok, outcome.Status); result = outcome.Result; True(result != 0);
            Equal(0, response.DisposeCount); Equal(0, response.TrackedContent.DisposeCount);
            Equal(2, handler.CallCount); Equal(1, handler.QueryCount); Equal(1, token.CallCount);
            var expected = size == SpikeContract.MaximumCellBytes ? SpikeStatus.Ok : SpikeStatus.ProtocolFailed;
            Equal(expected, Fetch(result, out var row));
            Equal(expected == SpikeStatus.Ok ? (byte)1 : (byte)0, row);
            if (expected == SpikeStatus.Ok)
            {
                var bytes = CurrentBytes(result);
                Equal(size, bytes.Length); True(bytes.All(value => value == (byte)'1'));
                Equal(SpikeStatus.Ok, Fetch(result, out row)); Equal((byte)0, row); // Validated physical EOF.
                Equal(SpikeStatus.Ok, Fetch(result, out row)); Equal((byte)0, row);
            }
            else
            {
                AssertError(SpikeStatus.ProtocolFailed, "protocol_reason=cell_limit");
                True(stream.Disposed); // Terminal fetch detaches HTTP before result close.
                var bytesRead = stream.BytesRead;
                var readCalls = stream.ReadCalls;
                var guarded = Enumerable.Repeat((byte)0xA5, 8).ToArray();
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Equal(SpikeStatus.ProtocolFailed, Fetch(result, out row)); Equal((byte)0, row);
                    AssertError(SpikeStatus.ProtocolFailed, "protocol_reason=cell_limit");
                    uint type = 99; byte isNull = 99; nuint required = 99;
                    fixed (byte* pointer = guarded)
                        Equal(SpikeStatus.ProtocolFailed, NativeBoundary.CurrentCell(result, 0, &type, &isNull,
                            pointer + 1, (nuint)guarded.Length - 2, &required));
                    Equal(0U, type); Equal((byte)0, isNull); Equal((nuint)0, required);
                    True(guarded.All(value => value == 0xA5));
                    AssertError(SpikeStatus.ProtocolFailed, "protocol_reason=cell_limit");
                    Equal(bytesRead, stream.BytesRead);
                    Equal(readCalls, stream.ReadCalls);
                }
            }
            True(stream.Disposed);
            Equal(1, response.DisposeCount); Equal(1, response.TrackedContent.DisposeCount);
            var terminalBytesRead = stream.BytesRead;
            var terminalReadCalls = stream.ReadCalls;
            if (operationPath)
            {
                Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
                Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
                Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseOperation(operation)); operation = 0;
            }

            // Keep the terminal result open: recovery must not depend on result_close.
            // Public operations need explicit retirement; private operations retire on detach.
            if (operationPath) recoveryOperation = Prepare(connection, 600000);
            var recovered = operationPath ? RunOperation(recoveryOperation) : RunLegacy(connection);
            Equal(SpikeStatus.Ok, recovered.Status); recoveryResult = recovered.Result; True(recoveryResult != 0);
            Equal(SpikeStatus.Ok, Fetch(recoveryResult, out row)); Equal((byte)1, row);
            True(CurrentBytes(recoveryResult).AsSpan().SequenceEqual("2"u8));
            Equal(SpikeStatus.Ok, Fetch(recoveryResult, out row)); Equal((byte)0, row);
            Equal(SpikeStatus.Ok, Fetch(recoveryResult, out row)); Equal((byte)0, row);
            True(recoveryStream.Disposed); Equal(1, recoveryStream.DisposeCount);
            Equal(1, recoveryResponse.DisposeCount); Equal(1, recoveryResponse.TrackedContent.DisposeCount);
            Equal(1, response.DisposeCount); Equal(1, response.TrackedContent.DisposeCount);
            Equal(4, handler.CallCount); Equal(2, handler.QueryCount); Equal(2, token.CallCount); // No replay.
            Equal(expected, Fetch(result, out row)); Equal((byte)0, row);
            if (expected != SpikeStatus.Ok) AssertError(expected, "protocol_reason=cell_limit");
            Equal(terminalBytesRead, stream.BytesRead);
            Equal(terminalReadCalls, stream.ReadCalls);
            if (operationPath)
            {
                Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(recoveryOperation));
                Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseOperation(recoveryOperation)); recoveryOperation = 0;
            }
            Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(result));
            Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseResult(result)); result = 0;
            Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(recoveryResult));
            Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseResult(recoveryResult)); recoveryResult = 0;
            Equal(SpikeStatus.Ok, NativeBoundary.CloseConnection(connection));
            Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseConnection(connection));
            Equal(1, handler.DisposeCount); Equal(1, token.DisposeCount);
            Equal(1, recoveryStream.DisposeCount);
            Equal(1, response.DisposeCount); Equal(1, response.TrackedContent.DisposeCount);
            Equal(1, recoveryResponse.DisposeCount); Equal(1, recoveryResponse.TrackedContent.DisposeCount);
        }
        finally
        {
            // Best-effort handle cleanup on an earlier assertion failure.
            if (result != 0) NativeBoundary.CloseResult(result);
            if (recoveryResult != 0) NativeBoundary.CloseResult(recoveryResult);
            if (operation != 0) NativeBoundary.CloseOperation(operation);
            if (recoveryOperation != 0) NativeBoundary.CloseOperation(recoveryOperation);
            if (Handles.TryGetConnection(connection, out _)) CloseAfterDrain(connection);
        }
    }
    var nested = CursorHeader() + "[" + new string('[', 64) + "0" + new string(']', 64) + "]]}]}";
    var depth = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(nested), 7));
    try { Equal(SpikeStatus.ProtocolFailed, Fetch(depth.Result, out _)); Equal("protocol_reason=depth_limit", Encoding.UTF8.GetString(Diagnostics.Message)); }
    finally { CloseCursor(depth); }
    var metadata = new GeneratedRowsStream("{\"Tables\":[", " ", SpikeContract.MaximumMetadataBytes + 1, "]}", separator: "");
    Throws<ProtocolException>(() => OpenCursor(metadata));
}

static unsafe void CursorEofOwnership()
{
    var item = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]]}]}"), 1));
    try
    {
        Equal(SpikeStatus.Ok, Fetch(item.Result, out _));
        Equal(SpikeStatus.Ok, Fetch(item.Result, out _));
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
        var next = Prepare(item.Connection); // EOF cursor no longer owns an operation lease.
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
        CloseAfterDrain(item.Connection);
        nuint columns, rows;
        Equal(SpikeStatus.Ok, NativeBoundary.ResultShape(item.Result, &columns, &rows));
        Equal((nuint)1, columns); Equal((nuint)1, rows);
        uint type; byte flag; nuint required;
        Equal(SpikeStatus.BufferTooSmall, NativeBoundary.Column(item.Result, 0, &type, &flag, null, 0, &required));
        Equal(SpikeStatus.InvalidState, NativeBoundary.CurrentCell(item.Result, 0, &type, &flag, null, 0, &required));
    }
    finally { CloseCursor(item); }
}

static unsafe void CursorIdle(bool timeout)
{
    var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]]}]}"), 1);
    var item = OpenCursor(stream, timeout ? 100U : 5000U);
    try
    {
        if (!timeout) Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(item.Operation));
        True(SpinWait.SpinUntil(() => stream.Disposed, 5000));
        Equal(timeout ? SpikeStatus.Timeout : SpikeStatus.Cancelled, Fetch(item.Result, out var row)); Equal((byte)0, row);
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
        // Only this known retired operation may still hold asynchronous drain leases.
        // Admission performs no SQL dispatch and retains the terminal result open.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ulong next = 0;
        SpinWait.SpinUntil(() =>
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Drain admission deadline exhausted.");
            ulong candidate = 99;
            var status = NativeBoundary.PrepareOperation(item.Connection, 0, &candidate);
            if (status == SpikeStatus.Busy) { Equal(0UL, candidate); return false; }
            if (status != SpikeStatus.Ok) throw new InvalidOperationException($"{(uint)status}");
            True(candidate != 0);
            next = candidate;
            return true;
        });
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
    }
    finally { CloseCursor(item); }
}

static void CursorCloseFetch()
{
    var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader()), 1) { BlockAtEnd = true };
    var item = OpenCursor(stream);
    try
    {
        var fetch = Task.Run(() => Fetch(item.Result, out _));
        True(stream.Blocked.Wait(5000));
        Equal(SpikeStatus.Busy, Fetch(item.Result, out _));
        Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(item.Result));
        True(fetch.Wait(5000)); Equal(SpikeStatus.Cancelled, fetch.Result);
        True(SpinWait.SpinUntil(() => stream.Disposed, 5000));
        Equal(SpikeStatus.InvalidHandle, Fetch(item.Result, out _));
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
        ulong next = 0;
        True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
        NativeBoundary.CloseOperation(next);
    }
    finally { CloseCursor(item); }
}

static unsafe void CursorRetiredDisposalDrain()
{
    // Product cursor and real cancellation callback, not an artificial retained lease.
    // This proves the mechanism, not the cause of a historical live failure.
    Equal(0x00010003U, NativeBoundary.AbiVersion());
    using var disposalEntered = new ManualResetEventSlim();
    using var disposalRelease = new ManualResetEventSlim();
    var aStream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]]}]}"), 1,
        disposalEntered, disposalRelease);
    var bStream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[2]]}]}"), 1);
    var handler = new SequenceHandler(MetadataResponse(), CursorResponse(aStream), MetadataResponse(), CursorResponse(bStream));
    var token = new StubTokenProvider(Token(SpikeContract.ApprovedResource));
    var connection = AddOfflineConnection(handler, token);
    ulong a = 0, aResult = 0, b = 0, bResult = 0;
    Task<SpikeStatus>? cancelling = null, closing = null;
    try
    {
        a = Prepare(connection, 600000);
        var first = RunOperation(a);
        Equal(SpikeStatus.Ok, first.Status); aResult = first.Result; True(aResult != 0);
        Equal(2, handler.CallCount); Equal(1, handler.QueryCount); Equal(1, token.CallCount);
        cancelling = Task.Run(() => NativeBoundary.CancelOperation(a));
        Wait(disposalEntered);
        Equal(SpikeStatus.Ok, Finish(cancelling));
        closing = Task.Run(() => NativeBoundary.CloseOperation(a));
        Equal(SpikeStatus.Ok, Finish(closing));
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(a));
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseOperation(a));
        ulong pending = 99;
        Equal(SpikeStatus.Busy, NativeBoundary.PrepareOperation(connection, 0, &pending));
        Equal(0UL, pending);
        Equal(SpikeStatus.Busy, NativeBoundary.CloseConnection(connection));
        True(!disposalRelease.IsSet && !aStream.Disposed && !aStream.DisposeBarrierTimedOut);
        Equal(0, aStream.DisposeCount); Equal(0, handler.DisposeCount); Equal(0, token.DisposeCount);
        Equal(2, handler.CallCount); Equal(1, handler.QueryCount); Equal(1, token.CallCount);

        disposalRelease.Set();
        True(SpinWait.SpinUntil(() =>
        {
            ulong candidate = 99;
            var status = NativeBoundary.PrepareOperation(connection, 0, &candidate);
            if (status == SpikeStatus.Busy) { Equal(0UL, candidate); return false; }
            Equal(SpikeStatus.Ok, status); True(candidate != 0); b = candidate; return true;
        }, TimeSpan.FromSeconds(5)));
        True(b != a);
        True(aStream.Disposed && !aStream.DisposeBarrierTimedOut); Equal(1, aStream.DisposeCount);
        Equal(2, handler.CallCount); Equal(1, handler.QueryCount); Equal(1, token.CallCount);
        Equal(SpikeStatus.Cancelled, Fetch(aResult, out var cancelledRow)); Equal((byte)0, cancelledRow);
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CancelOperation(a)); // Never retarget stale A to B.
        var second = RunOperation(b); // Exactly one B execution, no SQL retry.
        Equal(SpikeStatus.Ok, second.Status); bResult = second.Result; True(bResult != 0);
        Equal(SpikeStatus.Ok, Fetch(bResult, out var row)); Equal((byte)1, row);
        Equal(SpikeStatus.Ok, Fetch(bResult, out row)); Equal((byte)0, row); // Validated physical EOF.
        Equal(SpikeStatus.Ok, Fetch(bResult, out row)); Equal((byte)0, row);
        Equal(4, handler.CallCount); Equal(2, handler.QueryCount); Equal(2, token.CallCount);
        Equal(1, bStream.DisposeCount);
        Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(b)); b = 0;
        Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(aResult)); aResult = 0;
        Equal(SpikeStatus.Ok, NativeBoundary.CloseResult(bResult)); bResult = 0;
        CloseAfterDrain(connection);
        Equal(SpikeStatus.InvalidHandle, NativeBoundary.CloseConnection(connection));
        Equal(1, handler.DisposeCount); Equal(1, token.DisposeCount);
        Equal(1, aStream.DisposeCount); Equal(1, bStream.DisposeCount);
    }
    finally
    {
        // Release even on assertion failure, then join before disposing the gates.
        disposalRelease.Set();
        try
        {
            if (cancelling is not null) Finish(cancelling);
            if (closing is not null) Finish(closing);
        }
        finally
        {
            if (aResult != 0) NativeBoundary.CloseResult(aResult);
            if (bResult != 0) NativeBoundary.CloseResult(bResult);
            if (a != 0) NativeBoundary.CloseOperation(a);
            if (b != 0) NativeBoundary.CloseOperation(b);
            if (Handles.TryGetConnection(connection, out _)) CloseAfterDrain(connection);
        }
    }
}

static unsafe void CursorProductBoundary()
{
    if (typeof(RestProtocol).Assembly.GetName().Name != "eventhouse-engine") return; // Explicit historical spike profile.
    Equal(0x00010003U, NativeBoundary.AbiVersion());
    foreach (var privateOperation in new[] { false, true })
    {
        var connection = AddOfflineConnection(new SequenceHandler(MetadataResponse(), CursorResponse(
            new GeneratedRowsStream(CursorHeader(), "[1]", 2, "]}]}") )));
        var operation = privateOperation ? 0 : Prepare(connection);
        ulong result = 0;
        try
        {
            fixed (byte* sql = "SELECT 1"u8)
                Equal(SpikeStatus.Ok, privateOperation ? NativeBoundary.Execute(connection, sql, 8, &result) :
                    NativeBoundary.ExecuteOperation(operation, sql, 8, &result));
            uint type; byte flag; nuint required;
            Equal(SpikeStatus.InvalidState, NativeBoundary.Cell(result, 0, 0, &type, &flag, null, 0, &required));
            Equal(SpikeStatus.Ok, Fetch(result, out _));
            Equal(SpikeStatus.Ok, Fetch(result, out _));
            Equal(SpikeStatus.Ok, Fetch(result, out var row)); Equal((byte)0, row);
            if (!privateOperation) Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(operation));
            CloseAfterDrain(connection);
        }
        finally { NativeBoundary.CloseResult(result); if (operation != 0) NativeBoundary.CloseOperation(operation); if (Handles.TryGetConnection(connection, out _)) CloseAfterDrain(connection); }
    }
}

static unsafe bool TryPrepare(ulong connection, out ulong operation)
{
    ulong handle = 0;
    var status = NativeBoundary.PrepareOperation(connection, 5000, &handle);
    operation = handle;
    if (status == SpikeStatus.Busy) return false;
    Equal(SpikeStatus.Ok, status);
    return true;
}

static void CursorAggregateBounds()
{
    var column = "{\"ColumnName\":\"v\",\"DataType\":\"Other\",\"ColumnType\":\"dynamic\"}";
    foreach (var width in new[] { 4096, 4097 })
    {
        var stream = new GeneratedRowsStream("{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[", column, width, "],\"Rows\":[]}]}" );
        if (width == 4097)
        {
            try { OpenCursor(stream); throw new InvalidOperationException(); }
            catch (ProtocolException exception) { Equal(ProtocolReason.ColumnLimit, exception.Reason); }
        }
        else
        {
            var item = OpenCursor(stream);
            try { Equal(4096, item.Cursor.Columns.Count); Equal(SpikeStatus.Ok, Fetch(item.Result, out _)); }
            finally { CloseCursor(item); }
        }
    }
    var prefix = "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[" + column + "," + column + "," + column + "],\"Rows\":[[";
    // Three sub-limit cells, aggregate exactly8MiB or one byte over. Test data
    // stores cell-sized strings only, never an entire large response.
    foreach (var excess in new[] { 0, 1 })
    {
        var parts = new Stream[]
        {
            new MemoryStream(Encoding.UTF8.GetBytes(prefix)),
            new GeneratedRowsStream("", "1", SpikeContract.MaximumCellBytes - 1, ",", ""),
            new GeneratedRowsStream("", "1", SpikeContract.MaximumCellBytes - 1, ",", ""),
            new MemoryStream(Encoding.UTF8.GetBytes(excess == 0 ? "11]]}]}" : "111]]}]}"))
        };
        var item = OpenCursor(new ResponsePartsStream(parts), 600000);
        try
        {
            Equal(excess == 0 ? SpikeStatus.Ok : SpikeStatus.ProtocolFailed, Fetch(item.Result, out _));
            if (excess != 0) Equal("protocol_reason=row_limit", Encoding.UTF8.GetString(Diagnostics.Message));
        }
        finally { CloseCursor(item); }
    }
    var body = CursorHeader() + "]}]}";
    foreach (var excess in new[] { 0, 1 })
    {
        var whitespace = SpikeContract.MaximumMetadataBytes - Encoding.UTF8.GetByteCount(body) + 1 + excess;
        // Primary Rows' closing bracket is outside metadata accounting.
        var stream = new ResponsePartsStream([
            new GeneratedRowsStream("", " ", whitespace, "", ""),
            new MemoryStream(Encoding.UTF8.GetBytes(body))]);
        if (excess == 0)
        {
            var item = OpenCursor(stream);
            try { Equal(SpikeStatus.Ok, Fetch(item.Result, out _)); }
            finally { CloseCursor(item); }
        }
        else
        {
            try
            {
                var item = OpenCursor(stream);
                try { Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out _)); Equal("protocol_reason=metadata_limit", Encoding.UTF8.GetString(Diagnostics.Message)); }
                finally { CloseCursor(item); }
            }
            catch (ProtocolException exception) { Equal(ProtocolReason.MetadataLimit, exception.Reason); }
        }
    }
}

static void CursorAuxiliary()
{
    var success = Encoding.UTF8.GetString(Fixture("failed-query-status.json"))
        .Replace(", 1, \"Error\", 500,", ", 4, \"Info\", 0,", StringComparison.Ordinal);
    var valueTable = "{\"Columns\":[{\"ColumnName\":\"Value\",\"DataType\":\"String\",\"ColumnType\":\"string\"}],\"TableName\":\"Table_2\",\"Rows\":[[\"x\"]]}";
    success = success.Replace("\"Tables\": [", "\"Tables\": [" + valueTable + ",", StringComparison.Ordinal);
    var item = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(success), 1));
    try
    {
        Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
        Equal(SpikeStatus.Ok, Fetch(item.Result, out row)); Equal((byte)0, row);
    }
    finally { CloseCursor(item); }
    foreach (var body in new[]
    {
        "{\"Tables\":[{\"Rows\":[],\"TableName\":\"Table_0\",\"Columns\":[]}]}",
        "{\"Tables\":[{\"TableName\":\"Table_0\",\"Columns\":[{\"ColumnName\":\"x\",\"ColumnName\":\"x\",\"ColumnType\":\"string\"}],\"Rows\":[]}]}"
    }) Throws<ProtocolException>(() => OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(body), 1)));
    var invalidStatus = success.Replace(", 4, \"Info\", 0,", ", \"4\", \"Info\", 0,", StringComparison.Ordinal);
    var bad = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(invalidStatus), 3));
    try { Equal(SpikeStatus.Ok, Fetch(bad.Result, out _)); Equal(SpikeStatus.ProtocolFailed, Fetch(bad.Result, out _)); }
    finally { CloseCursor(bad); }
}

static void CursorFetchTermination()
{
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader()), 1) { BlockAtEnd = true };
        var item = OpenCursor(stream, mode == 1 ? 200U : 5000U);
        try
        {
            var fetch = Task.Run(() => Fetch(item.Result, out _));
            True(stream.Blocked.Wait(5000));
            if (mode == 0) NativeBoundary.CancelOperation(item.Operation);
            if (mode == 2) NativeBoundary.CloseOperation(item.Operation);
            True(fetch.Wait(5000));
            Equal(mode == 1 ? SpikeStatus.Timeout : SpikeStatus.Cancelled, fetch.Result);
            True(SpinWait.SpinUntil(() => stream.Disposed, 5000));
            Equal(mode == 1 ? SpikeStatus.Timeout : SpikeStatus.Cancelled, Fetch(item.Result, out _));
        }
        finally { CloseCursor(item); }
    }
}

static void CursorPhysicalEof()
{
    var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]]}]}"), 7) { BlockAtEnd = true };
    var item = OpenCursor(stream);
    try
    {
        Equal(SpikeStatus.Ok, Fetch(item.Result, out _));
        var fetch = Task.Run(() => Fetch(item.Result, out _));
        True(stream.Blocked.Wait(5000)); True(!fetch.IsCompleted);
        NativeBoundary.CancelOperation(item.Operation);
        True(fetch.Wait(5000)); Equal(SpikeStatus.Cancelled, fetch.Result);
    }
    finally { CloseCursor(item); }
    var cut = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1],"), 1) { ThrowAtEnd = true };
    var failed = OpenCursor(cut);
    try
    {
        Equal(SpikeStatus.Ok, Fetch(failed.Result, out _));
        Equal(SpikeStatus.TransportFailed, Fetch(failed.Result, out _));
        Equal(SpikeStatus.TransportFailed, Fetch(failed.Result, out _));
    }
    finally { CloseCursor(failed); }
}

static void CursorParserStorage()
{
    using var stream = new GeneratedRowsStream(CursorHeader(), "[{ \"x\": [1, 2], \"s\":\"雪\\u00e9\" }]", 10000, "]}]}");
    using var parser = new StreamingParser(stream);
    parser.InitialiseAsync(CancellationToken.None).GetAwaiter().GetResult();
    var count = 0;
    while (parser.NextAsync(CancellationToken.None).GetAwaiter().GetResult() is { } row)
    {
        Equal(1, row.Count);
        Equal("{ \"x\": [1, 2], \"s\":\"雪\\u00e9\" }", Encoding.UTF8.GetString(row[0].Value));
        count++;
    }
    Equal(10000, count);
    Equal(SpikeContract.InputWindowBytes, parser.MaximumInputCapacity);
    using var large = new GeneratedRowsStream(CursorHeader() + "[{\"x\":\"", "a", 300000, "\"}]]}]}" , "");
    using var spill = new StreamingParser(large);
    spill.InitialiseAsync(CancellationToken.None).GetAwaiter().GetResult();
    var current = spill.NextAsync(CancellationToken.None).GetAwaiter().GetResult()!;
    Equal(300008, current[0].Value.Length);
    True(spill.MaximumInputCapacity <= SpikeContract.MaximumCellBytes + SpikeContract.InputWindowBytes);
    True(spill.MaximumInputCapacity > SpikeContract.InputWindowBytes);
    True(spill.NextAsync(CancellationToken.None).GetAwaiter().GetResult() is null);
    spill.Dispose(); Equal(0, spill.InputCapacity);
}

static void CursorInvalidUtf8()
{
    // Metadata strings and property names must fail at execute too, rather than
    // allowing GetString to replace bad bytes or expose a decoding exception.
    foreach (var prefixText in new[] { "{\"", "{\"Tables\":[{\"TableName\":\"" })
    {
        var prefix = Encoding.UTF8.GetBytes(prefixText);
        var suffix = Encoding.UTF8.GetBytes(prefixText == "{\"" ? "\":0}" : "\",\"Columns\":[],\"Rows\":[]}]}" );
        var body = new byte[prefix.Length + 1 + suffix.Length];
        prefix.CopyTo(body, 0); body[prefix.Length] = 0xFF; suffix.CopyTo(body, prefix.Length + 1);
        foreach (var chunk in new[] { 1, 131072 })
        {
            try { OpenCursor(new ChunkedResponseStream(body, chunk)); throw new InvalidOperationException(); }
            catch (ProtocolException exception) { Equal(ProtocolReason.InvalidJson, exception.Reason); }
        }
    }
    foreach (var invalid in new[]
    {
        new byte[] { 0xFF }, new byte[] { 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 },
        new byte[] { 0xF4, 0x90, 0x80, 0x80 }, new byte[] { 0xE2, 0x82 }
    })
    foreach (var shape in new[] { 0, 1, 2 })
    foreach (var chunk in new[] { 1, 2, 7, 131072 })
    {
        var start = shape switch { 0 => "\"", 1 => "{\"x\":[\"", _ => "{\"" };
        var end = shape switch { 0 => "\"", 1 => "\"]}", _ => "\":1}" };
        var prefix = Encoding.UTF8.GetBytes(CursorHeader() + "[1],[" + start);
        var suffix = Encoding.UTF8.GetBytes(end + "]]}]}");
        var body = new byte[prefix.Length + invalid.Length + suffix.Length];
        prefix.CopyTo(body, 0); invalid.CopyTo(body, prefix.Length); suffix.CopyTo(body, prefix.Length + invalid.Length);
        var stream = new ChunkedResponseStream(body, chunk);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row)); Equal((byte)0, row);
            Equal("protocol_reason=invalid_json", Encoding.UTF8.GetString(Diagnostics.Message));
            Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row)); Equal((byte)0, row);
            Equal("protocol_reason=invalid_json", Encoding.UTF8.GetString(Diagnostics.Message));
            True(stream.Disposed);
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            ulong next = 0;
            True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
        }
        finally { CloseCursor(item); }
    }
}

static unsafe void CursorInlineMessageErrors()
{
    var cases = new List<(string Json, string Category)>();
    foreach (var key in new[] { "@message", "Message", "message" })
    foreach (var (code, category) in new[]
    {
        ("E_QUERY_RESULT_SET_TOO_LARGE", "result_truncated"),
        ("E_RUNAWAY_QUERY", "resource_limit"), ("E_QUERY_COMPLEXITY", "resource_limit"),
        ("E_QUERY_TIMEOUT", "service_timeout"), ("E_TIMEOUT", "service_timeout")
    })
    foreach (var envelope in new[] { "error", "OneApiErrors", "Exceptions", "@permanent" })
    {
        var member = JsonSerializer.Serialize(key) + ":" + JsonSerializer.Serialize("BODY_SENTINEL ('" + code + "'). SECRET_SENTINEL");
        var json = envelope switch
        {
            "error" => "{\"error\":{" + member + "}}",
            "@permanent" => "{" + member + ",\"@permanent\":false}",
            _ => "{" + JsonSerializer.Serialize(envelope) + ":[{" + member + "}]}"
        };
        cases.Add((json, category));
    }
    cases.AddRange(new (string, string)[]
    {
        ("{\"error\":{\"message\":\"E_TIMEOUT E_RUNAWAY_QUERY E_QUERY_RESULT_SET_TOO_LARGE\"}}", "result_truncated"),
        ("{\"Exceptions\":[{\"Message\":\"E_QUERY_RESULT_SET_TOO_LARGE\"},{\"ErrorCode\":\"E_TIMEOUT\"}]}", "result_truncated"),
        ("{\"error\":{\"Code\":\"E_TIMEOUT\",\"@message\":\"E_QUERY_COMPLEXITY\"}}", "resource_limit"),
        ("{\"error\":{\"message\":\"E_TIMEOUT\",\"code\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}}", "result_truncated"),
        ("{\"error\":{\"message\":\"BODY_SENTINEL E_UNKNOWN SECRET_SENTINEL\"}}", "other"),
        ("{\"error\":{\"message\":\"e_timeout X_E_TIMEOUT E_TIMEOUT_SUFFIX 1E_TIMEOUT E_TIMEOUT2 éE_TIMEOUT E_TIMEOUT雪\"}}", "other"),
        ("{\"error\":{\"message\":\"E_TIMEOUT\\nBODY_SENTINEL\"}}", "service_timeout"),
        ("{\"error\":{\"message\":\"\\u0045_TIMEOUT\"}}", "service_timeout"),
        ("{\"error\":{\"message\":123,\"@message\":null,\"Message\":true}}", "other"),
        ("{\"error\":{\"description\":\"E_TIMEOUT\",\"MESSAGE\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}}", "other"),
        ("{\"error\":{\"data\":{\"message\":\"E_TIMEOUT\",\"code\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}}}", "other"),
        ("{\"OneApiErrors\":[],\"data\":{\"message\":\"E_TIMEOUT\"}}", "other"),
        ("{\"message\":\"E_TIMEOUT\"}", "unknown"),
        ("{\"oneapierrors\":[{\"message\":\"E_TIMEOUT\"}]}", "unknown"),
        ("{\"error\":\"BODY_SENTINEL\",\"message\":\"E_TIMEOUT\"}", "unknown"),
        ("{\"data\":{\"error\":{\"message\":\"E_TIMEOUT\"}}}", "unknown")
    });
    foreach (var (json, category) in cases)
    foreach (var chunk in new[] { 1, 7, 131072 })
    {
        var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]," + json + "]}]}"), chunk);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            var expected = "protocol_reason=" + (category == "unknown" ? "unknown_row_object" : "inline_service_error;service_category=" + category) + ";unexpected_token=start_object";
            var buffer = Enumerable.Repeat((byte)0xA5, 8).ToArray();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row)); Equal((byte)0, row);
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
                uint type = 99; byte flag = 99; nuint required = 99;
                fixed (byte* pointer = buffer)
                    Equal(SpikeStatus.ProtocolFailed, NativeBoundary.CurrentCell(item.Result, 0, &type, &flag, pointer, (nuint)buffer.Length, &required));
                Equal(0U, type); Equal((byte)0, flag); Equal((nuint)0, required);
                True(buffer.All(value => value == 0xA5));
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
                True(!Encoding.UTF8.GetString(Diagnostics.Message).Contains("SENTINEL", StringComparison.Ordinal));
            }
            True(stream.Disposed);
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            ulong next = 0;
            True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
        }
        finally { CloseCursor(item); }
    }
}

static unsafe void CursorInlineServiceErrors()
{
    foreach (var (json, category) in new[]
    {
        ("{\"OneApiErrors\":[{\"error\":{\"code\":\"E_QUERY_RESULT_SET_TOO_LARGE\",\"message\":\"BODY_SENTINEL\"}}]}", "result_truncated"),
        ("{\"Exceptions\":[{\"ErrorCode\":\"E_RUNAWAY_QUERY\",\"message\":\"SECRET_SENTINEL\"}]}", "resource_limit"),
        ("{\"error\":{\"code\":\"E_QUERY_COMPLEXITY\"}}", "resource_limit"),
        ("{\"error\":{\"code\":\"E_QUERY_TIMEOUT\"}}", "service_timeout"),
        ("{\"@permanent\":false,\"Code\":\"E_TIMEOUT\"}", "service_timeout"),
        // A known message token selects a closed category without exposing its text.
        ("{\"error\":{\"code\":\"SECRET_SENTINEL\",\"message\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}}", "result_truncated"),
        ("{\"error\":{\"code\":\"SECRET_SENTINEL\",\"message\":\"UNKNOWN_SENTINEL\"}}", "other"),
        ("{\"OneApiErrors\":[]}", "other"),
        ("{\"oneapierrors\":[{\"code\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}]}", "unknown"),
        ("{\"code\":\"E_QUERY_RESULT_SET_TOO_LARGE\"}", "unknown")
    })
    foreach (var chunk in new[] { 1, 7, 131072 })
    {
        var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]," + json + "]}]}"), chunk);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            var expected = "protocol_reason=" + (category == "unknown" ? "unknown_row_object" : "inline_service_error;service_category=" + category) + ";unexpected_token=start_object";
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row)); Equal((byte)0, row);
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
                uint type; byte flag; nuint required;
                Equal(SpikeStatus.ProtocolFailed, NativeBoundary.CurrentCell(item.Result, 0, &type, &flag, null, 0, &required));
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
            }
            True(stream.Disposed);
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            ulong next = 0;
            True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
            NativeBoundary.CloseOperation(next);
        }
        finally { CloseCursor(item); }
    }
    var oversized = OpenCursor(new GeneratedRowsStream(CursorHeader() + "[1],{\"error\":{\"message\":\"", "a",
        SpikeContract.MaximumCellBytes + 1, "\"}}]}]}", ""));
    try
    {
        Equal(SpikeStatus.Ok, Fetch(oversized.Result, out _));
        Equal(SpikeStatus.ProtocolFailed, Fetch(oversized.Result, out _));
        Equal("protocol_reason=cell_limit", Encoding.UTF8.GetString(Diagnostics.Message));
    }
    finally { CloseCursor(oversized); }
}

static unsafe void CursorRowDiagnostics()
{
    foreach (var (body, reason, token) in new[]
    {
        (CursorHeader() + "[1],{\"error\":\"BODY_SENTINEL\"}]}]}", "unknown_row_object", "start_object"),
        (CursorHeader() + "[1],[1,\"BODY_SENTINEL\"]]}]}", "row_width_excess", "string"),
        (CursorHeader() + "[1],[]]}]}", "row_width_short", "end_array"),
        (CursorHeader() + "[1]]},{\"TableName\":\"Table_1\",\"Columns\":[{\"ColumnName\":\"Value\",\"DataType\":\"String\",\"ColumnType\":\"string\"}],\"Rows\":[{\"error\":\"BODY_SENTINEL\"}]}]}", "aux_row_start", "start_object")
    })
    {
        var stream = new ChunkedResponseStream(Encoding.UTF8.GetBytes(body), 1);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            var expected = "protocol_reason=" + reason + ";unexpected_token=" + token;
            for (var i = 0; i < 2; i++)
            {
                Equal(SpikeStatus.ProtocolFailed, Fetch(item.Result, out row)); Equal((byte)0, row);
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
                uint type; byte flag; nuint required;
                Equal(SpikeStatus.ProtocolFailed, NativeBoundary.CurrentCell(item.Result, 0, &type, &flag, null, 0, &required));
                Equal(expected, Encoding.UTF8.GetString(Diagnostics.Message));
                True(!expected.Contains("SENTINEL", StringComparison.Ordinal));
            }
            True(stream.Disposed);
        }
        finally { CloseCursor(item); }
    }
    // CellToken is defensive: structurally valid JSON cannot put a property/end
    // object token directly in a row. Verify its closed mapping without adding
    // an invalid-JSON acceptance path just to exercise unreachable parser state.
    AssertProtocolDiagnostic(() => new ProtocolException(ProtocolReason.CellToken, JsonTokenType.PropertyName),
        "cell_token;unexpected_token=property_name");
    AssertProtocolDiagnostic(() => new ProtocolException(ProtocolReason.CellToken, (JsonTokenType)255),
        "cell_token;unexpected_token=unknown");
}

static unsafe void CursorTerminalGetters()
{
    foreach (var mode in new[] { 0, 1, 2, 3 })
    {
        // Mode3 explicitly cancels between the two calls of the sizing/copy
        // protocol. The caller buffer must stay untouched on terminal failure.
        var body = mode == 2 ? Fixture("failed-query-status.json") :
            Encoding.UTF8.GetBytes(CursorHeader() + "[123]]}]}" );
        var stream = new ChunkedResponseStream(body, 1);
        var item = OpenCursor(stream, mode == 1 ? 1000U : 5000U);
        try
        {
            uint type = 99; byte isNull = 99; nuint required = 99;
            Equal(SpikeStatus.InvalidState, NativeBoundary.CurrentCell(item.Result, 0, &type, &isNull, null, 0, &required));
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            Equal(SpikeStatus.BufferTooSmall, NativeBoundary.CurrentCell(item.Result, 0, &type, &isNull, null, 0, &required));
            True(required > 0);
            var buffer = Enumerable.Repeat((byte)0xA5, (int)required + 2).ToArray();
            var expected = mode == 1 ? SpikeStatus.Timeout : mode == 2 ? SpikeStatus.ProtocolFailed : SpikeStatus.Cancelled;
            if (mode == 2) Equal(expected, Fetch(item.Result, out row));
            else if (mode != 1) Equal(SpikeStatus.Ok, NativeBoundary.CancelOperation(item.Operation));
            True(SpinWait.SpinUntil(() => stream.Disposed, 5000));
            var reads = stream.ReadCalls;
            var message = mode == 2 ? "protocol_reason=failed_query_status" : mode == 1 ? "Operation timed out." : "Operation was cancelled.";
            for (var repeat = 0; repeat < 3; repeat++)
            {
                type = 99; isNull = 99; required = 99;
                fixed (byte* pointer = buffer)
                    Equal(expected, NativeBoundary.CurrentCell(item.Result, 0, &type, &isNull, pointer + 1, (nuint)buffer.Length - 2, &required));
                Equal(0U, type); Equal((byte)0, isNull); Equal((nuint)0, required);
                True(buffer.All(value => value == 0xA5));
                Equal(message, Encoding.UTF8.GetString(Diagnostics.Message));
                Equal(expected, Fetch(item.Result, out row)); Equal((byte)0, row);
                Equal(message, Encoding.UTF8.GetString(Diagnostics.Message));
            }
            Equal(reads, stream.ReadCalls); // Terminal getter/next never touches HTTP.
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            ulong next = 0;
            True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
            // Failed result remains open throughout connection reuse.
        }
        finally { CloseCursor(item); }
    }

    var success = OpenCursor(new ChunkedResponseStream(Encoding.UTF8.GetBytes(CursorHeader() + "[1]]}]}"), 1));
    try
    {
        uint type; byte flag; nuint required;
        Equal(SpikeStatus.InvalidState, NativeBoundary.CurrentCell(success.Result, 0, &type, &flag, null, 0, &required));
        Equal(SpikeStatus.Ok, Fetch(success.Result, out _));
        Equal(SpikeStatus.Ok, Fetch(success.Result, out var row)); Equal((byte)0, row);
        Equal(SpikeStatus.InvalidState, NativeBoundary.CurrentCell(success.Result, 0, &type, &flag, null, 0, &required));
    }
    finally { CloseCursor(success); }
}

static void CursorMetadataAfterSpill()
{
    const string auxiliary = "]},{\"TableName\":\"Table_1\",\"Columns\":[{\"ColumnName\":\"Value\",\"DataType\":\"String\",\"ColumnType\":\"string\"}],\"Rows\":[[\"";
    foreach (var auxiliaryBytes in new[] { 1000, 600000 })
    {
        // Primary300k grows the parser input to524288. The following metadata
        // budget is smaller, and must not shrink that array below retained bytes.
        var stream = new ResponsePartsStream([
            new GeneratedRowsStream(CursorHeader() + "[\"", "a", 300000, "\"]", ""),
            new GeneratedRowsStream(auxiliary, "b", auxiliaryBytes, "\"]]}]}", "")]);
        var item = OpenCursor(stream);
        try
        {
            Equal(SpikeStatus.Ok, Fetch(item.Result, out var row)); Equal((byte)1, row);
            Equal(300002, CurrentBytes(item.Result).Length);
            var expected = auxiliaryBytes == 1000 ? SpikeStatus.Ok : SpikeStatus.ProtocolFailed;
            Equal(expected, Fetch(item.Result, out row)); Equal((byte)0, row);
            if (expected != SpikeStatus.Ok) Equal("protocol_reason=metadata_limit", Encoding.UTF8.GetString(Diagnostics.Message));
            Equal(expected, Fetch(item.Result, out row)); Equal((byte)0, row);
            if (expected != SpikeStatus.Ok) Equal("protocol_reason=metadata_limit", Encoding.UTF8.GetString(Diagnostics.Message));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(item.Operation));
            ulong next = 0;
            True(SpinWait.SpinUntil(() => TryPrepare(item.Connection, out next), 5000));
            Equal(SpikeStatus.Ok, NativeBoundary.CloseOperation(next));
        }
        finally { CloseCursor(item); }
    }
}

static unsafe void NativeStreamingSmoke(string path)
{
    // Deliberately retain the NativeAOT module for process lifetime. Never Free.
    var library = System.Runtime.InteropServices.NativeLibrary.Load(Path.GetFullPath(path));
    foreach (var name in new[]
    {
        "abi_version", "open", "execute", "result_shape", "result_column", "result_cell",
        "connection_close", "result_close", "error_code", "error_message", "operation_prepare",
        "operation_execute", "operation_cancel", "operation_close", "result_next", "result_current_cell"
    }) True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "eh_" + name + "_v1", out _));
    True(!System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "eh_spike_test_result_v1", out _));
    var version = (delegate* unmanaged[Cdecl]<uint>)System.Runtime.InteropServices.NativeLibrary.GetExport(library, "eh_abi_version_v1");
    Equal(0x00010003U, version());
    var next = (delegate* unmanaged[Cdecl]<ulong, byte*, uint>)System.Runtime.InteropServices.NativeLibrary.GetExport(library, "eh_result_next_v1");
    var cell = (delegate* unmanaged[Cdecl]<ulong, nuint, uint*, byte*, byte*, nuint, nuint*, uint>)System.Runtime.InteropServices.NativeLibrary.GetExport(library, "eh_result_current_cell_v1");
    var shape = (delegate* unmanaged[Cdecl]<ulong, nuint*, nuint*, uint>)System.Runtime.InteropServices.NativeLibrary.GetExport(library, "eh_result_shape_v1");
    var close = (delegate* unmanaged[Cdecl]<ulong, uint>)System.Runtime.InteropServices.NativeLibrary.GetExport(library, "eh_result_close_v1");
    for (var i = 0; i < 100; i++)
    {
        byte row = 99, flag = 99; uint type = 99; nuint required = 99, columns = 99, rows = 99;
        Equal(1U, next(0, null));
        Equal(3U, next(0xC011000000000001, &row)); Equal((byte)0, row);
        Equal(3U, cell(0, 0, &type, &flag, null, 0, &required));
        Equal(0U, type); Equal((byte)0, flag); Equal((nuint)0, required);
        Equal(3U, shape(0, &columns, &rows)); Equal((nuint)0, columns); Equal((nuint)0, rows);
        Equal(3U, close(0));
    }
}

internal sealed class CountedCursorResponse : HttpResponseMessage
{
    internal readonly CountedStreamContent TrackedContent;
    internal int DisposeCount;
    internal CountedCursorResponse(Stream stream) : base(HttpStatusCode.OK)
    {
        TrackedContent = new CountedStreamContent(stream);
        TrackedContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Content = TrackedContent;
    }
    protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }
}

internal sealed class CountedStreamContent(Stream stream) : StreamContent(stream)
{
    internal int DisposeCount;
    protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }
}

internal sealed class ChunkedResponseStream(byte[] bytes, int chunk,
    ManualResetEventSlim? disposalEntered = null, ManualResetEventSlim? disposalRelease = null) : Stream
{
    private int _position;
    private int _disposeStarted;
    internal bool BlockAtEnd;
    internal bool ThrowAtEnd;
    internal readonly ManualResetEventSlim Blocked = new();
    internal volatile bool Disposed;
    internal int ReadCalls;
    internal int DisposeCount;
    internal volatile bool DisposeBarrierTimedOut;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        if (_position == bytes.Length && ThrowAtEnd) throw new IOException("synthetic");
        if (_position == bytes.Length && BlockAtEnd)
        {
            Blocked.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _position);
        bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
    protected override void Dispose(bool disposing)
    {
        // Stream and owning HttpContent may both call Dispose. Resource disposal is once-only.
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        disposalEntered?.Set();
        if (disposalRelease is not null && !disposalRelease.Wait(TimeSpan.FromSeconds(5)))
            DisposeBarrierTimedOut = true; // Callback exceptions are swallowed, so assert this explicitly.
        Interlocked.Increment(ref DisposeCount);
        Disposed = true;
        base.Dispose(disposing);
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ResponsePartsStream(Stream[] parts) : Stream
{
    private int _part;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_part < parts.Length)
        {
            var count = await parts[_part].ReadAsync(buffer, cancellationToken);
            if (count != 0) return count;
            parts[_part++].Dispose();
        }
        return 0;
    }
    protected override void Dispose(bool disposing) { foreach (var part in parts) part.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class GeneratedRowsStream(string header, string row, int count, string trailer, string separator = ",") : Stream
{
    private readonly byte[] _header = Encoding.UTF8.GetBytes(header);
    private readonly byte[] _row = Encoding.UTF8.GetBytes(row);
    private readonly byte[] _separator = Encoding.UTF8.GetBytes(separator);
    private readonly byte[] _trailer = Encoding.UTF8.GetBytes(trailer);
    private long _position;
    internal long BytesRead;
    internal int ReadCalls;
    internal volatile bool Disposed;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        var total = _header.Length + (long)count * _row.Length + Math.Max(0, count - 1L) * _separator.Length + _trailer.Length;
        var written = 0;
        while (written < buffer.Length && _position < total)
        {
            ReadOnlySpan<byte> segment;
            var p = _position;
            if (p < _header.Length) segment = _header.AsSpan((int)p);
            else
            {
                p -= _header.Length;
                var rowsLength = (long)count * _row.Length + Math.Max(0, count - 1L) * _separator.Length;
                if (p >= rowsLength) segment = _trailer.AsSpan((int)(p - rowsLength));
                else
                {
                    var slot = (int)(p % (_row.Length + _separator.Length));
                    segment = slot < _row.Length ? _row.AsSpan(slot) : _separator.AsSpan(slot - _row.Length);
                }
            }
            var length = Math.Min(segment.Length, buffer.Length - written);
            segment[..length].CopyTo(buffer.Span[written..]);
            written += length; _position += length;
        }
        BytesRead += written;
        return ValueTask.FromResult(written);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class InspectRequestsHandler(byte[] fixture) : HttpMessageHandler
{
    internal readonly List<(Uri Uri, string Method, string? Authorization, string? Body)> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Method.Method, request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Requests.Count == 1
                ? "{\"AzureAD\":{\"KustoServiceResourceId\":\"https://kusto.kusto.windows.net\"}}"
                : Encoding.UTF8.GetString(fixture), Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class OperationHandler(int stage, ManualResetEventSlim entered, ManualResetEventSlim release,
    bool ignoreCancellation = false) : HttpMessageHandler
{
    internal int DisposeCount;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var metadata = request.RequestUri!.AbsolutePath.EndsWith("metadata", StringComparison.Ordinal);
        if (stage == 1 && metadata)
        {
            entered.Set();
            await Task.Run(() =>
            {
                if (!release.Wait(TimeSpan.FromSeconds(5), ignoreCancellation ? CancellationToken.None : cancellationToken))
                    throw new InvalidOperationException();
            }).ConfigureAwait(false);
        }
        if (metadata) return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"AzureAD\":{\"KustoServiceResourceId\":\"https://kusto.kusto.windows.net\"}}", Encoding.UTF8, "application/json")
        };
        if (stage == 2) return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = ReadContent()
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "typed-null-success.json")),
                Encoding.UTF8, "application/json")
        };
    }
    private HttpContent ReadContent()
    {
        var content = new StreamContent(new BlockingReadStream(entered));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
    protected override void Dispose(bool disposing) { Interlocked.Increment(ref DisposeCount); base.Dispose(disposing); }
}

internal sealed class OperationTokenProvider(bool block, ManualResetEventSlim entered, ManualResetEventSlim release, string token) : ITokenProvider
{
    public async ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (block)
        {
            entered.Set();
            await Task.Run(() =>
            {
                if (!release.Wait(TimeSpan.FromSeconds(5), cancellationToken)) throw new InvalidOperationException();
            }).ConfigureAwait(false);
        }
        return new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(5));
    }
    public void Dispose() { }
}

internal sealed class BlockingReadStream(ManualResetEventSlim entered) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        entered.Set();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class StubTokenProvider(string token) : ITokenProvider
{
    internal int CallCount { get; private set; }
    internal int DisposeCount { get; private set; }
    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(5)));
    }
    public void Dispose() { DisposeCount++; }
}

internal sealed class FailingTokenProvider(Exception exception) : ITokenProvider
{
    internal int CallCount { get; private set; }
    internal int DisposeCount { get; private set; }
    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromException<AccessToken>(exception);
    }
    public void Dispose() { DisposeCount++; }
}

internal sealed class SequenceHandler(params object[] responses) : HttpMessageHandler
{
    private readonly Queue<object> _responses = new(responses);
    internal int CallCount { get; private set; }
    internal int QueryCount { get; private set; }
    internal int DisposeCount { get; private set; }

    protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (request.RequestUri?.AbsolutePath == "/v1/rest/query") QueryCount++;
        if (_responses.Count == 0) throw new InvalidOperationException();
        var response = _responses.Dequeue();
        if (response is Exception exception) throw exception;
        return Task.FromResult((HttpResponseMessage)response);
    }
}
