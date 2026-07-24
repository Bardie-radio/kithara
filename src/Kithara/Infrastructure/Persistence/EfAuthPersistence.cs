using System.Text;
using System.Text.Json;
using Bardie.Orchestrator.Auth.Ports;
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

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.CountAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<Guid> EnsureUserWithBindingAsync(
        EnsureUserBindingRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var binding = await db.UserAuthBindings
            .Include(b => b.User)
            .FirstOrDefaultAsync(
                b => b.ProviderSlug == request.ProviderSlug
                    && b.ExternalSubject == request.ExternalSubject,
                cancellationToken)
            .ConfigureAwait(false);

        if (binding is not null)
        {
            binding.PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson)
                ? binding.PayloadJson
                : request.PayloadJson;
            // Sync rotate flag from adapter (SeedAdmin escalates; password-change clears — AUTH-ROT-001).
            binding.User.MustRotateCredentials = request.MustRotateCredentials;

            if (request.Roles is { Count: > 0 })
            {
                binding.PayloadJson = BindingPayloadJson.MergeRoles(binding.PayloadJson, request.Roles);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return binding.UserId;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Kind = UserKind.Durable,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active",
            MustRotateCredentials = request.MustRotateCredentials,
        };

        var payloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson;
        if (request.Roles is { Count: > 0 })
        {
            payloadJson = BindingPayloadJson.MergeRoles(payloadJson, request.Roles);
        }

        db.Users.Add(user);
        db.UserAuthBindings.Add(new UserAuthBinding
        {
            UserId = user.Id,
            ProviderSlug = request.ProviderSlug,
            ExternalSubject = request.ExternalSubject,
            PayloadJson = payloadJson,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
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
}
