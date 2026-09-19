using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet;

/// <summary>
/// Lowercases the exact member name to the whole word — "DarkGreen" →
/// "darkgreen". System.Text.Json ships no lowercase-all naming policy, so the
/// policy is derived here; <see cref="JsonNamingPolicy"/> is a non-sealed class
/// with a protected constructor and a virtual <see cref="JsonNamingPolicy.ConvertName"/>,
/// which keeps the entire enum-string machinery (pins, flags, round-trip) native.
/// </summary>
internal sealed class RivetLowerAllNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name.ToLowerInvariant();
}

/// <summary>
/// String-wire enum converter whose naming policy is the type itself: the
/// converter a source enum declares in [JsonConverter(typeof(...))] names both
/// the string wire and the casing convention — one declaration per enum, no
/// second attribute to keep in sync, no ambient configuration.
///
/// A member-level [JsonStringEnumMemberName("original")] always wins over the
/// policy; a bare [JsonConverter(typeof(JsonStringEnumConverter<...>))] (the
/// built-in, no policy) keeps the exact C# member names. The contract tool
/// derives the same wire values from the same converter type name, so the
/// emitted string union matches the runtime wire by construction.
/// </summary>
/// <remarks>
/// The declared failures are refusal, not fallback: the contract tool refuses a
/// policy casing that collides two members into one wire value (RIV1106), and
/// never silently falls back to numeric when a policy is declared.
/// </remarks>
public sealed class RivetLowerCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(new RivetLowerAllNamingPolicy())
    where T : struct, Enum;

/// <summary>camelCase enum wire values — "DarkGreen" → "darkGreen".</summary>
public sealed class RivetCamelCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;

/// <summary>snake_case enum wire values — "RestrictedByway2A" → "restricted_byway2_a".</summary>
public sealed class RivetSnakeCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower)
    where T : struct, Enum;

/// <summary>kebab-case enum wire values — "RestrictedByway2A" → "restricted-byway2-a".</summary>
public sealed class RivetKebabCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.KebabCaseLower)
    where T : struct, Enum;
