using Rivet.Tool.Model;

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
    IReadOnlyList<string>? Servers = null,
    OpenApiDocumentProvenance? Provenance = null
)
{
    public static OpenApiDocumentInfo Resolve(
        OpenApiDocumentProvenance? provenance,
        string? title,
        string? version,
        IReadOnlyList<string>? servers
    )
    {
        var resolvedTitle = title ?? provenance?.Info.Title ?? "API";
        var resolvedVersion = version ?? provenance?.Info.Version ?? "1.0.0";
        var resolvedProvenance = provenance is null
            ? null
            : provenance with
            {
                Info = provenance.Info with { Title = resolvedTitle, Version = resolvedVersion },
            };
        return new OpenApiDocumentInfo(
            resolvedTitle,
            resolvedVersion,
            servers is { Count: > 0 } ? servers : null,
            resolvedProvenance
        );
    }
}
