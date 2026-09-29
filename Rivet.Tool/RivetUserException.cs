namespace Rivet.Tool;

/// <summary>
/// A refusal caused by the user's input (source, spec or CLI arguments). The CLI prints
/// the message and exits 1; anything else escaping is a Rivet bug and keeps its stack trace.
/// </summary>
internal sealed class RivetUserException(string message) : Exception(message);
