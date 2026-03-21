using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Walks Roslyn symbols from [RivetType]-attributed records and produces
/// TsTypeDefinitions. Transitively discovers referenced types (enums, nested records).
/// </summary>
public sealed class TypeWalker
{
    private readonly HashSet<IAssemblySymbol> _walkableAssemblies;
    private readonly Dictionary<string, TsTypeDefinition> _definitions = new();
    private readonly Dictionary<string, TsType.Brand> _brands = new();
    private readonly Dictionary<string, TsType.StringUnion> _enums = new();
    private readonly Dictionary<string, string?> _typeNamespaces = new();
    private readonly HashSet<string> _visiting = new();

    // Pre-resolved type symbols for fast comparison (avoids ToDisplayString in hot paths)
    private readonly INamedTypeSymbol? _jsonObjectType;
    private readonly INamedTypeSymbol? _jsonArrayType;
    private readonly INamedTypeSymbol? _guidType;
    private readonly INamedTypeSymbol? _dateTimeType;
    private readonly INamedTypeSymbol? _dateTimeOffsetType;
    private readonly INamedTypeSymbol? _dateOnlyType;
    private readonly INamedTypeSymbol? _jsonElementType;
    private readonly INamedTypeSymbol? _jsonNodeType;

    private readonly INamedTypeSymbol? _listType;
    private readonly INamedTypeSymbol? _iListType;
    private readonly INamedTypeSymbol? _iCollectionType;
    private readonly INamedTypeSymbol? _iEnumerableType;
    private readonly INamedTypeSymbol? _iReadOnlyListType;
    private readonly INamedTypeSymbol? _iReadOnlyCollectionType;

    private readonly INamedTypeSymbol? _dictionaryType;
    private readonly INamedTypeSymbol? _iDictionaryType;
    private readonly INamedTypeSymbol? _iReadOnlyDictionaryType;

    private readonly INamedTypeSymbol? _jsonPropertyNameType;
    private readonly INamedTypeSymbol? _jsonIgnoreType;
    private readonly INamedTypeSymbol? _obsoleteType;

