using Kithara.Infrastructure.Persistence.Entities;

namespace Kithara.Infrastructure.Neck;

public sealed record ActiveTrackJob(
    string ModuleSlug,
    string TrackJobId,
    string TrackRef,
    int StatusAttempts = 0);

public sealed record NowPlayingSnapshot(
    string ModuleSlug,
    string TrackRef,
    string TrackJobId,
    string? Title,
    string? Artist,
    bool Paused)
{
    public NowPlayingInfo ToInfo() =>
        new(ModuleSlug, TrackRef, TrackJobId, Title, Artist, Paused);
}

public sealed record NowPlayingInfo(
    string ModuleSlug,
    string TrackRef,
    string TrackJobId,
    string? Title,
    string? Artist,
    bool Paused)
{
    /// <summary>ICY / REST display title: <c>Artist - Title</c>; empty when unknown (never raw trackRef).</summary>
    public string StreamTitle
    {
        get
        {
            var hasArtist = !string.IsNullOrWhiteSpace(Artist);
            var hasTitle = !string.IsNullOrWhiteSpace(Title);
            if (hasArtist && hasTitle)
            {
                return $"{Artist!.Trim()} - {Title!.Trim()}";
            }

            if (hasTitle)
            {
                return Title!.Trim();
            }

            if (hasArtist)
            {
                return Artist!.Trim();
            }

            return string.Empty;
        }
    }
}

public enum CreateStrunaError
{
    InvalidSlug,
    SlugConflict,
}

public sealed record CreateStrunaOutcome(Struna? Struna, CreateStrunaError? Error);

public sealed record DeleteStrunaOutcome(bool Deleted, IReadOnlyList<Guid> GuestUserIds);

public enum PlayTrackError
{
    StrunaNotFound,
    NothingToResume,
    ModuleFailed,
}

public sealed record PlayTrackOutcome(
    bool Ok,
    string? TrackJobId,
    PlayTrackError? Error,
    string? Detail);
