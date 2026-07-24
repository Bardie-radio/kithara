namespace Bardie.Harness.Auth.Models;

public sealed record MergedProviderDescriptor(
    string Id,
    string DisplayName,
    string ModuleSlug,
    string UiMode,
    IReadOnlyList<FormFieldDescriptor> FormFields,
    string? AuthorizeUrl);

public sealed record FormFieldDescriptor(
    string Name,
    string Label,
    string InputType,
    bool Required);

public sealed record AuthenticateResult(
    bool Allowed,
    string? AccessToken,
    string? RefreshToken,
    string TokenType,
    long ExpiresIn,
    string? ExternalSubject,
    Guid? UserId,
    bool MustRotateCredentials,
    string? FailureReason);

public sealed record RefreshResult(
    bool Allowed,
    string? AccessToken,
    string? RefreshToken,
    string TokenType,
    long ExpiresIn,
    string? FailureReason);

public sealed record SeedAdminResult(
    bool Created,
    string WelcomeLogText,
    Guid? UserId,
    string? ExternalSubject);
