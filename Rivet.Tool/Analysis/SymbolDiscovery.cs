using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Single-pass discovery of all Rivet-attributed symbols in the source assembly.
/// Replaces 4 separate full-namespace traversals with one.
/// </summary>
public sealed record DiscoveredSymbols(
    IReadOnlyList<INamedTypeSymbol> RivetTypes,
    IReadOnlyList<INamedTypeSymbol> ContractTypes,
    IReadOnlyList<INamedTypeSymbol> ClientTypes,
    IReadOnlyList<IMethodSymbol> EndpointMethods);

public static class SymbolDiscovery
{
    public static DiscoveredSymbols Discover(Compilation compilation)
    {
        var rivetTypeAttr = compilation.GetTypeByMetadataName("Rivet.RivetTypeAttribute");
        var contractAttr = compilation.GetTypeByMetadataName("Rivet.RivetContractAttribute");
        var clientAttr = compilation.GetTypeByMetadataName("Rivet.RivetClientAttribute");
        var endpointAttr = compilation.GetTypeByMetadataName("Rivet.RivetEndpointAttribute");

        var rivetTypes = new List<INamedTypeSymbol>();
        var contractTypes = new List<INamedTypeSymbol>();
        var clientTypes = new List<INamedTypeSymbol>();
        var endpointMethods = new List<IMethodSymbol>();

        // Single pass over source assembly types only — not referenced assemblies
        foreach (var type in RoslynExtensions.GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            var attributes = type.GetAttributes();

            if (rivetTypeAttr is not null && attributes.Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, rivetTypeAttr)))
            {
                rivetTypes.Add(type);
            }

            if (contractAttr is not null && attributes.Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, contractAttr)))
            {
                contractTypes.Add(type);
            }

            if (clientAttr is not null && attributes.Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, clientAttr)))
            {
                clientTypes.Add(type);
            }

            if (endpointAttr is not null)
            {
                foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (member.GetAttributes().Any(a =>
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, endpointAttr)))
                    {
                        endpointMethods.Add(member);
                    }
                }
            }
        }

        return new DiscoveredSymbols(rivetTypes, contractTypes, clientTypes, endpointMethods);
    }
}
