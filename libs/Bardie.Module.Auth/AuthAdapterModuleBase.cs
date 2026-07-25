using Bardie.Auth.V1;
using Bardie.Module.Channel.Manifest;
using Grpc.Core;

namespace Bardie.Module.Auth;

/// <summary>
/// Thin AuthAdapter base: health, provider-id matching, denied helper,
/// default SeedAdminBinding / UpdateUserBinding → Unimplemented.
/// Concrete Authenticate / GetProviders / binding RPCs stay in the module.
/// </summary>
public abstract class AuthAdapterModuleBase : AuthAdapter.AuthAdapterBase
{
    protected ModuleManifest Manifest { get; }

    protected AuthAdapterModuleBase(ModuleManifest manifest)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) =>
        Task.FromResult(new HealthResponse { Ok = true });

    /// <summary>
    /// True when <paramref name="providerId"/> is empty or equals the module slug (case-insensitive).
    /// </summary>
    protected bool MatchesProviderId(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId)
        || string.Equals(providerId, Manifest.Slug, StringComparison.OrdinalIgnoreCase);

    protected static AuthenticateResponse Denied() => new()
    {
        Allowed = false,
        TokenType = "Bearer",
    };

    public override Task<UpdateUserBindingResponse> UpdateUserBinding(
        UpdateUserBindingRequest request,
        ServerCallContext context) =>
        throw new RpcException(
            new Status(StatusCode.Unimplemented, "UpdateUserBinding is not supported by this auth module."));

    public override Task<SeedAdminBindingResponse> SeedAdminBinding(
        SeedAdminBindingRequest request,
        ServerCallContext context) =>
        throw new RpcException(
            new Status(StatusCode.Unimplemented, "SeedAdminBinding is not supported by this auth module."));
}
