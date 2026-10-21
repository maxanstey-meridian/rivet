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
///
/// The read side refuses integers: a bare <c>0</c> always throws
/// <see cref="JsonException"/>, and so does a numeric
/// string such as <c>"0"</c> from .NET 9 on (.NET 8's System.Text.Json still
/// reads it as the value). It is not an exact match on the advertised names:
/// System.Text.Json also reads the C# member name in any casing, whatever the
/// policy. The built-in <see cref="JsonStringEnumConverter{TEnum}"/> keeps
/// STJ's default and accepts integers as well.
///
/// None of this applies to route, query, header or form values. ASP.NET binds
/// those itself and never consults a JSON converter.
/// </remarks>
public sealed class RivetLowerCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(new RivetLowerAllNamingPolicy(), allowIntegerValues: false)
    where T : struct, Enum;

/// <summary>camelCase enum wire values — "DarkGreen" → "darkGreen".</summary>
public sealed class RivetCamelCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where T : struct, Enum;

/// <summary>snake_case enum wire values — "RestrictedByway2A" → "restricted_byway2_a".</summary>
public sealed class RivetSnakeCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    where T : struct, Enum;

/// <summary>kebab-case enum wire values — "RestrictedByway2A" → "restricted-byway2-a".</summary>
public sealed class RivetKebabCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false)
    where T : struct, Enum;
