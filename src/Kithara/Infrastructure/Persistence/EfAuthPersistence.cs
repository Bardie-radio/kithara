using System.Text.Json;
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

    public async Task<bool> HasAnyDurableUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AnyAsync(u => u.Kind == UserKind.Durable, cancellationToken)
            .ConfigureAwait(false);
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

    public async Task<Guid> CreateInvitedUserAsync(
        CreateInvitedUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvitePasswordHash);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var username = request.Username.Trim();
        var exists = await db.Users.AnyAsync(
                u => u.Username != null && u.Username.ToLower() == username.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw new AuthUsernameConflictException($"Username '{username}' is already taken.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Kind = UserKind.Durable,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active",
            Username = username,
            InvitePasswordHash = request.InvitePasswordHash,
            MustCompleteBinding = true,
            MustRotateCredentials = request.MustRotateCredentials,
            InviteRolesJson = SerializeRoles(request.Roles),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    public async Task<AuthUserRecord?> FindUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var needle = username.Trim();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Username != null && u.Username.ToLower() == needle.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);

        return user is null ? null : MapUser(user);
    }

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var needle = username.Trim();
        return await db.Users.AnyAsync(
                u => u.Username != null && u.Username.ToLower() == needle.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CompleteInviteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.InvitePasswordHash = null;
        user.MustCompleteBinding = false;
        user.MustRotateCredentials = false;
        user.InviteRolesJson = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        // Keep pending invites (MustCompleteBinding / InvitePasswordHash) — they are intentional.
        var orphans = await db.Users
            .Where(u => u.Kind == UserKind.Durable
                && !u.AuthBindings.Any()
                && !u.MustCompleteBinding
                && u.InvitePasswordHash == null)
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
                b => b.ProviderSlug == providerSlug
                    && b.ExternalSubject != null
                    && b.ExternalSubject.ToLower() == externalSubject.ToLower(),
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
                    && b.ExternalSubject != null
                    && b.ExternalSubject.ToLower() == request.ExternalSubject.ToLower(),
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
                    && b.ExternalSubject != null
                    && b.ExternalSubject.ToLower() == request.ExternalSubject.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);

        if (subjectOwner is not null && subjectOwner.UserId != explicitUserId)
        {
            throw new AuthBindingConflictException(
                $"External subject '{request.ExternalSubject}' is already bound for provider '{request.ProviderSlug}'.");
        }

        // Refuse claiming another durable user's host Username as a module subject (AUTH-INVITE).
        var usernameOwner = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Username != null
                    && u.Username.ToLower() == request.ExternalSubject.ToLower()
                    && u.Id != explicitUserId,
                cancellationToken)
            .ConfigureAwait(false);
        if (usernameOwner is not null)
        {
            throw new AuthBindingConflictException(
                $"External subject '{request.ExternalSubject}' conflicts with another user's username.");
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
                b => b.ProviderSlug == providerSlug
                    && b.ExternalSubject != null
                    && b.ExternalSubject.ToLower() == externalSubject.ToLower(),
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is null)
        {
            return null;
        }

        return MapUser(binding.User);
    }

    public async Task<AuthUserRecord?> FindUserByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        return user is null ? null : MapUser(user);
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

    private static AuthUserRecord MapUser(User user) =>
        new(
            user.Id,
            user.Kind.ToString(),
            user.Status,
            user.MustRotateCredentials,
            user.GuestStrunaId,
            user.ManagedByModuleSlug,
            user.Username,
            user.MustCompleteBinding,
            user.InvitePasswordHash,
            DeserializeRoles(user.InviteRolesJson));

    private static string? SerializeRoles(IReadOnlyList<string> roles)
    {
        if (roles.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(roles);
    }

    private static IReadOnlyList<string>? DeserializeRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
