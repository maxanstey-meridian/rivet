using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// What a route token's inline constraints say about its path parameter. A type constraint
/// (<c>int</c>, <c>long</c>, <c>guid</c>, <c>bool</c>, <c>datetime</c>, <c>decimal</c>,
/// <c>double</c>, <c>float</c>) types it; <c>alpha</c>, <c>length</c>, <c>min</c>,
/// <c>max</c>, <c>minlength</c>, <c>maxlength</c>, <c>range</c> and <c>regex</c> become the
/// matching schema keywords. Routing admits only what the constraints match and the binder
/// reads the declared type, so a constraint the declared type can't hold is refused
/// (RIV1125) rather than one of the two being picked for the spec. Constraints Rivet can't
/// read (<c>required</c>, which every path parameter already is, and custom ones registered
/// in code) add nothing.
/// </summary>
internal static class RouteConstraints
{
    private static readonly Dictionary<string, TsType.Primitive> _typeConstraints = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["int"] = new("number", "int32"),
        ["long"] = new("number", "int64"),
        ["guid"] = new("string", "uuid"),
        ["bool"] = new("boolean"),
        ["datetime"] = new("string", "date-time"),
        ["decimal"] = new("number", "decimal"),
        ["double"] = new("number", "double"),
        ["float"] = new("number", "float"),
    };

    // ASP.NET's AlphaRouteConstraint is ^[a-z]*$ matched ignoring case.
    private const string AlphaPattern = "^[a-zA-Z]*$";

    /// <summary>
    /// <paramref name="template"/>'s route tokens (<see cref="RouteParser.ParseRouteTokens"/>).
    /// A template routing refuses at startup (an unclosed parameter, an optional one with a
    /// default, two parameters in a row, and a repeated name unless
    /// <paramref name="readRepeats"/> lets one through, which only a static contract's field,
    /// the shape import writes, does) stops generation here rather
    /// than reaching the spec. So does a literal brace (<c>{{</c> or <c>}}</c>), which routing
    /// serves but an OpenAPI path can't hold (<see cref="RouteParser.HasLiteralBrace"/>).
    /// </summary>
    public static IReadOnlyList<RouteToken> Tokens(
        string template,
        string where,
        bool readRepeats = true
    )
    {
        IReadOnlyList<RouteToken> tokens;
        try
        {
            tokens = RouteParser.ParseRouteTokens(template, readRepeats);
        }
        catch (RoutePatternException exception)
        {
            throw Diagnostics.Error(
                Diagnostics.RouteConstraintContradictsType,
                $"route template '{template}' on {where}: routing refuses it at startup "
                    + $"({exception.Message.TrimEnd('.')}). Fix the template."
            );
        }

        if (RouteParser.HasLiteralBrace(template))
        {
            throw Diagnostics.Error(
                Diagnostics.RouteConstraintContradictsType,
                $"route template '{template}' on {where} carries a literal brace ('{{{{' or "
                    + "'}}'), which an OpenAPI path can't hold: path templating has no escape for "
                    + "one. Take the brace out of the path."
            );
        }

        return tokens;
    }

    /// <summary>
    /// The type <paramref name="token"/>'s type constraint gives its parameter, or null when
    /// it has none. Two type constraints that disagree admit nothing, so they are refused.
    /// </summary>
    public static TsType.Primitive? DeclaredType(RouteToken token, string where)
    {
        TsType.Primitive? declared = null;
        RouteConstraint? declaredBy = null;
        foreach (var constraint in token.Constraints)
        {
            if (!_typeConstraints.TryGetValue(constraint.Name, out var type))
            {
                continue;
            }

            if (declared is not null && declared != type)
            {
                throw Contradiction(
                    token,
                    where,
                    $"its constraints '{declaredBy}' and '{constraint}' give it two types, and a "
                        + "parameter has one; keep one of them"
                );
            }

            declared = type;
            declaredBy = constraint;
        }

        return declared;
    }

    /// <summary>
    /// <paramref name="param"/> with <paramref name="token"/>'s constraints applied. A type
    /// constraint must name the type <paramref name="declaredBy"/> gives the parameter; a
    /// keyword constraint must fit it (numeric bounds on an integer, length and pattern on a
    /// string) and is intersected with the bounds the member already carries, since routing
    /// and validation both apply.
    /// </summary>
    public static TsEndpointParam Apply(
        TsEndpointParam param,
        RouteToken token,
        string where,
        string declaredBy
    )
    {
        if (token.Constraints.Count == 0)
        {
            return param;
        }

        var leaf = Leaf(param.Type);
        if (DeclaredType(token, where) is { } constrained && !SameScalar(leaf, constrained))
        {
            var constraint = token.Constraints.First(constraint =>
                _typeConstraints.ContainsKey(constraint.Name)
            );
            throw Contradiction(
                token,
                where,
                $"its constraint '{constraint}' makes it {Describe(constrained)}, but {declaredBy} "
                    + $"is {Describe(leaf)}"
            );
        }

        var constraints = param.Constraints ?? new TsPropertyConstraints();
        foreach (var constraint in token.Constraints)
        {
            RequireFit(leaf, token, where, declaredBy, constraint);
            constraints = constraint.Name.ToLowerInvariant() switch
            {
                "min" => Numeric(constraints, Integer(token, where, constraint), null),
                "max" => Numeric(constraints, null, Integer(token, where, constraint)),
                "range" => Range(constraints, token, where, constraint),
                "length" => Length(constraints, token, where, constraint),
                "minlength" => Lengths(
                    constraints,
                    Count(token, where, constraint, constraint.Argument),
                    null
                ),
                "maxlength" => Lengths(
                    constraints,
                    null,
                    Count(token, where, constraint, constraint.Argument)
                ),
                "alpha" => Pattern(constraints, token, where, constraint, AlphaPattern),
                "regex" => Pattern(
                    constraints,
                    token,
                    where,
                    constraint,
                    IgnoringCase(
                        constraint.Argument
                            ?? throw Malformed(token, where, constraint, "a pattern")
                    )
                ),
                _ => constraints,
            };
        }

        if (
            constraints.Minimum > constraints.Maximum
            || constraints.Minimum >= constraints.ExclusiveMaximum
            || constraints.ExclusiveMinimum >= constraints.Maximum
            || constraints.ExclusiveMinimum >= constraints.ExclusiveMaximum
            || constraints.MinLength > constraints.MaxLength
        )
        {
            throw Contradiction(
                token,
                where,
                $"together with {declaredBy}'s own bounds its constraints admit no value"
            );
        }

        return param with
        {
            Constraints = constraints.HasAny ? constraints : null,
        };
    }

    private static void RequireFit(
        TsType? leaf,
        RouteToken token,
        string where,
        string declaredBy,
        RouteConstraint constraint
    )
    {
        var fits = constraint.Name.ToLowerInvariant() switch
        {
            "min" or "max" or "range" => IsInteger(leaf),
            "length" or "minlength" or "maxlength" or "alpha" or "regex" => IsString(leaf),
            _ => true,
        };
        if (!fits)
        {
            var needs = constraint.Name.ToLowerInvariant() is "min" or "max" or "range"
                ? "admits only integers"
                : "constrains the characters of a string";
            throw Contradiction(
                token,
                where,
                $"its constraint '{constraint}' {needs}, but {declaredBy} is {Describe(leaf)}"
            );
        }
    }

    private static TsPropertyConstraints Numeric(
        TsPropertyConstraints constraints,
        long? minimum,
        long? maximum
    ) =>
        constraints with
        {
            Minimum = Tighter(constraints.Minimum, minimum, Math.Max),
            Maximum = Tighter(constraints.Maximum, maximum, Math.Min),
        };

    private static TsPropertyConstraints Range(
        TsPropertyConstraints constraints,
        RouteToken token,
        string where,
        RouteConstraint constraint
    )
    {
        var (low, high) = Pair(token, where, constraint);
        return Numeric(
            constraints,
            ParseInteger(token, where, constraint, low),
            ParseInteger(token, where, constraint, high)
        );
    }

    private static TsPropertyConstraints Length(
        TsPropertyConstraints constraints,
        RouteToken token,
        string where,
        RouteConstraint constraint
    )
    {
        if (constraint.Argument?.Contains(',') == true)
        {
            var (low, high) = Pair(token, where, constraint);
            return Lengths(
                constraints,
                Count(token, where, constraint, low),
                Count(token, where, constraint, high)
            );
        }

        var exact = Count(token, where, constraint, constraint.Argument);
        return Lengths(constraints, exact, exact);
    }

    private static TsPropertyConstraints Lengths(
        TsPropertyConstraints constraints,
        int? minLength,
        int? maxLength
    ) =>
        constraints with
        {
            MinLength = (int?)Tighter(constraints.MinLength, minLength, Math.Max),
            MaxLength = (int?)Tighter(constraints.MaxLength, maxLength, Math.Min),
        };

    private static TsPropertyConstraints Pattern(
        TsPropertyConstraints constraints,
        RouteToken token,
        string where,
        RouteConstraint constraint,
        string pattern
    )
    {
        if (constraints.Pattern is { } existing && existing != pattern)
        {
            // A schema holds one pattern. Both apply on the server, and a lookahead
            // conjunction of the two would not mean what either says unanchored.
            throw Contradiction(
                token,
                where,
                $"'{constraint}' and the pattern '{existing}' both apply, and a schema holds one "
                    + "pattern; keep one of them"
            );
        }

        return constraints with
        {
            Pattern = pattern,
        };
    }

    /// <summary>
    /// <paramref name="pattern"/> rewritten to match ASCII letters in either case, as
    /// ASP.NET's regex constraint does (it compiles with <c>IgnoreCase</c>) and a JSON
    /// Schema pattern, which has no flags, otherwise would not: <c>a</c> becomes
    /// <c>[aA]</c>, and a class gains the other case of its letters (<c>[a-f]</c> becomes
    /// <c>[a-fA-F]</c>). Escapes (<c>\d</c>, <c>\x4a</c>, <c>\p{L}</c>) and group syntax
    /// (<c>(?&lt;name&gt;</c>, <c>(?:</c>) are copied as written. A pattern with no letter
    /// to fold comes back unchanged. Only ASCII letters written as themselves fold: an
    /// escaped letter (<c>\x4a</c>, <c>\u004A</c>) and a non-ASCII one stay one-case, so
    /// the spec admits a little less than routing does there.
    /// </summary>
    internal static string IgnoringCase(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        var index = 0;
        while (index < pattern.Length)
        {
            var c = pattern[index];
            if (c == '\\')
            {
                index = CopyEscape(pattern, index, builder);
            }
            else if (c == '[')
            {
                index = FoldClass(pattern, index, builder);
            }
            else if (c == '(' && index + 1 < pattern.Length && pattern[index + 1] == '?')
            {
                index = CopyGroupSyntax(pattern, index, builder);
            }
            else if (char.IsAsciiLetter(c))
            {
                builder.Append('[').Append(c).Append(OtherCase(c)).Append(']');
                index++;
            }
            else
            {
                builder.Append(c);
                index++;
            }
        }

        return builder.ToString();
    }

    private static int CopyEscape(string pattern, int index, StringBuilder builder)
    {
        var kind = index + 1 < pattern.Length ? pattern[index + 1] : '\0';
        var end = kind switch
        {
            'x' => index + 4,
            'u' => index + 6,
            'c' => index + 3,
            'p' or 'P' or 'k' when index + 2 < pattern.Length && pattern[index + 2] is '{' or '<' =>
                pattern.IndexOfAny(['}', '>'], index + 2) is var close and >= 0
                    ? close + 1
                    : pattern.Length,
            _ => index + 2,
        };

        end = Math.Min(end, pattern.Length);
        builder.Append(pattern, index, end - index);
        return end;
    }

    private static int CopyGroupSyntax(string pattern, int index, StringBuilder builder)
    {
        // (?: (?= (?! (?<= (?<! copy as is; (?<name> (?'name' and inline options (?i) (?i:
        // carry letters that are syntax, not text.
        var end = index + 2;
        if (end < pattern.Length && pattern[end] is '<' or '\'')
        {
            var close = pattern[end] == '<' ? '>' : '\'';
            if (end + 1 < pattern.Length && pattern[end + 1] is '=' or '!')
            {
                end += 2;
            }
            else
            {
                var found = pattern.IndexOf(close, end + 1);
                end = found < 0 ? pattern.Length : found + 1;
            }
        }
        else
        {
            while (
                end < pattern.Length && (char.IsAsciiLetter(pattern[end]) || pattern[end] == '-')
            )
            {
                end++;
            }
        }

        builder.Append(pattern, index, end - index);
        return end;
    }

    private static int FoldClass(string pattern, int index, StringBuilder builder)
    {
        // Each item's other case goes straight after it, never at the end of the class:
        // appended there it can meet a trailing '-' and form a new range ([a-z_-] would
        // become [a-z_-A-Z], a reversed range).
        builder.Append('[');
        index++;
        if (index < pattern.Length && pattern[index] == '^')
        {
            builder.Append('^');
            index++;
        }

        var first = true;
        while (index < pattern.Length && (pattern[index] != ']' || first))
        {
            first = false;
            if (pattern[index] == '\\')
            {
                // An escape, alone or as a range's start, is copied as written; its '-'
                // and end follow as literals or are folded like any other item.
                index = CopyEscape(pattern, index, builder);
                continue;
            }

            var low = pattern[index];
            var high = low;
            var isRange =
                index + 2 < pattern.Length
                && pattern[index + 1] == '-'
                && pattern[index + 2] != ']';
            if (isRange && pattern[index + 2] == '\\')
            {
                builder.Append(low).Append('-');
                index = CopyEscape(pattern, index + 2, builder);
                continue;
            }

            if (isRange)
            {
                high = pattern[index + 2];
                builder.Append(low).Append('-').Append(high);
                index += 3;
            }
            else
            {
                builder.Append(low);
                index++;
            }

            AppendOtherCase(low, high, 'a', 'z', builder);
            AppendOtherCase(low, high, 'A', 'Z', builder);
        }

        if (index < pattern.Length)
        {
            builder.Append(']');
            index++;
        }

        return index;
    }

    /// <summary>
    /// The other case of the letters <paramref name="low"/>..<paramref name="high"/> shares
    /// with <paramref name="from"/>..<paramref name="to"/> (one case's alphabet), as a
    /// single char or a range.
    /// </summary>
    private static void AppendOtherCase(
        char low,
        char high,
        char from,
        char to,
        StringBuilder builder
    )
    {
        var start = (char)Math.Max(low, from);
        var end = (char)Math.Min(high, to);
        if (start > end)
        {
            return;
        }

        builder.Append(OtherCase(start));
        if (end > start)
        {
            builder.Append('-').Append(OtherCase(end));
        }
    }

    private static char OtherCase(char c) =>
        char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);

    private static double? Tighter(
        double? current,
        double? added,
        Func<double, double, double> pick
    ) =>
        (current, added) switch
        {
            ({ } a, { } b) => pick(a, b),
            (null, { } b) => b,
            _ => current,
        };

    private static long Integer(RouteToken token, string where, RouteConstraint constraint) =>
        ParseInteger(token, where, constraint, constraint.Argument);

    private static long ParseInteger(
        RouteToken token,
        string where,
        RouteConstraint constraint,
        string? text
    ) =>
        long.TryParse(
            text?.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? value
            : throw Malformed(token, where, constraint, "an integer");

    private static int Count(
        RouteToken token,
        string where,
        RouteConstraint constraint,
        string? text
    ) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Malformed(token, where, constraint, "a non-negative length");

    private static (string Low, string High) Pair(
        RouteToken token,
        string where,
        RouteConstraint constraint
    ) =>
        constraint.Argument?.Split(',') is [var low, var high]
            ? (low, high)
            : throw Malformed(token, where, constraint, "two values separated by a comma");

    private static RivetUserException Malformed(
        RouteToken token,
        string where,
        RouteConstraint constraint,
        string expected
    ) =>
        Diagnostics.Error(
            Diagnostics.RouteConstraintContradictsType,
            $"route token '{{{token.Name}}}' on {where}: constraint '{constraint}' needs {expected} "
                + "as its argument, so routing refuses the template at startup. Fix the argument."
        );

    private static RivetUserException Contradiction(RouteToken token, string where, string why) =>
        Diagnostics.Error(
            Diagnostics.RouteConstraintContradictsType,
            $"route token '{{{token.Name}}}' on {where}: {why}. Routing admits only what the "
                + "constraints match and the binder reads the declared type, so the spec can't "
                + "describe both. Make them agree: change the type, or the constraint."
        );

    /// <summary>The scalar a parameter's type comes down to, through null and brands.</summary>
    private static TsType? Leaf(TsType type) =>
        type switch
        {
            TsType.Nullable nullable => Leaf(nullable.Inner),
            TsType.Brand brand => Leaf(brand.Inner),
            _ => type,
        };

    /// <summary>
    /// The two scalars emit the same schema: the same format, and the same JSON type, where
    /// a <c>number</c> with an integer format is the <c>integer</c> the emitter writes for it
    /// (<c>[RivetSchemaType("integer")] int</c> and <c>{id:int}</c> agree).
    /// </summary>
    private static bool SameScalar(TsType? leaf, TsType.Primitive constrained) =>
        leaf is TsType.Primitive primitive
        && JsonType(primitive) == JsonType(constrained)
        && primitive.Format == constrained.Format;

    private static string JsonType(TsType.Primitive primitive) =>
        IsInteger(primitive) ? "integer" : primitive.Name;

    private static bool IsInteger(TsType? leaf) =>
        leaf
            is TsType.Primitive
                {
                    Name: "number",
                    Format: "int8"
                        or "uint8"
                        or "int16"
                        or "uint16"
                        or "int32"
                        or "uint32"
                        or "int64"
                        or "uint64"
                }
                or TsType.Primitive { Name: "integer" };

    private static bool IsString(TsType? leaf) =>
        leaf is TsType.Primitive { Name: "string" } or TsType.StringUnion;

    private static string Describe(TsType? leaf) =>
        leaf switch
        {
            TsType.Primitive { Format: { } format } primitive => $"{primitive.Name} ({format})",
            TsType.Primitive primitive => primitive.Name,
            TsType.StringUnion => "a string enum",
            TsType.TypeRef reference => $"'{reference.Name}'",
            _ => "not a scalar",
        };
}
