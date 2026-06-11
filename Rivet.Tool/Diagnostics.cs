namespace Rivet.Tool;

/// <summary>
/// Stable diagnostic IDs for every warning Rivet writes to stderr.
/// Canonical format: <c>warning RIV1001: &lt;message&gt;</c> (MSBuild-ish, machine-parseable;
/// the leading "warning " token is the suite's long-standing grep convention).
///
/// ID ranges by pipeline stage:
///   RIV1xxx — extraction (Roslyn walkers, Rivet.Tool/Analysis)
///   RIV2xxx — emission   (OpenAPI emitter + contract JSON reader, Rivet.Tool/Emit)
///   RIV3xxx — import     (--from-openapi scaffold, Rivet.Tool/Import)
///   RIV4xxx — coverage   (--check, CoverageChecker)
///
/// The prefix is RIV, deliberately not "RV" — the sibling tool plumb owns the
/// RV-xxx rule namespace.
///
/// IDs are observability, not severity reform: every entry is a warning and the
/// exit-code policy is unchanged. Every ID must have a row in
/// docs/reference/diagnostics.md — DiagnosticsTests cross-checks the registry
/// against that page in both directions.
/// </summary>
public static class Diagnostics
{
    // ----- RIV1xxx: extraction -----
    public const string EndpointFieldNotStaticReadonly = "RIV1001";
    public const string ContractExampleUndeclaredStatus = "RIV1002";
    public const string RouteBoundJsonPropertyNameIgnored = "RIV1003";
    public const string ControllerExampleUndeclaredStatus = "RIV1004";
    // RIV1005 (FromHeaderParameterExcluded) retired in P2 wave 5: [FromHeader] params now
    // map to ParamSource.Header instead of being excluded. The number is never reused.
    public const string UnmappedTypedResult = "RIV1006";
    public const string TypeNameCollision = "RIV1007";
    public const string UnparseableRangeBound = "RIV1008";
    public const string UnsupportedTimeSpan = "RIV1009";
    public const string UnsupportedBigInteger = "RIV1010";
    public const string UnsupportedChar = "RIV1011";
    public const string UnsupportedObject = "RIV1012";
    public const string DictionaryKeyTypeDropped = "RIV1013";
    public const string PolymorphicNonStringTag = "RIV1014";
    public const string PolymorphicNoDerivedTypes = "RIV1015";
    public const string PolymorphicUnknownHandlingDropped = "RIV1016";
    public const string ResponseHeaderUndeclaredStatus = "RIV1017";

    // ----- RIV2xxx: emission -----
    public const string TaggedUnionComponentCollision = "RIV2001";
    public const string UndefinedSecurityScheme = "RIV2002";
    public const string DuplicateEndpoint = "RIV2003";
    public const string MultipartInputTypeMissing = "RIV2004";
    public const string UnknownTypeUntypedSchema = "RIV2005";
    public const string UnresolvedTypeParameter = "RIV2006";
    public const string GenericTemplateMissing = "RIV2007";
    public const string BrandConflictingUnderlyingTypes = "RIV2008";
    public const string ReservedHeaderParameterSkipped = "RIV2009";

    // ----- RIV3xxx: import -----
    public const string ImportAliasCycleBroken = "RIV3001";
    public const string ImportSecuritySchemesDropped = "RIV3002";
    public const string ImportOperationMethodDropped = "RIV3003";
    public const string ImportAdditionalPropertiesDropped = "RIV3004";
    public const string ImportDiscriminatorDropped = "RIV3005";
    public const string ImportAliasTargetMissing = "RIV3006";
    public const string ImportAliasRefCycle = "RIV3007";
    public const string ImportUnresolvableAliasReference = "RIV3008";
    public const string ImportUnresolvedSchema = "RIV3009";
    public const string ImportUnsupportedSchemaType = "RIV3010";
    public const string ImportArrayMissingItems = "RIV3011";
    public const string ImportEnumConstraintDropped = "RIV3012";
    public const string ImportDeclaredPropertiesDropped = "RIV3013";
    public const string ImportDictionaryKeyDropped = "RIV3014";

    // ----- RIV4xxx: coverage -----
    public const string CoverageMissingImplementation = "RIV4001";
    public const string CoverageHttpMethodMismatch = "RIV4002";
    public const string CoverageRouteMismatch = "RIV4003";

