using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Eventhouse.Engine.Spike;

internal sealed class EngineConnection : IDisposable
{
    private readonly string _database;
    private readonly ITokenProvider _tokenProvider;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    internal EngineConnection(Uri queryOrigin, string database, string tenantId, string clientId, string clientSecret, uint timeoutMilliseconds)
    {
        if (!FabricEndpoint.TryParse(queryOrigin.OriginalString, out var validatedOrigin)) throw new ArgumentException();
        _database = database;
        _tokenProvider = new AzureTokenProvider(tenantId, clientId, clientSecret);
        _httpClient = new HttpClient(CreateEventhouseHandler())
        {
            BaseAddress = validatedOrigin,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds is 0 ? 30_000 : timeoutMilliseconds);
    }

    internal EngineConnection(string database, ITokenProvider tokenProvider, HttpClient httpClient, TimeSpan timeout)
    {
        _database = database;
        _tokenProvider = tokenProvider;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri(SpikeContract.ApprovedQueryUri, UriKind.Absolute);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _timeout = timeout;
    }

    internal static SocketsHttpHandler CreateEventhouseHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    internal QueryResult Execute(ReadOnlySpan<byte> sql)
    {
        var query = sql.ToArray();
        using var cancellation = new CancellationTokenSource(_timeout);
        return ExecuteAsync(query, cancellation.Token).GetAwaiter().GetResult();
    }

    internal TimeSpan DefaultTimeout => _timeout;
    internal Uri QueryOrigin => _httpClient.BaseAddress!;

    internal QueryResult Execute(ReadOnlySpan<byte> sql, CancellationToken cancellationToken) =>
        ExecuteAsync(sql.ToArray(), cancellationToken).GetAwaiter().GetResult();

    internal QueryResult ExecuteStreaming(ReadOnlySpan<byte> sql, EngineOperation operation, bool privateOperation = false) =>
        ExecuteAsync(sql.ToArray(), operation.Token, operation, privateOperation).GetAwaiter().GetResult();

    private async Task<QueryResult> ExecuteAsync(byte[] sql, CancellationToken cancellationToken,
        EngineOperation? operation = null, bool privateOperation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ValidateMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var transportFailure = FindHttpTransportFailure(exception);
            if (transportFailure is null) throw;
            throw new EngineTransportException(TransportStage.Metadata, GetTransportReason(transportFailure));
        }

        Azure.Core.AccessToken token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var transportFailure = FindHttpTransportFailure(exception);
            if (transportFailure is null) throw;
            throw new EngineTransportException(TransportStage.Token, GetTransportReason(transportFailure));
        }

        if (!JwtAudienceValidator.HasApprovedAudience(token.Token))
        {
            throw new AuthenticationException();
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteQueryAsync(sql, token.Token, cancellationToken, operation, privateOperation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var transportFailure = FindHttpTransportFailure(exception);
            if (transportFailure is null) throw;
            throw new EngineTransportException(TransportStage.Query, GetTransportReason(transportFailure));
        }
    }

    private async Task<QueryResult> ExecuteQueryAsync(byte[] sql, string accessToken, CancellationToken cancellationToken,
        EngineOperation? operation, bool privateOperation)
    {
        var body = RestProtocol.BuildRequest(_database, sql);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rest/query");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("x-ms-app", "eventhouse-connectivity-wp003");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new QueryHttpException(response.StatusCode);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProtocolException(ProtocolReason.ContentType);
        }

        if (operation is not null)
        {
            var cursor = await StreamingResult.CreateAsync(response, operation, privateOperation).ConfigureAwait(false);
            response = null!; // Cursor now owns response and stream.
            return cursor;
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = RestProtocol.ParseResponse(bytes);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return result;
        }
        finally { response?.Dispose(); }
    }

    private static HttpRequestException? FindHttpTransportFailure(Exception exception, int depth = 0)
    {
        if (depth >= 16)
        {
            return null;
        }
        if (exception is HttpRequestException transportFailure)
        {
            return transportFailure;
        }
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var nestedTransportFailure = FindHttpTransportFailure(inner, depth + 1);
                if (nestedTransportFailure is not null)
                {
                    return nestedTransportFailure;
                }
            }
            return null;
        }
        return exception.InnerException is null ? null : FindHttpTransportFailure(exception.InnerException, depth + 1);
    }

    private static TransportReason GetTransportReason(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.ConfigurationLimitExceeded => TransportReason.Configuration,
        HttpRequestError.NameResolutionError => TransportReason.NameResolution,
        HttpRequestError.ConnectionError => TransportReason.Connection,
        HttpRequestError.SecureConnectionError => TransportReason.SecureConnection,
        HttpRequestError.HttpProtocolError => TransportReason.HttpProtocol,
        HttpRequestError.ExtendedConnectNotSupported => TransportReason.ExtendedConnect,
        HttpRequestError.VersionNegotiationError => TransportReason.VersionNegotiation,
        HttpRequestError.UserAuthenticationError => TransportReason.UserAuthentication,
        HttpRequestError.ProxyTunnelError => TransportReason.ProxyTunnel,
        HttpRequestError.InvalidResponse => TransportReason.InvalidResponse,
        HttpRequestError.ResponseEnded => TransportReason.ResponseEnded,
        _ => TransportReason.Unknown
    };

    private async Task ValidateMetadataAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/rest/auth/metadata");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AuthenticationException();
        }

        if (!IsJson(response.Content.Headers.ContentType?.MediaType))
        {
            throw new AuthenticationException();
        }

        try
        {
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("AzureAD", out var azureAd) ||
                azureAd.ValueKind != JsonValueKind.Object ||
                !azureAd.TryGetProperty("KustoServiceResourceId", out var resource) ||
                resource.ValueKind != JsonValueKind.String ||
                !string.Equals(resource.GetString(), SpikeContract.ApprovedResource, StringComparison.Ordinal))
            {
                throw new AuthenticationException();
            }
        }
        catch (JsonException)
        {
            throw new AuthenticationException();
        }
        catch (ProtocolException)
        {
            throw new AuthenticationException();
        }
    }

    private static bool IsJson(string? mediaType) => mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > SpikeContract.MaximumResponseBytes)
        {
            throw new ProtocolException(ProtocolReason.SizeHeader);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > SpikeContract.MaximumResponseBytes)
            {
                throw new ProtocolException(ProtocolReason.SizeRead);
            }

            output.Write(buffer, 0, read);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _tokenProvider.Dispose();
    }
}

internal sealed class QueryHttpException(HttpStatusCode statusCode) : Exception
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class AuthenticationException : Exception;

internal enum TransportStage
{
    Metadata,
    Token,
    Query,
    Unknown
}

internal enum TransportReason
{
    Configuration,
    NameResolution,
    Connection,
    SecureConnection,
    HttpProtocol,
    ExtendedConnect,
    VersionNegotiation,
    UserAuthentication,
    ProxyTunnel,
    InvalidResponse,
    ResponseEnded,
    Unknown
}

internal sealed class EngineTransportException(TransportStage stage, TransportReason reason) : Exception
{
    internal TransportStage Stage { get; } = stage;
    internal TransportReason Reason { get; } = reason;
}
