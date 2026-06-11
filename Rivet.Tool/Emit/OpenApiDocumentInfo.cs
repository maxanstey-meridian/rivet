namespace Rivet.Tool.Emit;

/// <summary>
/// Emit-time document metadata from the --title/--version/--server CLI flags.
/// Defaults reproduce the historical hardcoded info block; Servers is emitted
/// only when at least one --server was given (never an invented servers block).
/// This is CLI-provided emit-time data, not contract data — it does not round-trip
/// through the importer.
/// </summary>
public sealed record OpenApiDocumentInfo(
    string Title = "API",
    string Version = "1.0.0",
    IReadOnlyList<string>? Servers = null);
