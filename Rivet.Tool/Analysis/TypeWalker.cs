using System.Collections.Immutable;
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
    private readonly Dictionary<string, TsType> _enums = new();
    private readonly Dictionary<string, string?> _typeNamespaces = new();
    private readonly HashSet<string> _visiting = new();

    // A5: emitted-name registry keyed by fully-qualified name (namespace + arity).
    // Distinct types whose simple names collide get deterministic numeric suffixes
    // (discovery order), mirroring the component-name registry in OpenApiEmitter.
    private readonly Dictionary<string, string> _emittedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedNames = new(StringComparer.Ordinal);

    // Scalar C# types that map directly to TsType.Primitive (Guid → string/uuid, etc.)
    private readonly ImmutableDictionary<INamedTypeSymbol, TsType.Primitive> _scalarTypes;

    // JSON container types that map to non-Primitive TsTypes (special-cased in MapTypeCore)
    private readonly INamedTypeSymbol? _jsonObjectType;
    private readonly INamedTypeSymbol? _jsonArrayType;
    private readonly INamedTypeSymbol? _timeSpanType;
    private readonly INamedTypeSymbol? _bigIntegerType;

    // Generic collection/dictionary type sets for membership checks
    private readonly ImmutableHashSet<INamedTypeSymbol> _collectionTypes;
    private readonly ImmutableHashSet<INamedTypeSymbol> _dictionaryTypes;

    // Attribute symbols for property-level metadata
    private readonly INamedTypeSymbol? _jsonPropertyNameType;
    private readonly INamedTypeSymbol? _jsonIgnoreType;
    private readonly INamedTypeSymbol? _obsoleteType;

    // STJ polymorphism attributes (P2 wave 4): [JsonPolymorphic]/[JsonDerivedType]
    // base types lower to a TaggedUnion alias instead of silently flattening.
    private readonly INamedTypeSymbol? _jsonPolymorphicType;
    private readonly INamedTypeSymbol? _jsonDerivedTypeType;

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

        // Scalar type → TsType.Primitive lookup
        var scalars = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, TsType.Primitive>(SymbolEqualityComparer.Default);
        AddScalar(scalars, compilation, "System.Guid", new TsType.Primitive("string", "uuid"));
        AddScalar(scalars, compilation, "System.DateTime", new TsType.Primitive("string", "date-time"));
        AddScalar(scalars, compilation, "System.DateTimeOffset", new TsType.Primitive("string", "date-time", "DateTimeOffset"));
        AddScalar(scalars, compilation, "System.DateOnly", new TsType.Primitive("string", "date"));
        AddScalar(scalars, compilation, "System.TimeOnly", new TsType.Primitive("string", "time"));
        AddScalar(scalars, compilation, "System.Uri", new TsType.Primitive("string", "uri"));
        AddScalar(scalars, compilation, "System.Text.Json.JsonElement", new TsType.Primitive("unknown"));
        AddScalar(scalars, compilation, "System.Text.Json.Nodes.JsonNode", new TsType.Primitive("unknown", CSharpType: "JsonNode"));
        AddScalar(scalars, compilation, "Microsoft.AspNetCore.Http.IFormFile", new TsType.Primitive("File"));
        _scalarTypes = scalars.ToImmutable();

        _jsonObjectType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonObject");
        _jsonArrayType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonArray");

        // Diagnosed-unsupported scalars (FABLE_GAPS §7 item 12) — resolved up front
        // so the fallback path can name them instead of failing silently.
        _timeSpanType = compilation.GetTypeByMetadataName("System.TimeSpan");
        _bigIntegerType = compilation.GetTypeByMetadataName("System.Numerics.BigInteger");

        _collectionTypes = ResolveTypeSet(compilation,
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.IList`1",
            "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IEnumerable`1",
            "System.Collections.Generic.IReadOnlyList`1",
            "System.Collections.Generic.IReadOnlyCollection`1");

        _dictionaryTypes = ResolveTypeSet(compilation,
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.IReadOnlyDictionary`2");

        _jsonPropertyNameType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        _jsonIgnoreType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
        _obsoleteType = compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
        _jsonPolymorphicType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPolymorphicAttribute");
        _jsonDerivedTypeType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonDerivedTypeAttribute");
    }

    private static void AddScalar(
        ImmutableDictionary<INamedTypeSymbol, TsType.Primitive>.Builder builder,
        Compilation compilation,
        string metadataName,
        TsType.Primitive mapped)
    {
        var symbol = compilation.GetTypeByMetadataName(metadataName);
        if (symbol is not null)
            builder.Add(symbol, mapped);
    }

    private static ImmutableHashSet<INamedTypeSymbol> ResolveTypeSet(
        Compilation compilation,
        params string[] metadataNames)
    {
        var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var name in metadataNames)
        {
            var symbol = compilation.GetTypeByMetadataName(name);
            if (symbol is not null)
                builder.Add(symbol);
        }
        return builder.ToImmutable();
    }

    public IReadOnlyDictionary<string, TsTypeDefinition> Definitions => _definitions;
    public IReadOnlyDictionary<string, TsType.Brand> Brands => _brands;
    public IReadOnlyDictionary<string, TsType> Enums => _enums;
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
    public TsType MapType(ITypeSymbol symbol, string? context = null) => MapTypeCore(symbol, context);

    /// <summary>
    /// Returns true if the property has [JsonIgnore].
    /// </summary>
    public bool IsJsonIgnored(IPropertySymbol prop)
    {
        return _jsonIgnoreType is not null
            && prop.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonIgnoreType));
    }

    /// <summary>
    /// A3: flattens the property surface of a type across its BaseType chain
    /// (base-most first; derived declarations win on name collision — overrides and
    /// shadowing both resolve to the most-derived declaration). Stops at object/ValueType
    /// and at base types outside the walkable assemblies. Skips static/indexer/implicitly
    /// declared members and records' synthesized EqualityContract.
    /// </summary>
    public IReadOnlyList<IPropertySymbol> GetEffectiveProperties(ITypeSymbol type)
    {
        var chain = new List<ITypeSymbol>();
        var current = type;
        while (current is not null
            && current.SpecialType is not SpecialType.System_Object
            && current.SpecialType is not SpecialType.System_ValueType)
        {
            chain.Add(current);

            var baseType = (current as INamedTypeSymbol)?.BaseType;
            current = baseType is not null
                && baseType.ContainingAssembly is not null
                && _walkableAssemblies.Contains(baseType.ContainingAssembly)
                ? baseType
                : null;
        }

        chain.Reverse(); // base-most first, matching rivet-ts's X5 flatten semantics

        var ordered = new List<IPropertySymbol>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var link in chain)
        {
            foreach (var member in link.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.IsStatic || member.IsIndexer || member.IsImplicitlyDeclared)
                {
                    continue;
                }

                // Records synthesize EqualityContract; guard by name in case a compiler
                // version stops marking it implicitly declared.
                if (member.Name == "EqualityContract")
                {
                    continue;
                }

                if (indexByName.TryGetValue(member.Name, out var existingIndex))
                {
                    // Derived override/shadow wins, keeping the base's position
                    ordered[existingIndex] = member;
                }
                else
                {
                    indexByName[member.Name] = ordered.Count;
                    ordered.Add(member);
                }
            }
        }

        return ordered;
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
        // A5: resolve via the full-namespace registry — same FQN reuses its emitted name,
        // a simple-name collision gets a deterministic disambiguated name + loud diagnostic
        var name = GetEmittedName(definition);

        if (_definitions.ContainsKey(name) || _visiting.Contains(name))
        {
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

        // P2 wave 4: a [JsonPolymorphic]/[JsonDerivedType] base type registers as a
        // TaggedUnion alias definition (oneOf + discriminator + mapping) instead of
        // silently flattening to its own property surface. Diagnosed-unsupported
        // shapes (non-string tags, zero registrations) fall through to flattening.
        // Generic polymorphic bases keep the flattening path — a generic alias has
        // no monomorphisation template the emitter could instantiate.
        if (!definition.IsGenericType && TryBuildPolymorphicUnion(definition, name) is { } union)
        {
            _visiting.Remove(name);
            _definitions[name] = new TsTypeDefinition(name, typeParams, union, GetTypeDescription(definition));
            _typeNamespaces.TryAdd(name, GetNamespaceGroup(definition));
            return;
        }

        var properties = new List<TsPropertyDefinition>();

        // A3: include inherited properties by flattening the BaseType chain
        foreach (var member in GetEffectiveProperties(definition))
        {
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
            var tsType = MapTypeCore(member.Type, $"{name}.{member.Name}");
            var isOptional = IsOptionalProperty(member);
            var isDeprecated = _obsoleteType is not null
                && member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _obsoleteType));

            // Read metadata attributes
            string? format = null;
            string? defaultValue = null;
            TsPropertyConstraints? constraints = null;
            string? description = null;
            string? example = null;
            var isReadOnly = false;
            var isWriteOnly = false;
            var daConstraints = ReadDataAnnotationConstraints(member.GetAttributes());
            var daFormat = ReadDataAnnotationFormat(member.GetAttributes());
            foreach (var attr in member.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                if (attrName is "RivetFormatAttribute" && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string fmt)
                {
                    format = fmt;
                }
                else if (attrName is "RivetDefaultAttribute" && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string def)
                {
                    defaultValue = def;
                }
                else if (attrName is "RivetConstraintsAttribute")
                {
                    constraints = ReadConstraints(attr);
                }
                else if (attrName is "RivetDescriptionAttribute" && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string desc)
                {
                    description = desc;
                }
                else if (attrName is "RivetExampleAttribute" && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string ex)
                {
                    example = ex;
                }
                else if (attrName is "RivetReadOnlyAttribute")
                {
                    isReadOnly = true;
                }
                else if (attrName is "RivetWriteOnlyAttribute")
                {
                    isWriteOnly = true;
                }
            }

            // DA format is a fallback — explicit [RivetFormat] takes precedence.
            format ??= daFormat;

            // Merge DataAnnotation constraints with RivetConstraints.
            // DA provides standard fields; RivetConstraints provides exotic-only fields.
            if (daConstraints is not null && constraints is not null)
            {
                constraints = new TsPropertyConstraints(
                    MinLength: daConstraints.MinLength,
                    MaxLength: daConstraints.MaxLength,
                    Pattern: daConstraints.Pattern,
                    Minimum: daConstraints.Minimum,
                    Maximum: daConstraints.Maximum,
                    ExclusiveMinimum: constraints.ExclusiveMinimum,
                    ExclusiveMaximum: constraints.ExclusiveMaximum,
                    MultipleOf: constraints.MultipleOf,
                    MinItems: constraints.MinItems,
                    MaxItems: constraints.MaxItems,
                    UniqueItems: constraints.UniqueItems);
            }
            else
            {
                constraints ??= daConstraints;
            }

            // Apply format to the TsType if it's a primitive without one already
            if (format is not null && tsType is TsType.Primitive { Format: null } p)
            {
                tsType = p with { Format = format };
            }
            else if (format is not null && tsType is TsType.Nullable { Inner: TsType.Primitive { Format: null } np })
            {
                tsType = new TsType.Nullable(np with { Format = format });
            }

            properties.Add(new TsPropertyDefinition(tsName, tsType, isOptional, isDeprecated, format, defaultValue, constraints,
                description, example, isReadOnly, isWriteOnly));
        }

        // Read type-level [RivetDescription] attribute
        var typeDescription = GetTypeDescription(definition);

        _visiting.Remove(name);
        _definitions[name] = new TsTypeDefinition(name, typeParams, properties, typeDescription);
        _typeNamespaces.TryAdd(name, GetNamespaceGroup(definition));
    }

    private static string? GetTypeDescription(INamedTypeSymbol definition)
    {
        foreach (var attr in definition.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "RivetDescriptionAttribute"
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is string td)
            {
                return td;
            }
        }

        return null;
    }

    /// <summary>
    /// P2 wave 4: lowers a [JsonPolymorphic]/[JsonDerivedType] base type to a
    /// TaggedUnion whose variants are the [JsonDerivedType] registrations, matching
    /// System.Text.Json's wire semantics when serializing AS the base type: the
    /// discriminator property (default <c>$type</c>) is written first with the
    /// registration's tag, followed by the derived type's full flattened property
    /// surface. The base itself is a variant only if explicitly registered. Returns
    /// null when the symbol carries neither attribute, or when the shape is
    /// diagnosed-unsupported (non-string tags, zero registrations) — callers then
    /// fall back to the plain flattening path.
    /// </summary>
    private TsType.TaggedUnion? TryBuildPolymorphicUnion(INamedTypeSymbol definition, string name)
    {
        if (_jsonPolymorphicType is null && _jsonDerivedTypeType is null)
        {
            return null;
        }

        AttributeData? polymorphicAttr = null;
        var derivedAttrs = new List<AttributeData>();
        foreach (var attr in definition.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, _jsonPolymorphicType))
            {
                polymorphicAttr = attr;
            }
            else if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, _jsonDerivedTypeType))
            {
                derivedAttrs.Add(attr);
            }
        }

        if (polymorphicAttr is null && derivedAttrs.Count == 0)
        {
            return null;
        }

        if (polymorphicAttr is not null
            && polymorphicAttr.NamedArguments.Any(a => a.Key == "UnknownDerivedTypeHandling"))
        {
            Diagnostics.Warn(
                Diagnostics.PolymorphicUnknownHandlingDropped,
                $"[JsonPolymorphic] UnknownDerivedTypeHandling on '{name}' has no spec representation — " +
                "the emitted oneOf admits only the registered derived types");
        }

        if (derivedAttrs.Count == 0)
        {
            Diagnostics.Warn(
                Diagnostics.PolymorphicNoDerivedTypes,
                $"[JsonPolymorphic] on '{name}' has no [JsonDerivedType] registrations — " +
                "there is no variant set to emit; falling back to plain property flattening");
            return null;
        }

        var discriminator = "$type";
        if (polymorphicAttr?.NamedArguments
                .FirstOrDefault(a => a.Key == "TypeDiscriminatorPropertyName").Value.Value is string custom)
        {
            discriminator = custom;
        }

        var registrations = new List<(INamedTypeSymbol Type, string Tag)>();
        foreach (var attr in derivedAttrs)
        {
            if (attr.ConstructorArguments.Length == 0
                || attr.ConstructorArguments[0].Value is not INamedTypeSymbol derivedType)
            {
                continue;
            }

            var tagValue = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value : null;
            if (tagValue is not string tag)
            {
                // Do NOT stringify int tags: a spec validating string tags against an
                // int wire value would be a lie. Flatten the whole base, loudly.
                Diagnostics.Warn(
                    Diagnostics.PolymorphicNonStringTag,
                    $"[JsonDerivedType] on '{name}' registers '{derivedType.ToDisplayString()}' with " +
                    $"{(tagValue is null ? "no" : "a non-string")} discriminator tag — a string-discriminated " +
                    $"oneOf cannot represent it; falling back to plain property flattening for '{name}'");
                return null;
            }

            registrations.Add((derivedType, tag));
        }

        if (registrations.Count == 0)
        {
            return null;
        }

        var variants = new List<TsType.TaggedUnionVariant>();
        foreach (var (derivedType, tag) in registrations)
        {
            // Synthesized discriminator property first — a single-member StringUnion,
            // required (non-Nullable), mirroring the TS lowerer's variant shape —
            // then the derived type's full flattened property surface.
            var fields = new List<(string Name, TsType Type)>
            {
                (discriminator, new TsType.StringUnion([tag])),
            };

            foreach (var member in GetEffectiveProperties(derivedType))
            {
                if (IsJsonIgnored(member))
                {
                    continue;
                }

                var fieldName = GetJsonPropertyName(member) ?? Naming.ToCamelCase(member.Name);
                var fieldType = MapTypeCore(member.Type, $"{name}.{tag}.{member.Name}");

                // InlineObject has no optionality slot: required = non-Nullable.
                // Optional-but-non-nullable properties widen to Nullable so they
                // stay out of the variant's required array.
                if (IsOptionalProperty(member) && fieldType is not TsType.Nullable)
                {
                    fieldType = new TsType.Nullable(fieldType);
                }

                fields.Add((fieldName, fieldType));
            }

            variants.Add(new TsType.TaggedUnionVariant(tag, new TsType.InlineObject(fields)));
        }

        return new TsType.TaggedUnion(discriminator, variants);
    }

    /// <summary>
    /// A5: returns the emitted (schema/TS) name for a type. Keyed internally by
    /// fully-qualified name so distinct types never silently merge; the emitted name
    /// stays the short simple name unless it collides, in which case the later type
    /// gets a deterministic numeric suffix (discovery order) and a loud diagnostic —
    /// consistent with the OpenApiEmitter component-name registry.
    /// </summary>
    private string GetEmittedName(INamedTypeSymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        var key = definition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (_emittedNames.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var name = definition.Name;
        if (!_claimedNames.Add(name))
        {
            var pure = name;
            var i = 2;
            do
            {
                name = pure + i;
                i++;
            }
            while (!_claimedNames.Add(name));

            Diagnostics.Warn(
                Diagnostics.TypeNameCollision,
                $"type name collision — '{pure}' ({key}) collides with a previously walked type of the same name; " +
                $"emitting it as '{name}'. Use distinct type names to keep schema names stable.");
        }

        _emittedNames[key] = name;
        return name;
    }

    private TsType MapTypeCore(ITypeSymbol symbol, string? context = null)
    {
        // Nullable value type: int? → Nullable<int>
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = MapTypeCore(nullable.TypeArguments[0], context);
            return new TsType.Nullable(inner);
        }

        // Nullable reference type annotation.
        // A12: must run before the type-parameter check so Wrapper<T>(T? Value)
        // lowers as Nullable(TypeParam), not bare TypeParam.
        if (symbol.NullableAnnotation == NullableAnnotation.Annotated
            && symbol is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var inner = MapTypeCore(symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated), context);
            return new TsType.Nullable(inner);
        }

        // Type parameter (e.g. T in PagedResult<T>) → emit as-is
        if (symbol is ITypeParameterSymbol typeParam)
        {
            return new TsType.TypeParam(typeParam.Name);
        }

        // Array T[]
        if (symbol is IArrayTypeSymbol arrayType)
        {
            // byte[] (FABLE_GAPS spec/wire divergence): System.Text.Json serializes
            // byte[] as a base64 STRING on the wire, never as an integer array — the
            // spec must match the wire. Lowered as a string primitive with format
            // "base64" (emitted as contentEncoding: base64, the OpenAPI 3.1 idiom);
            // CSharpType pins the exact type for import round-trips. File-endpoint
            // byte[] outputs never reach here — ContractWalker intercepts them.
            if (arrayType.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return new TsType.Primitive("string", "base64", "byte[]");
            }

            return new TsType.Array(MapTypeCore(arrayType.ElementType, context));
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
            // CSharpType on the inner Primitive("unknown") preserves the original type for round-trips
            if (SymbolEqualityComparer.Default.Equals(namedType, _jsonObjectType))
            {
                return new TsType.Dictionary(new TsType.Primitive("unknown", CSharpType: "JsonObject"));
            }
            if (SymbolEqualityComparer.Default.Equals(namedType, _jsonArrayType))
            {
                return new TsType.Array(new TsType.Primitive("unknown", CSharpType: "JsonArray"));
            }

            // Collections: List<T>, IEnumerable<T>, IReadOnlyList<T>, IList<T>, ICollection<T>, IReadOnlyCollection<T>
            if (IsCollectionType(namedType) && namedType.TypeArguments.Length == 1)
            {
                return new TsType.Array(MapTypeCore(namedType.TypeArguments[0], context));
            }

            // Dictionary<K, V>
            if (IsDictionaryType(namedType) && namedType.TypeArguments.Length == 2)
            {
                // FABLE_GAPS §7 item 12: non-string keys carry their contract
                // representation on the Dictionary node (emitted as propertyNames):
                // enums (registering the previously-vanishing key-enum schema),
                // string-backed brands, and primitives System.Text.Json serializes
                // as string keys. Genuinely unsupported keys still degrade to
                // unconstrained strings — loudly, never silently.
                var keySymbol = namedType.TypeArguments[0];
                var key = MapDictionaryKey(keySymbol, context, out var keySupported);
                if (!keySupported)
                {
                    Diagnostics.Warn(
                        Diagnostics.DictionaryKeyTypeDropped,
                        $"dictionary key type '{keySymbol.ToDisplayString()}'{AtContext(context)} has no contract representation — " +
                        "keys are emitted as unconstrained strings");
                }

                return new TsType.Dictionary(MapTypeCore(namedType.TypeArguments[1], context), key);
            }

            // Enum → named string union type
            if (namedType.TypeKind == TypeKind.Enum)
            {
                // A5: full-namespace keyed naming — colliding enum names disambiguate
                // loudly instead of first-wins TryAdd
                var enumName = GetEmittedName(namedType);
                if (!_enums.ContainsKey(enumName))
                {
                    var members = namedType.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => f.HasConstantValue)
                        .Select(f =>
                        {
                            // Check for [JsonStringEnumMemberName("original")] attribute
                            var attr = f.GetAttributes().FirstOrDefault(a =>
                                a.AttributeClass?.Name is "JsonStringEnumMemberNameAttribute");
                            if (attr?.ConstructorArguments.Length > 0
                                && attr.ConstructorArguments[0].Value is string original)
                            {
                                return original;
                            }
                            return Naming.ToCamelCase(f.Name);
                        })
                        .ToList();

                    _enums[enumName] = new TsType.StringUnion(members);
                    _typeNamespaces.TryAdd(enumName, GetNamespaceGroup(namedType));
                }

                return new TsType.TypeRef(enumName);
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
                    // A5: brands used to be keyed by simple name with first-wins TryAdd
                    var brandName = GetEmittedName(namedType);
                    var brand = new TsType.Brand(brandName, MapTypeCore(voInner, context));
                    _brands.TryAdd(brandName, brand);
                    _typeNamespaces.TryAdd(brandName, GetNamespaceGroup(namedType));
                    return brand;
                }

                WalkType(namedType);
                var emittedName = GetEmittedName(namedType);

                // Closed generic (e.g. PagedResult<MessageDto>) → Generic node
                if (namedType.IsGenericType && !namedType.IsUnboundGenericType)
                {
                    var tsArgs = namedType.TypeArguments.Select(a => MapTypeCore(a, context)).ToList();
                    return new TsType.Generic(emittedName, tsArgs);
                }

                return new TsType.TypeRef(emittedName);
            }
        }

        // ValueTuple → inline object { key: string; value: number }
        if (symbol is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            var fields = tupleType.TupleElements
                .Select(e => (Naming.ToCamelCase(e.Name), MapTypeCore(e.Type, context)))
                .ToList();
            return new TsType.InlineObject(fields);
        }

        // Diagnosed-unsupported scalars (FABLE_GAPS §7 item 12): TimeSpan, BigInteger,
        // char and object used to fall through to the empty {} fallback schema with no
        // diagnostic naming the cause (the emitter's catch-all blamed JsonElement).
        // Diagnose, don't change the wire: the fallback schema is emitted as before.
        var unsupportedId = symbol.SpecialType switch
        {
            SpecialType.System_Char => Diagnostics.UnsupportedChar,
            SpecialType.System_Object => Diagnostics.UnsupportedObject,
            _ when SymbolEqualityComparer.Default.Equals(symbol, _timeSpanType) => Diagnostics.UnsupportedTimeSpan,
            _ when SymbolEqualityComparer.Default.Equals(symbol, _bigIntegerType) => Diagnostics.UnsupportedBigInteger,
            _ => null,
        };

        if (unsupportedId is not null)
        {
            Diagnostics.Warn(
                unsupportedId,
                $"unsupported type '{symbol.ToDisplayString()}'{AtContext(context)} has no schema mapping — emitting an untyped (empty) schema");
        }

        // Fallback
        return new TsType.Primitive("unknown");
    }

    private static string AtContext(string? context)
        => context is null ? "" : $" on '{context}'";

    /// <summary>
    /// FABLE_GAPS §7 item 12 (P2 wave 3): maps a dictionary key type to its contract
    /// representation, or null for plain string keys (the propertyNames-less default).
    /// Supported: string, enums (mapping registers the key enum's schema — the
    /// "vanishing key-enum" fix), string-backed value-object brands, and primitives
    /// System.Text.Json serializes as string dictionary keys (Guid, dates/times, Uri,
    /// numerics). Numeric keys become string-typed primitives keeping the numeric
    /// format, with CSharpType pinning the exact key type for import round-trips.
    /// Anything else sets <paramref name="supported"/> false — the caller diagnoses
    /// (RIV1013) and falls back to unconstrained string keys.
    /// </summary>
    private TsType? MapDictionaryKey(ITypeSymbol keySymbol, string? context, out bool supported)
    {
        supported = true;

        if (keySymbol.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (keySymbol.TypeKind == TypeKind.Enum)
        {
            return MapTypeCore(keySymbol, context);
        }

        if (keySymbol is INamedTypeSymbol named)
        {
            // String-backed value-object brand → $ref to the brand schema.
            // Shape-checked BEFORE mapping so an unsupported (non-string) brand key
            // never registers a brand schema as a side effect of the probe.
            if (named.TypeKind is TypeKind.Class or TypeKind.Struct
                && !named.IsGenericType
                && _walkableAssemblies.Contains(named.ContainingAssembly)
                && TryGetValueObjectInner(named) is { SpecialType: SpecialType.System_String })
            {
                return MapTypeCore(named, context);
            }

            if (MapPrimitive(named) is { } primitive)
            {
                // Guid/DateTime/DateTimeOffset/DateOnly/TimeOnly/Uri — already
                // string-typed with the right format (and CSharpType where needed)
                if (primitive.Name == "string")
                {
                    return primitive;
                }

                // Numeric keys are written as strings on the wire — keep the numeric
                // format but flip the type, and always record the exact C# key type
                // (string + int32 alone would not survive an import round-trip)
                if (primitive.Name == "number")
                {
                    return new TsType.Primitive("string", primitive.Format, primitive.CSharpType ?? primitive.Format switch
                    {
                        "int32" => "int",
                        "int64" => "long",
                        "float" => "float",
                        "double" => "double",
                        "decimal" => "decimal",
                        _ => null,
                    });
                }
            }
        }

        supported = false;
        return null;
    }

    /// <summary>
    /// True when the symbol is a supported collection (List/IList/ICollection/
    /// IEnumerable/IReadOnlyList/IReadOnlyCollection, or an array) whose element is
    /// the given type. Used by walkers to detect collection-of-IFormFile multipart
    /// parts (FABLE_GAPS §7 item 12).
    /// </summary>
    public bool IsCollectionOf(ITypeSymbol symbol, INamedTypeSymbol? element)
    {
        if (element is null)
        {
            return false;
        }

        return symbol switch
        {
            IArrayTypeSymbol array => SymbolEqualityComparer.Default.Equals(array.ElementType, element),
            INamedTypeSymbol { TypeArguments.Length: 1 } named when IsCollectionType(named)
                => SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], element),
            _ => false,
        };
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
            SpecialType.System_UInt32 => new TsType.Primitive("number", "uint32", "uint"),
            SpecialType.System_Int64 => new TsType.Primitive("number", "int64"),
            SpecialType.System_UInt64 => new TsType.Primitive("number", "uint64", "ulong"),
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
            return result;

        // Non-SpecialType primitives — resolved via dictionary lookup instead of per-field null checks
        if (_scalarTypes.TryGetValue(symbol, out var scalar))
            return scalar;

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
        => _collectionTypes.Contains(symbol.OriginalDefinition);

    private bool IsDictionaryType(INamedTypeSymbol symbol)
        => _dictionaryTypes.Contains(symbol.OriginalDefinition);

    private static TsPropertyConstraints? ReadConstraints(AttributeData attr)
    {
        int? GetInt(string name) => attr.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is int v && v >= 0 ? v : null;
        double? GetDouble(string name) => attr.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is double v && !double.IsNaN(v) ? v : null;
        bool? GetBool(string name) => attr.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is true ? true : null;

        var c = new TsPropertyConstraints(
            ExclusiveMinimum: GetDouble("ExclusiveMinimum"),
            ExclusiveMaximum: GetDouble("ExclusiveMaximum"),
            MultipleOf: GetDouble("MultipleOf"),
            MinItems: GetInt("MinItems"),
            MaxItems: GetInt("MaxItems"),
            UniqueItems: GetBool("UniqueItems"));

        return c.HasAny ? c : null;
    }

    private static TsPropertyConstraints? ReadDataAnnotationConstraints(
        ImmutableArray<AttributeData> attributes)
    {
        int? minLength = null;
        int? maxLength = null;
        string? pattern = null;
        double? minimum = null;
        double? maximum = null;

        foreach (var attr in attributes)
        {
            var name = attr.AttributeClass?.Name;
            switch (name)
            {
                case "MinLengthAttribute" when attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is int ml:
                    minLength = ml;
                    break;

                case "MaxLengthAttribute" when attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is int mxl:
                    maxLength = mxl;
                    break;

                case "StringLengthAttribute" when attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is int slMax:
                    maxLength = slMax;
                    var minLenArg = attr.NamedArguments
                        .FirstOrDefault(a => a.Key == "MinimumLength");
                    if (minLenArg.Value.Value is int slMin)
                        minLength = slMin;
                    break;

                case "RangeAttribute" when attr.ConstructorArguments.Length >= 2:
                    // A9: the (Type, string, string) overload puts an ITypeSymbol in arg 0 —
                    // the old Convert.ToDouble crashed the tool with InvalidCastException
                    var args = attr.ConstructorArguments;
                    var (minArg, maxArg) = args.Length >= 3 && args[0].Value is ITypeSymbol
                        ? (args[1].Value, args[2].Value)
                        : (args[0].Value, args[1].Value);

                    if (!TryConvertRangeBound(minArg, out var rangeMin)
                        || !TryConvertRangeBound(maxArg, out var rangeMax))
                    {
                        Diagnostics.Warn(
                            Diagnostics.UnparseableRangeBound,
                            $"unparseable [Range] bound ('{minArg}', '{maxArg}') — skipping the range constraint");
                        break;
                    }

                    // Filter sentinel values emitted by CSharpWriter for single-sided constraints
                    if (rangeMin is not double.MinValue)
                        minimum = rangeMin;
                    if (rangeMax is not double.MaxValue)
                        maximum = rangeMax;
                    break;

                case "RegularExpressionAttribute" when attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string pat:
                    pattern = pat;
                    break;
            }
        }

        var c = new TsPropertyConstraints(
            MinLength: minLength,
            MaxLength: maxLength,
            Pattern: pattern,
            Minimum: minimum,
            Maximum: maximum);

        return c.HasAny ? c : null;
    }

    /// <summary>
    /// A9: converts a [Range] constructor argument to double. Strings parse with
    /// InvariantCulture (the old Convert.ToDouble misparsed under comma-decimal locales).
    /// </summary>
    private static bool TryConvertRangeBound(object? value, out double result)
    {
        switch (value)
        {
            case string text:
                return double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out result);
            case int or long or short or byte or sbyte or uint or ulong or ushort or float or double or decimal:
                result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string? ReadDataAnnotationFormat(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            switch (attr.AttributeClass?.Name)
            {
                case "EmailAddressAttribute": return "email";
                case "UrlAttribute": return "uri";
            }
        }

        return null;
    }

    public static bool IsOptionalProperty(IPropertySymbol prop)
    {
        var attributes = prop.GetAttributes();

        if (attributes.Any(a => a.AttributeClass?.Name is "RivetOptionalAttribute"))
            return true;

        if (attributes.Any(a => a.AttributeClass?.Name is "RequiredAttribute"))
            return false;

        // Nullable reference/value types are optional unless [Required]
        if (prop.Type.NullableAnnotation == NullableAnnotation.Annotated)
            return true;

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
