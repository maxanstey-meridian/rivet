using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The HTTP surface a wire schema describes: request (what the host accepts) or
/// response (what the host sends). Property accessibility/ignore semantics differ
/// between them (planner-constraint:json-surface-position-aware).
/// </summary>
public enum JsonSurfaceDirection
{
    Request,
    Response,
}

/// <summary>
/// The serializer-supported wire surface of a property, per direction.
/// Both = present in request and response schemas; RequestOnly = deserializable but
/// never serialized; ResponseOnly = serialized but not deserializable; Excluded =
/// absent from both (reported loudly when the shape was otherwise visible).
/// </summary>
public enum JsonPropertySurface
{
    Both,
    RequestOnly,
    ResponseOnly,
    Excluded,
}

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
    private readonly HashSet<string> _visiting = new();

    // A5: emitted-name registry keyed by fully-qualified name (namespace + arity).
    // Distinct types whose simple names collide get deterministic numeric suffixes
    // (discovery order), mirroring the component-name registry in OpenApiEmitter.
    private readonly Dictionary<string, string> _emittedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generatedSchemaNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TsScalarMetadata> _generatedEnumMetadata = new(
        StringComparer.Ordinal
    );

    // Scalar C# types that map directly to TsType.Primitive (Guid → string/uuid, etc.)
    private readonly ImmutableDictionary<INamedTypeSymbol, TsType.Primitive> _scalarTypes;

    // JSON container types that map to non-Primitive TsTypes (special-cased in MapTypeCore)
    private readonly INamedTypeSymbol? _jsonObjectType;
    private readonly INamedTypeSymbol? _jsonArrayType;
    private readonly INamedTypeSymbol? _timeSpanType;
    private readonly INamedTypeSymbol? _bigIntegerType;

    private readonly INamedTypeSymbol? _listType;
    private readonly ImmutableHashSet<INamedTypeSymbol> _dictionaryTypes;

    private readonly WellKnownTypes _types;

    private TypeWalker(Compilation compilation, WellKnownTypes types)
    {
        // Build set of walkable assemblies: source + project references (not NuGet/framework)
        _walkableAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default)
        {
            compilation.Assembly,
        };
        foreach (var reference in compilation.References)
        {
            if (
                reference is CompilationReference
                && compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm
            )
            {
                _walkableAssemblies.Add(asm);
            }
        }

        // Scalar type → TsType.Primitive lookup
        var scalars = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, TsType.Primitive>(
            SymbolEqualityComparer.Default
        );
        AddScalar(scalars, compilation, "System.Guid", new TsType.Primitive("string", "uuid"));
        AddScalar(
            scalars,
            compilation,
            "System.DateTime",
            new TsType.Primitive("string", "date-time")
        );
        AddScalar(
            scalars,
            compilation,
            "System.DateTimeOffset",
            new TsType.Primitive("string", "date-time", "DateTimeOffset")
        );
        AddScalar(scalars, compilation, "System.DateOnly", new TsType.Primitive("string", "date"));
        AddScalar(scalars, compilation, "System.TimeOnly", new TsType.Primitive("string", "time"));
        AddScalar(scalars, compilation, "System.Uri", new TsType.Primitive("string", "uri"));
        AddScalar(
            scalars,
            compilation,
            "System.Text.Json.JsonElement",
            new TsType.Primitive("unknown", CSharpType: "JsonElement")
        );
        AddScalar(
            scalars,
            compilation,
            "System.Text.Json.Nodes.JsonNode",
            new TsType.Primitive("unknown", CSharpType: "JsonNode")
        );
        AddScalar(
            scalars,
            compilation,
            "Microsoft.AspNetCore.Http.IFormFile",
            new TsType.Primitive("File")
        );
        _scalarTypes = scalars.ToImmutable();

        _jsonObjectType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonObject");
        _jsonArrayType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonArray");

        // Diagnosed-unsupported scalars (FABLE_GAPS §7 item 12) — resolved up front
        // so the fallback path can name them instead of failing silently.
        _timeSpanType = compilation.GetTypeByMetadataName("System.TimeSpan");
        _bigIntegerType = compilation.GetTypeByMetadataName("System.Numerics.BigInteger");

        _listType = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");

        _dictionaryTypes = ResolveTypeSet(
            compilation,
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.IReadOnlyDictionary`2"
        );

        _types = types;
        LoadGeneratedSchemas(compilation.Assembly);
    }

    private void LoadGeneratedSchemas(IAssemblySymbol assembly)
    {
        foreach (
            var attribute in assembly
                .GetAttributes()
                .Where(attribute => attribute.Is(_types.RivetGeneratedSchema))
        )
        {
            if (
                attribute.ConstructorArguments.Length < 6
                || attribute.ConstructorArguments[0].Value is not string name
                || attribute.ConstructorArguments[1].Value is not string componentId
                || attribute.ConstructorArguments[4].Value is not bool nullable
                || attribute.ConstructorArguments[5].Value is not string metadataJson
            )
            {
                throw new RivetUserException(
                    "Invalid generated schema metadata in RivetGeneratedSchemaAttribute."
                );
            }

            var schemaType = attribute.ConstructorArguments[2].Value as string;
            var format = attribute.ConstructorArguments[3].Value as string;
            var metadata =
                JsonSerializer.Deserialize<TsScalarMetadata>(
                    metadataJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new TsScalarMetadata();
            var isEnum =
                attribute.ConstructorArguments.Length > 6
                && attribute.ConstructorArguments[6].Value is true;
            var schemaRef =
                attribute.ConstructorArguments.Length > 7
                    ? attribute.ConstructorArguments[7].Value as string
                    : null;
            var isArray =
                attribute.ConstructorArguments.Length > 8
                && attribute.ConstructorArguments[8].Value is true;
            var itemSchemaRef =
                attribute.ConstructorArguments.Length > 9
                    ? attribute.ConstructorArguments[9].Value as string
                    : null;
            if (!_generatedSchemaNames.Add(name))
            {
                throw new RivetUserException(
                    $"Generated schema name '{name}' collides with another generated type."
                );
            }

            if (isEnum)
            {
                _generatedEnumMetadata[name] = metadata;
                continue;
            }

            TsType leaf = isArray
                ? itemSchemaRef is null
                    ? throw new RivetUserException(
                        $"Generated array schema '{name}' has no item schema reference."
                    )
                    : new TsType.Array(new TsType.TypeRef(itemSchemaRef))
                : schemaRef is null
                    ? schemaType is null
                        ? new TsType.Primitive("unknown", CSharpType: "object")
                        : new TsType.Primitive(schemaType, format)
                    : new TsType.TypeRef(schemaRef);
            if (nullable && (isArray || schemaRef is null && schemaType is not null))
            {
                leaf = new TsType.Nullable(leaf);
            }

            if (_definitions.ContainsKey(name))
            {
                throw new RivetUserException(
                    $"Generated schema name '{name}' collides with another generated type."
                );
            }

            _definitions[name] = new TsTypeDefinition(
                name,
                [],
                leaf,
                metadata.Description,
                new TsTypeMetadata(componentId, TsTypeProvenance.Component),
                metadata
            );
        }
    }

    private static void AddScalar(
        ImmutableDictionary<INamedTypeSymbol, TsType.Primitive>.Builder builder,
        Compilation compilation,
        string metadataName,
        TsType.Primitive mapped
    )
    {
        var symbol = compilation.GetTypeByMetadataName(metadataName);
        if (symbol is not null)
        {
            builder.Add(symbol, mapped);
        }
    }

    private static ImmutableHashSet<INamedTypeSymbol> ResolveTypeSet(
        Compilation compilation,
        params string[] metadataNames
    )
    {
        var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(
            SymbolEqualityComparer.Default
        );
        foreach (var name in metadataNames)
        {
            var symbol = compilation.GetTypeByMetadataName(name);
            if (symbol is not null)
            {
                builder.Add(symbol);
            }
        }
        return builder.ToImmutable();
    }

    public IReadOnlyDictionary<string, TsTypeDefinition> Definitions => _definitions;
    public IReadOnlyDictionary<string, TsType.Brand> Brands => _brands;
    public IReadOnlyDictionary<string, TsType> Enums => _enums;

    /// <summary>
    /// Creates a walker and walks the provided [RivetType]-attributed types.
    /// Use SymbolDiscovery.Discover() to obtain the type list.
    /// </summary>
    public static TypeWalker Create(
        Compilation compilation,
        WellKnownTypes types,
        IReadOnlyList<INamedTypeSymbol> attributedTypes
    )
    {
        var walker = new TypeWalker(compilation, types);

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
    public TsType MapType(ITypeSymbol symbol, string? context = null) =>
        MapTypeCore(symbol, context);

    public TsType ApplyGeneratedSchemaRef(TsType type, string? schemaRef, string context)
    {
        if (schemaRef is null)
        {
            return type;
        }

        if (!_definitions.ContainsKey(schemaRef) && !_enums.ContainsKey(schemaRef))
        {
            throw new RivetUserException(
                $"{context} references unknown generated schema '{schemaRef}'."
            );
        }

        return new TsType.TypeRef(schemaRef);
    }

    /// <summary>
    /// Returns true when the property is never a named JSON member: [JsonExtensionData]
    /// always, and [JsonIgnore] Always (or the parameterless form). The condition forms
    /// Never/WhenWritingNull/WhenWritingDefault do not remove the property from either
    /// schema — WhenWritingNull/WhenWritingDefault only allow omission on the response
    /// wire, handled by requiredness (CanOmitOnWire). Direction does not change this
    /// answer; presence asymmetry lives in GetJsonPropertySurface. Fields are only
    /// surfaced through [JsonInclude] (GetJsonFieldSurface), so they are never ignored here.
    /// </summary>
    public bool IsJsonIgnored(ISymbol member)
    {
        if (member is not IPropertySymbol prop)
        {
            return false;
        }

        foreach (var attribute in prop.GetAttributes())
        {
            if (attribute.Is(_types.JsonExtensionData))
            {
                return true;
            }

            if (attribute.Is(_types.JsonIgnore))
            {
                var condition = ReadJsonIgnoreCondition(attribute);
                return condition switch
                {
                    JsonIgnoreCondition.WhenWritingNull => false,
                    JsonIgnoreCondition.WhenWritingDefault => false,
                    JsonIgnoreCondition.Never => false,
                    // No condition or Always: excluded from both surfaces.
                    _ => true,
                };
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the [JsonIgnore(Condition = …)] named argument. Null means the
    /// attribute's parameterless form, i.e. Always.
    /// </summary>
    private static JsonIgnoreCondition? ReadJsonIgnoreCondition(AttributeData attribute) =>
        attribute.NamedArguments.FirstOrDefault(kv => kv.Key == "Condition").Value.Value is int raw
            ? (JsonIgnoreCondition)raw
            : null;

    /// <summary>
    /// Whether the property is serialized/deserialized under default System.Text.Json
    /// web options for the queried direction, per observed serializer behavior:
    /// public get+set properties surface in both directions; get-only properties
    /// serialize on the response and deserialize only when bound by a matching
    /// constructor parameter (records — planner-constraint:stj-constructor-truth);
    /// set-only properties deserialize only (request); non-public members are absent
    /// unless [JsonInclude] (fields likewise). Unsupported shapes are reported and
    /// excluded rather than guessed.
    /// </summary>
    public JsonPropertySurface GetJsonPropertySurface(IPropertySymbol prop)
    {
        var hasInclude = prop.HasAttribute(_types.JsonInclude);

        // Non-public members surface only through [JsonInclude].
        var getterPublic = prop.GetMethod?.DeclaredAccessibility is Accessibility.Public;
        var setterPublic = prop.SetMethod?.DeclaredAccessibility is Accessibility.Public;

        if (prop.Type.Kind == SymbolKind.DynamicType)
        {
            // Dynamic members keep parity with the established object handling:
            // the untyped-schema representation, never silent disappearance
            // (planner-constraint:object-dynamic-parity).
            return JsonPropertySurface.Both;
        }

        if (!getterPublic && !setterPublic)
        {
            // Fully non-public (both accessors non-public): [JsonInclude] pulls the
            // member into the wire surface in both directions (STJ touches the member
            // directly). Without the include it is invisible to the serializer.
            return hasInclude ? JsonPropertySurface.Both : JsonPropertySurface.Excluded;
        }

        if (prop.GetMethod is null)
        {
            // Set-only property: STJ cannot serialize it — request surface only.
            return setterPublic || hasInclude
                ? JsonPropertySurface.RequestOnly
                : JsonPropertySurface.Excluded;
        }

        if (prop.SetMethod is null)
        {
            // Get-only property. STJ deserializes it only via a matching constructor
            // parameter (records) or a public parameterized constructor — response
            // surface otherwise (planner-constraint:stj-constructor-truth).
            if (getterPublic || hasInclude)
            {
                return IsDeserializableViaConstructor(prop)
                    ? JsonPropertySurface.Both
                    : JsonPropertySurface.ResponseOnly;
            }

            return JsonPropertySurface.Excluded;
        }

        if (getterPublic && setterPublic)
        {
            return JsonPropertySurface.Both;
        }

        // Mixed accessibility (e.g. public get + private set): STJ uses the public
        // accessor for its direction. [JsonInclude] on the property pulls the
        // non-public side in too — STJ serializes via the public accessor and
        // deserializes via the included non-public accessor, so the property
        // surfaces in both directions (planner-constraint:mixed-accessor-include-truth).
        if (hasInclude)
        {
            return JsonPropertySurface.Both;
        }

        return getterPublic ? JsonPropertySurface.ResponseOnly : JsonPropertySurface.RequestOnly;
    }

    /// <summary>
    /// The wire surface of a FIELD member under default System.Text.Json web options.
    /// Fields have no accessors: STJ touches the member directly, so [JsonInclude]
    /// means both-surface (serialized and deserialized) regardless of accessibility;
    /// without the include a field is invisible to the serializer
    /// (planner-constraint:jsoninclude-fields-represented).
    /// </summary>
    public JsonPropertySurface GetJsonFieldSurface(IFieldSymbol field)
    {
        if (field.Type.Kind == SymbolKind.DynamicType)
        {
            // Dynamic members keep parity with the established object handling:
            // untyped-schema representation, never silent disappearance
            // (planner-constraint:object-dynamic-parity).
            return JsonPropertySurface.Both;
        }

        return field.HasAttribute(_types.JsonInclude)
            ? JsonPropertySurface.Both
            : JsonPropertySurface.Excluded;
    }

    /// <summary>
    /// True when System.Text.Json can deserialize into the property through a
    /// constructor parameter with a matching name (case-insensitive), i.e. a record
    /// positional parameter or a [JsonConstructor]-attributed constructor.
    /// </summary>
    private bool IsDeserializableViaConstructor(IPropertySymbol prop)
    {
        var named = prop.ContainingType;

        // Records: primary constructor parameters bind properties by name.
        var constructors = named
            .Constructors.Where(constructor => !constructor.IsImplicitlyDeclared)
            .ToList();

        foreach (var constructor in constructors)
        {
            var hasJsonConstructor = constructor.HasAttribute(_types.JsonConstructor);

            foreach (var parameter in constructor.Parameters)
            {
                if (
                    string.Equals(parameter.Name, prop.Name, StringComparison.OrdinalIgnoreCase)
                    && (hasJsonConstructor || IsRecordPrimaryConstructor(named, constructor))
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRecordPrimaryConstructor(INamedTypeSymbol type, IMethodSymbol constructor)
    {
        // A record's primary constructor shares the record's parameter list: its
        // parameters generate the properties. Any other constructor (copies included)
        // is not the primary one. Roslyn marks the primary constructor as the one
        // whose declaring syntax is the record declaration's parameter list.
        foreach (var syntaxReference in constructor.DeclaringSyntaxReferences)
        {
            var node = syntaxReference.GetSyntax().Parent;
            if (node is ParameterListSyntax parameterList && parameterList.Parent is not null)
            {
                foreach (var typeSyntaxReference in type.DeclaringSyntaxReferences)
                {
                    if (typeSyntaxReference.GetSyntax() == parameterList.Parent)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the serializer may leave the member off the wire: [JsonIgnore]
    /// WhenWritingNull/WhenWritingDefault omission, or an optional member. Such a
    /// member cannot be required on the emitted schema (acceptance:json-property-surface).
    /// </summary>
    private bool CanOmitOnWire(ISymbol member) =>
        (
            member is IPropertySymbol prop
            && prop.GetAttribute(_types.JsonIgnore) is { } ignore
            && ReadJsonIgnoreCondition(ignore)
                is JsonIgnoreCondition.WhenWritingNull
                    or JsonIgnoreCondition.WhenWritingDefault
        ) || IsOptional(member);

    /// <summary>
    /// A3: flattens the wire-member surface of a type across its BaseType chain
    /// (base-most first; derived declarations win on name collision — overrides and
    /// shadowing both resolve to the most-derived declaration). Stops at object/ValueType
    /// and at base types outside the walkable assemblies. Skips static/indexer/implicitly
    /// declared members and records' synthesized EqualityContract. Properties are the
    /// primary surface; [JsonInclude] fields join them (properties win on a name
    /// collision, since an auto-property already represents its backing field).
    /// </summary>
    public IReadOnlyList<ISymbol> GetEffectiveProperties(ITypeSymbol type)
    {
        var chain = new List<ITypeSymbol>();
        var current = type;
        while (
            current is not null
            && current.SpecialType is not SpecialType.System_Object
            && current.SpecialType is not SpecialType.System_ValueType
        )
        {
            chain.Add(current);

            var baseType = (current as INamedTypeSymbol)?.BaseType;
            current =
                baseType is not null
                && baseType.ContainingAssembly is not null
                && _walkableAssemblies.Contains(baseType.ContainingAssembly)
                    ? baseType
                    : null;
        }

        chain.Reverse(); // base-most first, matching rivet-ts's X5 flatten semantics

        var ordered = new List<ISymbol>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var link in chain)
        {
            foreach (var member in link.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                {
                    continue;
                }

                if (member is IPropertySymbol property)
                {
                    if (property.IsStatic || property.IsIndexer)
                    {
                        continue;
                    }
                }
                else if (member is IFieldSymbol field)
                {
                    // [JsonInclude] public fields are statically visible wire surface
                    // (serialized and deserialized under default web options);
                    // non-included fields stay absent (planner-constraint:jsoninclude-fields-represented).
                    // Auto-property backing fields are compiler detail — the property
                    // represents them; properties win on a name collision.
                    if (field.IsStatic || field.IsConst || field.AssociatedSymbol is not null)
                    {
                        continue;
                    }

                    if (GetJsonFieldSurface(field) == JsonPropertySurface.Excluded)
                    {
                        continue;
                    }
                }
                else
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
    /// The request-surface properties of an input type: not JSON-ignored, not
    /// response-only and not header-bound.
    /// </summary>
    public IEnumerable<IPropertySymbol> GetRequestProperties(ITypeSymbol type) =>
        GetEffectiveProperties(type)
            .OfType<IPropertySymbol>()
            .Where(property =>
                !IsJsonIgnored(property)
                && GetJsonPropertySurface(property) != JsonPropertySurface.ResponseOnly
                && GetHeaderName(property) is null
            );

    /// <summary>
    /// The [JsonPropertyName] of a property or included field, or null.
    /// </summary>
    public string? GetJsonMemberName(ISymbol member) =>
        member.GetAttribute(_types.JsonPropertyName).StringArgument();

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
        var typeParams = definition.TypeParameters.Select(tp => tp.Name).ToList();

        // P2 wave 4: a [JsonPolymorphic]/[JsonDerivedType] base type registers as a
        // TaggedUnion alias definition (oneOf + discriminator + mapping) instead of
        // silently flattening to its own property surface. Diagnosed-unsupported
        // shapes (non-string tags, zero registrations) fall through to flattening.
        // Generic polymorphic bases keep the flattening path — a generic alias has
        // no monomorphisation template the emitter could instantiate.
        if (!definition.IsGenericType && TryBuildPolymorphicUnion(definition, name) is { } union)
        {
            _visiting.Remove(name);
            _definitions[name] = new TsTypeDefinition(
                name,
                typeParams,
                union,
                GetTypeDescription(definition),
                GetTypeMetadata(definition)
            );
            return;
        }

        // [RivetUnion] wrapper: the wire value is the BARE variant, never the
        // wrapper object — lower to an undiscriminated union of the property
        // types (oneOf on emission), mirroring the runtime converter.
        if (!definition.IsGenericType && TryBuildRivetUnion(definition, name) is { } plainUnion)
        {
            _visiting.Remove(name);
            _definitions[name] = new TsTypeDefinition(
                name,
                typeParams,
                plainUnion,
                GetTypeDescription(definition),
                GetTypeMetadata(definition)
            );
            return;
        }

        var properties = new List<TsPropertyDefinition>();

        // A3: include inherited members by flattening the BaseType chain. The shared
        // per-type component schema represents accessibility-derived asymmetry via
        // readOnly/writeOnly (planner-constraint:component-schema-directionality);
        // explicit [RivetReadOnly]/[RivetWriteOnly] take precedence over the derived
        // marker below.
        foreach (var member in GetEffectiveProperties(definition))
        {
            var memberSurface = member switch
            {
                IPropertySymbol propSymbol => GetJsonPropertySurface(propSymbol),
                IFieldSymbol fieldSymbol => GetJsonFieldSurface(fieldSymbol),
                _ => JsonPropertySurface.Excluded,
            };
            if (memberSurface == JsonPropertySurface.Excluded)
            {
                continue;
            }

            if (IsJsonIgnored(member))
            {
                continue;
            }

            // P2 wave 5: [RivetHeader] properties are request header params, never part
            // of a JSON schema — ContractWalker/EndpointWalker surface them as
            // ParamSource.Header params instead.
            if (member is IPropertySymbol headerCheck && GetHeaderName(headerCheck) is not null)
            {
                continue;
            }

            // [JsonPropertyName("x")] → use "x" instead of camelCase(Name)
            var jsonPropertyName = GetJsonMemberName(member);

            var tsName = jsonPropertyName ?? Naming.ToCamelCase(member.Name);
            var tsType = MapTypeCore(GetMemberType(member), $"{name}.{member.Name}");
            var isOptional = CanOmitOnWire(member);
            var isDeprecated = member.HasAttribute(_types.Obsolete);

            // Read metadata attributes
            string? format = null;
            var isFormatSpecified = false;
            string? schemaType = null;
            string? schemaRef = null;
            string? defaultValue = null;
            string? description = null;
            string? example = null;
            var isReadOnly = false;
            var isWriteOnly = false;
            var constraints = ReadConstraints(
                member.GetAttributes(),
                tsType is TsType.Array or TsType.Nullable { Inner: TsType.Array }
            );
            var daFormat = ReadDataAnnotationFormat(member.GetAttributes());
            foreach (var attr in member.GetAttributes())
            {
                if (attr.Is(_types.RivetFormat))
                {
                    isFormatSpecified = true;
                    format =
                        attr.ConstructorArguments.Length > 0
                        && attr.ConstructorArguments[0].Value is string fmt
                            ? fmt
                            : null;
                }
                else if (
                    attr.Is(_types.RivetSchemaType)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string primitiveType
                )
                {
                    schemaType = primitiveType;
                }
                else if (
                    attr.Is(_types.RivetSchemaRef)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string refName
                )
                {
                    schemaRef = refName;
                }
                else if (
                    attr.Is(_types.RivetDefault)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string def
                )
                {
                    defaultValue = def;
                }
                else if (
                    attr.Is(_types.RivetDescription)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string desc
                )
                {
                    description = desc;
                }
                else if (
                    attr.Is(_types.RivetExample)
                    && attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is string ex
                )
                {
                    example = ex;
                }
                else if (attr.Is(_types.RivetReadOnly))
                {
                    isReadOnly = true;
                }
                else if (attr.Is(_types.RivetWriteOnly))
                {
                    isWriteOnly = true;
                }
            }

            // Accessibility-derived asymmetry becomes readOnly/writeOnly on the shared
            // component schema (planner-constraint:component-schema-directionality);
            // explicit [RivetReadOnly]/[RivetWriteOnly] attributes take precedence
            // (their assignment above wins because the derived marker only fills false).
            if (!isReadOnly && memberSurface == JsonPropertySurface.ResponseOnly)
            {
                isReadOnly = true;
            }
            else if (!isWriteOnly && memberSurface == JsonPropertySurface.RequestOnly)
            {
                isWriteOnly = true;
            }

            // DA format is a fallback — explicit [RivetFormat] takes precedence.
            if (!isFormatSpecified)
            {
                format = daFormat;
            }

            // An explicit [RivetFormat] (even an empty one) replaces the inferred format;
            // a DataAnnotations format only fills a leaf that has none.
            tsType = tsType.WithLeaf(schemaType, isFormatSpecified ? format ?? "" : null);
            if (
                !isFormatSpecified
                && format is not null
                && tsType
                    is TsType.Primitive { Format: null }
                        or TsType.Nullable { Inner: TsType.Primitive { Format: null } }
            )
            {
                tsType = tsType.WithLeaf(null, format);
            }

            if (schemaRef is not null)
            {
                tsType = ApplyGeneratedSchemaRef(
                    tsType,
                    schemaRef,
                    $"Property '{name}.{member.Name}'"
                );
            }

            var generatedSchemaMetadata = ReadGeneratedSchemaMetadata(member);
            tsType = ApplyGeneratedSchemaMetadata(tsType, "", generatedSchemaMetadata);

            properties.Add(
                new TsPropertyDefinition(
                    tsName,
                    tsType,
                    isOptional,
                    isDeprecated,
                    format,
                    defaultValue,
                    constraints,
                    description,
                    example,
                    isReadOnly,
                    isWriteOnly,
                    generatedSchemaMetadata.GetValueOrDefault("")
                )
            );
        }

        // Read type-level [RivetDescription] attribute
        var typeDescription = GetTypeDescription(definition);

        _visiting.Remove(name);
        _definitions[name] = new TsTypeDefinition(
            name,
            typeParams,
            properties,
            typeDescription,
            GetTypeMetadata(definition),
            ReadGeneratedSchemaMetadata(definition).GetValueOrDefault("")
        );
    }

    private string? GetTypeDescription(INamedTypeSymbol definition) =>
        definition.GetAttribute(_types.RivetDescription).StringArgument();

    private TsTypeMetadata? GetTypeMetadata(INamedTypeSymbol definition)
    {
        var attribute = definition.GetAttribute(_types.RivetGeneratedType);
        if (attribute is null || attribute.ConstructorArguments.Length < 2)
        {
            return null;
        }

        var componentId = attribute.ConstructorArguments[0].Value as string;
        var provenance =
            Convert.ToInt32(attribute.ConstructorArguments[1].Value) == 0
                ? TsTypeProvenance.Component
                : TsTypeProvenance.Synthetic;
        return new TsTypeMetadata(componentId, provenance);
    }

    private Dictionary<string, TsScalarMetadata> ReadGeneratedSchemaMetadata(ISymbol symbol)
    {
        var result = new Dictionary<string, TsScalarMetadata>(StringComparer.Ordinal);
        foreach (
            var attribute in symbol
                .GetAttributes()
                .Where(attribute => attribute.Is(_types.RivetGeneratedSchemaMetadata))
        )
        {
            if (attribute.ConstructorArguments is not [{ Value: string pointer }, ..])
            {
                throw new RivetUserException(
                    "Invalid generated schema metadata in RivetGeneratedSchemaMetadataAttribute."
                );
            }

            int? IntAt(int index) =>
                attribute.ConstructorArguments[index].Value is int value && value >= 0
                    ? value
                    : null;
            double? DoubleAt(int index) =>
                attribute.ConstructorArguments[index].Value is double value && !double.IsNaN(value)
                    ? value
                    : null;
            string? StringAt(int index) => attribute.ConstructorArguments[index].Value as string;
            bool BoolAt(int index) => attribute.ConstructorArguments[index].Value is true;

            var constraints = new TsPropertyConstraints(
                MinLength: IntAt(6),
                MaxLength: IntAt(7),
                Pattern: StringAt(8),
                Minimum: DoubleAt(9),
                Maximum: DoubleAt(10),
                ExclusiveMinimum: DoubleAt(11),
                ExclusiveMaximum: DoubleAt(12),
                MultipleOf: DoubleAt(13),
                MinItems: IntAt(14),
                MaxItems: IntAt(15),
                UniqueItems: BoolAt(16) ? true : null
            );
            var xml =
                StringAt(17) is not null
                || StringAt(18) is not null
                || StringAt(19) is not null
                || BoolAt(20)
                || BoolAt(21)
                    ? new TsSchemaXmlMetadata(
                        StringAt(17),
                        StringAt(18),
                        StringAt(19),
                        BoolAt(20),
                        BoolAt(21)
                    )
                    : null;
            result[pointer] = new TsScalarMetadata(
                Description: StringAt(2),
                DefaultValue: StringAt(3),
                Example: StringAt(4),
                Examples: StringAt(5),
                Constraints: constraints.HasAny ? constraints : null,
                Title: StringAt(1),
                Xml: xml,
                Format: StringAt(22),
                IsFormatSpecified: BoolAt(23),
                IsNullable: BoolAt(24),
                IsDeprecated: BoolAt(25),
                IsReadOnly: BoolAt(26),
                IsWriteOnly: BoolAt(27),
                Required: StringAt(28) is { } requiredJson
                    ? JsonSerializer.Deserialize<List<string>>(requiredJson)
                    : null
            );
        }
        return result;
    }

    private static TsType ApplyGeneratedSchemaMetadata(
        TsType type,
        string pointer,
        IReadOnlyDictionary<string, TsScalarMetadata> metadata
    ) =>
        type switch
        {
            TsType.Nullable nullable => new TsType.Nullable(
                ApplyGeneratedSchemaMetadata(nullable.Inner, pointer, metadata)
            ),
            TsType.Array array => new TsType.Array(
                ApplyGeneratedSchemaMetadata(array.Element, pointer + "/items", metadata),
                metadata.GetValueOrDefault(pointer + "/items")
            ),
            TsType.Dictionary dictionary => new TsType.Dictionary(
                ApplyGeneratedSchemaMetadata(
                    dictionary.Value,
                    pointer + "/additionalProperties",
                    metadata
                ),
                dictionary.Key,
                metadata.GetValueOrDefault(pointer + "/additionalProperties")
            ),
            _ => type,
        };

    public TsType MapPropertyType(IPropertySymbol property) =>
        MapType(property.Type)
            .WithLeaf(property.GetAttribute(_types.RivetSchemaType).StringArgument(), null);

    /// <summary>
    /// Lowers a [RivetUnion] wrapper record to an undiscriminated union of its
    /// property types. Nullable wrappers are stripped — every variant property
    /// is optional by construction (only one is ever set), so nullability is a
    /// wrapper artifact, not part of the union's wire shape. Returns null when
    /// the attribute is absent; an attributed record with no usable properties
    /// is diagnosed and falls back to plain flattening.
    /// </summary>
    private TsType.Union? TryBuildRivetUnion(INamedTypeSymbol definition, string name)
    {
        if (!definition.HasAttribute(_types.RivetUnion))
        {
            return null;
        }

        var variants = new List<TsType>();
        foreach (var member in GetEffectiveProperties(definition))
        {
            var mapped = MapTypeCore(GetMemberType(member), $"{name}.{member.Name}");
            variants.Add(mapped is TsType.Nullable nullable ? nullable.Inner : mapped);
        }

        if (variants.Count == 0)
        {
            Diagnostics.Warn(
                Diagnostics.RivetUnionNoVariants,
                $"[RivetUnion] on '{name}' has no variant properties — falling back to plain property flattening"
            );
            return null;
        }

        return new TsType.Union(variants);
    }

    /// <summary>
    /// Lowers a [JsonPolymorphic]/[JsonDerivedType] base type to a
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
        AttributeData? polymorphicAttr = null;
        var derivedAttrs = new List<AttributeData>();
        foreach (var attr in definition.GetAttributes())
        {
            if (attr.Is(_types.JsonPolymorphic))
            {
                polymorphicAttr = attr;
            }
            else if (attr.Is(_types.JsonDerivedType))
            {
                derivedAttrs.Add(attr);
            }
        }

        if (polymorphicAttr is null && derivedAttrs.Count == 0)
        {
            return null;
        }

        if (
            polymorphicAttr is not null
            && polymorphicAttr.NamedArguments.Any(a => a.Key == "UnknownDerivedTypeHandling")
        )
        {
            Diagnostics.Warn(
                Diagnostics.PolymorphicUnknownHandlingDropped,
                $"[JsonPolymorphic] UnknownDerivedTypeHandling on '{name}' has no spec representation — "
                    + "the emitted oneOf admits only the registered derived types"
            );
        }

        if (derivedAttrs.Count == 0)
        {
            Diagnostics.Warn(
                Diagnostics.PolymorphicNoDerivedTypes,
                $"[JsonPolymorphic] on '{name}' has no [JsonDerivedType] registrations — "
                    + "there is no variant set to emit; falling back to plain property flattening"
            );
            return null;
        }

        var discriminator = "$type";
        if (
            polymorphicAttr
                ?.NamedArguments.FirstOrDefault(a => a.Key == "TypeDiscriminatorPropertyName")
                .Value.Value
            is string custom
        )
        {
            discriminator = custom;
        }

        var registrations = new List<(INamedTypeSymbol Type, string Tag)>();
        foreach (var attr in derivedAttrs)
        {
            if (
                attr.ConstructorArguments.Length == 0
                || attr.ConstructorArguments[0].Value is not INamedTypeSymbol derivedType
            )
            {
                continue;
            }

            var tagValue =
                attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value : null;
            if (tagValue is not string tag)
            {
                // Do NOT stringify int tags: a spec validating string tags against an
                // int wire value would be a lie. Flatten the whole base, loudly.
                Diagnostics.Warn(
                    Diagnostics.PolymorphicNonStringTag,
                    $"[JsonDerivedType] on '{name}' registers '{derivedType.ToDisplayString()}' with "
                        + $"{(tagValue is null ? "no" : "a non-string")} discriminator tag — a string-discriminated "
                        + $"oneOf cannot represent it; falling back to plain property flattening for '{name}'"
                );
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
            var fields = new List<TsType.InlineObjectField>
            {
                new(discriminator, new TsType.StringUnion([tag])),
            };

            foreach (var member in GetEffectiveProperties(derivedType))
            {
                if (IsJsonIgnored(member))
                {
                    continue;
                }

                // Variants are the shared wire shape for both directions
                // (planner-constraint:tagged-union-variant-surface): keep every
                // non-Excluded member and represent RequestOnly/ResponseOnly
                // asymmetry via the field surface marker (emitted as
                // writeOnly/readOnly), never Both-only filtering.
                var memberSurface = member switch
                {
                    IPropertySymbol propSymbol => GetJsonPropertySurface(propSymbol),
                    IFieldSymbol fieldSymbol => GetJsonFieldSurface(fieldSymbol),
                    _ => JsonPropertySurface.Excluded,
                };
                if (memberSurface == JsonPropertySurface.Excluded)
                {
                    continue;
                }

                var fieldName = GetJsonMemberName(member) ?? Naming.ToCamelCase(member.Name);
                var fieldType = MapTypeCore(GetMemberType(member), $"{name}.{tag}.{member.Name}");

                fields.Add(
                    new TsType.InlineObjectField(
                        fieldName,
                        fieldType,
                        CanOmitOnWire(member),
                        memberSurface switch
                        {
                            JsonPropertySurface.RequestOnly => TsType
                                .InlineObjectFieldSurface
                                .RequestOnly,
                            JsonPropertySurface.ResponseOnly => TsType
                                .InlineObjectFieldSurface
                                .ResponseOnly,
                            _ => TsType.InlineObjectFieldSurface.Both,
                        }
                    )
                );
            }

            variants.Add(
                new TsType.TaggedUnionVariant(
                    tag,
                    new TsType.InlineObject(fields),
                    GetTypeMetadata(derivedType)
                )
            );
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
            } while (!_claimedNames.Add(name));

            Diagnostics.Warn(
                Diagnostics.TypeNameCollision,
                $"type name collision — '{pure}' ({key}) collides with a previously walked type of the same name; "
                    + $"emitting it as '{name}'. Use distinct type names to keep schema names stable."
            );
        }

        _emittedNames[key] = name;
        return name;
    }

    private TsType MapTypeCore(ITypeSymbol symbol, string? context = null)
    {
        // Nullable value type: int? → Nullable<int>
        if (
            symbol is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } nullable
        )
        {
            var inner = MapTypeCore(nullable.TypeArguments[0], context);
            return new TsType.Nullable(inner);
        }

        // Nullable reference type annotation.
        // A12: must run before the type-parameter check so Wrapper<T>(T? Value)
        // lowers as Nullable(TypeParam), not bare TypeParam.
        if (
            symbol.NullableAnnotation == NullableAnnotation.Annotated
            && symbol
                is not INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                }
        )
        {
            var inner = MapTypeCore(
                symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                context
            );
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
                return new TsType.Dictionary(
                    new TsType.Primitive("unknown", CSharpType: "JsonObject")
                );
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
                        $"dictionary key type '{keySymbol.ToDisplayString()}'{AtContext(context)} has no contract representation — "
                            + "keys are emitted as unconstrained strings"
                    );
                }

                return new TsType.Dictionary(MapTypeCore(namedType.TypeArguments[1], context), key);
            }

            // Enum → named union type. Ordinary enums are numeric by default
            // (matching ordinary System.Text.Json serialization); a type-level
            // [JsonConverter(typeof(JsonStringEnumConverter<...>))] or a
            // Rivet*EnumConverter family declaration opts into a string union
            // honoring [JsonStringEnumMemberName].
            if (namedType.TypeKind == TypeKind.Enum)
            {
                // A5: full-namespace keyed naming — colliding enum names disambiguate
                // loudly instead of first-wins TryAdd
                var enumName = GetEmittedName(namedType);
                if (!_enums.ContainsKey(enumName))
                {
                    var fields = namedType
                        .GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => f.HasConstantValue)
                        .ToList();
                    var (isStringEnum, enumNamingPolicy) = GetEnumWireShape(namedType);
                    IReadOnlyList<string>? stringMembers = null;
                    if (isStringEnum)
                    {
                        if (
                            namedType.HasAttribute(_types.Flags)
                            || fields.Select(f => EnumLiteral(f.ConstantValue)).Distinct().Count()
                                != fields.Count
                        )
                        {
                            throw new RivetUserException(
                                $"error {Diagnostics.UnsupportedEnumConverter}: string enum '{namedType.Name}' contains flags or aliased values. Use distinct values without Flags, or a numeric enum."
                            );
                        }
                        var candidates = fields
                            .Select(f => EnumWireValue(f, enumNamingPolicy))
                            .ToList();
                        if (candidates.Distinct(StringComparer.Ordinal).Count() != candidates.Count)
                        {
                            // Reachable with a family converter casing two member
                            // names together, or with duplicate
                            // [JsonStringEnumMemberName] pins — either way the
                            // string union would not match a distinct wire-value set.
                            var colliding = candidates
                                .GroupBy(value => value, StringComparer.Ordinal)
                                .First(group => group.Count() > 1)
                                .Key;
                            var collidingMembers = string.Join(
                                ", ",
                                fields
                                    .Where(f => EnumWireValue(f, enumNamingPolicy) == colliding)
                                    .Select(f => f.Name)
                            );
                            throw new RivetUserException(
                                $"error {Diagnostics.EnumWireValueCollision}: enum '{namedType.Name}' members "
                                    + $"({collidingMembers}) produce the same wire value '{colliding}'. "
                                    + "Use distinct member names or explicit wire names."
                            );
                        }
                        else
                        {
                            stringMembers = candidates;
                        }
                    }

                    if (stringMembers is not null)
                    {
                        _enums[enumName] = new TsType.StringUnion(
                            stringMembers,
                            GetTypeMetadata(namedType),
                            GetTypeFormat(namedType),
                            GetTypeDescription(namedType),
                            _generatedEnumMetadata.GetValueOrDefault(enumName),
                            enumNamingPolicy is null
                                ? null
                                : Naming.ToPolicyToken(enumNamingPolicy.Value)
                        );
                    }
                    else
                    {
                        var format = GetTypeFormat(namedType);
                        _enums[enumName] = new TsType.IntUnion(
                            fields
                                .Select(field => EnumLiteral(field.ConstantValue))
                                .Distinct()
                                .ToList(),
                            format,
                            GetTypeMetadata(namedType),
                            GetTypeDescription(namedType),
                            _generatedEnumMetadata.GetValueOrDefault(enumName)
                        );
                    }
                }

                return new TsType.TypeRef(enumName);
            }

            // Named record/class from source or project-referenced assembly → walk transitively
            if (
                namedType.TypeKind is TypeKind.Class or TypeKind.Struct
                && _walkableAssemblies.Contains(namedType.ContainingAssembly)
            )
            {
                // Scalar brand: explicit [RivetScalar] opt-in — the Value type
                // determines the branded-scalar wire representation. Without the
                // attribute a one-property type is an ordinary object schema.
                var scalarInner = TryGetScalarInner(namedType);
                if (scalarInner is not null)
                {
                    var brandName = GetEmittedName(namedType);
                    var brand = new TsType.Brand(
                        brandName,
                        MapTypeCore(scalarInner, context).WithLeaf(null, GetTypeFormat(namedType)),
                        GetTypeMetadata(namedType),
                        GetTypeDescription(namedType)
                    );
                    _brands.TryAdd(brandName, brand);
                    return brand;
                }

                WalkType(namedType);
                var emittedName = GetEmittedName(namedType);

                // Closed generic (e.g. PagedResult<MessageDto>) → Generic node
                if (namedType.IsGenericType && !namedType.IsUnboundGenericType)
                {
                    var tsArgs = namedType
                        .TypeArguments.Select(a => MapTypeCore(a, context))
                        .ToList();
                    return new TsType.Generic(emittedName, tsArgs);
                }

                return new TsType.TypeRef(emittedName);
            }
        }

        // ValueTuple → inline object { key: string; value: number }
        if (symbol is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            var fields = tupleType
                .TupleElements.Select(e =>
                {
                    var fieldType = MapTypeCore(e.Type, context);
                    return new TsType.InlineObjectField(Naming.ToCamelCase(e.Name), fieldType);
                })
                .ToList();
            return new TsType.InlineObject(fields);
        }

        // Diagnosed-unsupported scalars (FABLE_GAPS §7 item 12): TimeSpan and BigInteger
        // fall through to the empty {} fallback schema with a diagnostic naming the
        // cause — diagnose, don't change the wire. char (length-1 string) and object
        // (deliberately untyped) graduated to supported mappings in P2 wave 6
        // (RIV1011/RIV1012 retired).
        var unsupportedId = symbol switch
        {
            _ when SymbolEqualityComparer.Default.Equals(symbol, _timeSpanType) =>
                Diagnostics.UnsupportedTimeSpan,
            _ when SymbolEqualityComparer.Default.Equals(symbol, _bigIntegerType) =>
                Diagnostics.UnsupportedBigInteger,
            _ => null,
        };

        if (unsupportedId is not null)
        {
            Diagnostics.Warn(
                unsupportedId,
                $"unsupported type '{symbol.ToDisplayString()}'{AtContext(context)} has no schema mapping — emitting an untyped (empty) schema"
            );
        }

        // Fallback
        return new TsType.Primitive("unknown");
    }

    /// <summary>
    /// The wire value of one enum member: an explicit
    /// [JsonStringEnumMemberName("original")] wins; otherwise the casing the
    /// declared converter names applies; without either, the wire value is the
    /// exact C# member name — JsonStringEnumConverter with no naming policy
    /// writes the CLR name verbatim, so Rivet must not camel-case it
    /// (acceptance:string-enum-preserves-member-name).
    /// </summary>
    private string EnumWireValue(IFieldSymbol field, RivetNamingPolicy? policy) =>
        field.GetAttribute(_types.JsonStringEnumMemberName).StringArgument()
        ?? (policy is null ? field.Name : Naming.ToPolicyCase(field.Name, policy.Value));

    /// <summary>
    /// The string-wire shape an enum declares through its type-level
    /// [JsonConverter]: whether the converter writes strings at all, and the
    /// casing convention its name declares. The converter type is the single
    /// declared fact — the built-in JsonStringEnumConverter keeps CLR names
    /// verbatim (no policy), and each Rivet*EnumConverter family member carries
    /// its casing in the class name (attribute args cannot hold a naming
    /// policy). Null when the enum is numeric-wire.
    /// </summary>
    private (bool IsString, RivetNamingPolicy? Policy) GetEnumWireShape(INamedTypeSymbol type)
    {
        var converters = type.GetAttributes()
            .Where(a => IsJsonConverterAttribute(a.AttributeClass))
            .ToArray();
        if (converters.Length == 0)
        {
            return (false, null);
        }
        if (
            converters.Length != 1
            || !converters[0].Is(_types.JsonConverter)
            || converters[0].ConstructorArguments is not [{ Value: INamedTypeSymbol converterType }]
        )
        {
            throw new RivetUserException(
                $"error {Diagnostics.UnsupportedEnumConverter}: enum '{type.Name}' must use one explicit [JsonConverter(typeof(...))] declaration; custom converter attributes are unsupported."
            );
        }

        if (
            converterType.IsGenericType
            && (
                converterType.TypeArguments.Length != 1
                || !SymbolEqualityComparer.Default.Equals(converterType.TypeArguments[0], type)
            )
        )
        {
            throw new RivetUserException(
                $"error {Diagnostics.UnsupportedEnumConverter}: converter for enum '{type.Name}' must target that enum."
            );
        }
        return (converterType.ContainingNamespace.ToDisplayString(), converterType.Name) switch
        {
            ("System.Text.Json.Serialization", "JsonStringEnumConverter") => (true, null),
            ("System.Text.Json.Serialization", "JsonNumberEnumConverter") => (false, null),
            ("Rivet", "RivetLowerCaseEnumConverter") => (true, RivetNamingPolicy.LowerCase),
            ("Rivet", "RivetCamelCaseEnumConverter") => (true, RivetNamingPolicy.CamelCase),
            ("Rivet", "RivetSnakeCaseEnumConverter") => (true, RivetNamingPolicy.SnakeCase),
            ("Rivet", "RivetKebabCaseEnumConverter") => (true, RivetNamingPolicy.KebabCase),
            _ => throw new RivetUserException(
                $"error {Diagnostics.UnsupportedEnumConverter}: enum '{type.Name}' uses unsupported converter '{converterType.ToDisplayString()}'. Use a built-in enum converter or a Rivet enum policy converter."
            ),
        };
    }

    private string? GetTypeFormat(INamedTypeSymbol type) =>
        type.GetAttribute(_types.RivetFormat).StringArgument();

    /// <summary>
    /// The exact decimal literal of an enum constant, for every legal underlying type
    /// (acceptance:numeric-enums-cover-all-legal-underlying-values).
    /// </summary>
    private static string EnumLiteral(object? constantValue) =>
        Convert.ToString(constantValue, CultureInfo.InvariantCulture) is { Length: > 0 } literal
            ? literal
            // Unreachable for legal C# enums; reached only if the walker's field
            // selection drifts. Refuse rather than guess.
            : throw new InvalidOperationException(
                "Enum member has no constant value and cannot be emitted as a numeric "
                    + "union member."
            );

    private static string AtContext(string? context) => context is null ? "" : $" on '{context}'";

    /// <summary>
    /// FABLE_GAPS §7 item 12 (P2 wave 3): maps a dictionary key type to its contract
    /// representation, or null for plain string keys (the propertyNames-less default).
    /// Supported: string, enums (mapping registers the key enum's schema — the
    /// "vanishing key-enum" fix), string-backed value-object brands, and primitives
    /// System.Text.Json serializes as string dictionary keys (Guid, dates/times, Uri,
    /// char, numerics). Numeric keys become string-typed primitives keeping the numeric
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
            if (
                named.TypeKind is TypeKind.Class or TypeKind.Struct
                && _walkableAssemblies.Contains(named.ContainingAssembly)
                && TryGetScalarInner(named) is { SpecialType: SpecialType.System_String }
            )
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
                    return new TsType.Primitive(
                        "string",
                        primitive.Format,
                        primitive.CSharpType
                            ?? primitive.Format switch
                            {
                                "int32" => "int",
                                "int64" => "long",
                                "float" => "float",
                                "double" => "double",
                                "decimal" => "decimal",
                                _ => null,
                            }
                    );
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
            IArrayTypeSymbol array => SymbolEqualityComparer.Default.Equals(
                array.ElementType,
                element
            ),
            INamedTypeSymbol { TypeArguments.Length: 1 } named when IsCollectionType(named) =>
                SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], element),
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
            // char (P2 wave 6): System.Text.Json writes char as a single-character
            // JSON string (and char dictionary keys as single-character property
            // names) — the emitter pins both length bounds to 1; CSharpType recovers
            // the exact type on import.
            SpecialType.System_Char => new TsType.Primitive("string", null, "char"),
            // object (P2 wave 6): "any JSON value" — the untyped (empty) schema is
            // the honest spec for it, deliberately and silently. CSharpType "object"
            // tells the emitter the untyped emission is intentional (no RIV2005);
            // the wire schema stays a bare {} with no sidecar.
            SpecialType.System_Object => new TsType.Primitive("unknown", null, "object"),
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
        {
            return result;
        }

        // Non-SpecialType primitives — resolved via dictionary lookup instead of per-field null checks
        if (_scalarTypes.TryGetValue(symbol, out var scalar))
        {
            return scalar;
        }

        return null;
    }

    private bool IsJsonConverterAttribute(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, _types.JsonConverter))
            {
                return true;
            }
        }
        return false;
    }

    private ITypeSymbol? TryGetScalarInner(INamedTypeSymbol symbol)
    {
        if (!symbol.HasAttribute(_types.RivetScalar))
        {
            return null;
        }

        var props = symbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public)
            .ToList();

        if (
            symbol.IsGenericType
            || symbol.IsAbstract
            || (
                symbol.BaseType is not null
                && symbol.BaseType.SpecialType
                    is not (SpecialType.System_Object or SpecialType.System_ValueType)
            )
            || props.Count != 1
            || props[0].Name != "Value"
            || props[0].IsIndexer
            || props[0].GetMethod?.DeclaredAccessibility != Accessibility.Public
            || props[0]
                .GetAttributes()
                .Any(a =>
                    a.AttributeClass?.ContainingNamespace.ToDisplayString()
                        == "System.Text.Json.Serialization"
                    || IsJsonConverterAttribute(a.AttributeClass)
                )
            || symbol.GetAttributes().Count(a => IsJsonConverterAttribute(a.AttributeClass)) != 1
            || !symbol.InstanceConstructors.Any(c =>
                c.DeclaredAccessibility == Accessibility.Public
                && c.Parameters.Length == 1
                && c.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, props[0].Type)
            )
        )
        {
            throw new RivetUserException(
                $"error {Diagnostics.InvalidRivetScalarShape}: [RivetScalar] type "
                    + $"'{symbol.ToDisplayString()}' must be a non-generic class/struct/record "
                    + "with exactly one public readable non-indexer property "
                    + "named 'Value' and a public constructor accepting its type. Inheritance, competing converters and property-level JSON settings are unsupported."
            );
        }

        return props[0].Type;
    }

    /// <summary>
    /// FABLE_ROUNDTRIP cross-corpus #1: a bodyless-method input can only lower to
    /// route/query params when its JSON surface IS its property surface. Maps,
    /// collections and scalars serialize as a single value — enumerating their CLR
    /// properties (Count, Keys, Comparer, Capacity, …) invents wire params.
    /// </summary>
    public bool IsParamLowerable(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol || type.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        if (type.SpecialType is not SpecialType.None)
        {
            return false; // string, int, bool, object, …
        }

        if (type is INamedTypeSymbol named)
        {
            if (
                named.OriginalDefinition.SpecialType is SpecialType.System_Nullable_T
                && named.TypeArguments is [var inner]
            )
            {
                return IsParamLowerable(inner);
            }

            if (
                _scalarTypes.ContainsKey(named)
                || IsDictionaryType(named)
                || IsCollectionType(named)
            )
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCollectionType(INamedTypeSymbol symbol) =>
        symbol.OriginalDefinition.SpecialType
            is SpecialType.System_Collections_Generic_IList_T
                or SpecialType.System_Collections_Generic_ICollection_T
                or SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_IReadOnlyList_T
                or SpecialType.System_Collections_Generic_IReadOnlyCollection_T
        || SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, _listType);

    /// <summary>
    /// True for the scalar/simple shapes an explicit [FromForm] parameter declares
    /// as a single form field: primitives, string, the well-known scalar structs
    /// (Guid/DateTime/…), enums, and Nullable/array/collection surfaces of those.
    /// Anything else (class/record DTOs) is the form body. Consulted only inside
    /// the [FromForm] attribute branch — never as a general binding inference —
    /// and shares the scalar surface with MapType so the two cannot drift.
    /// </summary>
    public bool IsSimpleFormType(ITypeSymbol type)
    {
        if (
            type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } nullable
        )
        {
            return IsSimpleFormType(nullable.TypeArguments[0]);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        if (type.SpecialType is not SpecialType.None)
        {
            return true;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return IsSimpleFormType(arrayType.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            if (_scalarTypes.ContainsKey(named))
            {
                return true;
            }

            if (IsCollectionType(named) && named.TypeArguments.Length == 1)
            {
                return IsSimpleFormType(named.TypeArguments[0]);
            }
        }

        return false;
    }

    private bool IsDictionaryType(INamedTypeSymbol symbol) =>
        _dictionaryTypes.Contains(symbol.OriginalDefinition);

    /// <summary>
    /// Reads the DataAnnotations constraints plus the two facets DataAnnotations lacks
    /// (<c>[RivetConstraints(MultipleOf, UniqueItems)]</c>). <c>[MinLength]</c>,
    /// <c>[MaxLength]</c> and <c>[Length]</c> count items on an array and characters
    /// on anything else.
    /// </summary>
    private TsPropertyConstraints? ReadConstraints(
        ImmutableArray<AttributeData> attributes,
        bool isArray
    )
    {
        int? minLength = null;
        int? maxLength = null;
        string? pattern = null;
        double? minimum = null;
        double? maximum = null;
        double? exclusiveMinimum = null;
        double? exclusiveMaximum = null;
        double? multipleOf = null;
        bool? uniqueItems = null;

        foreach (var attr in attributes)
        {
            switch (attr.ConstructorArguments)
            {
                case [{ Value: int ml }, ..] when attr.Is(_types.MinLength):
                    minLength = ml;
                    break;

                case [{ Value: int mxl }, ..] when attr.Is(_types.MaxLength):
                    maxLength = mxl;
                    break;

                case [{ Value: int lMin }, { Value: int lMax }] when attr.Is(_types.Length):
                    minLength = lMin;
                    maxLength = lMax;
                    break;

                case [{ Value: int slMax }, ..] when attr.Is(_types.StringLength):
                    maxLength = slMax;
                    if (attr.NamedArgument("MinimumLength") is int slMin)
                    {
                        minLength = slMin;
                    }

                    break;

                case { Length: >= 2 } when attr.Is(_types.Range):
                    // A9: the (Type, string, string) overload puts an ITypeSymbol in arg 0 —
                    // the old Convert.ToDouble crashed the tool with InvalidCastException
                    var args = attr.ConstructorArguments;
                    var (minArg, maxArg) =
                        args.Length >= 3 && args[0].Value is ITypeSymbol
                            ? (args[1].Value, args[2].Value)
                            : (args[0].Value, args[1].Value);

                    if (
                        !TryConvertRangeBound(minArg, out var rangeMin)
                        || !TryConvertRangeBound(maxArg, out var rangeMax)
                    )
                    {
                        Diagnostics.Warn(
                            Diagnostics.UnparseableRangeBound,
                            $"unparseable [Range] bound ('{minArg}', '{maxArg}') — skipping the range constraint"
                        );
                        break;
                    }

                    // double.MinValue/MaxValue mark an open side (CSharpWriter emits them
                    // for single-sided constraints).
                    if (rangeMin is not double.MinValue)
                    {
                        if (attr.NamedArgument("MinimumIsExclusive") is true)
                        {
                            exclusiveMinimum = rangeMin;
                        }
                        else
                        {
                            minimum = rangeMin;
                        }
                    }

                    if (rangeMax is not double.MaxValue)
                    {
                        if (attr.NamedArgument("MaximumIsExclusive") is true)
                        {
                            exclusiveMaximum = rangeMax;
                        }
                        else
                        {
                            maximum = rangeMax;
                        }
                    }

                    break;

                case [{ Value: string pat }, ..] when attr.Is(_types.RegularExpression):
                    pattern = pat;
                    break;

                case [] when attr.Is(_types.RivetConstraints):
                    if (attr.NamedArgument("MultipleOf") is double m && !double.IsNaN(m))
                    {
                        multipleOf = m;
                    }

                    if (attr.NamedArgument("UniqueItems") is true)
                    {
                        uniqueItems = true;
                    }

                    break;
            }
        }

        var c = new TsPropertyConstraints(
            MinLength: isArray ? null : minLength,
            MaxLength: isArray ? null : maxLength,
            Pattern: pattern,
            Minimum: minimum,
            Maximum: maximum,
            ExclusiveMinimum: exclusiveMinimum,
            ExclusiveMaximum: exclusiveMaximum,
            MultipleOf: multipleOf,
            MinItems: isArray ? minLength : null,
            MaxItems: isArray ? maxLength : null,
            UniqueItems: uniqueItems
        );

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
                    out result
                );
            case int
            or long
            or short
            or byte
            or sbyte
            or uint
            or ulong
            or ushort
            or float
            or double
            or decimal:
                result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private string? ReadDataAnnotationFormat(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.Is(_types.EmailAddress))
            {
                return "email";
            }
            if (attr.Is(_types.Url))
            {
                return "uri";
            }
        }

        return null;
    }

    /// <summary>
    /// P2 wave 5: the wire header name of a [RivetHeader] property, or null when the
    /// property is not header-bound. The attribute's name argument keeps the original
    /// casing ("Notion-Version"); without one the property name itself is the header name.
    /// </summary>
    public string? GetHeaderName(IPropertySymbol prop) =>
        prop.GetAttribute(_types.RivetHeader) is { } attr
            ? attr.StringArgument() ?? prop.Name
            : null;

    /// <summary>
    /// Requiredness of a wire member (property or field): [RivetOptional] wins, then
    /// [Required], then the C# `required` keyword, then nullability.
    /// </summary>
    public bool IsOptional(ISymbol member)
    {
        if (member.HasAttribute(_types.RivetOptional))
        {
            return true;
        }

        if (member.HasAttribute(_types.Required))
        {
            return false;
        }

        // The C# `required` keyword: must be set at construction, may still be
        // null — the one form that expresses required-AND-nullable (a real axis:
        // 139 github-corpus properties). DataAnnotations [Required] cannot say
        // this (it rejects null at MVC binding); the keyword can.
        if (member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
        {
            return false;
        }

        return GetMemberType(member).NullableAnnotation == NullableAnnotation.Annotated;
    }

    /// <summary>The declared type of a wire member (property or field).</summary>
    public static ITypeSymbol GetMemberType(ISymbol member) =>
        member switch
        {
            IPropertySymbol prop => prop.Type,
            IFieldSymbol field => field.Type,
            _ => throw new InvalidOperationException(
                $"Wire member '{member.Name}' is neither a property nor a field."
            ),
        };
}