    public TypeWalker(Compilation compilation)
    {
        // Build set of walkable assemblies: source + project references (not NuGet/framework)
        _walkableAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
        foreach (var reference in compilation.References)
        {
            if (reference is CompilationReference
                && compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
            {
                _walkableAssemblies.Add(asm);
            }
        }

        _jsonObjectType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonObject");
        _jsonArrayType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonArray");
        _guidType = compilation.GetTypeByMetadataName("System.Guid");
        _dateTimeType = compilation.GetTypeByMetadataName("System.DateTime");
        _dateTimeOffsetType = compilation.GetTypeByMetadataName("System.DateTimeOffset");
        _dateOnlyType = compilation.GetTypeByMetadataName("System.DateOnly");
        _jsonElementType = compilation.GetTypeByMetadataName("System.Text.Json.JsonElement");
        _jsonNodeType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonNode");

        _listType = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        _iListType = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
        _iCollectionType = compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1");
        _iEnumerableType = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        _iReadOnlyListType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        _iReadOnlyCollectionType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyCollection`1");

        _dictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        _iDictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2");
        _iReadOnlyDictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2");

        _jsonPropertyNameType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        _jsonIgnoreType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
        _obsoleteType = compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
    }

    public IReadOnlyDictionary<string, TsTypeDefinition> Definitions => _definitions;
    public IReadOnlyDictionary<string, TsType.Brand> Brands => _brands;
    public IReadOnlyDictionary<string, TsType.StringUnion> Enums => _enums;
    public IReadOnlyDictionary<string, string?> TypeNamespaces => _typeNamespaces;
    public bool HasErrors { get; private set; }

    /// <summary>
    /// Creates a walker and walks the provided [RivetType]-attributed types.
    /// Use SymbolDiscovery.Discover() to obtain the type list.
    /// </summary>
    public static TypeWalker Create(
        Compilation compilation,
        IReadOnlyList<INamedTypeSymbol> attributedTypes)
    {
        var walker = new TypeWalker(compilation);

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

    /// <summary>
    /// Returns true if the property has [JsonIgnore].
    /// </summary>
    public bool IsJsonIgnored(IPropertySymbol prop)
    {
        return _jsonIgnoreType is not null
            && prop.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonIgnoreType));
    }

    /// <summary>
    /// Returns the [JsonPropertyName] value if present, null otherwise.
    /// </summary>
    public string? GetJsonPropertyName(IPropertySymbol prop)
    {
        if (_jsonPropertyNameType is null)
        {
            return null;
        }

        var attr = prop.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonPropertyNameType));

        if (attr is not null && attr.ConstructorArguments.Length == 1
            && attr.ConstructorArguments[0].Value is string name)
        {
            return name;
        }

        return null;
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
            // Check for collision: same simple name, different fully-qualified name
            if (_definitions.ContainsKey(name))
            {
                var existingNs = _typeNamespaces.GetValueOrDefault(name);
                var incomingNs = GetNamespaceGroup(definition);
                if (existingNs != incomingNs)
                {
                    Console.Error.WriteLine(
                        $"error: type name collision — '{name}' exists in namespace '{existingNs ?? "(global)"}' and '{incomingNs ?? "(global)"}'. " +
                        "Use distinct type names or namespaces.");
                    HasErrors = true;
                }
            }

            return;
        }

        // Enums referenced transitively are added to _enums in MapTypeCore.
        // If an enum is the root entry point (via [RivetType]), walk it through
        // MapTypeCore so it gets registered, then return — no TsTypeDefinition needed.
        if (definition.TypeKind == TypeKind.Enum)
        {
            MapTypeCore(definition);
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

            // [JsonIgnore] → skip property
            if (_jsonIgnoreType is not null
                && member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonIgnoreType)))
            {
                continue;
            }

            // [JsonPropertyName("x")] → use "x" instead of camelCase(Name)
            string? jsonPropertyName = null;
            if (_jsonPropertyNameType is not null)
            {
                var attr = member.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonPropertyNameType));
                if (attr is not null && attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is string propName)
                {
                    jsonPropertyName = propName;
                }
            }

            var tsName = jsonPropertyName ?? Naming.ToCamelCase(member.Name);
            var tsType = MapTypeCore(member.Type);
            var isOptional = IsOptionalProperty(member);
            var isDeprecated = _obsoleteType is not null
                && member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _obsoleteType));

            properties.Add(new TsPropertyDefinition(tsName, tsType, isOptional, isDeprecated));
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
            // Primitives (SpecialType-based: string, bool, int, etc.)
            var primitive = MapPrimitive(namedType);
            if (primitive is not null)
            {
                return primitive;
            }

            // JsonObject → Record<string, unknown>, JsonArray → unknown[]
            if (SymbolEqualityComparer.Default.Equals(namedType, _jsonObjectType))
            {
                return new TsType.Dictionary(new TsType.Primitive("unknown"));
            }
            if (SymbolEqualityComparer.Default.Equals(namedType, _jsonArrayType))
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

            // Named record/class from source or project-referenced assembly → walk transitively
            if (namedType.TypeKind is TypeKind.Class or TypeKind.Struct
                && _walkableAssemblies.Contains(namedType.ContainingAssembly))
            {
                // Value Object convention: single property named "Value" → branded type
                // Skip for generic types — Wrapper<T>(T Value) is a generic record, not a VO
                var voInner = namedType.IsGenericType ? null : TryGetValueObjectInner(namedType);
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

        // ValueTuple → inline object { key: string; value: number }
        if (symbol is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            var fields = tupleType.TupleElements
                .Select(e => (Naming.ToCamelCase(e.Name), MapTypeCore(e.Type)))
                .ToList();
            return new TsType.InlineObject(fields);
        }

        // Fallback
        return new TsType.Primitive("unknown");
    }

    private TsType.Primitive? MapPrimitive(INamedTypeSymbol symbol)
    {
        // Special types via Roslyn's built-in classification (fast path)
        // CSharpType is set only when the type can't be recovered from Name+Format alone
        var result = symbol.SpecialType switch
        {
            SpecialType.System_String => new TsType.Primitive("string"),
            SpecialType.System_Boolean => new TsType.Primitive("boolean"),
            SpecialType.System_Int32 => new TsType.Primitive("number", "int32"),
            SpecialType.System_UInt32 => new TsType.Primitive("number", "int32", "uint"),
            SpecialType.System_Int64 => new TsType.Primitive("number", "int64"),
            SpecialType.System_UInt64 => new TsType.Primitive("number", "int64", "ulong"),
            SpecialType.System_Single => new TsType.Primitive("number", "float"),
            SpecialType.System_Double => new TsType.Primitive("number", "double"),
            SpecialType.System_Decimal => new TsType.Primitive("number", "decimal"),
            SpecialType.System_Int16 => new TsType.Primitive("number", "int16", "short"),
            SpecialType.System_UInt16 => new TsType.Primitive("number", "uint16", "ushort"),
            SpecialType.System_Byte => new TsType.Primitive("number", "uint8", "byte"),
            SpecialType.System_SByte => new TsType.Primitive("number", "int8", "sbyte"),
            _ => (TsType.Primitive?)null,
        };

        if (result is not null)
        {
            return result;
        }

        // Non-SpecialType primitives — resolved via symbol comparison instead of ToDisplayString
        if (SymbolEqualityComparer.Default.Equals(symbol, _guidType))
        {
            return new TsType.Primitive("string", "uuid");
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, _dateTimeType))
        {
            return new TsType.Primitive("string", "date-time");
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, _dateTimeOffsetType))
        {
            return new TsType.Primitive("string", "date-time", "DateTimeOffset");
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, _dateOnlyType))
        {
            return new TsType.Primitive("string", "date");
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, _jsonElementType)
            || SymbolEqualityComparer.Default.Equals(symbol, _jsonNodeType))
        {
            return new TsType.Primitive("unknown");
        }

        return null;
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

    private bool IsCollectionType(INamedTypeSymbol symbol)
    {
        var orig = symbol.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(orig, _listType)
            || SymbolEqualityComparer.Default.Equals(orig, _iListType)
            || SymbolEqualityComparer.Default.Equals(orig, _iCollectionType)
            || SymbolEqualityComparer.Default.Equals(orig, _iEnumerableType)
            || SymbolEqualityComparer.Default.Equals(orig, _iReadOnlyListType)
            || SymbolEqualityComparer.Default.Equals(orig, _iReadOnlyCollectionType);
    }

    private bool IsDictionaryType(INamedTypeSymbol symbol)
    {
        var orig = symbol.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(orig, _dictionaryType)
            || SymbolEqualityComparer.Default.Equals(orig, _iDictionaryType)
            || SymbolEqualityComparer.Default.Equals(orig, _iReadOnlyDictionaryType);
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
