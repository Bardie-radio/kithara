namespace Bardie.Module.Source;

/// <summary>
/// Session audio plane format locked for MVP
/// (<c>s16le</c> / 48&nbsp;kHz / stereo — see grpc-source-module / ADR 004).
/// Neck silence + encode and every source writer must agree on these values.
/// </summary>
public static class CanonicalPcm
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerSample = sizeof(short);
    public const int BytesPerFrame = BytesPerSample * Channels;
    public const int BytesPerSecond = SampleRate * BytesPerFrame;
}
