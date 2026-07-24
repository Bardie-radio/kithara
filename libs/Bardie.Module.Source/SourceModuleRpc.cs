using System.Diagnostics;
using Bardie.Source.V1;
using Grpc.Core;

namespace Bardie.Module.Source;

/// <summary>Shared RPC / IO helpers for source module façades and playback loops.</summary>
public static class SourceModuleRpc
{
    public static RpcException MapStartFailure(Exception ex) =>
        ex switch
        {
            ArgumentException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
            InvalidOperationException => new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message)),
            _ => new RpcException(new Status(StatusCode.Internal, ex.Message)),
        };

    public static bool IsBrokenPipe(IOException ex) =>
        ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException?.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) == true;

    public static void TagTrackJob(Activity? activity, TrackJob job, string moduleSlug)
    {
        ArgumentNullException.ThrowIfNull(job);
        activity?.SetTag("struna.id", job.StrunaId);
        activity?.SetTag("source.module", moduleSlug);
        activity?.SetTag("track.job_id", job.TrackJobId);
        activity?.SetTag("track.ref", job.TrackRef);
    }
}

/// <summary>Polls <see cref="ITrackJobRegistry"/> and writes <see cref="TrackStatusEvent"/> updates.</summary>
public static class TrackStatusStreaming
{
    public static async Task WriteEventsAsync(
        ITrackJobRegistry jobs,
        string trackJobId,
        IServerStreamWriter<TrackStatusEvent> responseStream,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackJobId);
        ArgumentNullException.ThrowIfNull(responseStream);

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(200);

        TrackJob? job = null;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (jobs.TryGet(trackJobId, out job) && job is not null)
            {
                break;
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (job is null)
        {
            // Still missing after brief wait — host likely raced a replace/stop.
            await responseStream.WriteAsync(
                    new TrackStatusEvent
                    {
                        TrackJobId = trackJobId,
                        State = TrackState.Ended,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        TrackState? last = null;
        string? lastTitle = null;
        string? lastArtist = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!jobs.TryGet(trackJobId, out job) || job is null)
            {
                if (last is not null and not TrackState.Ended and not TrackState.Error)
                {
                    await responseStream.WriteAsync(
                            new TrackStatusEvent
                            {
                                TrackJobId = trackJobId,
                                State = TrackState.Ended,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            }

            var title = job.Title ?? string.Empty;
            var artist = job.Artist ?? string.Empty;
            if (last != job.State
                || !string.Equals(lastTitle, title, StringComparison.Ordinal)
                || !string.Equals(lastArtist, artist, StringComparison.Ordinal))
            {
                last = job.State;
                lastTitle = title;
                lastArtist = artist;
                await responseStream.WriteAsync(
                        new TrackStatusEvent
                        {
                            TrackJobId = job.TrackJobId,
                            State = job.State,
                            Title = title,
                            Artist = artist,
                            ErrorMessage = job.ErrorMessage ?? string.Empty,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (job.State is TrackState.Ended or TrackState.Error)
                {
                    break;
                }
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
