# Bardie.Harness.Auth

Auth module **harness** library for Bardie hosts (Kithara today; external hosts later).

**Package id:** `Bardie.Harness.Auth` · **Version:** `0.1.0` · **TFM:** `net10.0`

Depends on [`Bardie.Logos.Contracts`](https://github.com/Bardie-radio/logos/tree/main/src/Bardie.Logos.Contracts) + [`Bardie.Logos.Channel`](https://github.com/Bardie-radio/logos/tree/main/src/Bardie.Logos.Channel).

## What it owns

- Auth module catalog (slug, JWKS, capabilities)
- Discovery merge (`GetProviders`), route `Authenticate` / `Refresh` / `UpdateUserBinding`
- Host invite bootstrap (AUTH-INVITE) and claim JWT mint stay in the Kithara host wrapper — not generic harness RPCs
- Host port `IAuthPersistence` for user + binding persistence
- Dials modules via Logos Channel mTLS helpers

Bardie-only extras (guests, join secrets, REST BFF) stay in the Kithara host wrappers — see [org 07](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/07-modules-beyond-bardie.md).

## Consume

```csharp
services.AddAuthModuleHarness();
// host must register IAuthPersistence
```

Pack: `dotnet pack libs/Bardie.Harness.Auth/Bardie.Harness.Auth.csproj -c Release`
