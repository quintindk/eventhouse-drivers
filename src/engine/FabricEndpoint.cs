namespace Eventhouse.Engine.Spike;

internal static class FabricEndpoint
{
    private const string Suffix = ".kusto.fabric.microsoft.com";

    internal static bool TryParse(string value, out Uri? origin)
    {
        origin = null;
        const string scheme = "https://";
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var host = value.AsSpan(scheme.Length);
        if (host.EndsWith("/", StringComparison.Ordinal)) host = host[..^1];
        if (host.Length > 253 || host.Length <= Suffix.Length ||
            !host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;

        // Validate the raw authority before Uri can remove empty delimiters, ports,
        // escapes, trailing dots or other ambiguous spellings.
        var labelLength = 0;
        char previous = '\0';
        foreach (var character in host)
        {
            if (character == '.')
            {
                if (labelLength == 0 || previous == '-') return false;
                labelLength = 0;
            }
            else
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character == '-') ||
                    (labelLength == 0 && character == '-') || ++labelLength > 63) return false;
            }
            previous = character;
        }
        if (labelLength == 0 || previous == '-') return false;
        origin = new Uri(scheme + host.ToString().ToLowerInvariant() + "/", UriKind.Absolute);
        return true;
    }
}
