namespace Bardie.Harness.Auth.Models;

public sealed record MergedProviderDescriptor(
    string Id,
    string DisplayName,
    string ModuleSlug,
    string UiMode,
    IReadOnlyList<FormFieldDescriptor> LoginFormFields,
    IReadOnlyList<FormFieldDescriptor> BindFormFields,
    string? AuthorizeUrl)
{
    /// <summary>Legacy alias for <see cref="LoginFormFields"/>.</summary>
    public IReadOnlyList<FormFieldDescriptor> FormFields => LoginFormFields;
}

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

public sealed record InviteBootstrapResult(
    bool Created,
    Guid? UserId,
    string Username,
    string RegistrationPassword);

public sealed record UpdateUserBindingResult(
    bool Ok,
    Guid? UserId,
    string? ExternalSubject,
    bool MustRotateCredentials,
    string? FailureReason);
