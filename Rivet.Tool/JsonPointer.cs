using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tool;

/// <summary>
/// RFC 6901 JSON Pointers in URI-fragment form (<c>#/a/b</c>). The BCL has no pointer API,
/// and JsonPointer.Net is not worth a dependency for this much code.
/// Fragments are percent-decoded before splitting (RFC 6901 §6), so <c>%2F</c> is a
/// separator; a literal '/' in a name must be written <c>~1</c>.
/// </summary>
internal static class JsonPointer
{
    public static string Escape(string token) =>
        token
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    public static string Unescape(string token) =>
        token
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// The decoded reference tokens of a local fragment (<c>#</c> is the empty list), or null
    /// when <paramref name="reference"/> is not a local JSON Pointer fragment.
    /// </summary>
    public static IReadOnlyList<string>? FromUriFragment(string reference)
    {
        if (reference == "#")
        {
            return [];
        }

        return reference.StartsWith("#/", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(reference[2..]).Split('/').Select(Unescape).ToArray()
            : null;
    }

    /// <summary>
    /// The component name of a <c>#/components/{kind}/{name}</c> reference. False for any other
    /// reference, including pointers into a component.
    /// </summary>
    public static bool TryGetComponentName(
        string reference,
        string kind,
        [NotNullWhen(true)] out string? name
    )
    {
        name = null;
        if (
            FromUriFragment(reference) is not ["components", var referenceKind, var componentName]
            || referenceKind != kind
        )
        {
            return false;
        }

        name = componentName;
        return true;
    }

    public static bool TryResolve(
        JsonNode root,
        string reference,
        [NotNullWhen(true)] out JsonNode? target
    )
    {
        target = null;
        if (FromUriFragment(reference) is not { } tokens)
        {
            return false;
        }

        var current = root;
        foreach (var token in tokens)
        {
            JsonNode? next = current switch
            {
                JsonObject obj => obj.TryGetPropertyValue(token, out var child) ? child : null,
                JsonArray array when TryIndex(token, array.Count, out var index) => array[index],
                _ => null,
            };
            if (next is null)
            {
                return false;
            }
            current = next;
        }

        target = current;
        return true;
    }

    public static bool TryResolve(JsonElement root, string reference, out JsonElement target)
    {
        target = default;
        if (FromUriFragment(reference) is not { } tokens)
        {
            return false;
        }

        var current = root;
        foreach (var token in tokens)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object when current.TryGetProperty(token, out var child):
                    current = child;
                    break;
                case JsonValueKind.Array
                    when TryIndex(token, current.GetArrayLength(), out var index):
                    current = current[index];
                    break;
                default:
                    return false;
            }
        }

        target = current;
        return true;
    }

    /// <summary>RFC 6901 array index: "0" or a decimal without leading zeros, in range.</summary>
    public static bool TryIndex(string token, int count, out int index)
    {
        index = -1;
        return token.Length > 0
            && (token == "0" || token[0] != '0')
            && token.All(char.IsAsciiDigit)
            && int.TryParse(token, out index)
            && index < count;
    }
}
