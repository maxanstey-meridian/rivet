using Microsoft.CodeAnalysis;

namespace Rivet.Tool;

internal static class RoslynExtensions
{
    /// <summary>Every type in the namespace tree, including nested types.</summary>
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceOrTypeSymbol container)
    {
        foreach (var type in container.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetAllTypes(type))
            {
                yield return nested;
            }
        }

        if (container is INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
            {
                foreach (var type in GetAllTypes(child))
                {
                    yield return type;
                }
            }
        }
    }

    public static bool Is(this AttributeData attribute, INamedTypeSymbol? type) =>
        type is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, type);

    public static AttributeData? GetAttribute(this ISymbol symbol, INamedTypeSymbol? type) =>
        type is null ? null : symbol.GetAttributes().FirstOrDefault(a => a.Is(type));

    public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol? type) =>
        symbol.GetAttribute(type) is not null;

    /// <summary>
    /// <paramref name="type"/> is <paramref name="target"/> or derives from it. A generic
    /// <paramref name="target"/> matches any construction of it.
    /// </summary>
    public static bool DerivesFrom(this ITypeSymbol? type, INamedTypeSymbol? target)
    {
        if (target is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="type"/> is the interface <paramref name="target"/> or implements it. A
    /// generic <paramref name="target"/> matches any construction of it, as in
    /// <see cref="DerivesFrom"/>; a check that needs one construction compares the types itself.
    /// </summary>
    public static bool Implements(this ITypeSymbol? type, INamedTypeSymbol? target) =>
        target is not null
        && type is not null
        && (
            SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target)
            || type.AllInterfaces.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target)
            )
        );

    /// <summary>
    /// The attributes reflection's <c>GetCustomAttributes(inherit: true)</c> hands ASP.NET for
    /// the first symbol of <paramref name="chain"/> (a type then its bases, or a method then
    /// the methods it overrides), each with the symbol it sits on: the nearest symbol's own,
    /// then a farther one's whose class is <c>Inherited</c> and, unless it allows several, not
    /// already found nearer. <paramref name="attributeUsage"/> is
    /// <c>System.AttributeUsageAttribute</c>, read from each attribute class or its nearest
    /// base, else the defaults (inherited, one per symbol).
    /// </summary>
    public static IEnumerable<(AttributeData Attribute, ISymbol On)> InheritedAttributes(
        this IEnumerable<ISymbol> chain,
        INamedTypeSymbol? attributeUsage
    )
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var nearest = true;
        foreach (var symbol in chain)
        {
            var here = new List<INamedTypeSymbol>();
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass is not { } attributeClass)
                {
                    continue;
                }

                var (allowMultiple, inherited) = Usage(attributeClass);
                if (!nearest && (!inherited || !allowMultiple && seen.Contains(attributeClass)))
                {
                    continue;
                }

                here.Add(attributeClass);
                yield return (attribute, symbol);
            }

            seen.UnionWith(here);
            nearest = false;
        }

        (bool AllowMultiple, bool Inherited) Usage(INamedTypeSymbol attributeClass)
        {
            for (var type = attributeClass; type is not null; type = type.BaseType)
            {
                if (type.GetAttribute(attributeUsage) is { } usage)
                {
                    return (
                        usage.NamedArgument(nameof(AttributeUsageAttribute.AllowMultiple)) as bool?
                            ?? false,
                        usage.NamedArgument(nameof(AttributeUsageAttribute.Inherited)) as bool?
                            ?? true
                    );
                }
            }

            return (false, true);
        }
    }

    /// <summary><paramref name="type"/> and its base types, nearest first.</summary>
    public static IEnumerable<ITypeSymbol> SelfAndBases(this ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>The methods <paramref name="method"/> overrides, nearest first. Empty unless it's an override.</summary>
    public static IEnumerable<IMethodSymbol> OverriddenChain(this IMethodSymbol method)
    {
        for (
            var overridden = method.OverriddenMethod;
            overridden is not null;
            overridden = overridden.OverriddenMethod
        )
        {
            yield return overridden;
        }
    }

    /// <summary>
    /// The operation body of <paramref name="method"/> when its source is in the compilation:
    /// an ordinary method's or accessor's, or a local function's own operation. A partial
    /// method's is its implementation's. Null for a method only metadata knows.
    /// </summary>
    public static IOperation? SourceBody(this IMethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        definition = definition.PartialImplementationPart ?? definition;
        if (
            definition.ContainingAssembly
                is not ISourceAssemblySymbol { Compilation: var compilation }
            || definition.DeclaringSyntaxReferences is not [var reference]
        )
        {
            return null;
        }

        var syntax = reference.GetSyntax();
        return compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
    }

    /// <summary>The first constructor argument as a string, or null.</summary>
    public static string? StringArgument(this AttributeData? attribute) =>
        attribute?.ConstructorArguments is [{ Value: string value }, ..] ? value : null;

    /// <summary>The value of a named attribute argument, or null when it is not set.</summary>
    public static object? NamedArgument(this AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value;

    /// <summary>Where a node starts, as a compiler prints it: <c>path(line,column)</c>.</summary>
    public static string SourcePosition(this SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }

    /// <summary>The string values of an attribute argument, flattening arrays.</summary>
    public static IEnumerable<string> Strings(this TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Array ? constant.Values.SelectMany(Strings)
        : constant.Value is string value ? [value]
        : [];
}
