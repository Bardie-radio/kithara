using Bardie.Module.Channel.Hosting;
using Bardie.Modules.V1;
using Grpc.Core;

namespace Kithara.Features.Modules;

public sealed class ModuleRegistryService : ModuleRegistry.ModuleRegistryBase
{
    private readonly ModuleRegistryOperations _operations;

    public ModuleRegistryService(ModuleRegistryOperations operations)
    {
        _operations = operations;
    }

    public override async Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        string? presentedSlug = null;
        if (context.UserState.TryGetValue(ModuleChannelBootstrapInterceptor.ModuleSlugUserStateKey, out var presented)
            && presented is string slug)
        {
            presentedSlug = slug;
        }

        return await _operations
            .RegisterAsync(request, presentedSlug, context.CancellationToken)
            .ConfigureAwait(false);
    }

    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        string? presentedSlug = null;
        if (context.UserState.TryGetValue(ModuleChannelBootstrapInterceptor.ModuleSlugUserStateKey, out var presented)
            && presented is string slug)
        {
            presentedSlug = slug;
        }

        return Task.FromResult(_operations.Heartbeat(request, presentedSlug));
    }
}
