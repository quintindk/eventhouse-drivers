using System.Text;

namespace Eventhouse.Engine.Spike;

internal static class Diagnostics
{
    private static readonly byte[] BoundaryFailureMessage = "Unhandled managed exception."u8.ToArray();
    [ThreadStatic]
    private static SpikeStatus s_code;

    [ThreadStatic]
    private static byte[]? s_message;

    internal static void Clear()
    {
        s_code = SpikeStatus.Ok;
        s_message = [];
    }

    internal static SpikeStatus Set(SpikeStatus code, string fixedMessage)
    {
        s_code = code;
        s_message = Encoding.UTF8.GetBytes(fixedMessage);
        return code;
    }

    internal static SpikeStatus SetBoundaryFailure()
    {
        s_code = SpikeStatus.InternalError;
        s_message = BoundaryFailureMessage;
        return SpikeStatus.InternalError;
    }

    internal static SpikeStatus Code => s_code;

    internal static ReadOnlySpan<byte> Message => s_message ?? [];
}
