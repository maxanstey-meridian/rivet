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
    private readonly Dictionary<string, TsType.Brand> _brands = new();
    private readonly Dictionary<string, TsType.StringUnion> _enums = new();
    private readonly Dictionary<string, string?> _typeNamespaces = new();
    private readonly HashSet<string> _visiting = new();

    public TypeWalker(IAssemblySymbol sourceAssembly)
    {
        _sourceAssembly = sourceAssembly;
    }

    public IReadOnlyDictionary<string, TsTypeDefinition> Definitions => _definitions;
    public IReadOnlyDictionary<string, TsType.Brand> Brands => _brands;
    public IReadOnlyDictionary<string, TsType.StringUnion> Enums => _enums;
    public IReadOnlyDictionary<string, string?> TypeNamespaces => _typeNamespaces;

    /// <summary>
    /// Finds all [RivetType]-attributed types in the compilation and walks them.
    /// Returns the definitions collected so far.
    /// </summary>
    public static IReadOnlyList<TsTypeDefinition> Walk(Compilation compilation)
    {
        var walker = Create(compilation);
        return [.. walker._definitions.Values];
    }

    /// <summary>
    /// Creates a walker and discovers [RivetType]-attributed types.
    /// Returns the walker instance so endpoint analysis can share it.
    /// </summary>
    public static TypeWalker Create(Compilation compilation)
    {
        var walker = new TypeWalker(compilation.Assembly);
        var attributeSymbol = compilation.GetTypeByMetadataName("Rivet.RivetTypeAttribute");

        if (attributeSymbol is null)
        {
            return walker;
        }

        var attributedTypes = FindAttributedTypes(compilation, attributeSymbol);

        foreach (var type in attributedTypes)
        {
            walker.WalkType(type);
        }

        return walker;
    }

    /// <summary>
    /// Maps a Roslyn type symbol to its TsType representation.
    /// Used by EndpointWalker for parameter and return types.
    /// </summary>
    public TsType MapType(ITypeSymbol symbol) => MapTypeCore(symbol);

    private static IEnumerable<INamedTypeSymbol> FindAttributedTypes(
        Compilation compilation,
        INamedTypeSymbol attributeSymbol)
    {
        return RoslynExtensions.GetAllTypes(compilation.GlobalNamespace)
            .Where(t => t.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol)));
    }

    /// <summary>
    /// Walks a named type, producing a TsTypeDefinition and recursively
    /// discovering any referenced types (records, enums).
    /// For generic types, walks the unbound (original) definition.
    /// </summary>
    private void WalkType(INamedTypeSymbol symbol)
    {
        // For closed generics like PagedResult<MessageDto>, walk the open definition
        var definition = symbol.IsGenericType ? symbol.OriginalDefinition : symbol;
        var name = definition.Name;

        if (_definitions.ContainsKey(name) || _visiting.Contains(name))
        {
            return;
        }

        // Enums are emitted inline as string unions, not as separate definitions
        if (definition.TypeKind == TypeKind.Enum)
        {
            return;
        }

        _visiting.Add(name);

        // Extract type parameter names (e.g. "T", "TItem")
        var typeParams = definition.TypeParameters
            .Select(tp => tp.Name)
            .ToList();

        var properties = new List<TsPropertyDefinition>();

        foreach (var member in definition.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.IsStatic || member.IsIndexer || member.IsImplicitlyDeclared)
            {
                continue;
            }

            var tsName = Naming.ToCamelCase(member.Name);
            var tsType = MapTypeCore(member.Type);
            var isOptional = IsOptionalProperty(member);

            properties.Add(new TsPropertyDefinition(tsName, tsType, isOptional));
        }

        _visiting.Remove(name);
        _definitions[name] = new TsTypeDefinition(name, typeParams, properties);
        _typeNamespaces.TryAdd(name, GetNamespaceGroup(definition));
    }

    private TsType MapTypeCore(ITypeSymbol symbol)
    {
        // Type parameter (e.g. T in PagedResult<T>) → emit as-is
        if (symbol is ITypeParameterSymbol typeParam)
        {
            return new TsType.TypeParam(typeParam.Name);
        }

        // Nullable value type: int? → Nullable<int>
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = MapTypeCore(nullable.TypeArguments[0]);
            return new TsType.Nullable(inner);
        }

        // Nullable reference type annotation
        if (symbol.NullableAnnotation == NullableAnnotation.Annotated
            && symbol is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var inner = MapTypeCore(symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            return new TsType.Nullable(inner);
        }

        // Array T[]
        if (symbol is IArrayTypeSymbol arrayType)
        {
            return new TsType.Array(MapTypeCore(arrayType.ElementType));
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            // Primitives
            var primitive = MapPrimitive(namedType);
            if (primitive is not null)
            {
                return primitive;
            }

            // JsonObject → Record<string, unknown>, JsonArray → unknown[]
            var displayName = namedType.ToDisplayString();
            if (displayName is "System.Text.Json.Nodes.JsonObject")
            {
                return new TsType.Dictionary(new TsType.Primitive("unknown"));
            }
            if (displayName is "System.Text.Json.Nodes.JsonArray")
            {
                return new TsType.Array(new TsType.Primitive("unknown"));
            }

            // Collections: List<T>, IEnumerable<T>, IReadOnlyList<T>, IList<T>, ICollection<T>, IReadOnlyCollection<T>
            if (IsCollectionType(namedType) && namedType.TypeArguments.Length == 1)
            {
                return new TsType.Array(MapTypeCore(namedType.TypeArguments[0]));
            }

            // Dictionary<string, T>
            if (IsDictionaryType(namedType) && namedType.TypeArguments.Length == 2)
            {
                return new TsType.Dictionary(MapTypeCore(namedType.TypeArguments[1]));
            }

            // Enum → named string union type
            if (namedType.TypeKind == TypeKind.Enum)
            {
                if (!_enums.ContainsKey(namedType.Name))
                {
                    var members = namedType.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => f.HasConstantValue)
                        .Select(f => f.Name)
                        .ToList();

                    _enums[namedType.Name] = new TsType.StringUnion(members);
                    _typeNamespaces.TryAdd(namedType.Name, GetNamespaceGroup(namedType));
                }

                return new TsType.TypeRef(namedType.Name);
            }

            // Named record/class from source assembly → walk transitively
            if (namedType.TypeKind is TypeKind.Class or TypeKind.Struct
                && SymbolEqualityComparer.Default.Equals(namedType.ContainingAssembly, _sourceAssembly))
            {
                // Value Object convention: single property named "Value" → branded type
                var voInner = TryGetValueObjectInner(namedType);
                if (voInner is not null)
                {
                    var brand = new TsType.Brand(namedType.Name, MapTypeCore(voInner));
                    _brands.TryAdd(namedType.Name, brand);
                    _typeNamespaces.TryAdd(namedType.Name, GetNamespaceGroup(namedType));
                    return brand;
                }

                WalkType(namedType);

                // Closed generic (e.g. PagedResult<MessageDto>) → Generic node
                if (namedType.IsGenericType && !namedType.IsUnboundGenericType)
                {
                    var tsArgs = namedType.TypeArguments.Select(MapTypeCore).ToList();
                    return new TsType.Generic(namedType.Name, tsArgs);
                }

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
                or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                or SpecialType.System_Byte or SpecialType.System_SByte => new TsType.Primitive("number"),
            // Types identified by metadata name (no SpecialType)
            _ => symbol.ToDisplayString() switch
            {
                "System.Guid" => new TsType.Primitive("string"),
                "System.DateTime" => new TsType.Primitive("string"),
                "System.DateTimeOffset" => new TsType.Primitive("string"),
                "System.DateOnly" => new TsType.Primitive("string"),
                "System.Text.Json.JsonElement" => new TsType.Primitive("unknown"),
                "System.Text.Json.Nodes.JsonNode" => new TsType.Primitive("unknown"),
                _ => null,
            }
        };
    }

    /// <summary>
    /// Detects Value Object convention: a record with exactly one non-implicit
    /// property named "Value". Returns the inner type symbol, or null.
    /// </summary>
    private static ITypeSymbol? TryGetValueObjectInner(INamedTypeSymbol symbol)
    {
        var props = symbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && !p.IsImplicitlyDeclared)
            .ToList();

        if (props.Count == 1 && props[0].Name == "Value")
        {
            return props[0].Type;
        }

        return null;
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

    /// <summary>
    /// Gets the last segment of the containing namespace for grouping.
    /// Returns null for types in the global namespace.
    /// </summary>
    private static string? GetNamespaceGroup(INamedTypeSymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return null;
        }

        return ns.Name;
    }
}
