namespace Bardie.Harness.Auth.Ports;

/// <summary>Thrown when creating an invited user whose username is already taken.</summary>
public sealed class AuthUsernameConflictException : InvalidOperationException
{
    public AuthUsernameConflictException(string message)
        : base(message)
    {
    }
}
