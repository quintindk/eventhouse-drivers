using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;

namespace Eventhouse.Engine.Spike;

internal interface ITokenProvider : IDisposable
{
    ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken);
}

internal sealed class AzureTokenProvider : ITokenProvider
{
    private readonly HttpClient _tokenHttpClient;
    private readonly ClientSecretCredential _credential;

    internal AzureTokenProvider(string tenantId, string clientId, string clientSecret)
    {
        var handler = CreateHandler();
        _tokenHttpClient = new HttpClient(handler, disposeHandler: true);
        _credential = new ClientSecretCredential(tenantId, clientId, clientSecret, new ClientSecretCredentialOptions
        {
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            Transport = new HttpClientTransport(_tokenHttpClient)
        });
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    };

    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken) =>
        _credential.GetTokenAsync(new TokenRequestContext([SpikeContract.Scope]), cancellationToken);

    public void Dispose() => _tokenHttpClient.Dispose();
}

internal static class JwtAudienceValidator
{
    internal static bool HasApprovedAudience(string token)
    {
        byte[]? decoded = null;
        try
        {
            if (token.Length is 0 or > 16_384)
            {
                return false;
            }

            var firstDot = token.IndexOf('.');
            var secondDot = firstDot < 0 ? -1 : token.IndexOf('.', firstDot + 1);
            if (firstDot <= 0 || secondDot <= firstDot + 1 || token.IndexOf('.', secondDot + 1) >= 0)
            {
                return false;
            }

            var payload = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
            var maximum = Base64Url.GetMaxDecodedLength(payload.Length);
            decoded = new byte[maximum];
            if (!Base64Url.TryDecode(payload, decoded, out var written))
            {
                return false;
            }

            using var document = JsonDocument.Parse(decoded.AsMemory(0, written));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("aud", out var audience) &&
                audience.ValueKind == JsonValueKind.String &&
                string.Equals(audience.GetString(), SpikeContract.ApprovedResource, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            if (decoded is not null)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
    }
}

internal static class Base64Url
{
    internal static int GetMaxDecodedLength(int encodedLength) => checked(((encodedLength + 3) / 4) * 3);

    internal static bool TryDecode(ReadOnlySpan<char> input, Span<byte> destination, out int written)
    {
        written = 0;
        if (input.IsEmpty || input.Length % 4 == 1)
        {
            return false;
        }

        Span<char> padded = input.Length + 2 <= 1024 ? stackalloc char[input.Length + 2] : new char[input.Length + 2];
        for (var index = 0; index < input.Length; index++)
        {
            padded[index] = input[index] switch
            {
                '-' => '+',
                '_' => '/',
                var character => character
            };
        }

        var paddedLength = input.Length;
        while (paddedLength % 4 != 0)
        {
            padded[paddedLength++] = '=';
        }

        return Convert.TryFromBase64Chars(padded[..paddedLength], destination, out written);
    }
}
