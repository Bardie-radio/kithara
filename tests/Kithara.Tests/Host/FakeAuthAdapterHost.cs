using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bardie.Auth.V1;
using Bardie.Module.Auth;
using Bardie.Module.Channel.Manifest;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kithara.Tests.Host;

/// <summary>In-process password auth adapter for META-QA-001 host E2E (no Bes/mTLS).</summary>
public sealed class FakeAuthAdapterHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AuthModuleJwtService _tokens;

    private FakeAuthAdapterHost(WebApplication app, AuthModuleJwtService tokens, string address, string jwksJson)
    {
        _app = app;
        _tokens = tokens;
        GrpcAddress = address;
        JwksJson = jwksJson;
    }

    public string GrpcAddress { get; }

    public string JwksJson { get; }

    public string Slug => "bes";

    public static async Task<FakeAuthAdapterHost> StartAsync()
    {
        var port = GetFreePort();
        var address = $"http://127.0.0.1:{port}";
        var keyDir = Path.Combine(Path.GetTempPath(), "kithara-fake-bes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDir);

        var manifest = new ModuleManifest
        {
            Slug = "bes",
            Kind = "auth",
            DisplayName = "Bes (test)",
            OtelServiceName = "bardie.auth.bes",
            Capabilities = ["updateBinding"],
        };

        var tokens = new AuthModuleJwtService(
            Options.Create(new AuthModuleJwtOptions
            {
                SigningKeyPath = Path.Combine(keyDir, "jwt.pem"),
            }),
            manifest);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(manifest);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton<FakePasswordAuthAdapter>();

        var app = builder.Build();
        app.MapGrpcService<FakePasswordAuthAdapter>();
        await app.StartAsync().ConfigureAwait(false);

        return new FakeAuthAdapterHost(app, tokens, address, tokens.ExportJwksJson());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _tokens.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class FakePasswordAuthAdapter : AuthAdapterModuleBase
{
    private const int MinPasswordLength = 8;
    private static readonly string[] DefaultRoles = ["user"];
    private readonly AuthModuleJwtService _tokens;

    public FakePasswordAuthAdapter(ModuleManifest manifest, AuthModuleJwtService tokens)
        : base(manifest)
    {
        _tokens = tokens;
    }

    public override Task<GetProvidersResponse> GetProviders(GetProvidersRequest request, ServerCallContext context)
    {
        var response = new GetProvidersResponse();
        response.Providers.Add(new ProviderDescriptor
        {
            Id = Manifest.Slug,
            DisplayName = Manifest.DisplayName ?? "Bes",
            LoginForm = new FormSchemaUi
            {
                Fields =
                {
                    new FormField { Name = "username", Label = "Username", InputType = "text", Required = true },
                    new FormField { Name = "password", Label = "Password", InputType = "password", Required = true },
                },
            },
            BindForm = new FormSchemaUi
            {
                Fields =
                {
                    new FormField { Name = "password", Label = "Password", InputType = "password", Required = true },
                },
            },
        });
        return Task.FromResult(response);
    }

    public override Task<AuthenticateResponse> Authenticate(AuthenticateRequest request, ServerCallContext context)
    {
        if (!MatchesProviderId(request.ProviderId))
        {
            return Task.FromResult(Denied());
        }

        request.Payload.TryGetValue("username", out var username);
        request.Payload.TryGetValue("password", out var password);
        username = username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(Denied());
        }

        var binding = TryReadBinding(request.ExistingBindingPayload.Span);
        if (binding is null || !FixedTimeEquals(binding.PasswordHash, Hash(password)))
        {
            return Task.FromResult(Denied());
        }

        var roles = binding.Roles.Count > 0 ? binding.Roles.ToArray() : DefaultRoles;
        var (access, refresh, expiresIn) = _tokens.MintTokens(username, binding.MustRotate, roles);
        var response = new AuthenticateResponse
        {
            Allowed = true,
            ExternalSubject = username,
            EnsureUser = true,
            BindingPayload = ByteString.CopyFrom(BuildBindingBytes(binding.PasswordHash, roles, binding.MustRotate)),
            AccessToken = access,
            RefreshToken = refresh,
            TokenType = "Bearer",
            ExpiresIn = expiresIn,
            MustRotateCredentials = binding.MustRotate,
        };
        response.Roles.AddRange(roles);
        return Task.FromResult(response);
    }

    public override Task<UpdateUserBindingResponse> UpdateUserBinding(
        UpdateUserBindingRequest request,
        ServerCallContext context)
    {
        if (!MatchesProviderId(request.ProviderId))
        {
            return Task.FromResult(new UpdateUserBindingResponse { Ok = false, Error = "Unknown provider." });
        }

        var ceremony = request.Ceremony;
        if (ceremony == BindingCeremony.Unspecified)
        {
            ceremony = request.ExistingBindingPayload.IsEmpty
                ? BindingCeremony.Bind
                : BindingCeremony.Update;
        }

        return Task.FromResult(ceremony switch
        {
            BindingCeremony.Bind => Bind(request),
            BindingCeremony.Update => Update(request),
            _ => new UpdateUserBindingResponse { Ok = false, Error = "Unknown binding ceremony." },
        });
    }

    private UpdateUserBindingResponse Bind(UpdateUserBindingRequest request)
    {
        if (!request.ExistingBindingPayload.IsEmpty)
        {
            return new UpdateUserBindingResponse { Ok = false, Error = "A binding already exists for this account." };
        }

        request.Payload.TryGetValue("username", out var username);
        request.Payload.TryGetValue("password", out var password);
        username = username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new UpdateUserBindingResponse { Ok = false, Error = "Username and password are required." };
        }

        if (password.Length < MinPasswordLength)
        {
            return new UpdateUserBindingResponse
            {
                Ok = false,
                Error = $"Password must be at least {MinPasswordLength} characters.",
            };
        }

        var hash = Hash(password);
        var response = new UpdateUserBindingResponse
        {
            Ok = true,
            ExternalSubject = username,
            BindingPayload = ByteString.CopyFrom(BuildBindingBytes(hash, DefaultRoles, mustRotate: false)),
            MustRotateCredentials = false,
        };
        response.Roles.AddRange(DefaultRoles);
        return response;
    }

    private UpdateUserBindingResponse Update(UpdateUserBindingRequest request)
    {
        var binding = TryReadBinding(request.ExistingBindingPayload.Span);
        if (binding is null)
        {
            return new UpdateUserBindingResponse { Ok = false, Error = "No existing credentials to update." };
        }

        request.Payload.TryGetValue("password", out var password);
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
        {
            return new UpdateUserBindingResponse
            {
                Ok = false,
                Error = $"Password must be at least {MinPasswordLength} characters.",
            };
        }

        var roles = binding.Roles.Count > 0 ? binding.Roles.ToArray() : DefaultRoles;
        var response = new UpdateUserBindingResponse
        {
            Ok = true,
            ExternalSubject = string.Empty,
            BindingPayload = ByteString.CopyFrom(BuildBindingBytes(Hash(password), roles, mustRotate: false)),
            MustRotateCredentials = false,
        };
        response.Roles.AddRange(roles);
        return response;
    }

    private static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static byte[] BuildBindingBytes(string passwordHash, IReadOnlyList<string> roles, bool mustRotate)
    {
        var json = JsonSerializer.Serialize(new
        {
            PasswordHash = passwordHash,
            roles = roles.ToArray(),
            mustRotate,
        });
        return Encoding.UTF8.GetBytes(json);
    }

    private static BindingState? TryReadBinding(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            var root = doc.RootElement;
            var hash = root.GetProperty("PasswordHash").GetString();
            if (string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            var roles = new List<string>();
            if (root.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rolesEl.EnumerateArray())
                {
                    var s = r.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        roles.Add(s);
                    }
                }
            }

            var mustRotate = root.TryGetProperty("mustRotate", out var mr) && mr.GetBoolean();
            return new BindingState(hash, roles, mustRotate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record BindingState(string PasswordHash, IReadOnlyList<string> Roles, bool MustRotate);
}
