using Bardie.Source.V1;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Neck;

public sealed partial class Neck
{
    /// <summary>
    /// Starts (or replaces) a track job. Empty module/trackRef = unpause: ResumeTrack + silence off when Running.
    /// Encoder stays up across play / skip. Silence stays on until TrackStatus Running (PCM flowing).
    /// </summary>
    public async Task<PlayTrackOutcome> PlayTrackAsync(
        Guid strunaId,
        string? moduleSlug,
        string? trackRef,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(strunaId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PlayTrackCoreAsync(strunaId, moduleSlug, trackRef, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PlayTrackOutcome> PlayTrackCoreAsync(
        Guid strunaId,
        string? moduleSlug,
        string? trackRef,
        CancellationToken cancellationToken)
    {
        var struna = await GetStrunaAsync(strunaId, cancellationToken).ConfigureAwait(false);
        if (struna is null)
        {
            return new PlayTrackOutcome(false, null, PlayTrackError.StrunaNotFound, null);
        }

        if (string.IsNullOrWhiteSpace(moduleSlug) || string.IsNullOrWhiteSpace(trackRef))
        {
            return await UnpauseCoreAsync(strunaId, cancellationToken).ConfigureAwait(false);
        }

        var previousModule = _jobs.TryGetValue(strunaId, out var previous) ? previous.ModuleSlug : null;
        var hadTrackedJob = previous is not null;
        await StopCurrentTrackAsync(strunaId, cancellationToken).ConfigureAwait(false);

        // Only sweep orphans when Neck had no bookkeeping — Magpie Create already cancels siblings.
        // Calling StopTracksForStruna immediately before StartTrack races the new job on Magpie.
        // See NECK-SWP-001 / kithara#26 — symptom treatment on the hot path.
        if (!hadTrackedJob)
        {
            await StopOrphanWritersAsync(strunaId, moduleSlug.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(previousModule)
                 && !string.Equals(previousModule, moduleSlug.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await StopOrphanWritersAsync(strunaId, previousModule, cancellationToken)
                .ConfigureAwait(false);
        }

        // Keep encoder; silence fills the gap until TrackStatus Running (module writing PCM).
        // After host restart the DB row exists but the encode session may not — restore first.
        await EnsureEncodeAliveAsync(strunaId, struna.Slug, recreateFifo: false, cancellationToken)
            .ConfigureAwait(false);
        _encoder.SetSilence(strunaId, true);

        var fifo = await EnsureStrunaFifoAsync(strunaId, cancellationToken).ConfigureAwait(false);
        var start = await _orch.StartTrackAsync(
                moduleSlug.Trim(),
                strunaId.ToString("D"),
                trackRef.Trim(),
                fifo,
                cancellationToken)
            .ConfigureAwait(false);

        if (!start.Ok || string.IsNullOrWhiteSpace(start.TrackJobId))
        {
            return new PlayTrackOutcome(
                false,
                null,
                PlayTrackError.ModuleFailed,
                start.FailureReason ?? "start_track_failed");
        }

        var job = new ActiveTrackJob(start.ModuleSlug!, start.TrackJobId, trackRef.Trim());
        _jobs[strunaId] = job;
        SetNowPlaying(strunaId, new NowPlayingSnapshot(
            job.ModuleSlug,
            job.TrackRef,
            job.TrackJobId,
            Title: null,
            Artist: null,
            Paused: false));
        StartStatusWatcher(strunaId, job);
        return new PlayTrackOutcome(true, start.TrackJobId, null, null);
    }

    /// <summary>
    /// Pause: silence on + optional module <c>PauseTrack</c> when the source advertises pause.
    /// Idempotent — always succeeds for an alive Struna (silence feeder is the contract).
    /// </summary>
    public async Task<PlayTrackOutcome> PauseTrackAsync(
        Guid strunaId,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(strunaId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _encoder.SetSilence(strunaId, true);

            string? trackJobId = null;
            if (_jobs.TryGetValue(strunaId, out var job))
            {
                trackJobId = job.TrackJobId;
                var pause = await _orch.PauseTrackAsync(job.ModuleSlug, job.TrackJobId, cancellationToken)
                    .ConfigureAwait(false);
                if (!pause.Ok)
                {
                    _logger.LogInformation(
                        "PauseTrack unavailable or failed for Struna {Id}: {Reason}; silence feeder active",
                        strunaId,
                        pause.FailureReason);
                }
            }
            else
            {
                // Desync / idle: kill orphan Magpie writers so silence can own the FIFO.
                var moduleHint = _nowPlaying.TryGetValue(strunaId, out var orphanSnap)
                    ? orphanSnap.ModuleSlug
                    : null;
                await StopOrphanWritersAsync(strunaId, moduleHint, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_nowPlaying.TryGetValue(strunaId, out var snap))
            {
                SetNowPlaying(strunaId, snap with { Paused = true });
            }

            return new PlayTrackOutcome(true, trackJobId, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stop current job and start the next queue entry, if any. Never restarts FFmpeg.</summary>
    public async Task<PlayTrackOutcome> SkipAsync(Guid strunaId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(strunaId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var moduleHint = _jobs.TryGetValue(strunaId, out var job) ? job.ModuleSlug : null;
            await StopCurrentTrackAsync(strunaId, cancellationToken).ConfigureAwait(false);
            await StopOrphanWritersAsync(strunaId, moduleHint, cancellationToken).ConfigureAwait(false);
            _encoder.SetSilence(strunaId, true);

            var outcome = await AdvanceQueueHeadCoreAsync(strunaId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outcome.TrackJobId))
            {
                // User skipped to idle — clear now-playing. Natural Magpie end keeps the last
                // snapshot so REST/ICY stay truthful while encoded audio drains.
                ClearNowPlaying(strunaId);
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    public NowPlayingInfo? GetNowPlaying(Guid strunaId)
    {
        if (_nowPlaying.TryGetValue(strunaId, out var snap))
        {
            return snap.ToInfo();
        }

        return _jobs.TryGetValue(strunaId, out var job)
            ? new NowPlayingInfo(job.ModuleSlug, job.TrackRef, job.TrackJobId, null, null, false)
            : null;
    }

    /// <summary>ICY <c>StreamTitle</c> text from the Neck snapshot (same source as REST now-playing).</summary>
    public string GetStreamTitle(Guid strunaId)
    {
        var now = GetNowPlaying(strunaId);
        if (now is null || now.Paused)
        {
            return string.Empty;
        }

        return now.StreamTitle;
    }

    public bool TryGetActiveTrack(Guid strunaId, out ActiveTrackJob? job) =>
        _jobs.TryGetValue(strunaId, out job);

    private async Task<PlayTrackOutcome> UnpauseCoreAsync(Guid strunaId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(strunaId, out var existing))
        {
            return new PlayTrackOutcome(false, null, PlayTrackError.NothingToResume, null);
        }

        var resume = await _orch.ResumeTrackAsync(
                existing.ModuleSlug,
                existing.TrackJobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resume.Ok)
        {
            _logger.LogInformation(
                "ResumeTrack unavailable or failed for Struna {Id}: {Reason}; waiting for Running before silence off",
                strunaId,
                resume.FailureReason);
        }

        if (_nowPlaying.TryGetValue(strunaId, out var snap))
        {
            SetNowPlaying(strunaId, snap with { Paused = false });
        }

        // Silence stays on until TrackStatus Running (module writing again).
        return new PlayTrackOutcome(true, existing.TrackJobId, null, null);
    }

    private void StartStatusWatcher(Guid strunaId, ActiveTrackJob job)
    {
        StopStatusWatcher(strunaId);
        var cts = new CancellationTokenSource();
        _statusWatchers[strunaId] = cts;
        _ = Task.Run(() => WatchTrackStatusAsync(strunaId, job, cts.Token), CancellationToken.None);
    }

    private void StopStatusWatcher(Guid strunaId)
    {
        if (_statusWatchers.TryRemove(strunaId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            cts.Dispose();
        }
    }

    private async Task WatchTrackStatusAsync(
        Guid strunaId,
        ActiveTrackJob job,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? ownedCts = null;
        var sawRunning = false;
        var resubscribe = false;
        try
        {
            await foreach (var evt in _orch.TrackStatusAsync(job.ModuleSlug, job.TrackJobId, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!IsCurrentJob(strunaId, job.TrackJobId))
                {
                    break;
                }

                ApplyTrackStatusToNowPlaying(strunaId, job, evt);
                ApplyTrackStatusToSilence(strunaId, evt.State, ref sawRunning);

                if (ShouldIgnorePrematureEnded(evt.State, sawRunning, job.StatusAttempts))
                {
                    _logger.LogWarning(
                        "Ignoring Ended before Running for job {JobId} on Struna {Id} (attempt {Attempt}); resubscribing",
                        job.TrackJobId,
                        strunaId,
                        job.StatusAttempts);
                    resubscribe = true;
                    break;
                }

                if (evt.State is not (TrackState.Ended or TrackState.Error))
                {
                    continue;
                }

                if (evt.State == TrackState.Error)
                {
                    _logger.LogWarning(
                        "Track job {JobId} on Struna {Id} errored: {Error}",
                        job.TrackJobId,
                        strunaId,
                        evt.ErrorMessage);
                }

                ownedCts = await FinalizeEndedTrackAsync(strunaId, job, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Watcher cancelled (stop / delete / replace).
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TrackStatus watcher ended for Struna {Id} job {JobId}",
                strunaId,
                job.TrackJobId);
            if (IsCurrentJob(strunaId, job.TrackJobId) && !cancellationToken.IsCancellationRequested)
            {
                resubscribe = true;
            }
        }
        finally
        {
            ownedCts?.Dispose();
        }

        if (resubscribe
            && !cancellationToken.IsCancellationRequested
            && IsCurrentJob(strunaId, job.TrackJobId))
        {
            StartStatusWatcher(strunaId, job with { StatusAttempts = job.StatusAttempts + 1 });
        }
    }

    private bool IsCurrentJob(Guid strunaId, string trackJobId) =>
        _jobs.TryGetValue(strunaId, out var current)
        && string.Equals(current.TrackJobId, trackJobId, StringComparison.Ordinal);

    private void ApplyTrackStatusToNowPlaying(Guid strunaId, ActiveTrackJob job, TrackStatusEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Title) || !string.IsNullOrWhiteSpace(evt.Artist))
        {
            var prev = _nowPlaying.TryGetValue(strunaId, out var snap)
                ? snap
                : new NowPlayingSnapshot(
                    job.ModuleSlug,
                    job.TrackRef,
                    job.TrackJobId,
                    null,
                    null,
                    false);
            SetNowPlaying(
                strunaId,
                prev with
                {
                    Title = string.IsNullOrWhiteSpace(evt.Title) ? prev.Title : evt.Title,
                    Artist = string.IsNullOrWhiteSpace(evt.Artist) ? prev.Artist : evt.Artist,
                    Paused = evt.State == TrackState.Paused,
                });
            return;
        }

        if (evt.State == TrackState.Paused && _nowPlaying.TryGetValue(strunaId, out var pausedSnap))
        {
            SetNowPlaying(strunaId, pausedSnap with { Paused = true });
        }
        else if (evt.State == TrackState.Running && _nowPlaying.TryGetValue(strunaId, out var runSnap))
        {
            SetNowPlaying(strunaId, runSnap with { Paused = false });
        }
    }

    private void ApplyTrackStatusToSilence(Guid strunaId, TrackState state, ref bool sawRunning)
    {
        if (state == TrackState.Running)
        {
            sawRunning = true;
            _encoder.SetSilence(strunaId, false);
        }
        else if (state == TrackState.Paused)
        {
            _encoder.SetSilence(strunaId, true);
        }
    }

    private static bool ShouldIgnorePrematureEnded(TrackState state, bool sawRunning, int statusAttempts) =>
        state == TrackState.Ended && !sawRunning && statusAttempts < 2;

    /// <summary>
    /// Stop module job, drop Neck bookkeeping for this watcher, silence on, advance queue.
    /// Returns the watcher CTS to dispose when this loop owns it.
    /// </summary>
    private async Task<CancellationTokenSource?> FinalizeEndedTrackAsync(
        Guid strunaId,
        ActiveTrackJob job,
        CancellationToken watcherToken)
    {
        var gate = Gate(strunaId);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!IsCurrentJob(strunaId, job.TrackJobId))
            {
                return null;
            }

            var stop = await _orch.StopTrackAsync(
                    job.ModuleSlug,
                    job.TrackJobId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!stop.Ok)
            {
                _logger.LogDebug(
                    "Best-effort StopTrack after end for Struna {Id}: {Reason}",
                    strunaId,
                    stop.FailureReason);
            }

            _jobs.TryRemove(strunaId, out _);
            CancellationTokenSource? ownedCts = null;
            if (_statusWatchers.TryRemove(strunaId, out var removed))
            {
                if (removed.Token.Equals(watcherToken))
                {
                    ownedCts = removed;
                }
                else
                {
                    removed.Cancel();
                    removed.Dispose();
                }
            }

            _encoder.SetSilence(strunaId, true);
            try
            {
                await AdvanceQueueHeadCoreAsync(strunaId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Queue advance failed after track end on Struna {Id}",
                    strunaId);
                ClearNowPlaying(strunaId);
            }

            return ownedCts;
        }
        finally
        {
            gate.Release();
        }
    }

    private void SetNowPlaying(Guid strunaId, NowPlayingSnapshot snapshot)
    {
        _nowPlaying[strunaId] = snapshot;
        _encoder.SetStreamTitle(strunaId, snapshot.ToInfo().StreamTitle);
    }

    private void ClearNowPlaying(Guid strunaId)
    {
        _nowPlaying.TryRemove(strunaId, out _);
        _encoder.SetStreamTitle(strunaId, string.Empty);
    }

    /// <summary>
    /// Best-effort cancel of module writers for this Struna (orphans when Neck lost the job id).
    /// When <paramref name="preferredModule"/> is null, dials every playable source.
    /// </summary>
    private async Task StopOrphanWritersAsync(
        Guid strunaId,
        string? preferredModule,
        CancellationToken cancellationToken)
    {
        var strunaKey = strunaId.ToString("D");
        if (!string.IsNullOrWhiteSpace(preferredModule))
        {
            var one = await _orch.StopTracksForStrunaAsync(preferredModule, strunaKey, cancellationToken)
                .ConfigureAwait(false);
            if (!one.Ok)
            {
                _logger.LogDebug(
                    "StopTracksForStruna on {Module} for {Id}: {Reason}",
                    preferredModule,
                    strunaId,
                    one.FailureReason);
            }

            return;
        }

        foreach (var source in _orch.OrderPlayableSources())
        {
            var result = await _orch.StopTracksForStrunaAsync(source.Slug, strunaKey, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogDebug(
                    "StopTracksForStruna on {Module} for {Id}: {Reason}",
                    source.Slug,
                    strunaId,
                    result.FailureReason);
            }
        }
    }

    private async Task StopCurrentTrackAsync(Guid strunaId, CancellationToken cancellationToken)
    {
        StopStatusWatcher(strunaId);
        if (!_jobs.TryRemove(strunaId, out var job))
        {
            return;
        }

        var stop = await _orch.StopTrackAsync(job.ModuleSlug, job.TrackJobId, cancellationToken)
            .ConfigureAwait(false);
        if (!stop.Ok)
        {
            _logger.LogWarning(
                "StopTrack failed for Struna {Id}: {Reason}",
                strunaId,
                stop.FailureReason);
        }
    }
}
