namespace Kithara.Infrastructure.Neck;

/// <summary>
/// Neck options. Env overlay <c>BARDIE_STRUNA_FIFO_PATH</c> is applied in <c>AddKitharaNeck</c> only.
/// </summary>
public sealed class NeckOptions
{
    public const string SectionName = "Neck";

    /// <summary>
    /// Root for live Struna PCM FIFOs (shared Compose volume with source modules).
    /// Not library/download blob storage — that is <c>BARDIE_STORAGE_PATH</c>.
    /// Example: <c>/fifos</c> → <c>/fifos/strunas/{strunaId}.pcm</c>.
    /// </summary>
    public string StrunaFifoRoot { get; set; } = "data/struna-fifos";

    /// <summary>
    /// Directory containing libav* shared libraries for FFmpeg.AutoGen (same as Magpie).
    /// Env: <c>BARDIE_FFMPEG_ROOT</c>. When unset, common system lib paths are probed.
    /// </summary>
    public string? FfmpegRootPath { get; set; }

    /// <summary>Operator MP3 bitrate for the locked encode profile (~128 kbps).</summary>
    public int Mp3BitrateKbps { get; set; } = 128;
}
