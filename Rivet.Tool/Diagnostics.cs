namespace Rivet.Tool;

/// <summary>
/// Stable diagnostic IDs for warnings and fatal errors Rivet writes to stderr.
/// Canonical formats are <c>warning RIV1001: &lt;message&gt;</c> and
/// <c>error RIV2002: &lt;message&gt;</c> (MSBuild-ish and machine-parseable).
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
/// Most entries are non-fatal warnings. Fatal error diagnostics abort the operation
/// and return a non-zero exit code. Every ID must have a row in
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

    // RIV1011 (UnsupportedChar) retired in P2 wave 6: char now maps to a length-1 string
    // schema (the System.Text.Json wire shape) with x-rivet-csharp-type. The number is
    // never reused.
    // RIV1012 (UnsupportedObject) retired in P2 wave 6: object/object? now map to the
    // untyped (empty) schema deliberately and silently — "any JSON value" is exactly
    // what the type declares. The number is never reused.
    public const string DictionaryKeyTypeDropped = "RIV1013";
    public const string PolymorphicNonStringTag = "RIV1014";
    public const string PolymorphicNoDerivedTypes = "RIV1015";
    public const string PolymorphicUnknownHandlingDropped = "RIV1016";
    public const string ResponseHeaderUndeclaredStatus = "RIV1017";
    public const string RivetUnionNoVariants = "RIV1018";
    public const string RouteTokenWithoutInputProperty = "RIV1019";
    public const string InputTypeNotParamLowerable = "RIV1020";
    public const string DuplicateResponseStatus = "RIV1021";
    public const string InvalidRequestBodyProvenance = "RIV1022";
    public const string ImportedSchemaProvenanceConflict = "RIV1023";

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
    public const string DuplicateResponseStatusInIr = "RIV2010";
    public const string DuplicateSecuritySchemeDefinition = "RIV2011";

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
    public const string ImportNamedScalarAlgebraUnsupported = "RIV3015";
    public const string ImportEmptyParameterNameDropped = "RIV3020";
    public const string ImportReservedContentTypeHeaderDropped = "RIV3021";
    public const string ImportReservedAuthorizationHeaderDropped = "RIV3022";
    public const string ImportReservedAcceptHeaderDropped = "RIV3023";

    // ----- RIV4xxx: coverage -----
    public const string CoverageMissingImplementation = "RIV4001";
    public const string CoverageHttpMethodMismatch = "RIV4002";
    public const string CoverageRouteMismatch = "RIV4003";
    public const string CoverageOrphanedBinding = "RIV4004";

    /// <summary>
    /// Registry of every diagnostic ID with a one-line trigger description.
    /// DiagnosticsTests cross-checks this against docs/reference/diagnostics.md
    /// so neither the registry nor the doc page can rot.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Registry = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        [EndpointFieldNotStaticReadonly] =
            "Contract endpoint field is not 'static readonly' — it may not be read correctly at generation time.",
        [ContractExampleUndeclaredStatus] =
            "Response example targets a status code the contract endpoint does not declare — the example is ignored.",
        [RouteBoundJsonPropertyNameIgnored] =
            "[JsonPropertyName] on a route-bound property is ignored for route interpolation — the contract param keeps the route name.",
        [ControllerExampleUndeclaredStatus] =
            "Response example targets a status code the controller endpoint does not declare — the example is ignored.",
        [UnmappedTypedResult] =
            "Unmapped typed result branch in Results<...> — the response branch is omitted from the contract.",
        [TypeNameCollision] =
            "Two walked types share a simple name — the later type is emitted under a disambiguated name.",
        [UnparseableRangeBound] =
            "[Range] bound could not be parsed — the range constraint is skipped.",
        [UnsupportedTimeSpan] =
            "TimeSpan has no schema mapping — emitted as an untyped (empty) schema.",
        [UnsupportedBigInteger] =
            "BigInteger has no schema mapping — emitted as an untyped (empty) schema.",
        [DictionaryKeyTypeDropped] =
            "Dictionary key type has no contract representation (supported: string, enums, string-backed brands, string-serializable primitives) — keys are emitted as unconstrained strings.",
        [PolymorphicNonStringTag] =
            "[JsonDerivedType] registration with a non-string (int or absent) discriminator tag — a string-discriminated oneOf cannot represent it; the base type falls back to plain property flattening.",
        [PolymorphicNoDerivedTypes] =
            "[JsonPolymorphic] base type has no [JsonDerivedType] registrations — there is no variant set to emit; the type falls back to plain property flattening.",
        [PolymorphicUnknownHandlingDropped] =
            "[JsonPolymorphic] UnknownDerivedTypeHandling has no spec representation — the emitted oneOf admits only the registered derived types.",
        [ResponseHeaderUndeclaredStatus] =
            ".WithResponseHeader() targets a status code the contract endpoint does not declare — the header is ignored.",
        [RivetUnionNoVariants] =
            "[RivetUnion] wrapper has no variant properties — there is no union to emit; the type falls back to plain property flattening.",
        [RouteTokenWithoutInputProperty] =
            "Route token has no matching property on the endpoint's input type (after normalized matching: case-insensitive, '_'/'-' stripped) — emitted as an untyped string path param.",
        [InputTypeNotParamLowerable] =
            "The input type on a bodyless method (GET/DELETE/.AcceptsBinary) is a dictionary, collection or scalar — it has no property surface to lower to query params, so the input is dropped (route tokens still emit as untyped path params).",
        [DuplicateResponseStatus] =
            "An authored contract declares the same response status more than once; generation fails because the contract cannot execute as declared.",
        [InvalidRequestBodyProvenance] =
            "A [RivetRequestBody] type is not represented independently by the endpoint input type.",
        [ImportedSchemaProvenanceConflict] =
            "Imported generated C# changed while raw schema provenance still targets its original typed shape — extraction fails instead of emitting stale OpenAPI.",
        [TaggedUnionComponentCollision] =
            "Synthesized tagged-union variant component collides with an existing schema — the existing schema wins.",
        [UndefinedSecurityScheme] =
            "Endpoint references a security scheme with no definition — generation fails rather than inventing security semantics.",
        [DuplicateEndpoint] =
            "Two endpoints share an HTTP method + path — the later definition wins.",
        [MultipartInputTypeMissing] =
            "Multipart input type is absent from the contract's type definitions — the request schema is built inline from the endpoint's params.",
        [UnknownTypeUntypedSchema] =
            "'unknown' type (JsonElement/JsonNode or an unmapped C# type) in the OpenAPI schema — emitted as untyped.",
        [UnresolvedTypeParameter] =
            "Unresolved generic type parameter in the OpenAPI schema — emitted as object.",
        [GenericTemplateMissing] =
            "Generic template is absent from the contract's type definitions — a free-form object schema is emitted for the instantiation.",
        [BrandConflictingUnderlyingTypes] =
            "Brand declared with conflicting underlying types — the first declaration wins.",
        [ReservedHeaderParameterSkipped] =
            "Header parameter named Accept, Content-Type or Authorization — OpenAPI forbids these as header parameters; the parameter is omitted from the spec.",
        [DuplicateResponseStatusInIr] =
            "External contract IR declares the same response status more than once; the duplicate is dropped and the first declaration is kept.",
        [DuplicateSecuritySchemeDefinition] =
            "A security scheme name is configured as both the primary and an additional definition.",
        [ImportAliasCycleBroken] =
            "Alias schema is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object.",
        [ImportSecuritySchemesDropped] =
            "Document declares multiple security schemes — only the first is imported; alternatives and scopes are not represented.",
        [ImportOperationMethodDropped] =
            "TRACE operation dropped — the HTTP method has no contract representation.",
        [ImportAdditionalPropertiesDropped] =
            "Retired diagnostic ID: mixed named objects now retain typed properties and extension data.",
        [ImportDiscriminatorDropped] =
            "Discriminator with no reversible polymorphic shape (plain object without oneOf, or oneOf whose mapping is absent/unusable) — imported without dispatch semantics.",
        [ImportAliasTargetMissing] =
            "Alias schema references a missing schema — consumers fall back to JsonElement.",
        [ImportAliasRefCycle] =
            "Alias schema is part of a $ref cycle — consumers fall back to JsonElement.",
        [ImportUnresolvableAliasReference] =
            "Reference to an unresolvable alias schema (cycle or missing target) — using JsonElement.",
        [ImportUnresolvedSchema] =
            "Schema could not be resolved to a C# type — mapped to JsonElement.",
        [ImportUnsupportedSchemaType] = "Unhandled JSON Schema 'type' — mapped to JsonElement.",
        [ImportArrayMissingItems] = "Array schema without 'items' — mapped to List<JsonElement>.",
        [ImportEnumConstraintDropped] =
            "Enum constraint that cannot become a C# enum (single-value, mixed/float, out-of-int32-range) — degraded to a primitive.",
        [ImportDeclaredPropertiesDropped] =
            "Retired diagnostic ID: mixed inline objects now retain typed properties and extension data.",
        [ImportDictionaryKeyDropped] =
            "Dictionary 'propertyNames' schema has no C# dictionary-key representation — imported with string keys.",
        [ImportNamedScalarAlgebraUnsupported] =
            "Named scalar uses schema algebra outside primitive/flat-enum preservation — existing fallback mapping retained.",
        [ImportEmptyParameterNameDropped] =
            "Parameter has an empty name — the invalid parameter is dropped while the operation and its other parameters are preserved.",
        [ImportReservedContentTypeHeaderDropped] =
            "Reserved Content-Type header parameter is dropped — request media types are represented by requestBody.content.",
        [ImportReservedAuthorizationHeaderDropped] =
            "Reserved Authorization header parameter is dropped — authentication is represented by security and security schemes.",
        [ImportReservedAcceptHeaderDropped] =
            "Reserved Accept header parameter is dropped — response media types are represented by response content.",
        [CoverageMissingImplementation] = "Contract endpoint has no matching implementation.",
        [CoverageHttpMethodMismatch] = "Implementation HTTP method differs from the contract's.",
        [CoverageRouteMismatch] = "Implementation route differs from the contract's.",
        [CoverageOrphanedBinding] =
            "Contract endpoint is bound in a route handler but no terminal result from that binding is returned.",
    };

    /// <summary>
    /// Writes the canonical machine-parseable warning line to stderr:
    /// <c>warning RIV1001: message</c>.
    /// </summary>
    public static void Warn(string id, string message) =>
        Console.Error.WriteLine($"warning {id}: {message}");

    /// <summary>
    /// Prefixes a collected warning string (e.g. ImportResult.Warnings) with its ID:
    /// <c>RIV3001: message</c>. Program.cs prepends "warning " when printing,
    /// producing the same canonical stderr line as <see cref="Warn"/>.
    /// </summary>
    public static string Prefix(string id, string message) => $"{id}: {message}";
}