    /// <summary>
    /// Registry of every diagnostic ID with a one-line trigger description.
    /// DiagnosticsTests cross-checks this against docs/reference/diagnostics.md
    /// so neither the registry nor the doc page can rot.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Registry = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EndpointFieldNotStaticReadonly] = "Contract endpoint field is not 'static readonly' — it may not be read correctly at generation time.",
        [ContractExampleUndeclaredStatus] = "Response example targets a status code the contract endpoint does not declare — the example is ignored.",
        [RouteBoundJsonPropertyNameIgnored] = "[JsonPropertyName] on a route-bound property is ignored for route interpolation — the contract param keeps the route name.",
        [ControllerExampleUndeclaredStatus] = "Response example targets a status code the controller endpoint does not declare — the example is ignored.",
        [UnmappedTypedResult] = "Unmapped typed result branch in Results<...> — the response branch is omitted from the contract.",
        [TypeNameCollision] = "Two walked types share a simple name — the later type is emitted under a disambiguated name.",
        [UnparseableRangeBound] = "[Range] bound could not be parsed — the range constraint is skipped.",
        [UnsupportedTimeSpan] = "TimeSpan has no schema mapping — emitted as an untyped (empty) schema.",
        [UnsupportedBigInteger] = "BigInteger has no schema mapping — emitted as an untyped (empty) schema.",
        [UnsupportedChar] = "char has no schema mapping — emitted as an untyped (empty) schema.",
        [UnsupportedObject] = "object has no schema mapping — emitted as an untyped (empty) schema.",
        [DictionaryKeyTypeDropped] = "Dictionary key type has no contract representation (supported: string, enums, string-backed brands, string-serializable primitives) — keys are emitted as unconstrained strings.",
        [PolymorphicNonStringTag] = "[JsonDerivedType] registration with a non-string (int or absent) discriminator tag — a string-discriminated oneOf cannot represent it; the base type falls back to plain property flattening.",
        [PolymorphicNoDerivedTypes] = "[JsonPolymorphic] base type has no [JsonDerivedType] registrations — there is no variant set to emit; the type falls back to plain property flattening.",
        [PolymorphicUnknownHandlingDropped] = "[JsonPolymorphic] UnknownDerivedTypeHandling has no spec representation — the emitted oneOf admits only the registered derived types.",
        [ResponseHeaderUndeclaredStatus] = ".WithResponseHeader() targets a status code the contract endpoint does not declare — the header is ignored.",
        [TaggedUnionComponentCollision] = "Synthesized tagged-union variant component collides with an existing schema — the existing schema wins.",
        [UndefinedSecurityScheme] = "Endpoint references a security scheme with no definition — a default bearer securityScheme component is emitted.",
        [DuplicateEndpoint] = "Two endpoints share an HTTP method + path — the later definition wins.",
        [MultipartInputTypeMissing] = "Multipart input type is absent from the contract's type definitions — the request schema is built inline from the endpoint's params.",
        [UnknownTypeUntypedSchema] = "'unknown' type (JsonElement/JsonNode or an unmapped C# type) in the OpenAPI schema — emitted as untyped.",
        [UnresolvedTypeParameter] = "Unresolved generic type parameter in the OpenAPI schema — emitted as object.",
        [GenericTemplateMissing] = "Generic template is absent from the contract's type definitions — a free-form object schema is emitted for the instantiation.",
        [BrandConflictingUnderlyingTypes] = "Brand declared with conflicting underlying types — the first declaration wins.",
        [ReservedHeaderParameterSkipped] = "Header parameter named Accept, Content-Type or Authorization — OpenAPI forbids these as header parameters; the parameter is omitted from the spec.",
        [ImportAliasCycleBroken] = "Alias schema is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object.",
        [ImportSecuritySchemesDropped] = "Document declares multiple security schemes — only the first is imported; alternatives and scopes are not represented.",
        [ImportOperationMethodDropped] = "HEAD/OPTIONS/TRACE operation dropped — the HTTP method has no contract representation.",
        [ImportAdditionalPropertiesDropped] = "Named schema declares both 'properties' and 'additionalProperties' — imported as a record; extra members are not represented.",
        [ImportDiscriminatorDropped] = "Discriminator with no reversible polymorphic shape (plain object without oneOf, or oneOf whose mapping is absent/unusable) — imported without dispatch semantics.",
        [ImportAliasTargetMissing] = "Alias schema references a missing schema — consumers fall back to JsonElement.",
        [ImportAliasRefCycle] = "Alias schema is part of a $ref cycle — consumers fall back to JsonElement.",
        [ImportUnresolvableAliasReference] = "Reference to an unresolvable alias schema (cycle or missing target) — using JsonElement.",
        [ImportUnresolvedSchema] = "Schema could not be resolved to a C# type — mapped to JsonElement.",
        [ImportUnsupportedSchemaType] = "Unhandled JSON Schema 'type' — mapped to JsonElement.",
        [ImportArrayMissingItems] = "Array schema without 'items' — mapped to List<JsonElement>.",
        [ImportEnumConstraintDropped] = "Enum constraint that cannot become a C# enum (single-value, mixed/float, out-of-int32-range) — degraded to a primitive.",
        [ImportDeclaredPropertiesDropped] = "Inline schema declares both 'properties' and 'additionalProperties' — imported as a dictionary; the declared properties are not represented.",
        [ImportDictionaryKeyDropped] = "Dictionary 'propertyNames' schema has no C# dictionary-key representation — imported with string keys.",
        [CoverageMissingImplementation] = "Contract endpoint has no matching implementation.",
        [CoverageHttpMethodMismatch] = "Implementation HTTP method differs from the contract's.",
        [CoverageRouteMismatch] = "Implementation route differs from the contract's.",
    };

    /// <summary>
    /// Writes the canonical machine-parseable warning line to stderr:
    /// <c>warning RIV1001: message</c>.
    /// </summary>
    public static void Warn(string id, string message)
        => Console.Error.WriteLine($"warning {id}: {message}");

    /// <summary>
    /// Prefixes a collected warning string (e.g. ImportResult.Warnings) with its ID:
    /// <c>RIV3001: message</c>. Program.cs prepends "warning " when printing,
    /// producing the same canonical stderr line as <see cref="Warn"/>.
    /// </summary>
    public static string Prefix(string id, string message)
        => $"{id}: {message}";
}
