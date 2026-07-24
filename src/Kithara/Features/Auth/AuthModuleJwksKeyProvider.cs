using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bardie.Harness.Auth.Catalog;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Kithara.Features.Auth;

/// <summary>
/// Resolves signing keys from registered auth-module JWKS (inline JSON or URI).
/// Snapshot is refreshed asynchronously; the JWT resolver reads the snapshot only (AUTH-JWKS-001).
/// </summary>
public sealed class AuthModuleJwksKeyProvider
{
    private readonly IAuthModuleCatalog _catalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthModuleJwksKeyProvider> _logger;
    private readonly object _snapshotGate = new();
    private ImmutableArray<SecurityKey> _snapshot = ImmutableArray<SecurityKey>.Empty;

    public AuthModuleJwksKeyProvider(
        IAuthModuleCatalog catalog,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<AuthModuleJwksKeyProvider> logger)
    {
        _catalog = catalog;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Sync-safe snapshot for <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>.</summary>
    public IReadOnlyList<SecurityKey> GetCachedSigningKeys()
    {
        lock (_snapshotGate)
        {
            return _snapshot;
        }
    }

    public async Task RefreshSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<SecurityKey>();
        foreach (var module in _catalog.List())
        {
            var moduleKeys = await GetKeysForModuleAsync(module, cancellationToken).ConfigureAwait(false);
            keys.AddRange(moduleKeys);
        }

        var snapshot = keys.ToImmutableArray();
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> GetKeysForModuleAsync(
        AuthModuleRegistration module,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"jwks:{module.Slug}:{module.JwksUri}:{Hash(module.JwksJson)}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<SecurityKey>? cached) && cached is not null)
        {
            return cached;
        }

        string? jwksJson = module.JwksJson;
        if (string.IsNullOrWhiteSpace(jwksJson) && !string.IsNullOrWhiteSpace(module.JwksUri))
        {
            try
            {
                var client = _httpClientFactory.CreateClient(nameof(AuthModuleJwksKeyProvider));
                jwksJson = await client.GetStringAsync(module.JwksUri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch JWKS for module {Slug} from {Uri}", module.Slug, module.JwksUri);
            }
        }

        var keys = ParseJwks(jwksJson);
        _cache.Set(cacheKey, keys, TimeSpan.FromMinutes(5));
        return keys;
    }

    private static IReadOnlyList<SecurityKey> ParseJwks(string? jwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            if (!doc.RootElement.TryGetProperty("keys", out var keysElement)
                || keysElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var keys = new List<SecurityKey>();
            foreach (var keyElement in keysElement.EnumerateArray())
            {
                var jwk = new JsonWebKey(keyElement.GetRawText());
                keys.Add(jwk);
            }

            return keys;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Hash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "empty";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
    }
}

/// <summary>Keeps the JWKS signing-key snapshot warm so JWT validation never blocks on async I/O.</summary>
public sealed class AuthModuleJwksRefreshHostedService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly AuthModuleJwksKeyProvider _keys;
    private readonly ILogger<AuthModuleJwksRefreshHostedService> _logger;

    public AuthModuleJwksRefreshHostedService(
        AuthModuleJwksKeyProvider keys,
        ILogger<AuthModuleJwksRefreshHostedService> logger)
    {
        _keys = keys;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _keys.RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JWKS snapshot refresh failed.");
        }
    }
}
