using System.Text.Json;

namespace Rivet.Tool;

/// <summary>
/// The one rule for JSON a user writes into C#: a <c>[RivetExample]</c>, a <c>[RivetDefault]</c>,
/// scalar metadata, or a builder's JSON argument is a JSON literal, and anything else is
/// <see cref="Diagnostics.InvalidBuilderArgument"/> rather than a guess.
/// </summary>
internal static class AuthoredJson
{
    /// <summary>
    /// <paramref name="json"/> parsed, or RIV1123 naming <paramref name="what"/> carried it.
    /// </summary>
    public static JsonElement Parse(string json, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"{what} must be a JSON literal, got '{json}' ({exception.Message})"
            );
        }
    }
}
