namespace Rivet.Tool;

/// <summary>
/// A refusal caused by the user's input (source, spec or CLI arguments). The CLI prints
/// the message and exits 1; anything else escaping is a Rivet bug and keeps its stack trace.
/// The constructor is private, so the one way to build one is <see cref="ForError"/> (which
/// <see cref="Diagnostics.Error"/> calls): every user error carries a registered error ID.
/// </summary>
internal sealed class RivetUserException : Exception
{
    private RivetUserException(string message)
        : base(message) { }

    /// <summary>
    /// The error for <paramref name="id"/>, which must be registered as an error; its message
    /// is the canonical <c>error RIV2002: message</c> line. Call sites use
    /// <see cref="Diagnostics.Error"/>.
    /// </summary>
    internal static RivetUserException ForError(string id, string message)
    {
        Diagnostics.Require(id, Diagnostics.Severity.Error);
        return new RivetUserException(Diagnostics.Format(id, message));
    }
}
