# Bardie.Module.Auth

Shared helpers for **JWT-minting auth adapter modules** (Bes, …).

**Package id:** `Bardie.Module.Auth` · **Version:** `0.1.0` · **TFM:** `net10.0`

Depends on [`Bardie.Contracts`](../Bardie.Contracts/README.md) + [`Bardie.Module.Channel`](../Bardie.Module.Channel/README.md). Does **not** depend on `Bardie.Module.Hosting`.

## Consume

```csharp
builder.Services.AddAuthModuleJwt(builder.Configuration);
// optional PostConfigure for module-specific env (e.g. BES_JWT_*)
```

| Type | Role |
|------|------|
| `AuthModuleJwtOptions` / `AuthModuleJwtService` | Mint access+refresh, validate refresh, export JWKS |
| `AuthJwksRegisterRequestCustomizer` | Attach JWKS on Module Registry Register |
| `AuthAdapterModuleBase` | `Health`, provider-id match, `Denied()`, default `UpdateUserBinding` / `SeedAdminBinding` → Unimplemented |
| `ModuleManifestAuthBag` | Parse opaque `auth.loginFormFields` / `auth.bindFormFields` → discovery schemas |

Password hashing and concrete `Authenticate` / `UpdateUserBinding` / `SeedAdminBinding` stay in the module. Prefer declaring login + bind form fields in `module.manifest.json`.

Pack: `dotnet pack libs/Bardie.Module.Auth/Bardie.Module.Auth.csproj -c Release`
