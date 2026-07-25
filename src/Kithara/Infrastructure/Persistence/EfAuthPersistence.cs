using Bardie.Harness.Auth.Ports;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Persistence;

public sealed class EfAuthPersistence : IAuthPersistence
{
    private readonly IDbContextFactory<KitharaDbContext> _dbContextFactory;

    public EfAuthPersistence(IDbContextFactory<KitharaDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAnyAuthBindingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.UserAuthBindings.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateDurableUserAsync(
        bool mustRotateCredentials,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Kind = UserKind.Durable,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active",
            MustRotateCredentials = mustRotateCredentials,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnboundDurableUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var orphans = await db.Users
            .Where(u => u.Kind == UserKind.Durable && !u.AuthBindings.Any())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (orphans.Count == 0)
        {
            return 0;
        }

        db.Users.RemoveRange(orphans);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return orphans.Count;
    }

    public async Task<AuthBindingRecord?> FindBindingBySubjectAsync(
        string providerSlug,
        string externalSubject,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var binding = await db.UserAuthBindings
            .AsNoTracking()
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.ProviderSlug == providerSlug && b.ExternalSubject == externalSubject,
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return null;
        }

        return new AuthBindingRecord(
            binding.UserId,
            binding.ProviderSlug,
            binding.ExternalSubject ?? externalSubject,
            binding.PayloadJson,
            binding.User.MustRotateCredentials);
    }

    public async Task<AuthBindingRecord?> FindBindingByUserAsync(
        Guid userId,
        string providerSlug,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var binding = await db.UserAuthBindings
            .AsNoTracking()
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.UserId == userId && b.ProviderSlug == providerSlug,
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return null;
        }

        return new AuthBindingRecord(
            binding.UserId,
            binding.ProviderSlug,
            binding.ExternalSubject ?? string.Empty,
            binding.PayloadJson,
            binding.User.MustRotateCredentials);
    }

    public async Task<Guid> EnsureUserWithBindingAsync(
        EnsureUserBindingRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (request.UserId is { } explicitUserId)
        {
            return await EnsureBindingForExplicitUserAsync(db, request, explicitUserId, cancellationToken)
                .ConfigureAwait(false);
        }

        var bySubject = await db.UserAuthBindings
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.ProviderSlug == request.ProviderSlug
                    && b.ExternalSubject == request.ExternalSubject,
                cancellationToken)
            .ConfigureAwait(false);

        if (bySubject is not null)
        {
            ApplyBindingUpdate(bySubject, request);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return bySubject.UserId;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Kind = UserKind.Durable,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active",
            MustRotateCredentials = request.MustRotateCredentials,
        };
        db.Users.Add(user);
        db.UserAuthBindings.Add(CreateBinding(user.Id, request));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    private static async Task<Guid> EnsureBindingForExplicitUserAsync(
        KitharaDbContext db,
        EnsureUserBindingRequest request,
        Guid explicitUserId,
        CancellationToken cancellationToken)
    {
        var subjectOwner = await db.UserAuthBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.ProviderSlug == request.ProviderSlug
                    && b.ExternalSubject == request.ExternalSubject,
                cancellationToken)
            .ConfigureAwait(false);

        if (subjectOwner is not null && subjectOwner.UserId != explicitUserId)
        {
            throw new AuthBindingConflictException(
                $"External subject '{request.ExternalSubject}' is already bound for provider '{request.ProviderSlug}'.");
        }

        var binding = await db.UserAuthBindings
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.UserId == explicitUserId && b.ProviderSlug == request.ProviderSlug,
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is not null)
        {
            ApplyBindingUpdate(binding, request);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return binding.UserId;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == explicitUserId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"User '{explicitUserId}' was not found.");
        user.MustRotateCredentials = request.MustRotateCredentials;
        db.UserAuthBindings.Add(CreateBinding(user.Id, request));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    private static void ApplyBindingUpdate(UserAuthBinding binding, EnsureUserBindingRequest request)
    {
        binding.PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson)
            ? binding.PayloadJson
            : request.PayloadJson;
        binding.ExternalSubject = request.ExternalSubject;
        binding.User.MustRotateCredentials = request.MustRotateCredentials;

        if (request.Roles is { Count: > 0 })
        {
            binding.PayloadJson = BindingPayloadJson.MergeRoles(binding.PayloadJson, request.Roles);
        }
    }

    private static UserAuthBinding CreateBinding(Guid userId, EnsureUserBindingRequest request)
    {
        var payloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson;
        if (request.Roles is { Count: > 0 })
        {
            payloadJson = BindingPayloadJson.MergeRoles(payloadJson, request.Roles);
        }

        return new UserAuthBinding
        {
            UserId = userId,
            ProviderSlug = request.ProviderSlug,
            ExternalSubject = request.ExternalSubject,
            PayloadJson = payloadJson,
        };
    }

    public async Task<AuthUserRecord?> FindUserByBindingSubjectAsync(
        string providerSlug,
        string externalSubject,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var binding = await db.UserAuthBindings
            .AsNoTracking()
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.ProviderSlug == providerSlug && b.ExternalSubject == externalSubject,
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return null;
        }

        return new AuthUserRecord(
            binding.UserId,
            binding.User.Kind.ToString(),
            binding.User.Status,
            binding.User.MustRotateCredentials,
            binding.User.GuestStrunaId,
            binding.User.ManagedByModuleSlug);
    }

    public async Task<AuthUserRecord?> FindUserByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        return new AuthUserRecord(
            user.Id,
            user.Kind.ToString(),
            user.Status,
            user.MustRotateCredentials,
            user.GuestStrunaId,
            user.ManagedByModuleSlug);
    }

    public async Task SetMustRotateAsync(
        Guid userId,
        bool mustRotateCredentials,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.MustRotateCredentials = mustRotateCredentials;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
