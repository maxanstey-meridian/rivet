using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Walks Roslyn symbols from [RivetType]-attributed records and produces
/// TsTypeDefinitions. Transitively discovers referenced types (enums, nested records).
/// </summary>
public sealed class TypeWalker
{
    private readonly IAssemblySymbol _sourceAssembly;
    private readonly Dictionary<string, TsTypeDefinition> _definitions = new();
    private readonly HashSet<string> _visiting = new();

    private TypeWalker(IAssemblySymbol sourceAssembly)
    {
        _sourceAssembly = sourceAssembly;
    }

    public IReadOnlyDictionary<string, TsTypeDefinition> Definitions => _definitions;

    /// <summary>
    /// Finds all [RivetType]-attributed types in the compilation and walks them.
    /// </summary>
    public static IReadOnlyList<TsTypeDefinition> Walk(Compilation compilation)
    {
        var walker = new TypeWalker(compilation.Assembly);
        var attributeSymbol = compilation.GetTypeByMetadataName("Rivet.RivetTypeAttribute");

        if (attributeSymbol is null)
        {
            return [];
        }

        var attributedTypes = FindAttributedTypes(compilation, attributeSymbol);

        foreach (var type in attributedTypes)
        {
            walker.WalkType(type);
        }

        return [.. walker._definitions.Values];
    }

    private static IEnumerable<INamedTypeSymbol> FindAttributedTypes(
        Compilation compilation,
        INamedTypeSymbol attributeSymbol)
    {
        return GetAllTypes(compilation.GlobalNamespace)
            .Where(t => t.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol)));
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
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

    /// <summary>
    /// Walks a named type, producing a TsTypeDefinition and recursively
    /// discovering any referenced types (records, enums).
    /// </summary>
    private void WalkType(INamedTypeSymbol symbol)
    {
        var name = symbol.Name;

        if (_definitions.ContainsKey(name) || _visiting.Contains(name))
        {
            return;
        }

        // Enums are emitted inline as string unions, not as separate definitions
        if (symbol.TypeKind == TypeKind.Enum)
        {
            return;
        }

        _visiting.Add(name);

        var properties = new List<TsPropertyDefinition>();

        foreach (var member in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.IsStatic || member.IsIndexer || member.IsImplicitlyDeclared)
            {
                continue;
            }

            var tsName = ToCamelCase(member.Name);
            var tsType = MapType(member.Type);
            var isOptional = IsOptionalProperty(member);

            properties.Add(new TsPropertyDefinition(tsName, tsType, isOptional));
        }

        _visiting.Remove(name);
        _definitions[name] = new TsTypeDefinition(name, properties);
    }

    private TsType MapType(ITypeSymbol symbol)
    {
        // Nullable value type: int? → Nullable<int>
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = MapType(nullable.TypeArguments[0]);
            return new TsType.Nullable(inner);
        }

        // Nullable reference type annotation
        if (symbol.NullableAnnotation == NullableAnnotation.Annotated
            && symbol is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var inner = MapType(symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            return new TsType.Nullable(inner);
        }

        // Array T[]
        if (symbol is IArrayTypeSymbol arrayType)
        {
            return new TsType.Array(MapType(arrayType.ElementType));
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            // Primitives
            var primitive = MapPrimitive(namedType);
            if (primitive is not null)
            {
                return primitive;
            }

            // Collections: List<T>, IEnumerable<T>, IReadOnlyList<T>, IList<T>, ICollection<T>, IReadOnlyCollection<T>
            if (IsCollectionType(namedType) && namedType.TypeArguments.Length == 1)
            {
                return new TsType.Array(MapType(namedType.TypeArguments[0]));
            }

            // Dictionary<string, T>
            if (IsDictionaryType(namedType) && namedType.TypeArguments.Length == 2)
            {
                return new TsType.Dictionary(MapType(namedType.TypeArguments[1]));
            }

            // Enum → string union
            if (namedType.TypeKind == TypeKind.Enum)
            {
                var members = namedType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => f.HasConstantValue)
                    .Select(f => f.Name)
                    .ToList();

                return new TsType.StringUnion(members);
            }

            // Named record/class from source assembly → walk transitively, emit TypeRef
            if (namedType.TypeKind is TypeKind.Class or TypeKind.Struct
                && SymbolEqualityComparer.Default.Equals(namedType.ContainingAssembly, _sourceAssembly))
            {
                WalkType(namedType);
                return new TsType.TypeRef(namedType.Name);
            }
        }

        // Fallback
        return new TsType.Primitive("unknown");
    }

    private static TsType.Primitive? MapPrimitive(INamedTypeSymbol symbol)
    {
        // Special types via Roslyn's built-in classification
        return symbol.SpecialType switch
        {
            SpecialType.System_String => new TsType.Primitive("string"),
            SpecialType.System_Boolean => new TsType.Primitive("boolean"),
            SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                or SpecialType.System_Byte => new TsType.Primitive("number"),
            // Types identified by metadata name (no SpecialType)
            _ => symbol.ToDisplayString() switch
            {
                "System.Guid" => new TsType.Primitive("string"),
                "System.DateTime" => new TsType.Primitive("string"),
                "System.DateTimeOffset" => new TsType.Primitive("string"),
                "System.DateOnly" => new TsType.Primitive("string"),
                _ => null,
            }
        };
    }

    private static bool IsCollectionType(INamedTypeSymbol symbol)
    {
        var name = symbol.OriginalDefinition.ToDisplayString();
        return name is
            "System.Collections.Generic.List<T>" or
            "System.Collections.Generic.IList<T>" or
            "System.Collections.Generic.ICollection<T>" or
            "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.IReadOnlyList<T>" or
            "System.Collections.Generic.IReadOnlyCollection<T>";
    }

    private static bool IsDictionaryType(INamedTypeSymbol symbol)
    {
        var name = symbol.OriginalDefinition.ToDisplayString();
        return name is
            "System.Collections.Generic.Dictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>" or
            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
    }

    private static bool IsOptionalProperty(IPropertySymbol _)
    {
        // All record properties are required in the TS output.
        // Nullable affects the type (T | null), not presence.
        // TODO: support default parameter values as optional when needed.
        return false;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
