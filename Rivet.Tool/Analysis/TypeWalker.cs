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
/// between them.
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

    // Emitted-name registry keyed by fully-qualified name (namespace + arity), and the
    // claimed simple names mapped back to the type that claimed them. Two distinct types
    // with one simple name abort generation (RIV1007): schema names are global.
    private readonly Dictionary<string, string> _emittedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _claimedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generatedSchemaNames = new(StringComparer.Ordinal);

    private const string GeneratedClaimant = "the generated schema ";

    // Generated scalar schemas (no $ref, not an array) a [RivetScalar] brand of the same name
    // may claim: the importer writes a brand's metadata there, and the brand's schema reads it.
    private readonly HashSet<string> _generatedBrandSchemaNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TsScalarMetadata> _generatedEnumMetadata = new(
        StringComparer.Ordinal
    );

    // Scalar C# types that map directly to TsType.Primitive (Guid → string/uuid, etc.)
    private readonly ImmutableDictionary<INamedTypeSymbol, TsType.Primitive> _scalarTypes;

    // JSON container types that map to non-Primitive TsTypes (special-cased in MapTypeCore)
    private readonly INamedTypeSymbol? _jsonObjectType;
    private readonly INamedTypeSymbol? _jsonArrayType;

    private readonly INamedTypeSymbol? _bigIntegerType;

    // Number scalars outside SpecialType that System.Text.Json applies number handling to.
    private readonly ImmutableHashSet<INamedTypeSymbol> _extraNumberTypes;

    private readonly INamedTypeSymbol? _listType;
    private readonly ImmutableHashSet<INamedTypeSymbol> _dictionaryTypes;

    // Closed generic instantiations already judged for number handling through their
    // type arguments (RIV1112).
    private readonly HashSet<ITypeSymbol> _numberHandlingCheckedInstantiations = new(
        SymbolEqualityComparer.Default
    );

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

        // Scalar type → TsType.Primitive lookup, from the table the importer reads too
        var scalars = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, TsType.Primitive>(
            SymbolEqualityComparer.Default
        );
        foreach (var (metadataName, primitive) in BuiltInScalars.FrameworkTypes)
        {
            AddScalar(scalars, compilation, metadataName, primitive);
        }
        _scalarTypes = scalars.ToImmutable();

        _jsonObjectType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonObject");
        _jsonArrayType = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonArray");

        // BigInteger has no System.Text.Json converter — resolved up front so the
        // fallback path can refuse it by name.
        _bigIntegerType = compilation.GetTypeByMetadataName("System.Numerics.BigInteger");

        _extraNumberTypes = ResolveTypeSet(
            compilation,
            "System.Int128",
            "System.UInt128",
            "System.Half"
        );

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
                throw Diagnostics.Error(
                    Diagnostics.MalformedGeneratedMetadata,
                    "Invalid generated schema metadata in RivetGeneratedSchemaAttribute."
                );
            }

            var schemaType = attribute.ConstructorArguments[2].Value as string;
            var format = attribute.ConstructorArguments[3].Value as string;
            var metadata =
                DeserializeGeneratedMetadata<TsScalarMetadata>(
                    metadataJson,
                    $"RivetGeneratedSchemaAttribute metadata of '{name}'"
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
                throw Diagnostics.Error(
                    Diagnostics.MalformedGeneratedMetadata,
                    $"Generated schema name '{name}' collides with another generated type."
                );
            }

            if (isEnum)
            {
                RequireAnnotatedEnum(name);
                _generatedEnumMetadata[name] = metadata;
                continue;
            }

            // A generated schema is a component with no C# type behind it, so a walked type
            // with its name would replace it: claim the name, and GetEmittedName refuses the
            // type (RIV1007) naming both. A scalar one may annotate a [RivetScalar] brand.
            _claimedNames[name] = $"{GeneratedClaimant}'{name}' (component '{componentId}')";
            if (!isArray && schemaRef is null)
            {
                _generatedBrandSchemaNames.Add(name);
            }

            TsType leaf = isArray
                ? itemSchemaRef is null
                    ? throw Diagnostics.Error(
                        Diagnostics.MalformedGeneratedMetadata,
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
                throw Diagnostics.Error(
                    Diagnostics.MalformedGeneratedMetadata,
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

    /// <summary>
    /// An <c>isEnum</c> generated schema annotates the walked enum of the same name (its
    /// metadata joins that enum's schema), so a name no enum has is stale or hand-edited
    /// metadata, with nothing to sit on: RIV1122.
    /// </summary>
    private void RequireAnnotatedEnum(string name)
    {
        static IEnumerable<INamedTypeSymbol> TypesNamed(
            INamespaceOrTypeSymbol container,
            string name
        ) =>
            container
                .GetTypeMembers(name)
                .Concat(
                    container
                        .GetMembers()
                        .OfType<INamespaceOrTypeSymbol>()
                        .SelectMany(inner => TypesNamed(inner, name))
                );

        var types = _walkableAssemblies
            .SelectMany(assembly => TypesNamed(assembly.GlobalNamespace, name))
            .ToList();
        if (types.Any(type => type.TypeKind == TypeKind.Enum))
        {
            return;
        }

        var found =
            types.Count == 0
                ? "the compilation has no type of that name"
                : $"'{types[0].ToDisplayString()}' isn't an enum";
        throw Diagnostics.Error(
            Diagnostics.MalformedGeneratedMetadata,
            $"[assembly: RivetGeneratedSchema(\"{name}\", ...)] is an enum's metadata (isEnum), but "
                + $"{found}, so there is no enum schema for it to describe: the generated code is stale "
                + "or hand-edited. Re-import the OpenAPI document, or delete the attribute."
        );
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
            throw Diagnostics.Error(
                Diagnostics.MalformedGeneratedMetadata,
                $"{context} references unknown generated schema '{schemaRef}'."
            );
        }

        return new TsType.TypeRef(schemaRef);
    }

    private TsType ApplyGeneratedSchemaRefAt(
        TsType type,
        string pointer,
        string schemaRef,
        string context
    ) =>
        (type, pointer) switch
        {
            (_, "") => ApplyGeneratedSchemaRef(type, schemaRef, context),
            (TsType.Nullable nullable, _) => nullable with
            {
                Inner = ApplyGeneratedSchemaRefAt(nullable.Inner, pointer, schemaRef, context),
            },
            (TsType.Array array, _) when pointer.StartsWith("/items", StringComparison.Ordinal) =>
                array with
                {
                    Element = ApplyGeneratedSchemaRefAt(
                        array.Element,
                        pointer["/items".Length..],
                        schemaRef,
                        context
                    ),
                },
            (TsType.Dictionary dictionary, _)
                when pointer.StartsWith("/additionalProperties", StringComparison.Ordinal) =>
                dictionary with
                {
                    Value = ApplyGeneratedSchemaRefAt(
                        dictionary.Value,
                        pointer["/additionalProperties".Length..],
                        schemaRef,
                        context
                    ),
                },
            _ => throw Diagnostics.Error(
                Diagnostics.MalformedGeneratedMetadata,
                $"{context} references generated schema '{schemaRef}' at a pointer the type has no slot for."
            ),
        };

    /// <summary>
    /// Returns true when the member is never a named JSON member: [JsonExtensionData]
    /// always (a property, or a [JsonInclude] field; its entries are the object's
    /// additionalProperties, see <see cref="ReadAdditionalProperties"/>), and [JsonIgnore]
    /// Always (or the parameterless form). The condition forms
    /// Never/WhenWritingNull/WhenWritingDefault do not remove the property from either
    /// schema — WhenWritingNull/WhenWritingDefault only allow omission on the response
    /// wire, handled by requiredness (CanOmitOnWire). Direction does not change this
    /// answer; presence asymmetry lives in GetJsonPropertySurface. Fields are only
    /// surfaced through [JsonInclude] (GetJsonFieldSurface), so [JsonIgnore] isn't read here.
    /// </summary>
    public bool IsJsonIgnored(ISymbol member)
    {
        if (member.HasAttribute(_types.JsonExtensionData))
        {
            return true;
        }

        if (member is not IPropertySymbol prop)
        {
            return false;
        }

        foreach (var attribute in prop.GetAttributes())
        {
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
        attribute.NamedArgument("Condition") is int raw ? (JsonIgnoreCondition)raw : null;

    /// <summary>
    /// Whether the property is serialized/deserialized under default System.Text.Json
    /// web options for the queried direction, per observed serializer behavior:
    /// public get+set properties surface in both directions; get-only properties
    /// serialize on the response and deserialize only when bound by a matching
    /// constructor parameter (records);
    /// set-only properties deserialize only (request); non-public members are absent
    /// unless [JsonInclude] (fields likewise). Unsupported shapes are reported and
    /// excluded rather than guessed. <paramref name="boundType"/> is the type being
    /// (de)serialized, whose constructor binds get-only properties, inherited ones
    /// included.
    /// </summary>
    public JsonPropertySurface GetJsonPropertySurface(
        IPropertySymbol prop,
        INamedTypeSymbol boundType
    )
    {
        var hasInclude = prop.HasAttribute(_types.JsonInclude);

        // Non-public members surface only through [JsonInclude].
        var getterPublic = prop.GetMethod?.DeclaredAccessibility is Accessibility.Public;
        var setterPublic = prop.SetMethod?.DeclaredAccessibility is Accessibility.Public;

        if (prop.Type.Kind == SymbolKind.DynamicType)
        {
            // Dynamic members keep parity with the established object handling:
            // the untyped-schema representation, never silent disappearance.
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
            // Get-only property. STJ deserializes it only via a matching parameter of
            // the constructor it deserializes through; response surface otherwise.
            if (getterPublic || hasInclude)
            {
                return IsDeserializableViaConstructor(prop, boundType)
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
        // surfaces in both directions.
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
    /// without the include a field is invisible to the serializer.
    /// </summary>
    public JsonPropertySurface GetJsonFieldSurface(IFieldSymbol field)
    {
        if (field.Type.Kind == SymbolKind.DynamicType)
        {
            // Dynamic members keep parity with the established object handling:
            // untyped-schema representation, never silent disappearance.
            return JsonPropertySurface.Both;
        }

        return field.HasAttribute(_types.JsonInclude)
            ? JsonPropertySurface.Both
            : JsonPropertySurface.Excluded;
    }

    /// <summary>
    /// True when the constructor System.Text.Json deserializes the bound type through has
    /// a parameter matching the property's name (case-insensitive).
    /// </summary>
    private bool IsDeserializableViaConstructor(IPropertySymbol prop, INamedTypeSymbol boundType) =>
        GetDeserializationConstructor(boundType) is { } constructor
        && constructor.Parameters.Any(parameter =>
            string.Equals(parameter.Name, prop.Name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// System.Text.Json's constructor choice: a [JsonConstructor] constructor wins; a
    /// struct otherwise uses its default constructor; a class otherwise uses its public
    /// parameterless constructor, else its only public constructor (a record's or a C# 12
    /// class's primary constructor, say), else none. Null means no parameters bind.
    /// </summary>
    private IMethodSymbol? GetDeserializationConstructor(INamedTypeSymbol type)
    {
        RefuseAmbiguousJsonConstructor(type);
        var attributed = type.InstanceConstructors.FirstOrDefault(constructor =>
            constructor.HasAttribute(_types.JsonConstructor)
        );
        if (attributed is not null || type.IsValueType)
        {
            return attributed;
        }

        var publicConstructors = type
            .InstanceConstructors.Where(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
            )
            .ToList();
        return publicConstructors.FirstOrDefault(constructor => constructor.Parameters.Length == 0)
            ?? (publicConstructors is [var lone] ? lone : null);
    }

    /// <summary>
    /// Refuses a type with more than one [JsonConstructor] constructor (RIV1116):
    /// System.Text.Json throws on it at runtime, so there is no constructor choice for the
    /// spec to follow.
    /// </summary>
    private void RefuseAmbiguousJsonConstructor(INamedTypeSymbol type)
    {
        var attributed = type.InstanceConstructors.Count(constructor =>
            constructor.HasAttribute(_types.JsonConstructor)
        );
        if (attributed > 1)
        {
            throw Diagnostics.Error(
                Diagnostics.AmbiguousJsonConstructor,
                $"type '{type.ToDisplayString()}' has "
                    + $"{attributed} [JsonConstructor] constructors. System.Text.Json refuses such a type at "
                    + "runtime, so there is no constructor whose parameters the spec can follow. Keep "
                    + "[JsonConstructor] on one constructor."
            );
        }
    }

    // A record's primary constructor shares the record's parameter list: its parameters
    // generate the properties. Roslyn gives the primary constructor the record declaration
    // itself as its declaring syntax; explicit constructors have their own declaration and
    // the copy constructor has none.
    private static bool IsRecordPrimaryConstructor(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is RecordDeclarationSyntax { ParameterList: not null }
        );

    /// <summary>
    /// The attributes that describe a wire member: its own, plus, when the walked type is
    /// a record, those on the walked record's own primary-constructor parameter of the
    /// same name. An attribute written on a positional parameter without
    /// <c>[property:]</c> lands on the parameter only; of the attributes the walker reads,
    /// only the DataAnnotations (constraints, <c>[EmailAddress]</c>, <c>[Url]</c>) may
    /// target a parameter, and MVC validation reads them there, through the bound type's
    /// constructor, so the spec must too. A base record's parameter is never read: MVC
    /// doesn't construct the base.
    /// </summary>
    private static ImmutableArray<AttributeData> GetWireMemberAttributes(
        ISymbol member,
        INamedTypeSymbol walkedType
    )
    {
        var own = member.GetAttributes();
        if (member is not IPropertySymbol property || !walkedType.IsRecord)
        {
            return own;
        }

        foreach (var constructor in walkedType.InstanceConstructors)
        {
            if (!IsRecordPrimaryConstructor(constructor))
            {
                continue;
            }

            var parameter = constructor.Parameters.FirstOrDefault(p => p.Name == property.Name);
            return parameter is null ? own : own.AddRange(parameter.GetAttributes());
        }

        return own;
    }

    /// <summary>
    /// Whether an object-schema property may be absent: only when declared so with
    /// [RivetOptional], or when [JsonIgnore(WhenWritingNull|WhenWritingDefault)] lets the
    /// serializer omit it. System.Text.Json writes null members by default, so
    /// nullability alone keeps a property required (typed <c>T | null</c>).
    /// </summary>
    public bool CanOmitOnWire(ISymbol member) =>
        member.HasAttribute(_types.RivetOptional)
        || member is IPropertySymbol prop
            && prop.GetAttribute(_types.JsonIgnore) is { } ignore
            && ReadJsonIgnoreCondition(ignore)
                is JsonIgnoreCondition.WhenWritingNull
                    or JsonIgnoreCondition.WhenWritingDefault;

    /// <summary>
    /// Flattens the wire-member surface of a type across its BaseType chain
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
                    // non-included fields stay absent.
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
                && GetJsonPropertySurface(
                    property,
                    type as INamedTypeSymbol ?? property.ContainingType
                ) != JsonPropertySurface.ResponseOnly
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
        // Resolve via the full-namespace registry: the same FQN reuses its emitted name,
        // and a simple-name collision aborts generation (RIV1007)
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

        RefuseUnmodelledTypeConverter(definition);
        RefuseAmbiguousJsonConstructor(definition);

        _visiting.Add(name);

        // Extract type parameter names (e.g. "T", "TItem")
        var typeParams = definition.TypeParameters.Select(tp => tp.Name).ToList();

        // A [JsonPolymorphic]/[JsonDerivedType] base type registers as a
        // TaggedUnion alias definition (oneOf + discriminator + mapping) instead of
        // silently flattening to its own property surface. Non-string tags and
        // unrepresentable registrations (RIV1014) are refused there; zero registrations
        // (RIV1015) and an unknown-type fallback or IgnoreUnrecognizedTypeDiscriminators
        // (RIV1016) are refused first, generic or not, since no host can serve them.
        // Generic polymorphic bases keep the flattening path — a generic alias has
        // no monomorphisation template the emitter could instantiate.
        // Both union definitions keep the type's root schema metadata, as a record does: an
        // imported hierarchy member's nullability, say, decides how its use sites ref it.
        RefuseUnservablePolymorphism(definition, name);
        RefuseUnregisteredAuthoredTagSchema(definition);
        if (!definition.IsGenericType && TryBuildPolymorphicUnion(definition, name) is { } union)
        {
            _visiting.Remove(name);
            _definitions[name] = new TsTypeDefinition(
                name,
                typeParams,
                union,
                GetTypeDescription(definition),
                GetTypeMetadata(definition),
                ReadGeneratedSchemaMetadata(definition).GetValueOrDefault("")
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
                GetTypeMetadata(definition),
                ReadGeneratedSchemaMetadata(definition).GetValueOrDefault("")
            );
            return;
        }

        var properties = new List<TsPropertyDefinition>();

        // Include inherited members by flattening the BaseType chain.
        foreach (var member in GetEffectiveProperties(definition))
        {
            if (
                BuildPropertyDefinition(member, definition, $"{name}.{member.Name}") is { } property
            )
            {
                properties.Add(property);
            }
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
            ReadGeneratedSchemaMetadata(definition).GetValueOrDefault(""),
            ReadAdditionalProperties(definition)
        );
    }

    /// <summary>
    /// Whether <paramref name="type"/>'s JSON takes members beyond its properties, as
    /// System.Text.Json decides it: <c>true</c> when a <c>[JsonExtensionData]</c> bag (its
    /// own or a base's) reads and writes them, <c>false</c> when the type itself is
    /// <c>[JsonUnmappedMemberHandling(Disallow)]</c> (the attribute isn't inherited), null
    /// otherwise. A bag STJ refuses at runtime (the wrong type, a second bag, or one beside
    /// Disallow) is refused here instead (RIV1118).
    /// </summary>
    private bool? ReadAdditionalProperties(INamedTypeSymbol type)
    {
        var bags = GetEffectiveProperties(type)
            .Where(member =>
                member.HasAttribute(_types.JsonExtensionData)
                && GetJsonMemberSurface(member, type) != JsonPropertySurface.Excluded
            )
            .ToList();
        string Name(ISymbol member) => $"{member.ContainingType.Name}.{member.Name}";

        if (bags.Count > 1)
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidJsonExtensionData,
                $"'{type.ToDisplayString()}' has more than one [JsonExtensionData] member, counting "
                    + $"its base types ({string.Join(", ", bags.Select(bag => $"'{Name(bag)}'"))}), and "
                    + "System.Text.Json refuses such a type at runtime. Keep one."
            );
        }

        var bag = bags.SingleOrDefault();
        if (bag is not null && !IsExtensionDataType(GetMemberType(bag)))
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidJsonExtensionData,
                $"[JsonExtensionData] on '{Name(bag)}' has type "
                    + $"'{GetMemberType(bag).ToDisplayString()}', which System.Text.Json refuses at runtime: "
                    + "an extension data member is a Dictionary<string, JsonElement>, a "
                    + "Dictionary<string, object> (or another IDictionary<string, JsonElement> or "
                    + "IDictionary<string, object>), or a JsonObject. Give it one of those types."
            );
        }

        var disallow =
            type.GetAttribute(_types.JsonUnmappedMemberHandling)
                is { ConstructorArguments: [{ Value: int handling }] }
            && handling == (int)JsonUnmappedMemberHandling.Disallow;
        if (bag is not null && disallow)
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidJsonExtensionData,
                $"'{type.ToDisplayString()}' is [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] "
                    + $"and has the [JsonExtensionData] member '{Name(bag)}', a pair System.Text.Json refuses "
                    + "at runtime: the bag takes exactly the members Disallow refuses. Drop the bag to refuse "
                    + "unknown members (additionalProperties: false), or Disallow to keep them "
                    + "(additionalProperties: {})."
            );
        }

        // An imported type's bag carries what its source schema admits, so the source's word
        // wins: {} when it wrote additionalProperties, nothing when it left it out (the same
        // JSON Schema), as for every other keyword the import preserves.
        var generated = type.GetAttribute(_types.RivetGeneratedType);
        var open =
            bag is not null
            && (
                generated is null || generated.NamedArgument("DeclaresAdditionalProperties") is true
            );
        return open ? true
            : disallow ? false
            : null;
    }

    // What System.Text.Json accepts as a [JsonExtensionData] bag besides JsonObject: anything
    // implementing IDictionary<string, object> or IDictionary<string, JsonElement>.
    private bool IsExtensionDataType(ITypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, _jsonObjectType)
        || (
            type is INamedTypeSymbol named ? type.AllInterfaces.Add(named) : type.AllInterfaces
        ).Any(candidate =>
            SymbolEqualityComparer.Default.Equals(
                candidate.OriginalDefinition,
                _types.IDictionaryOfTKeyTValue
            )
            && candidate.TypeArguments[0].SpecialType == SpecialType.System_String
            && (
                candidate.TypeArguments[1].SpecialType == SpecialType.System_Object
                || SymbolEqualityComparer.Default.Equals(
                    candidate.TypeArguments[1],
                    _types.JsonElement
                )
            )
        );

    /// <summary>
    /// The wire property <paramref name="member"/> contributes to <paramref name="owner"/>'s
    /// schema, with every annotation a record property carries; null when System.Text.Json
    /// neither writes nor reads it, or it binds a header. Records and polymorphic variants
    /// both describe their members through it, so a variant says what its record would.
    /// </summary>
    private TsPropertyDefinition? BuildPropertyDefinition(
        ISymbol member,
        INamedTypeSymbol owner,
        string context
    )
    {
        if (!IsSchemaMember(member, owner))
        {
            return null;
        }

        var memberSurface = GetJsonMemberSurface(member, owner);
        // [JsonPropertyName("x")] → use "x" instead of camelCase(Name)
        var jsonPropertyName = GetJsonMemberName(member);

        var tsName = jsonPropertyName ?? Naming.ToCamelCase(member.Name);
        var tsType = MapTypeCore(GetMemberType(member), context);
        // Before the annotations: a converter (or number handling) beside a [RivetSchemaType]
        // that can't pin it is the converter's refusal, whose fix covers both (RIV1111/1112),
        // not RIV1117's.
        RefuseUnmodelledMemberSerialization(member, owner, tsType, context);
        var isOptional = CanOmitOnWire(member);
        var attributes = GetWireMemberAttributes(member, owner);
        if (isOptional)
        {
            RefuseRequiredOmittableMember(member, attributes, context);
        }
        var (annotatedType, annotations, rootMetadata) = Annotate(
            member,
            attributes,
            tsType,
            context
        );

        // Accessibility-derived asymmetry becomes readOnly/writeOnly on the shared
        // component schema, alongside any explicit [RivetReadOnly]/[RivetWriteOnly].
        var isReadOnly =
            annotations.IsReadOnly || memberSurface == JsonPropertySurface.ResponseOnly;
        var isWriteOnly =
            annotations.IsWriteOnly || memberSurface == JsonPropertySurface.RequestOnly;

        return new TsPropertyDefinition(
            tsName,
            annotatedType,
            isOptional,
            annotations.IsDeprecated,
            annotations.Format,
            annotations.DefaultValue,
            annotations.Constraints,
            annotations.Description,
            annotations.Example,
            isReadOnly,
            isWriteOnly,
            rootMetadata
        );
    }

    /// <summary>
    /// <paramref name="member"/>'s mapped <paramref name="type"/> as its schema carries it, and
    /// the annotations beside it: <c>[RivetSchemaType]</c> and the format on its leaf, its
    /// <c>[RivetSchemaRef]</c>s, and the generated metadata on its items and values. The
    /// generated metadata at the member's own schema comes back apart, since a property
    /// definition and a param both hold it beside the type. A record property and a param
    /// lowered from a member (<see cref="AnnotatedParam"/>) both read it here, so the two
    /// can't drift.
    /// </summary>
    private (TsType Type, MemberAnnotations Annotations, TsScalarMetadata? RootMetadata) Annotate(
        ISymbol member,
        ImmutableArray<AttributeData> attributes,
        TsType type,
        string context
    )
    {
        var annotations = ReadMemberAnnotations(member, attributes, type);
        var (schemaRef, nestedSchemaRefs) = ReadSchemaRefs(attributes);
        type = annotations.ApplyLeaf(type);

        if (schemaRef is not null)
        {
            type = ApplyGeneratedSchemaRef(type, schemaRef, $"Property '{context}'");
        }
        foreach (var (refPointer, refName) in nestedSchemaRefs)
        {
            type = ApplyGeneratedSchemaRefAt(
                type,
                refPointer,
                refName,
                $"Property '{context}' at '{refPointer}'"
            );
        }

        var generatedSchemaMetadata = ReadGeneratedSchemaMetadata(member);
        type = ApplyGeneratedSchemaMetadata(type, "", generatedSchemaMetadata);
        return (type, annotations, generatedSchemaMetadata.GetValueOrDefault(""));
    }

    /// <summary>
    /// <paramref name="param"/> carrying what <paramref name="member"/> says about its schema,
    /// as the member's schema would if it were a body property (<see cref="Annotate"/>): its
    /// annotated type, description, constraints, default, deprecated, example, format, and
    /// the generated metadata at its own schema.
    /// <paramref name="member"/> is the input property or field a route, query, header or
    /// form-field param is lowered from (its positional record parameter's attributes count
    /// too), or the action or handler parameter itself. A <c>[RivetDefault]</c> wins over
    /// <paramref name="param"/>'s own default (a C# default value, which is source text).
    /// </summary>
    public TsEndpointParam AnnotatedParam(TsEndpointParam param, ISymbol member)
    {
        var (attributes, context, site) = member is IParameterSymbol parameter
            ? (
                parameter.GetAttributes(),
                $"{parameter.ContainingSymbol.Name}({parameter.Name})",
                $"[RivetExample] on parameter '{parameter.Name}'"
            )
            : (
                GetWireMemberAttributes(member, member.ContainingType),
                $"{member.ContainingType.Name}.{member.Name}",
                $"[RivetExample] on '{member.Name}'"
            );
        var (type, annotations, rootMetadata) = Annotate(member, attributes, param.Type, context);
        return param with
        {
            Type = type,
            Description = annotations.Description,
            IsDeprecated = annotations.IsDeprecated,
            DefaultValue = annotations.DefaultValue ?? param.DefaultValue,
            Constraints = annotations.Constraints,
            SchemaExamples = SchemaExamples(annotations.Example, site),
            Format = annotations is { IsFormatSpecified: true, Format: not "" }
                ? annotations.Format
                : null,
            IsFormatSpecified = annotations.IsFormatSpecified,
            ScalarMetadata = rootMetadata,
        };
    }

    /// <summary>
    /// A member's [RivetExample] as the one-item schema <c>examples</c> array a body
    /// property's schema carries, so the param says what the property would.
    /// </summary>
    private static JsonElement? SchemaExamples(string? example, string what)
    {
        if (example is null)
        {
            return null;
        }

        var value = AuthoredJson.Parse(example, what);
        using var array = JsonDocument.Parse($"[{value.GetRawText()}]");
        return array.RootElement.Clone();
    }

    /// <summary>
    /// RIV1119: a member that may be left out (<c>[RivetOptional]</c>, or a <c>[JsonIgnore]</c>
    /// condition that omits it when written) can't also be one System.Text.Json demands on
    /// read (<c>[JsonRequired]</c>, or C# <c>required</c>): the request schema would admit a
    /// body the host refuses, and one schema can't say both.
    /// </summary>
    private void RefuseRequiredOmittableMember(
        ISymbol member,
        ImmutableArray<AttributeData> attributes,
        string context
    )
    {
        var demanded =
            attributes.Any(attribute => attribute.Is(_types.JsonRequired)) ? "[JsonRequired]"
            : member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }
                ? "required"
            : null;
        if (demanded is null)
        {
            return;
        }

        var omittable = member.HasAttribute(_types.RivetOptional)
            ? "[RivetOptional]"
            : $"[JsonIgnore(Condition = JsonIgnoreCondition.{ReadJsonIgnoreCondition(member.GetAttribute(_types.JsonIgnore)!)})]";
        throw Diagnostics.Error(
            Diagnostics.RequiredMemberMayBeOmitted,
            $"'{context}' is {demanded} but also {omittable}: System.Text.Json refuses a request "
                + $"without it, while {omittable} says it may be left out, and one schema can't say "
                + $"both. Drop {demanded} if clients may leave it out, or drop {omittable} if it's "
                + "always on the wire."
        );
    }

    /// <summary>
    /// The annotations a member's attributes give its schema beyond its name, type and
    /// optionality: what a <see cref="TsPropertyDefinition"/> carries. Read the same way
    /// for a record property and for a lowered route, query or header parameter, so the
    /// two can't drift. <see cref="IsReadOnly"/> and <see cref="IsWriteOnly"/> are the
    /// explicit <c>[RivetReadOnly]</c>/<c>[RivetWriteOnly]</c> only; a property also takes
    /// them from its accessors. <see cref="Format"/> is <c>[RivetFormat]</c>'s value when
    /// <see cref="IsFormatSpecified"/>, otherwise the DataAnnotations one
    /// (<c>[EmailAddress]</c>, <c>[Url]</c>).
    /// </summary>
    public sealed record MemberAnnotations(
        bool IsDeprecated,
        string? Format,
        bool IsFormatSpecified,
        string? SchemaType,
        string? DefaultValue,
        TsPropertyConstraints? Constraints,
        string? Description,
        string? Example,
        bool IsReadOnly,
        bool IsWriteOnly
    )
    {
        /// <summary>
        /// <paramref name="type"/> with <c>[RivetSchemaType]</c> and the format applied to its
        /// primitive leaf: an explicit <c>[RivetFormat]</c> (even an empty one) replaces the
        /// inferred format, and a DataAnnotations format only fills a leaf that has none.
        /// </summary>
        public TsType ApplyLeaf(TsType type)
        {
            type = type.WithLeaf(SchemaType, IsFormatSpecified ? Format ?? "" : null);
            return
                !IsFormatSpecified
                && Format is not null
                && type
                    is TsType.Primitive { Format: null }
                        or TsType.Nullable { Inner: TsType.Primitive { Format: null } }
                ? type.WithLeaf(null, Format)
                : type;
        }
    }

    /// <summary>
    /// The annotations in <paramref name="attributes"/>, those of <paramref name="member"/>
    /// (a property or field with its positional record parameter's, or a handler parameter),
    /// whether or not it is a schema member: a <c>[RivetHeader]</c> property has them as well.
    /// <paramref name="type"/> is the member's mapped type; it decides whether a length
    /// constraint is an item count.
    /// </summary>
    private MemberAnnotations ReadMemberAnnotations(
        ISymbol member,
        ImmutableArray<AttributeData> attributes,
        TsType type
    )
    {
        string? format = null;
        var isFormatSpecified = false;
        string? schemaType = null;
        string? defaultValue = null;
        string? description = null;
        string? example = null;
        var isReadOnly = false;
        var isWriteOnly = false;
        foreach (var attr in attributes)
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

        type.RequirePrimitiveLeaf(schemaType, DescribeMember(member));

        return new MemberAnnotations(
            member.HasAttribute(_types.Obsolete),
            isFormatSpecified ? format : ReadDataAnnotationFormat(attributes),
            isFormatSpecified,
            schemaType,
            defaultValue,
            ReadConstraints(
                attributes,
                type is TsType.Array or TsType.Nullable { Inner: TsType.Array }
            ),
            description,
            example,
            isReadOnly,
            isWriteOnly
        );
    }

    /// <summary>
    /// <c>[RivetSchemaRef]</c> on a member: the generated schema it names for the whole
    /// member, and any it names at a pointer inside the member's type.
    /// </summary>
    private (string? SchemaRef, List<(string Pointer, string Name)> Nested) ReadSchemaRefs(
        ImmutableArray<AttributeData> attributes
    )
    {
        string? schemaRef = null;
        var nested = new List<(string Pointer, string Name)>();
        foreach (var attr in attributes)
        {
            if (
                attr.Is(_types.RivetSchemaRef)
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is string refName
            )
            {
                var refPointer =
                    attr.ConstructorArguments.Length > 1
                        ? attr.ConstructorArguments[1].Value as string ?? ""
                        : "";
                if (refPointer.Length == 0)
                {
                    schemaRef = refName;
                }
                else
                {
                    nested.Add((refPointer, refName));
                }
            }
        }

        return (schemaRef, nested);
    }

    private JsonPropertySurface GetJsonMemberSurface(ISymbol member, INamedTypeSymbol owner) =>
        member switch
        {
            IPropertySymbol property => GetJsonPropertySurface(property, owner),
            IFieldSymbol field => GetJsonFieldSurface(field),
            _ => JsonPropertySurface.Excluded,
        };

    /// <summary>
    /// Whether <paramref name="member"/> is a property of <paramref name="owner"/>'s JSON:
    /// System.Text.Json writes or reads it, and it doesn't bind a header ([RivetHeader]
    /// properties are request header params, which ContractWalker/EndpointWalker surface as
    /// ParamSource.Header params instead).
    /// </summary>
    private bool IsSchemaMember(ISymbol member, INamedTypeSymbol owner) =>
        GetJsonMemberSurface(member, owner) != JsonPropertySurface.Excluded
        && !IsJsonIgnored(member)
        && !(member is IPropertySymbol header && GetHeaderName(header) is not null);

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

    private static readonly JsonSerializerOptions _generatedMetadataOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// JSON inside a generated attribute the importer wrote (schema metadata here, OpenAPI
    /// provenance in <see cref="OpenApiProvenanceWalker"/>); JSON that doesn't read is stale
    /// or hand-edited generated code (RIV1122), not a crash.
    /// </summary>
    internal static T? DeserializeGeneratedMetadata<T>(string json, string context)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _generatedMetadataOptions);
        }
        catch (JsonException exception)
        {
            throw Diagnostics.Error(
                Diagnostics.MalformedGeneratedMetadata,
                $"{context} isn't valid JSON ({exception.Message}). Re-import the OpenAPI document."
            );
        }
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
                throw Diagnostics.Error(
                    Diagnostics.MalformedGeneratedMetadata,
                    "Invalid generated schema metadata in RivetGeneratedSchemaMetadataAttribute."
                );
            }

            int? IntAt(int index) =>
                index < attribute.ConstructorArguments.Length
                && attribute.ConstructorArguments[index].Value is int value
                && value >= 0
                    ? value
                    : null;
            double? DoubleAt(int index) =>
                index < attribute.ConstructorArguments.Length
                && attribute.ConstructorArguments[index].Value is double value
                && !double.IsNaN(value)
                    ? value
                    : null;
            string? StringAt(int index) =>
                index < attribute.ConstructorArguments.Length
                    ? attribute.ConstructorArguments[index].Value as string
                    : null;
            bool BoolAt(int index) =>
                index < attribute.ConstructorArguments.Length
                && attribute.ConstructorArguments[index].Value is true;

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
                UniqueItems: BoolAt(16) ? true : null,
                MinProperties: IntAt(29),
                MaxProperties: IntAt(30)
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
                    ? DeserializeGeneratedMetadata<List<string>>(
                        requiredJson,
                        $"RivetGeneratedSchemaMetadataAttribute required list at '{pointer}'"
                    )
                    : null,
                Enum: StringAt(32),
                EnumListsNull: BoolAt(31)
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

    /// <summary>A member as a diagnostic names it: where it's declared, and its C# type.</summary>
    private static string DescribeMember(ISymbol member) =>
        member switch
        {
            IParameterSymbol parameter =>
                $"parameter '{parameter.Name}' (of type '{parameter.Type.ToDisplayString()}') of "
                    + $"'{parameter.ContainingSymbol.ContainingType?.Name}.{parameter.ContainingSymbol.Name}'",
            _ =>
                $"'{member.ContainingType.Name}.{member.Name}' (of type '{GetMemberType(member).ToDisplayString()}')",
        };

    /// <summary>
    /// Lowers a [RivetUnion] wrapper record to an undiscriminated union of its
    /// property types. Nullable wrappers are stripped — every variant property
    /// is optional by construction (only one is ever set), so nullability is a
    /// wrapper artifact, not part of the union's wire shape. Returns null when
    /// the attribute is absent; an attributed record with no usable properties
    /// is refused (RIV1018). Member-level serializer attributes are not checked:
    /// the union converter serializes the bare variant value and never reads them.
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
            throw Diagnostics.Error(
                Diagnostics.RivetUnionNoVariants,
                $"[RivetUnion] on '{name}' has no variant "
                    + "properties, so there is no union to emit. Add one property per variant type, "
                    + "or remove [RivetUnion]."
            );
        }

        return new TsType.Union(variants);
    }

    /// <summary>
    /// Refuses a [JsonPolymorphic] type no host can serve, whether or not it's generic:
    /// an UnknownDerivedTypeHandling fallback or IgnoreUnrecognizedTypeDiscriminators
    /// (RIV1016), and no [JsonDerivedType] registrations at all (RIV1015).
    /// </summary>
    private void RefuseUnservablePolymorphism(INamedTypeSymbol definition, string name)
    {
        var polymorphicAttr = definition.GetAttribute(_types.JsonPolymorphic);
        if (polymorphicAttr is null)
        {
            return;
        }

        // FailSerialization (0) is STJ's default and what the oneOf describes; a fallback
        // writes an unregistered derived type as its base or nearest registered ancestor,
        // a payload the oneOf, admitting only the registered types, rejects.
        if (polymorphicAttr.NamedArgument("UnknownDerivedTypeHandling") is int and not 0)
        {
            throw Diagnostics.Error(
                Diagnostics.PolymorphicUnknownHandling,
                $"[JsonPolymorphic] on '{name}' sets "
                    + "UnknownDerivedTypeHandling to a fallback, so the host writes a derived type it never "
                    + "registered as its base or nearest registered ancestor, and the spec's oneOf, which "
                    + "admits only the registered [JsonDerivedType]s, can't describe that payload. Remove the "
                    + "setting so an unregistered type fails to serialize, and register every type the API "
                    + "sends with [JsonDerivedType]."
            );
        }

        // Ignoring unrecognized discriminators reads an unknown tag as the base itself, a
        // payload the oneOf, admitting only the registered tags, rejects.
        if (polymorphicAttr.NamedArgument("IgnoreUnrecognizedTypeDiscriminators") is true)
        {
            throw Diagnostics.Error(
                Diagnostics.PolymorphicUnknownHandling,
                $"[JsonPolymorphic] on '{name}' sets "
                    + "IgnoreUnrecognizedTypeDiscriminators, so the host reads a body with a tag it never "
                    + "registered as the base type, and the spec's oneOf, which admits only the registered "
                    + "tags, rejects that body. Remove the setting so an unknown tag fails to read, and "
                    + "register every type the API accepts with [JsonDerivedType]."
            );
        }

        // System.Text.Json refuses the type outright once options are frozen ("should
        // specify at least one derived type"), so no host can serve it.
        if (!definition.HasAttribute(_types.JsonDerivedType))
        {
            throw Diagnostics.Error(
                Diagnostics.PolymorphicNoDerivedTypes,
                $"[JsonPolymorphic] on '{name}' has no "
                    + "[JsonDerivedType] registrations, and System.Text.Json refuses to serialize such a "
                    + "type at all. Register each derived type with "
                    + "[JsonDerivedType(typeof(Derived), \"tag\")], or remove [JsonPolymorphic]."
            );
        }
    }

    /// <summary>
    /// Lowers a [JsonPolymorphic]/[JsonDerivedType] base type to a
    /// TaggedUnion whose variants are the [JsonDerivedType] registrations, matching
    /// System.Text.Json's wire semantics when serializing AS the base type: the
    /// discriminator property (default <c>$type</c>) is written first with the
    /// registration's tag, followed by the derived type's full flattened property
    /// surface. The base itself is a variant only if explicitly registered. Returns
    /// null when the symbol carries neither attribute, and callers then take the plain
    /// flattening path. A registration with an int or missing tag, or of a type that
    /// isn't a named class or struct, is refused (RIV1014), as is an authored tag schema
    /// that isn't a JSON object or can't carry its tag (RIV1122).
    /// <see cref="RefuseUnservablePolymorphism"/> has already refused no registrations
    /// (RIV1015) and an UnknownDerivedTypeHandling fallback or
    /// IgnoreUnrecognizedTypeDiscriminators (RIV1016).
    /// </summary>
    private TsType.TaggedUnion? TryBuildPolymorphicUnion(INamedTypeSymbol definition, string name)
    {
        var polymorphicAttr = definition.GetAttribute(_types.JsonPolymorphic);
        var derivedAttrs = definition
            .GetAttributes()
            .Where(attr => attr.Is(_types.JsonDerivedType))
            .ToList();
        if (derivedAttrs.Count == 0)
        {
            return null;
        }

        var discriminator = "$type";
        if (polymorphicAttr?.NamedArgument("TypeDiscriminatorPropertyName") is string custom)
        {
            discriminator = custom;
        }

        var registrations = new List<(INamedTypeSymbol Type, string Tag)>();
        foreach (var attr in derivedAttrs)
        {
            var registered =
                attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value : null;
            if (registered is not INamedTypeSymbol derivedType)
            {
                // System.Text.Json throws on such a registration once options freeze, and
                // the oneOf has no named schema to point at; dropping it would describe a
                // union the host never serves.
                throw Diagnostics.Error(
                    Diagnostics.PolymorphicNonStringTag,
                    $"[JsonDerivedType] on '{name}' registers "
                        + $"'{(registered as ITypeSymbol)?.ToDisplayString() ?? "nothing"}', which isn't a named "
                        + "class, struct or record, so the oneOf has no variant schema to give it. Register "
                        + "only types that derive from the base, as in "
                        + "[JsonDerivedType(typeof(Derived), \"tag\")]."
                );
            }

            var tagValue =
                attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value : null;
            if (tagValue is not string tag)
            {
                // Do NOT stringify int tags: a spec validating string tags against an
                // int wire value would be a lie, and flattening the base describes a
                // payload System.Text.Json never writes. A tagless registration is
                // written with no discriminator at all and cannot be read back as the base.
                throw Diagnostics.Error(
                    Diagnostics.PolymorphicNonStringTag,
                    $"[JsonDerivedType] on '{name}' registers "
                        + $"'{derivedType.ToDisplayString()}' with "
                        + $"{(tagValue is null ? "no" : "a non-string")} discriminator tag, which a "
                        + "string-discriminated oneOf cannot represent. Give every registration a string tag, "
                        + $"as in [JsonDerivedType(typeof({derivedType.Name}), \"{Naming.ToCamelCase(derivedType.Name)}\")]."
                );
            }

            registrations.Add((derivedType, tag));
        }

        var variants = new List<TsType.TaggedUnionVariant>();
        foreach (var (derivedType, tag) in registrations)
        {
            RefuseUnmodelledTypeConverter(derivedType);
            RefuseAmbiguousJsonConstructor(derivedType);

            // The discriminator property first: a required single-member StringUnion (the
            // emitter writes the authored tag schema instead when the variant's metadata
            // carries one). Then the derived type's members, each described exactly as its
            // own record describes it; a member STJ only reads or only writes stays in the
            // shared variant shape as writeOnly or readOnly.
            var properties = new List<TsPropertyDefinition>
            {
                new(discriminator, new TsType.StringUnion([tag])),
            };
            foreach (var member in GetEffectiveProperties(derivedType))
            {
                if (
                    BuildPropertyDefinition(member, derivedType, $"{name}.{tag}.{member.Name}") is
                    { } property
                )
                {
                    properties.Add(property);
                }
            }

            variants.Add(
                new TsType.TaggedUnionVariant(
                    tag,
                    new TsType.InlineObject(properties, ReadAdditionalProperties(derivedType)),
                    GetTypeMetadata(derivedType),
                    GetTypeDescription(derivedType),
                    ReadGeneratedSchemaMetadata(derivedType).GetValueOrDefault(""),
                    ReadAuthoredTagSchema(derivedType, tag)
                )
            );
        }

        return new TsType.TaggedUnion(discriminator, variants);
    }

    /// <summary>
    /// The tag property's schema the source authored for <paramref name="derivedType"/>
    /// ([RivetGeneratedType]'s <c>DiscriminatorSchemaJson</c>), checked against the tag it is
    /// registered with: a schema that can't carry that tag would make the spec refuse the
    /// payload System.Text.Json writes.
    /// </summary>
    private string? ReadAuthoredTagSchema(INamedTypeSymbol derivedType, string tag)
    {
        if (
            derivedType
                .GetAttribute(_types.RivetGeneratedType)
                ?.NamedArgument("DiscriminatorSchemaJson")
            is not string json
        )
        {
            return null;
        }

        if (TsType.TaggedUnionVariant.TagSchemaProblem(json, tag) is { } problem)
        {
            throw Diagnostics.Error(
                Diagnostics.MalformedGeneratedMetadata,
                "[RivetGeneratedType] on "
                    + $"'{derivedType.ToDisplayString()}' sets DiscriminatorSchemaJson to '{json}', which "
                    + $"{problem}, so it can't be the schema of the tag '{tag}' the type is registered with. "
                    + "Re-import the OpenAPI document, or correct the JSON to a schema object that admits the tag."
            );
        }

        return json;
    }

    /// <summary>
    /// Refuses an authored tag schema on a type no base (nor the type itself) registers with
    /// [JsonDerivedType]: only a variant has a tag property for it to describe, so anywhere
    /// else it would be dropped without a word.
    /// </summary>
    private void RefuseUnregisteredAuthoredTagSchema(INamedTypeSymbol definition)
    {
        if (
            definition
                .GetAttribute(_types.RivetGeneratedType)
                ?.NamedArgument("DiscriminatorSchemaJson")
            is null
        )
        {
            return;
        }

        for (var owner = definition; owner is not null; owner = owner.BaseType)
        {
            if (
                owner
                    .GetAttributes()
                    .Any(attr =>
                        attr.Is(_types.JsonDerivedType)
                        && attr.ConstructorArguments.Length > 0
                        && SymbolEqualityComparer.Default.Equals(
                            (
                                attr.ConstructorArguments[0].Value as INamedTypeSymbol
                            )?.OriginalDefinition,
                            definition.OriginalDefinition
                        )
                    )
            )
            {
                return;
            }
        }

        throw Diagnostics.Error(
            Diagnostics.MalformedGeneratedMetadata,
            "[RivetGeneratedType] on "
                + $"'{definition.ToDisplayString()}' sets DiscriminatorSchemaJson, but no base registers "
                + "the type with [JsonDerivedType], so there is no tag property for the schema to describe. "
                + "Re-import the OpenAPI document, or remove DiscriminatorSchemaJson."
        );
    }

    /// <summary>
    /// Returns the emitted schema name for a type: its simple name. Keyed internally by
    /// fully-qualified name so the same type always reuses its name. Schema names are
    /// global, so two distinct types sharing a simple name (different namespaces,
    /// nesting, or generic arity) abort generation (RIV1007) rather than one of them
    /// being renamed by discovery order.
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
        var display = definition.ToDisplayString();
        var annotatesBrand =
            _generatedBrandSchemaNames.Remove(name) && definition.HasAttribute(_types.RivetScalar);
        if (!annotatesBrand && _claimedNames.TryGetValue(name, out var claimant))
        {
            throw Diagnostics.Error(
                Diagnostics.TypeNameCollision,
                claimant.StartsWith(GeneratedClaimant, StringComparison.Ordinal)
                    ? $"{claimant} and '{display}' would both emit the schema name '{name}', and "
                        + "schema names are global: the type would replace the generated schema. Rename "
                        + "the type, or re-import the OpenAPI document so the two get distinct names."
                    : $"'{claimant}' and '{display}' would both emit "
                        + $"the schema name '{name}', and schema names are global. Rename one of the two "
                        + "types so every type the contract reaches has a unique simple name."
            );
        }

        _claimedNames[name] = display;
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
        // Must run before the type-parameter check so Wrapper<T>(T? Value)
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
            // byte[] is a base64 string (BuiltInScalars.ByteArray). File-endpoint byte[]
            // outputs never reach here: ContractWalker intercepts them.
            if (arrayType.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return BuiltInScalars.ByteArray.Primitive;
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
                // Non-string keys carry their contract
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
                // Full-namespace keyed naming: colliding enum names abort generation
                // (RIV1007) instead of first-wins TryAdd
                var enumName = GetEmittedName(namedType);
                if (!_enums.ContainsKey(enumName))
                {
                    var fields = namedType
                        .GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => f.HasConstantValue)
                        .ToList();
                    var (isStringEnum, enumNamingPolicy) = GetEnumWireShape(_types, namedType);
                    IReadOnlyList<string>? stringMembers = null;
                    if (isStringEnum)
                    {
                        if (
                            namedType.HasAttribute(_types.Flags)
                            || fields.Select(f => EnumLiteral(f.ConstantValue)).Distinct().Count()
                                != fields.Count
                        )
                        {
                            throw Diagnostics.Error(
                                Diagnostics.UnsupportedEnumConverter,
                                $"string enum '{namedType.Name}' contains flags or aliased values. Use distinct values without Flags, or a numeric enum."
                            );
                        }
                        var candidates = fields
                            .Select(f => EnumWireValue(_types, f, enumNamingPolicy))
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
                                    .Where(f =>
                                        EnumWireValue(_types, f, enumNamingPolicy) == colliding
                                    )
                                    .Select(f => f.Name)
                            );
                            throw Diagnostics.Error(
                                Diagnostics.EnumWireValueCollision,
                                $"enum '{namedType.Name}' members "
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
                    RefuseNumberHandlingThroughTypeArguments(namedType, context);
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
            var properties = tupleType
                .TupleElements.Select(e => new TsPropertyDefinition(
                    Naming.ToCamelCase(e.Name),
                    MapTypeCore(e.Type, context)
                ))
                .ToList();
            return new TsType.InlineObject(properties);
        }

        // BigInteger: System.Text.Json has no converter for it, so it serializes the
        // struct's public properties ({"isZero":…,"sign":…}) and cannot read it back.
        // No schema describes that honestly, so refuse.
        if (SymbolEqualityComparer.Default.Equals(symbol, _bigIntegerType))
        {
            throw Diagnostics.Error(
                Diagnostics.UnsupportedBigInteger,
                $"'{symbol.ToDisplayString()}'{AtContext(context)} "
                    + "has no System.Text.Json converter: it serializes as an object of its IsZero/Sign/... "
                    + "properties and cannot be read back. Expose the value as a string of digits, or as "
                    + "long or decimal when the range allows."
            );
        }

        // Fallback: a type with no schema mapping. The emitter refuses it (RIV2005) by name.
        return new TsType.Primitive("unknown", CSharpType: symbol.ToDisplayString());
    }

    /// <summary>
    /// The string wire value of each member of a string-wire enum, in declaration order,
    /// derived exactly as the emitted string union is; null for a numeric-wire enum or a
    /// converter Rivet cannot represent. Generation refuses that converter (RIV1108) when it
    /// walks the enum; a caller inspecting enums generation may never walk must not abort.
    /// </summary>
    internal static IReadOnlyList<(IFieldSymbol Member, string WireValue)>? StringEnumWireValues(
        WellKnownTypes types,
        INamedTypeSymbol enumType
    )
    {
        var (isString, policy, unsupported) = ReadEnumWireShape(types, enumType);
        return isString && unsupported is null
            ? enumType
                .GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue)
                .Select(f => (f, EnumWireValue(types, f, policy)))
                .ToList()
            : null;
    }

    /// <summary>
    /// The wire value of one enum member: an explicit
    /// [JsonStringEnumMemberName("original")] wins; otherwise the casing the
    /// declared converter names applies; without either, the wire value is the
    /// exact C# member name — JsonStringEnumConverter with no naming policy
    /// writes the CLR name verbatim, so Rivet must not camel-case it
    /// (acceptance:string-enum-preserves-member-name).
    /// </summary>
    private static string EnumWireValue(
        WellKnownTypes types,
        IFieldSymbol field,
        RivetNamingPolicy? policy
    ) =>
        field.GetAttribute(types.JsonStringEnumMemberName).StringArgument()
        ?? (policy is null ? field.Name : Naming.ToPolicyCase(field.Name, policy.Value));

    /// <summary>
    /// The string-wire shape an enum declares through its type-level
    /// [JsonConverter]: whether the converter writes strings at all, and the
    /// casing convention its name declares. The converter type is the single
    /// declared fact — the built-in JsonStringEnumConverter keeps CLR names
    /// verbatim (no policy), and each Rivet*EnumConverter family member carries
    /// its casing in the class name (attribute args cannot hold a naming
    /// policy). Null when the enum is numeric-wire. Throws RIV1108 for a converter
    /// Rivet cannot represent.
    /// </summary>
    private static (bool IsString, RivetNamingPolicy? Policy) GetEnumWireShape(
        WellKnownTypes types,
        INamedTypeSymbol type
    )
    {
        var (isString, policy, unsupported) = ReadEnumWireShape(types, type);
        return unsupported is null
            ? (isString, policy)
            : throw Diagnostics.Error(Diagnostics.UnsupportedEnumConverter, unsupported);
    }

    /// <summary>
    /// <see cref="GetEnumWireShape"/> without the throw: <c>Unsupported</c> carries why the
    /// converter cannot be represented, or is null when it can.
    /// </summary>
    private static (
        bool IsString,
        RivetNamingPolicy? Policy,
        string? Unsupported
    ) ReadEnumWireShape(WellKnownTypes types, INamedTypeSymbol type)
    {
        var converters = type.GetAttributes()
            .Where(a => IsJsonConverterAttribute(types, a.AttributeClass))
            .ToArray();
        if (converters.Length == 0)
        {
            return (false, null, null);
        }
        if (
            converters.Length != 1
            || !converters[0].Is(types.JsonConverter)
            || converters[0].ConstructorArguments is not [{ Value: INamedTypeSymbol converterType }]
        )
        {
            return (
                false,
                null,
                $"enum '{type.Name}' must use one explicit [JsonConverter(typeof(...))] declaration; custom converter attributes are unsupported."
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
            return (false, null, $"converter for enum '{type.Name}' must target that enum.");
        }
        return (converterType.ContainingNamespace.ToDisplayString(), converterType.Name) switch
        {
            ("System.Text.Json.Serialization", "JsonStringEnumConverter") => (true, null, null),
            ("System.Text.Json.Serialization", "JsonNumberEnumConverter") => (false, null, null),
            ("Rivet", "RivetLowerCaseEnumConverter") => (true, RivetNamingPolicy.LowerCase, null),
            ("Rivet", "RivetCamelCaseEnumConverter") => (true, RivetNamingPolicy.CamelCase, null),
            ("Rivet", "RivetSnakeCaseEnumConverter") => (true, RivetNamingPolicy.SnakeCase, null),
            ("Rivet", "RivetKebabCaseEnumConverter") => (true, RivetNamingPolicy.KebabCase, null),
            _ => (
                false,
                null,
                $"enum '{type.Name}' uses unsupported converter '{converterType.ToDisplayString()}'. Use a built-in enum converter or a Rivet enum policy converter."
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
    /// Maps a dictionary key type to its contract
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
    /// parts.
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

    private static readonly Dictionary<SpecialType, TsType.Primitive> _keywordTypes =
        BuiltInScalars.KeywordTypes.ToDictionary(row => row.Type, row => row.Primitive);

    private TsType.Primitive? MapPrimitive(INamedTypeSymbol symbol)
    {
        if (_keywordTypes.TryGetValue(symbol.SpecialType, out var keyword))
        {
            return keyword;
        }

        // Non-SpecialType primitives — resolved via dictionary lookup instead of per-field null checks
        if (_scalarTypes.TryGetValue(symbol, out var scalar))
        {
            return scalar;
        }

        return null;
    }

    private static bool IsJsonConverterAttribute(WellKnownTypes types, INamedTypeSymbol? type) =>
        type.DerivesFrom(types.JsonConverter);

    // The JsonNumberHandling bits that put strings on a number's wire. AllowReadingFromString
    // is deliberately absent: it only widens what the host reads, numbers are still written
    // as numbers, so every value the spec advertises still round-trips.
    private const JsonNumberHandling WireChangingNumberHandling =
        JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowNamedFloatingPointLiterals;

    /// <summary>
    /// Refuses a type-level [JsonConverter] Rivet cannot model (RIV1111): anything but the
    /// [RivetScalar]/[RivetUnion] converters, which the walker lowers itself. Enum converters
    /// are judged by <see cref="ReadEnumWireShape"/> (RIV1108).
    /// </summary>
    private void RefuseUnmodelledTypeConverter(INamedTypeSymbol type)
    {
        var converter = type.GetAttributes()
            .FirstOrDefault(a =>
                IsJsonConverterAttribute(_types, a.AttributeClass)
                && !a.Is(_types.RivetScalar)
                && !a.Is(_types.RivetUnion)
            );
        if (converter is not null)
        {
            throw Diagnostics.Error(
                Diagnostics.UnmodelledJsonConverter,
                $"type '{type.ToDisplayString()}' carries "
                    + $"{DescribeConverter(converter)}, and Rivet cannot see what that converter writes, so "
                    + "the spec would describe the CLR shape instead of the wire. Make the type a "
                    + "[RivetScalar] wrapper (one Value property) or a [RivetUnion], or expose a DTO "
                    + "whose properties are the wire shape."
            );
        }
    }

    /// <summary>
    /// Refuses member-level serializer settings that change the wire in ways the walker
    /// does not model: a property/field [JsonConverter] (RIV1111), and an effective
    /// [JsonNumberHandling] — the member's own, else the walked type's, exactly as
    /// System.Text.Json resolves it — that writes strings for a number or a collection of
    /// numbers (RIV1112). [RivetSchemaType] states the wire and lifts both refusals, but only
    /// where it takes effect: on a primitive leaf.
    /// </summary>
    private void RefuseUnmodelledMemberSerialization(
        ISymbol member,
        INamedTypeSymbol walkedType,
        TsType mapped,
        string context
    )
    {
        if (
            member.HasAttribute(_types.RivetSchemaType)
            && mapped is TsType.Primitive or TsType.Nullable { Inner: TsType.Primitive }
        )
        {
            return;
        }

        var converter = member
            .GetAttributes()
            .FirstOrDefault(a => IsJsonConverterAttribute(_types, a.AttributeClass));
        if (converter is not null)
        {
            throw Diagnostics.Error(
                Diagnostics.UnmodelledJsonConverter,
                $"'{context}' carries "
                    + $"{DescribeConverter(converter)}, and Rivet cannot see what that converter writes, so "
                    + "the spec would describe the CLR type instead of the wire. On a primitive-typed "
                    + "property, pin the wire with [RivetSchemaType(\"string\")] (or whichever JSON type it "
                    + "writes); otherwise make the value a [RivetScalar] wrapper, or for an enum put a "
                    + "supported converter on the enum type itself."
            );
        }

        if (StringsForNumbers(member, walkedType, GetMemberType(member)) is { } strings)
        {
            throw Diagnostics.Error(
                Diagnostics.UnmodelledNumberHandling,
                $"'{context}' is serialized with "
                    + $"{strings}, or pin a primitive-typed property with [RivetSchemaType(\"string\")]."
            );
        }
    }

    /// <summary>
    /// RIV1112's finding for a member of <paramref name="memberType"/>: the wire-changing
    /// [JsonNumberHandling] that reaches it from the member or from <paramref name="owner"/>,
    /// described up to the fix's alternatives, or null when none does or the member carries no
    /// number.
    /// </summary>
    private string? StringsForNumbers(ISymbol member, ITypeSymbol owner, ITypeSymbol memberType)
    {
        var (handling, source) =
            ReadNumberHandling(member) is { } own ? (own, "the member")
            : ReadNumberHandling(owner) is { } inherited
                ? (inherited, $"type '{owner.ToDisplayString()}'")
            : ((JsonNumberHandling?)null, "");
        return
            handling is { } wire
            && (wire & WireChangingNumberHandling) != 0
            && IsNumberOrNumberCollection(memberType)
            ? $"JsonNumberHandling.{wire} (from [JsonNumberHandling] on {source}), which puts strings "
                + "on the wire where the spec says number. Remove it (AllowReadingFromString alone is "
                + "fine: it only widens what the host reads)"
            : null;
    }

    /// <summary>
    /// RIV1112 for generics. The open definition is one schema with T where the value goes,
    /// so wire-changing [JsonNumberHandling] on a T-typed member only puts strings on the
    /// wire once an instantiation binds T to a number (Wrapper&lt;int&gt;, not
    /// Wrapper&lt;string&gt;). Each closed instantiation the walker maps is judged on its
    /// substituted member types, following the substitution into nested generics
    /// (Outer&lt;int&gt; holding Inner&lt;int&gt;) and collections.
    /// </summary>
    private void RefuseNumberHandlingThroughTypeArguments(ITypeSymbol type, string? context)
    {
        switch (type)
        {
            case INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } nullable:
                RefuseNumberHandlingThroughTypeArguments(nullable.TypeArguments[0], context);
                return;
            case IArrayTypeSymbol array:
                RefuseNumberHandlingThroughTypeArguments(array.ElementType, context);
                return;
            case INamedTypeSymbol { TypeArguments.Length: 1 } collection
                when IsCollectionType(collection):
                RefuseNumberHandlingThroughTypeArguments(collection.TypeArguments[0], context);
                return;
            case INamedTypeSymbol { TypeArguments.Length: 2 } dictionary
                when IsDictionaryType(dictionary):
                RefuseNumberHandlingThroughTypeArguments(dictionary.TypeArguments[1], context);
                return;
            case INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } closed
                when _numberHandlingCheckedInstantiations.Add(closed):
                break;
            default:
                return;
        }

        var instantiation = (INamedTypeSymbol)type;
        var definition = instantiation.OriginalDefinition;
        foreach (var member in GetEffectiveProperties(instantiation))
        {
            var declared = member.OriginalDefinition;
            var memberType = GetMemberType(member);
            // A member the type arguments don't reach was judged on the open definition.
            if (SymbolEqualityComparer.Default.Equals(memberType, GetMemberType(declared)))
            {
                continue;
            }

            if (!IsSchemaMember(declared, definition))
            {
                continue;
            }

            if (StringsForNumbers(declared, definition, memberType) is { } strings)
            {
                var site = context is null ? "" : $"'{context}' uses ";
                throw Diagnostics.Error(
                    Diagnostics.UnmodelledNumberHandling,
                    $"{site}'{instantiation.ToDisplayString()}', "
                        + $"which binds '{definition.ToDisplayString()}.{declared.Name}' to "
                        + $"'{memberType.ToDisplayString()}'. That member is serialized with {strings}, "
                        + "or instantiate the type with a non-number type argument."
                );
            }

            RefuseNumberHandlingThroughTypeArguments(memberType, context);
        }
    }

    private JsonNumberHandling? ReadNumberHandling(ISymbol symbol) =>
        symbol.GetAttribute(_types.JsonNumberHandling)?.ConstructorArguments is [{ Value: int raw }]
            ? (JsonNumberHandling)raw
            : null;

    private static string DescribeConverter(AttributeData attribute) =>
        attribute.ConstructorArguments is [{ Value: INamedTypeSymbol converterType }]
            ? $"[JsonConverter(typeof({converterType.ToDisplayString()}))]"
            : $"[{attribute.AttributeClass?.ToDisplayString()}]";

    /// <summary>
    /// The shapes System.Text.Json applies number handling to: a number (Int128, UInt128
    /// and Half included), or a collection/dictionary whose values are numbers (through
    /// Nullable). byte[] is a base64 string and is excluded. A type parameter is not a
    /// number: each closed instantiation is judged on what it binds T to
    /// (<see cref="RefuseNumberHandlingThroughTypeArguments"/>).
    /// </summary>
    private bool IsNumberOrNumberCollection(ITypeSymbol type) =>
        type switch
        {
            INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } nullable => IsNumberOrNumberCollection(nullable.TypeArguments[0]),
            IArrayTypeSymbol array => array.ElementType.SpecialType != SpecialType.System_Byte
                && IsNumberOrNumberCollection(array.ElementType),
            INamedTypeSymbol { TypeArguments.Length: 1 } named when IsCollectionType(named) =>
                IsNumberOrNumberCollection(named.TypeArguments[0]),
            INamedTypeSymbol { TypeArguments.Length: 2 } named when IsDictionaryType(named) =>
                IsNumberOrNumberCollection(named.TypeArguments[1]),
            _ => type.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_Double
                || type is INamedTypeSymbol named && _extraNumberTypes.Contains(named),
        };

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
                    || IsJsonConverterAttribute(_types, a.AttributeClass)
                )
            || symbol.GetAttributes().Count(a => IsJsonConverterAttribute(_types, a.AttributeClass))
                != 1
            || !symbol.InstanceConstructors.Any(c =>
                c.DeclaredAccessibility == Accessibility.Public
                && c.Parameters.Length == 1
                && c.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, props[0].Type)
            )
        )
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidRivetScalarShape,
                "[RivetScalar] type "
                    + $"'{symbol.ToDisplayString()}' must be a non-generic class/struct/record "
                    + "with exactly one public readable non-indexer property "
                    + "named 'Value' and a public constructor accepting its type. Inheritance, competing converters and property-level JSON settings are unsupported."
            );
        }

        return props[0].Type;
    }

    /// <summary>
    /// A bodyless-method input can only lower to
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
                    // The (Type, string, string) overload puts an ITypeSymbol in arg 0 —
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
    /// Converts a [Range] constructor argument to double. Strings parse with
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
    /// The wire header name of a [RivetHeader] property, or null when the
    /// property is not header-bound. The attribute's name argument keeps the original
    /// casing ("Notion-Version"); without one the property name itself is the header name.
    /// </summary>
    public string? GetHeaderName(IPropertySymbol prop) =>
        prop.GetAttribute(_types.RivetHeader) is { } attr
            ? attr.StringArgument() ?? prop.Name
            : null;

    /// <summary>
    /// Whether a property bound as a query, header or form parameter is optional. This
    /// follows binder semantics, not the JSON wire: [RivetOptional], or a nullable type
    /// (an absent value binds as null). Object-schema properties use
    /// <see cref="CanOmitOnWire"/>.
    /// </summary>
    public bool IsOptional(ISymbol member) =>
        member.HasAttribute(_types.RivetOptional)
        || GetMemberType(member).NullableAnnotation == NullableAnnotation.Annotated;

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
