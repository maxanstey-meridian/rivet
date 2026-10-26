namespace Rivet.Tool;

/// <summary>
/// Stable diagnostic IDs for warnings and fatal errors Rivet writes to stderr.
/// Canonical formats are <c>warning RIV1019: &lt;message&gt;</c> and
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
/// Each entry is either a warning, which lets the operation carry on, or a fatal error,
/// which aborts it with a non-zero exit code. Every ID must have a row in
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

    // RIV1005 (FromHeaderParameterExcluded) retired: [FromHeader] params now
    // map to ParamSource.Header instead of being excluded. The number is never reused.
    public const string UnmappedTypedResult = "RIV1006";
    public const string TypeNameCollision = "RIV1007";
    public const string UnparseableRangeBound = "RIV1008";

    // RIV1009 (UnsupportedTimeSpan) retired: TimeSpan now maps to the string
    // System.Text.Json writes (the "c" format) with x-rivet-csharp-type. The number is
    // never reused.
    public const string UnsupportedBigInteger = "RIV1010";

    // RIV1011 (UnsupportedChar) retired: char now maps to a length-1 string
    // schema (the System.Text.Json wire shape) with x-rivet-csharp-type. The number is
    // never reused.
    // RIV1012 (UnsupportedObject) retired: object/object? now map to the
    // untyped (empty) schema deliberately and silently — "any JSON value" is exactly
    // what the type declares. The number is never reused.
    public const string DictionaryKeyTypeDropped = "RIV1013";
    public const string PolymorphicNonStringTag = "RIV1014";
    public const string PolymorphicNoDerivedTypes = "RIV1015";
    public const string PolymorphicUnknownHandling = "RIV1016";
    public const string ResponseHeaderUndeclaredStatus = "RIV1017";
    public const string RivetUnionNoVariants = "RIV1018";
    public const string RouteTokenWithoutInputProperty = "RIV1019";
    public const string InputTypeNotParamLowerable = "RIV1020";
    public const string DuplicateResponseStatus = "RIV1021";
    public const string InvalidRequestBodyProvenance = "RIV1022";
    public const string ImportedSchemaProvenanceConflict = "RIV1023";
    public const string UnresolvedBindingSource = "RIV1100";

    // RIV1101 (JsonIncludeFieldNotRepresented) retired: registered in 0.42.0 as a reserved
    // slot for [JsonInclude] shapes the walker couldn't prove, but every shape was already
    // determined and nothing ever emitted it. The number is never reused.
    public const string BodyForbiddenStatusExample = "RIV1102";
    public const string InvalidRivetScalarShape = "RIV1103";
    public const string MixedFormFileParameters = "RIV1104";

    // RIV1105 (RivetEnumNamingPolicyWithoutStringConverter) retired in the converter-family
    // wave: the naming policy lives in the converter class name itself, so a policy cannot
    // exist without a string wire. The number is never reused.
    public const string EnumWireValueCollision = "RIV1106";
    public const string UnsupportedEnumConverter = "RIV1108";
    public const string ConflictingResponseDeclaration = "RIV1107";
    public const string DuplicateResponseHeader = "RIV1109";
    public const string UnbindableEnumParameter = "RIV1110";
    public const string UnmodelledJsonConverter = "RIV1111";
    public const string UnmodelledNumberHandling = "RIV1112";
    public const string UnreadableContractEndpoint = "RIV1113";
    public const string InvalidSecurityDeclaration = "RIV1114";
    public const string BindingInferenceUnreadable = "RIV1115";
    public const string AmbiguousJsonConstructor = "RIV1116";

    // RIV1024-RIV1099 are reserved for extraction diagnostics emitted by the
    // rivet/php sibling runtime. Native Rivet diagnostics must not use them.

    // ----- RIV2xxx: emission -----
    public const string TaggedUnionComponentCollision = "RIV2001";
    public const string UndefinedSecurityScheme = "RIV2002";
    public const string DuplicateEndpoint = "RIV2003";
    public const string MultipartInputTypeMissing = "RIV2004";
    public const string UnknownTypeUntypedSchema = "RIV2005";
    public const string UnresolvedTypeParameter = "RIV2006";
    public const string GenericTemplateMissing = "RIV2007";
    public const string BrandConflictingUnderlyingTypes = "RIV2008";
    public const string ReservedHeaderParameter = "RIV2009";
    public const string DuplicateResponseStatusInIr = "RIV2010";
    public const string DuplicateSecuritySchemeDefinition = "RIV2011";
    public const string ConflictingOperations = "RIV2012";
    public const string EquivalentPathTemplates = "RIV2013";

    // ----- RIV3xxx: import -----
    public const string ImportAliasCycleBroken = "RIV3001";

    // RIV3002 (ImportSecuritySchemesDropped) retired: every security alternative, AND-group
    // and scope imports into .Secure()/.AndSecure()/.OrAnonymous(). The number is never
    // reused.
    public const string ImportOperationMethodDropped = "RIV3003";

    // RIV3004 (ImportAdditionalPropertiesDropped) retired: mixed named objects keep typed
    // properties plus extension data. The number is never reused.
    public const string ImportDiscriminatorDropped = "RIV3005";
    public const string ImportAliasTargetMissing = "RIV3006";

    // RIV3007 (ImportAliasRefCycle) retired: alias cycles are broken before mapping and
    // reported as RIV3001. The number is never reused.
    public const string ImportUnresolvableAliasReference = "RIV3008";
    public const string ImportUnresolvedSchema = "RIV3009";
    public const string ImportUnsupportedSchemaType = "RIV3010";
    public const string ImportArrayMissingItems = "RIV3011";

    // RIV3012 (ImportEnumConstraintDropped) retired: every literal enum keeps its values
    // in generated metadata, with its C# type taken from the values. The number is never
    // reused.

    // RIV3013 (ImportDeclaredPropertiesDropped) retired: mixed inline objects keep typed
    // properties plus extension data. The number is never reused.
    public const string ImportDictionaryKeyDropped = "RIV3014";
    public const string ImportNamedScalarAlgebraUnsupported = "RIV3015";
    public const string ImportSwaggerServerNotProjected = "RIV3019";
    public const string ImportEmptyParameterNameDropped = "RIV3020";
    public const string ImportReservedContentTypeHeaderDropped = "RIV3021";
    public const string ImportReservedAuthorizationHeaderDropped = "RIV3022";
    public const string ImportReservedAcceptHeaderDropped = "RIV3023";
    public const string ImportBodyForbiddenStatusContentDropped = "RIV3024";
    public const string ImportSchemalessParameterDropped = "RIV3025";
    public const string ImportCallbackDropped = "RIV3027";
    public const string ImportPathItemOutsidePathsDropped = "RIV3028";
    public const string ImportEquivalentPathTemplateMerged = "RIV3029";
    public const string ImportUnsatisfiableSecurity = "RIV3030";
    public const string ImportIntegerEnumValueUnheld = "RIV3031";

    // ----- RIV4xxx: coverage -----
    public const string CoverageMissingImplementation = "RIV4001";
    public const string CoverageHttpMethodMismatch = "RIV4002";
    public const string CoverageRouteMismatch = "RIV4003";
    public const string CoverageOrphanedBinding = "RIV4004";
    public const string CoverageUnbindableEnumParameter = "RIV4005";
    public const string CoverageInputBindingMismatch = "RIV4006";

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
            "Contract endpoint field is not 'static readonly' — the runtime could serve a different definition than the initializer the spec is read from; generation fails.",
        [ContractExampleUndeclaredStatus] =
            "Response example targets a status code the contract endpoint does not declare, a media type its .ResponseContent status doesn't declare, or no media type when that status declares several — the spec has no response content to carry it; generation fails.",
        [RouteBoundJsonPropertyNameIgnored] =
            "[JsonPropertyName] on a route-bound property is ignored for route interpolation — the contract param keeps the route name.",
        [ControllerExampleUndeclaredStatus] =
            "Response example targets a status code the controller endpoint does not declare — the spec has no response to carry it; generation fails.",
        [UnmappedTypedResult] =
            "Unmapped typed result branch in Results<...> or an action with no declared success response — the contract is refused, not silently completed.",
        [TypeNameCollision] =
            "Two walked types share a simple name (different namespaces, nesting or generic arity) — schema names are global, so generation fails until one is renamed.",
        [UnparseableRangeBound] =
            "[Range] bound could not be parsed — the range constraint is skipped.",
        [UnsupportedBigInteger] =
            "BigInteger has no System.Text.Json converter (it serializes its IsZero/Sign/... properties and cannot be read back) — generation fails; expose a string or a bounded number instead.",
        [DictionaryKeyTypeDropped] =
            "Dictionary key type has no contract representation (supported: string, enums, string-backed brands, string-serializable primitives) — keys are emitted as unconstrained strings.",
        [PolymorphicNonStringTag] =
            "[JsonDerivedType] registration with a non-string (int or absent) discriminator tag — a string-discriminated oneOf cannot represent it; generation fails until every registration has a string tag.",
        [PolymorphicNoDerivedTypes] =
            "[JsonPolymorphic] base type has no [JsonDerivedType] registrations — System.Text.Json refuses to serialize it, so generation fails until a derived type is registered or the attribute removed.",
        [PolymorphicUnknownHandling] =
            "[JsonPolymorphic] sets UnknownDerivedTypeHandling to a fallback or IgnoreUnrecognizedTypeDiscriminators — the host would write an unregistered derived type, or read an unknown tag, as a base the oneOf doesn't admit, so generation fails until the setting is removed.",
        [ResponseHeaderUndeclaredStatus] =
            ".WithResponseHeader() targets a status code the contract endpoint does not declare — the spec has no response to carry it; generation fails.",
        [RivetUnionNoVariants] =
            "[RivetUnion] wrapper has no variant properties — there is no union to emit; generation fails.",
        [RouteTokenWithoutInputProperty] =
            "Route token has no matching property on the endpoint's input type (after normalized matching: case-insensitive, '_'/'-' stripped) — emitted as an untyped string path param.",
        [InputTypeNotParamLowerable] =
            "The input type on a request with no JSON body (GET/DELETE/.AcceptsBinary) is a dictionary, collection or scalar — it has no properties to lower to route/query params; generation fails instead of dropping the input.",
        [DuplicateResponseHeader] =
            "A contract endpoint declares the same response header name (case-insensitively) more than once for one status; generation fails instead of emitting both spellings.",
        [DuplicateResponseStatus] =
            "An authored contract declares the same response status more than once; generation fails because the contract cannot execute as declared.",
        [InvalidRequestBodyProvenance] =
            "A [RivetRequestBody] type is not represented independently by the endpoint input type.",
        [ImportedSchemaProvenanceConflict] =
            "Imported generated C# changed while raw schema provenance still targets its original typed shape — extraction fails instead of emitting stale OpenAPI.",
        [UnresolvedBindingSource] =
            "A parameter's binding source cannot be established from explicit declarations or the retained narrow conventions, or a [RivetEndpoint] method or abstract [RivetContract] method has no HTTP verb or no route template — the contract is refused, not silently completed.",
        [BodyForbiddenStatusExample] =
            "A response example or content is authored on a body-forbidden status (1xx, 204, 205, 304) — HTTP forbids a message body there; generation fails instead of emitting body content the host cannot send.",
        [InvalidRivetScalarShape] =
            "A [RivetScalar] type is not a non-generic class/struct/record with one public readable Value property and a matching public constructor, without inheritance, competing converters or property-level JSON settings — generation fails instead of silently falling back to object semantics.",
        [MixedFormFileParameters] =
            "An endpoint mixes form fields or files with an explicit body parameter ([FromBody] or a [FromForm] DTO) whose combined shape Rivet cannot faithfully represent — the contract is refused instead of silently dropping the declared input.",
        [UnsupportedEnumConverter] =
            "An enum converter is unsupported, targets another enum, or declares string flags/aliases whose runtime values cannot be represented as distinct member names. Generation fails; use a supported converter and distinct values, or numeric enum serialization.",
        [EnumWireValueCollision] =
            "Two enum members produce the same wire value (the declared converter's casing or duplicate [JsonStringEnumMemberName] pins) — generation fails instead of emitting an incorrect numeric schema.",
        [ConflictingResponseDeclaration] =
            "An endpoint declares the same response status twice with different bodies ([ProducesResponseType]/.Returns vs a mapped Results<> branch) — one status carries exactly one shape; generation fails instead of silently letting the attribute win.",
        [UnbindableEnumParameter] =
            "A [RivetEndpoint]/[RivetClient] endpoint binds a string-wire enum from route, query, header or form, and the host's enum parser would refuse an advertised wire value — it ignores the JSON converter. An MVC action (instance method) refuses a value differing from its member name by more than case; a minimal-API handler (static method) refuses any difference; generation fails.",
        [UnmodelledJsonConverter] =
            "A [JsonConverter] Rivet cannot model sits on a walked type or member (anything but a supported enum converter, [RivetScalar] or [RivetUnion], and on a member not pinned by [RivetSchemaType]) — the wire is whatever the converter writes, so generation fails instead of emitting the declared CLR shape.",
        [UnmodelledNumberHandling] =
            "[JsonNumberHandling] with WriteAsString or AllowNamedFloatingPointLiterals on a walked type or member puts strings on the number's wire — generation fails unless [RivetSchemaType] pins the member's schema (AllowReadingFromString alone is accepted: it only widens what the host reads).",
        [UnreadableContractEndpoint] =
            "A contract endpoint can't be read statically: a field with no initializer or one that isn't a Define.* builder chain, an endpoint declared as a property, a call that isn't a Rivet builder method, an example with nothing in it, or any string, int or bool builder argument (route, status, summary, media type, scheme, scope...) that isn't a compile-time constant — generation fails instead of dropping the operation or quietly defaulting the value; use a literal or a const.",
        [InvalidSecurityDeclaration] =
            "A contract endpoint's security calls contradict each other (.Anonymous() alongside .Secure() or .OrAnonymous(), or .AndSecure() with no .Secure() alternative to join) — generation fails instead of guessing which one was meant.",
        [BindingInferenceUnreadable] =
            "ApiBehaviorOptions.SuppressInferBindingSourcesForParameters is assigned a value that isn't a compile-time constant, so whether MVC infers binding sources can't be read; generation and --check stop. Read only when an annotated MVC action (generation) or a contract's MVC implementation (--check) is on an [ApiController] controller.",
        [AmbiguousJsonConstructor] =
            "A walked type has more than one [JsonConstructor] constructor — System.Text.Json refuses it at runtime, so there is no constructor choice for the spec to follow; generation fails.",
        [TaggedUnionComponentCollision] =
            "A tagged-union variant's producer-pinned component id is already used by a different schema — generation fails, because one id cannot $ref two shapes.",
        [UndefinedSecurityScheme] =
            "Endpoint references a security scheme with no definition — generation fails rather than inventing security semantics.",
        [DuplicateEndpoint] =
            "Two endpoints share an HTTP method + path with equivalent declarations — one representative is emitted (incompatible ones fail with RIV2012).",
        [MultipartInputTypeMissing] =
            "Multipart input type is absent from the contract's type definitions — the request schema is built inline from the endpoint's params.",
        [UnknownTypeUntypedSchema] =
            "A type with no schema mapping (an 'unknown' primitive whose csharpType isn't one of the any-JSON types), or a $ref to a schema component nothing defines — generation fails instead of emitting an untyped stand-in. The any-JSON types (TypeScript unknown, object, JsonElement, JsonDocument, JsonNode, JsonObject, JsonArray, JsonValue) map to {} with no diagnostic.",
        [UnresolvedTypeParameter] =
            "A generic type parameter reaches the OpenAPI schema unbound — generation fails instead of emitting it as object.",
        [GenericTemplateMissing] =
            "A generic instantiation's template is absent from the contract's type definitions — generation fails instead of emitting a free-form object.",
        [BrandConflictingUnderlyingTypes] =
            "Contract JSON declares one brand name two different ways — generation fails instead of letting the first declaration win.",
        [ReservedHeaderParameter] =
            "Header parameter named Accept, Content-Type or Authorization — OpenAPI ignores these as header parameters, so the spec would never carry it and clients would never send it; generation fails.",
        [DuplicateResponseStatusInIr] =
            "Controller metadata or contract JSON declares the same response status more than once — generation fails instead of keeping the first declaration.",
        [DuplicateSecuritySchemeDefinition] = "Two --security values define the same scheme name.",
        [ConflictingOperations] =
            "Two incompatible endpoints declare the same normalized HTTP method + route — generation fails instead of writing a lossy spec.",
        [EquivalentPathTemplates] =
            "Two routes differ only in parameter names — OpenAPI treats them as one path, so generation fails instead of writing an invalid document.",
        [ImportAliasCycleBroken] =
            "Alias schema is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object.",
        [ImportOperationMethodDropped] =
            "TRACE operation dropped — the HTTP method has no contract representation.",
        [ImportDiscriminatorDropped] =
            "Discriminator with no reversible polymorphic shape (plain object without oneOf, or oneOf whose mapping is absent/unusable) — imported without dispatch semantics; or a discriminator mapping entry that points into another file — that entry is dropped and the rest of the discriminator kept.",
        [ImportAliasTargetMissing] =
            "Alias schema references a missing schema — consumers fall back to JsonElement.",
        [ImportUnresolvableAliasReference] =
            "Reference to an unresolvable alias schema (cycle or missing target) — using JsonElement.",
        [ImportUnresolvedSchema] =
            "Schema could not be resolved to a C# type — mapped to JsonElement.",
        [ImportUnsupportedSchemaType] = "Unhandled JSON Schema 'type' — mapped to JsonElement.",
        [ImportArrayMissingItems] = "Array schema without 'items' — mapped to List<JsonElement>.",
        [ImportDictionaryKeyDropped] =
            "Dictionary 'propertyNames' schema has no C# dictionary-key representation — imported with string keys.",
        [ImportNamedScalarAlgebraUnsupported] =
            "Named scalar uses const, schema composition or heterogeneous leaf types — existing fallback mapping retained.",
        [ImportSwaggerServerNotProjected] =
            "Swagger 2 document declares a host without schemes, or schemes without a host — no server URL is invented from half of one, so no server is imported.",
        [ImportEmptyParameterNameDropped] =
            "Parameter has an empty name — the invalid parameter is dropped while the operation and its other parameters are preserved.",
        [ImportReservedContentTypeHeaderDropped] =
            "Reserved Content-Type header parameter is dropped — request media types are represented by requestBody.content.",
        [ImportReservedAuthorizationHeaderDropped] =
            "Reserved Authorization header parameter is dropped — authentication is represented by security and security schemes.",
        [ImportReservedAcceptHeaderDropped] =
            "Reserved Accept header parameter is dropped — response media types are represented by response content.",
        [ImportBodyForbiddenStatusContentDropped] =
            "Response content authored on a body-forbidden status (1xx/204/205/304) is dropped; HTTP forbids a message body there; the status and its description/headers are preserved.",
        [ImportSchemalessParameterDropped] =
            "Parameter declares content (or nothing) instead of a schema — Rivet parameters carry a schema, so the parameter is dropped.",
        [ImportCallbackDropped] =
            "Operation or component callback dropped — Rivet contracts describe calls the API receives, not calls it makes.",
        [ImportPathItemOutsidePathsDropped] =
            "Webhook or components.pathItems entry dropped — only path items under 'paths' become contract endpoints.",
        [ImportEquivalentPathTemplateMerged] =
            "Path repeats an earlier path's template under other parameter names, which OpenAPI forbids — its operations merge into the earlier path and adopt its path parameter names.",
        [ImportUnsatisfiableSecurity] =
            "A security alternative names a scheme components.securitySchemes never defines, so no caller can satisfy it — the alternative is dropped, and an operation left with none is dropped too rather than imported as open.",
        [ImportIntegerEnumValueUnheld] =
            "Integer enum lists values its C# integer type can't hold — the values stay in the spec, but C# code using that type can't read or write them.",
        [CoverageMissingImplementation] = "Contract endpoint has no matching implementation.",
        [CoverageHttpMethodMismatch] = "Implementation HTTP method differs from the contract's.",
        [CoverageRouteMismatch] = "Implementation route differs from the contract's.",
        [CoverageOrphanedBinding] =
            "Contract endpoint is bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns.",
        [CoverageUnbindableEnumParameter] =
            "A handler binds a string-wire enum from route, query, header or form, and the host's enum parser (minimal API: exact member name; MVC: member name ignoring case; Azure Functions: JSON, so no name at all) would refuse an advertised wire value.",
        [CoverageInputBindingMismatch] =
            "A handler's bound inputs disagree with its contract's: it binds an input the contract doesn't declare, never binds one it does, or binds one from a different source (route, query, header, body, form).",
    };

    /// <summary>
    /// Writes the canonical machine-parseable warning line to stderr:
    /// <c>warning RIV1019: message</c>.
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
