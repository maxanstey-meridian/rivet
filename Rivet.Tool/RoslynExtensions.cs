using Microsoft.CodeAnalysis;

namespace Rivet.Tool;

internal static class RoslynExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var nested in ns.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(nested))
            {
                yield return type;
            }
        }
    }

    public static bool Is(this AttributeData attribute, INamedTypeSymbol? type) =>
        type is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, type);

    public static AttributeData? GetAttribute(this ISymbol symbol, INamedTypeSymbol? type) =>
        type is null ? null : symbol.GetAttributes().FirstOrDefault(a => a.Is(type));

    public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol? type) =>
        symbol.GetAttribute(type) is not null;

    /// <summary>The first constructor argument as a string, or null.</summary>
    public static string? StringArgument(this AttributeData? attribute) =>
        attribute?.ConstructorArguments is [{ Value: string value }, ..] ? value : null;
}
