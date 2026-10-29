using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The security one operation declares, step by step: the contract builder's
/// <c>.Secure()</c>/<c>.AndSecure()</c>/<c>.OrAnonymous()</c>/<c>.Anonymous()</c> calls in chain
/// order, or their attribute twins on a controller, an action, or an abstract contract method in
/// source order. Both read
/// through this one model, so a sequence means the same thing either way and the same
/// sequences contradict (RIV1114): an anonymous declaration beside any alternative, and an
/// AND with no secured alternative before it to join.
/// </summary>
internal sealed class SecurityDeclaration(
    SecurityDeclaration.Spelling spelling,
    Func<string, RivetUserException> invalid
)
{
    /// <summary>
    /// How the declaration's steps are written, for its diagnostics, and what the steps are
    /// (<see cref="Noun"/>: calls or attributes).
    /// </summary>
    internal sealed record Spelling(
        string Anonymous,
        string Secure,
        string AndSecure,
        string OrAnonymous,
        string Noun
    );

    internal static readonly Spelling BuilderCalls = new(
        ".Anonymous()",
        ".Secure()",
        ".AndSecure()",
        ".OrAnonymous()",
        "calls"
    );

    internal static readonly Spelling Attributes = new(
        "[RivetAnonymous]",
        "[RivetSecure]",
        "[RivetAndSecure]",
        "[RivetOrAnonymous]",
        "attributes"
    );

    private readonly SecurityRequirementsBuilder _requirements = new();
    private int _alternatives;
    private bool _lastAlternativeHasSchemes;
    private bool _anonymous;

    public void Anonymous() => _anonymous = true;

    public void Secure(string scheme, IEnumerable<string> scopes)
    {
        _requirements.AddRequirement(_alternatives);
        _requirements.AddScheme(_alternatives++, scheme, scopes);
        _lastAlternativeHasSchemes = true;
    }

    public void AndSecure(string scheme, IEnumerable<string> scopes)
    {
        if (!_lastAlternativeHasSchemes)
        {
            throw invalid(
                $"{spelling.AndSecure} must follow {spelling.Secure}: it adds a scheme to the "
                    + $"alternative {spelling.Secure} started"
            );
        }

        _requirements.AddScheme(_alternatives - 1, scheme, scopes);
    }

    public void OrAnonymous()
    {
        _requirements.AddRequirement(_alternatives++);
        _lastAlternativeHasSchemes = false;
    }

    /// <summary>
    /// The declared requirements: <c>security: []</c> for an anonymous declaration, null when
    /// nothing was declared (the operation inherits the document's).
    /// </summary>
    public SecurityRequirements? Build()
    {
        if (_anonymous && _alternatives > 0)
        {
            throw invalid(
                $"{spelling.Anonymous} declares that the endpoint has no security requirement, so it "
                    + $"can't be combined with {spelling.Secure} or {spelling.OrAnonymous}; use "
                    + $"{spelling.OrAnonymous} alongside {spelling.Secure} to make credentials optional"
            );
        }

        return _anonymous ? new SecurityRequirements([]) : _requirements.Build();
    }

    /// <summary>
    /// Whether an attribute is ASP.NET's <c>[AllowAnonymous]</c>: any <c>IAllowAnonymous</c>, or
    /// a class derived from <c>AllowAnonymousAttribute</c>. The spec (K.3) and <c>--check</c>
    /// (K.5) read it by this one rule.
    /// </summary>
    internal static bool IsAllowAnonymous(WellKnownTypes wkt, ITypeSymbol? attributeClass) =>
        attributeClass.Implements(wkt.IAllowAnonymous)
        || attributeClass.DerivesFrom(wkt.AllowAnonymous);

    /// <summary>
    /// The security a <c>[RivetEndpoint]</c>/<c>[RivetClient]</c> action (or an abstract
    /// <c>[RivetContract]</c> method) declares with <c>[RivetSecure]</c>, <c>[RivetAndSecure]</c>,
    /// <c>[RivetOrAnonymous]</c> and <c>[RivetAnonymous]</c>, or null when it inherits the
    /// document's. The nearest level that declares any security says all of it: the method,
    /// then the methods it overrides, then its class, then the class's bases. ASP.NET's
    /// <c>[AllowAnonymous]</c> (any <c>IAllowAnonymous</c> attribute) declares the level
    /// anonymous when no Rivet security attribute sits beside it; beside them it's left to
    /// ASP.NET, so <c>[AllowAnonymous]</c> with <c>[RivetSecure]</c> and <c>[RivetOrAnonymous]</c>
    /// reads as optional credentials. <c>[Authorize]</c> names policies, not schemes, so it
    /// isn't read here (<c>--check</c> compares it with this).
    /// </summary>
    internal static SecurityRequirements? FromAttributes(
        WellKnownTypes wkt,
        IMethodSymbol method,
        string label
    )
    {
        foreach (var level in DeclarationLevels(method))
        {
            var attributes = level.GetAttributes();
            var declared = attributes
                .Where(attribute =>
                    attribute.Is(wkt.RivetSecure)
                    || attribute.Is(wkt.RivetAndSecure)
                    || attribute.Is(wkt.RivetOrAnonymous)
                    || attribute.Is(wkt.RivetAnonymous)
                )
                .ToList();
            if (declared.Count == 0)
            {
                if (attributes.Any(attribute => IsAllowAnonymous(wkt, attribute.AttributeClass)))
                {
                    return new SecurityRequirements([]);
                }

                continue;
            }

            var where = level is IMethodSymbol ? $"on '{level.Name}'" : $"on class '{level.Name}'";
            RivetUserException Invalid(string reason) =>
                Diagnostics.Error(
                    Diagnostics.InvalidSecurityDeclaration,
                    $"{label}: {reason} ({where})."
                );

            if (
                declared.Any(attribute => attribute.Is(wkt.RivetAndSecure))
                && declared
                    .Select(attribute => attribute.ApplicationSyntaxReference?.SyntaxTree)
                    .Distinct()
                    .Count() > 1
            )
            {
                throw Invalid(
                    "[RivetAndSecure] joins the [RivetSecure] above it, but these security attributes "
                        + "are split across partial declarations in different files, whose order C# "
                        + "doesn't define; declare them together in one file"
                );
            }

            var declaration = new SecurityDeclaration(Attributes, Invalid);
            foreach (var attribute in declared)
            {
                if (attribute.Is(wkt.RivetAnonymous))
                {
                    declaration.Anonymous();
                }
                else if (attribute.Is(wkt.RivetOrAnonymous))
                {
                    declaration.OrAnonymous();
                }
                else
                {
                    var (scheme, scopes) = SchemeAndScopes(attribute, Invalid);
                    if (attribute.Is(wkt.RivetSecure))
                    {
                        declaration.Secure(scheme, scopes);
                    }
                    else
                    {
                        declaration.AndSecure(scheme, scopes);
                    }
                }
            }

            return declaration.Build();
        }

        return null;
    }

    private static IEnumerable<ISymbol> DeclarationLevels(IMethodSymbol method)
    {
        foreach (var current in method.OverriddenChain().Prepend(method))
        {
            yield return current;
        }

        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            yield return type;
        }
    }

    private static (string Scheme, IReadOnlyList<string> Scopes) SchemeAndScopes(
        AttributeData attribute,
        Func<string, RivetUserException> invalid
    )
    {
        var name = $"[{attribute.AttributeClass!.Name.Replace("Attribute", "")}]";
        if (attribute.ConstructorArguments is not [{ Value: string scheme }, var scopes])
        {
            throw invalid($"{name} names no scheme");
        }

        var values = scopes.Values.Select(scope => scope.Value as string).ToList();
        return values.Contains(null)
            ? throw invalid($"{name} passes a null scope")
            : (scheme, values.OfType<string>().ToList());
    }
}
