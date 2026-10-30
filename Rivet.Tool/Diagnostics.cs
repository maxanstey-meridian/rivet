namespace Rivet.Tool;

/// <summary>
/// Stable diagnostic IDs for warnings and fatal errors Rivet writes to stderr.
/// Canonical formats are <c>warning RIV1003: &lt;message&gt;</c> and
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
    public const string RouteTokenUndeclared = "RIV1019";
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
    public const string UnreadableMvcBindingSetup = "RIV1115";
    public const string AmbiguousJsonConstructor = "RIV1116";
    public const string SchemaTypeOnNonPrimitive = "RIV1117";
    public const string InvalidJsonExtensionData = "RIV1118";
    public const string RequiredMemberMayBeOmitted = "RIV1119";
    public const string InvalidStatusKey = "RIV1120";
    public const string InvalidSecuritySchemeMetadata = "RIV1121";
    public const string MalformedGeneratedMetadata = "RIV1122";
    public const string InvalidBuilderArgument = "RIV1123";
    public const string UnreadableFunctionsHostJson = "RIV1124";
    public const string RouteConstraintContradictsType = "RIV1125";
    public const string CompilationFailed = "RIV1126";

    // RIV1024-RIV1099 were once reserved for the dropped rivet/php runtime; never reused.

    // ----- RIV2xxx: emission -----
    public const string TaggedUnionComponentCollision = "RIV2001";
    public const string UndefinedSecurityScheme = "RIV2002";
    public const string DuplicateEndpoint = "RIV2003";

    // RIV2004 (MultipartInputTypeMissing) retired: an inputTypeName with no definition in the
    // contract's types is a name hint for the inline multipart body, which rivet-ts builds that
    // way on purpose, so there was nothing to fix. The number is never reused.
    public const string UnknownTypeUntypedSchema = "RIV2005";
    public const string UnresolvedTypeParameter = "RIV2006";
    public const string GenericTemplateMissing = "RIV2007";
    public const string BrandConflictingUnderlyingTypes = "RIV2008";
    public const string ReservedHeaderParameter = "RIV2009";
    public const string DuplicateResponseStatusInIr = "RIV2010";
    public const string DuplicateSecuritySchemeDefinition = "RIV2011";
    public const string ConflictingOperations = "RIV2012";
    public const string EquivalentPathTemplates = "RIV2013";
    public const string ConflictingComponentExample = "RIV2015";
    public const string PreservedFieldUnattachable = "RIV2016";
    public const string InvalidContractJson = "RIV2017";
    public const string InvalidSecurityOption = "RIV2018";
    public const string FormFieldAnnotationDropped = "RIV2019";
    public const string CommittedSpecOutOfDate = "RIV2020";
    public const string InputPathNotFound = "RIV2021";

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
    public const string InvalidOpenApiDocument = "RIV3032";
    public const string ImportClosedSchemaLeftOpen = "RIV3033";

    // ----- RIV4xxx: coverage -----
    public const string CoverageMissingImplementation = "RIV4001";
    public const string CoverageHttpMethodMismatch = "RIV4002";
    public const string CoverageRouteMismatch = "RIV4003";
    public const string CoverageOrphanedBinding = "RIV4004";
    public const string CoverageUnbindableEnumParameter = "RIV4005";
    public const string CoverageInputBindingMismatch = "RIV4006";

    // RIV4007 is skipped, not retired: an unreleased commit gave it to what is now RIV1115
    // (UnreadableMvcBindingSetup), so it stays unused to keep git history unambiguous.
    public const string CoverageSecurityMismatch = "RIV4008";
    public const string CoverageAuthorizationUnreadable = "RIV4009";

    /// <summary>A warning lets the operation carry on; an error aborts it with exit code 1.</summary>
    public enum Severity
    {
        Warning,
        Error,
    }

    /// <summary>A registered diagnostic: its severity and a one-line trigger description.</summary>
    public sealed record Entry(Severity Severity, string Description);

    /// <summary>
    /// Registry of every diagnostic ID with its severity and a one-line trigger description.
    /// The severity is decided here, once: <see cref="Warn"/> and <see cref="Prefix"/> refuse
    /// an error ID and <see cref="Error"/> refuses a warning ID. DiagnosticsTests cross-checks
    /// this against docs/reference/diagnostics.md, Severity column included, so neither the
    /// registry nor the doc page can rot.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Entry> Registry = new Dictionary<
        string,
        Entry
    >(StringComparer.Ordinal)
    {
        [EndpointFieldNotStaticReadonly] = new(
            Severity.Error,
            "Contract endpoint field is not 'static readonly' — the runtime could serve a different definition than the initializer the spec is read from; generation fails."
        ),
        [ContractExampleUndeclaredStatus] = new(
            Severity.Error,
            "Response example targets a status code the contract endpoint does not declare, a media type its .ResponseContent status doesn't declare, or no media type when that status declares several — the spec has no response content to carry it; generation fails."
        ),
        [RouteBoundJsonPropertyNameIgnored] = new(
            Severity.Warning,
            "[JsonPropertyName] on a route-bound property is ignored for route interpolation — the contract param keeps the route name."
        ),
        [ControllerExampleUndeclaredStatus] = new(
            Severity.Error,
            "Response example targets a status code the controller endpoint does not declare — the spec has no response to carry it; generation fails."
        ),
        [UnmappedTypedResult] = new(
            Severity.Error,
            "Unmapped typed result branch in Results<...> or an action with no declared success response — the contract is refused, not silently completed."
        ),
        [TypeNameCollision] = new(
            Severity.Error,
            "Two walked types share a simple name (different namespaces, nesting or generic arity), or a walked type takes the name of a [RivetGeneratedSchema] component it doesn't annotate (only the enum or [RivetScalar] brand of that name may) — schema names are global, so generation fails until one is renamed."
        ),
        [UnparseableRangeBound] = new(
            Severity.Warning,
            "[Range] bound could not be parsed — the range constraint is skipped."
        ),
        [UnsupportedBigInteger] = new(
            Severity.Error,
            "BigInteger has no System.Text.Json converter (it serializes its IsZero/Sign/... properties and cannot be read back) — generation fails; expose a string or a bounded number instead."
        ),
        [DictionaryKeyTypeDropped] = new(
            Severity.Warning,
            "Dictionary key type has no contract representation (supported: string, enums, string-backed brands, string-serializable primitives) — keys are emitted as unconstrained strings."
        ),
        [PolymorphicNonStringTag] = new(
            Severity.Error,
            "[JsonDerivedType] registration a string-discriminated oneOf cannot represent: a non-string (int or absent) discriminator tag, or a registered type that isn't a named class, struct or record — generation fails until every registration is a derived type with a string tag."
        ),
        [PolymorphicNoDerivedTypes] = new(
            Severity.Error,
            "[JsonPolymorphic] base type has no [JsonDerivedType] registrations — System.Text.Json refuses to serialize it, so generation fails until a derived type is registered or the attribute removed."
        ),
        [PolymorphicUnknownHandling] = new(
            Severity.Error,
            "[JsonPolymorphic] sets UnknownDerivedTypeHandling to a fallback or IgnoreUnrecognizedTypeDiscriminators — the host would write an unregistered derived type, or read an unknown tag, as a base the oneOf doesn't admit, so generation fails until the setting is removed."
        ),
        [ResponseHeaderUndeclaredStatus] = new(
            Severity.Error,
            ".WithResponseHeader() targets a status code the contract endpoint does not declare — the spec has no response to carry it; generation fails."
        ),
        [RivetUnionNoVariants] = new(
            Severity.Error,
            "[RivetUnion] wrapper has no variant properties — there is no union to emit; generation fails."
        ),
        [RouteTokenUndeclared] = new(
            Severity.Error,
            "Route token has no typed declaration: no matching property on the endpoint's input type (after normalized matching: case-insensitive, '_'/'-' stripped), no .Parameter<T>(name, \"path\", ...), no type constraint, or, on a [RivetEndpoint] action, no action parameter. Its type is unknown, so generation fails instead of guessing string."
        ),
        [InputTypeNotParamLowerable] = new(
            Severity.Error,
            "The input type on a request with no JSON body (GET/DELETE/.AcceptsBinary) is a dictionary, collection or scalar — it has no properties to lower to route/query params; generation fails instead of dropping the input."
        ),
        [DuplicateResponseHeader] = new(
            Severity.Error,
            "A contract endpoint declares the same response header name (case-insensitively) more than once for one status; generation fails instead of emitting both spellings."
        ),
        [DuplicateResponseStatus] = new(
            Severity.Error,
            "An authored contract declares the same response status more than once; generation fails because the contract cannot execute as declared."
        ),
        [InvalidRequestBodyProvenance] = new(
            Severity.Error,
            "A [RivetRequestBody] type is not represented independently by the endpoint input type, or carries files the input type doesn't."
        ),
        [ImportedSchemaProvenanceConflict] = new(
            Severity.Error,
            "Imported generated C# changed while raw schema provenance still targets its original typed shape — extraction fails instead of emitting stale OpenAPI."
        ),
        [UnresolvedBindingSource] = new(
            Severity.Error,
            "A parameter's binding source cannot be established from explicit declarations or the retained narrow conventions, a [RivetEndpoint] method or abstract [RivetContract] method has no HTTP verb or no route template, or a form input member is read under different names, or from outside the form, by the hosts that can serve it, or skipped by only one of them (MVC's [BindNever], [BindingBehavior(Never)] or [Bind] include list, the form mapper's [IgnoreDataMember]) — the contract is refused, not silently completed."
        ),
        [BodyForbiddenStatusExample] = new(
            Severity.Error,
            "A response example or content is authored on a body-forbidden status (1xx, 204, 205, 304) — HTTP forbids a message body there; generation fails instead of emitting body content the host cannot send."
        ),
        [InvalidRivetScalarShape] = new(
            Severity.Error,
            "A [RivetScalar] type is not a non-generic class/struct/record with one public readable Value property and a matching public constructor, without inheritance, competing converters or property-level JSON settings — generation fails instead of silently falling back to object semantics."
        ),
        [MixedFormFileParameters] = new(
            Severity.Error,
            "An endpoint mixes form fields or files with an explicit body parameter ([FromBody] or a [FromForm] DTO) whose combined shape Rivet cannot faithfully represent, or a contract combines .AcceptsBinary() with .AcceptsFile() or .FormEncoded() — the contract is refused instead of silently dropping the declared input."
        ),
        [UnsupportedEnumConverter] = new(
            Severity.Error,
            "An enum converter is unsupported, targets another enum, or declares string flags/aliases whose runtime values cannot be represented as distinct member names. Generation fails; use a supported converter and distinct values, or numeric enum serialization."
        ),
        [EnumWireValueCollision] = new(
            Severity.Error,
            "Two enum members produce the same wire value (the declared converter's casing or duplicate [JsonStringEnumMemberName] pins) — generation fails instead of emitting an incorrect numeric schema."
        ),
        [ConflictingResponseDeclaration] = new(
            Severity.Error,
            "An endpoint declares the same response status twice with different bodies ([ProducesResponseType]/.Returns vs a mapped Results<> branch) — one status carries exactly one shape; generation fails instead of silently letting the attribute win."
        ),
        [UnbindableEnumParameter] = new(
            Severity.Error,
            "A [RivetEndpoint]/[RivetClient] endpoint binds a string-wire enum from route, query, header or form, and the host's enum parser would refuse an advertised wire value — it ignores the JSON converter. An MVC action (instance method) refuses a value differing from its member name by more than case; a minimal-API handler (static method) refuses any difference; generation fails."
        ),
        [UnmodelledJsonConverter] = new(
            Severity.Error,
            "A [JsonConverter] Rivet cannot model sits on a walked type or member (anything but a supported enum converter, [RivetScalar] or [RivetUnion], and on a member not pinned by [RivetSchemaType]) — the wire is whatever the converter writes, so generation fails instead of emitting the declared CLR shape."
        ),
        [UnmodelledNumberHandling] = new(
            Severity.Error,
            "[JsonNumberHandling] with WriteAsString or AllowNamedFloatingPointLiterals on a walked type or member puts strings on the number's wire — generation fails unless [RivetSchemaType] pins the member's schema (AllowReadingFromString alone is accepted: it only widens what the host reads)."
        ),
        [UnreadableContractEndpoint] = new(
            Severity.Error,
            "A contract endpoint can't be read statically: a field with no initializer or one that isn't a Define.* builder chain, an endpoint declared as a property, a call that isn't a Rivet builder method, an example with nothing in it, or any string, int or bool builder argument (route, status, summary, media type, scheme, scope...) that isn't a compile-time constant — generation fails instead of dropping the operation or quietly defaulting the value; use a literal or a const."
        ),
        [InvalidSecurityDeclaration] = new(
            Severity.Error,
            "A contract endpoint's security calls contradict each other (.Anonymous() alongside .Secure() or .OrAnonymous(), or .AndSecure() with no .Secure() alternative to join), or the same contradiction among [RivetSecure]/[RivetAndSecure]/[RivetOrAnonymous]/[RivetAnonymous] on a controller or action (where [RivetAndSecure] joins the [RivetSecure] before it in source order, and may not be split across partial declarations in different files) — generation fails instead of guessing which one was meant."
        ),
        [UnreadableMvcBindingSetup] = new(
            Severity.Error,
            "MVC binding setup that can't be read statically, so what MVC binds can't be known; generation and --check stop. ApiBehaviorOptions.SuppressInferBindingSourcesForParameters assigned a value that isn't a compile-time constant (read only when an annotated MVC action, in generation, or a contract's MVC implementation, in --check, is on an [ApiController] controller); a property filter (IPropertyFilterProvider) other than [Bind]'s include list on a parameter, type or member MVC binds member by member, or on a contract's form input type; or a BindingBehaviorAttribute subclass that sets its own behaviour. A minimal-API-only check never reads the last two."
        ),
        [AmbiguousJsonConstructor] = new(
            Severity.Error,
            "A walked type has more than one [JsonConstructor] constructor — System.Text.Json refuses it at runtime, so there is no constructor choice for the spec to follow; generation fails."
        ),
        [SchemaTypeOnNonPrimitive] = new(
            Severity.Error,
            "[RivetSchemaType] sits on a member whose type isn't a primitive or a nullable primitive, or .WithResponseHeader<T>(schemaType: ...) names a T that isn't one, where it can't re-type anything — generation fails instead of ignoring it; mark a type that is a string (or another scalar) on the wire [RivetScalar] instead."
        ),
        [InvalidJsonExtensionData] = new(
            Severity.Error,
            "A [JsonExtensionData] member System.Text.Json refuses at runtime: a type other than JsonObject or one implementing IDictionary<string, JsonElement> or IDictionary<string, object>, a second bag on the type or down its base chain, or a bag on a type marked [JsonUnmappedMemberHandling(Disallow)] — generation fails."
        ),
        [RequiredMemberMayBeOmitted] = new(
            Severity.Error,
            "A member is [JsonRequired] (or C# required) but may be omitted when written ([RivetOptional], or [JsonIgnore(Condition = WhenWritingNull/WhenWritingDefault)]): reading demands it while writing may leave it out, and one schema can't say both — generation fails until one of the two is dropped."
        ),
        [InvalidStatusKey] = new(
            Severity.Error,
            "A response status OpenAPI can't key a response by (a key that isn't a code, an nXX range or default, or an int outside 100-599), so generation fails instead of publishing a response no client can match."
        ),
        [InvalidSecuritySchemeMetadata] = new(
            Severity.Error,
            "An [assembly: RivetSecurityScheme], [RivetOAuthFlow], [RivetGlobalSecurity], or [RivetGlobalSecurityScheme] says something no security scheme or requirement can be: a scheme name declared twice; an unsupported type, apiKey location, or OAuth2 flow; a scheme missing its type or the field its type needs (HTTP scheme, OpenID Connect URL...); scope names and descriptions that don't pair up; a flow or scope declared twice, or a null scope; a flow on a scheme that isn't a declared oauth2 one; a requirement scheme on an alternative nothing starts; or arguments Rivet can't read. Generation fails instead of guessing the scheme."
        ),
        [MalformedGeneratedMetadata] = new(
            Severity.Error,
            "Generated metadata Rivet reads back is malformed, usually stale or hand-edited generated code: a [RivetGeneratedType] DiscriminatorSchemaJson that isn't valid JSON or a JSON object, whose enum, const or type can't carry the tag the variant is registered with, or that sits on a type no base registers with [JsonDerivedType]; a [RivetGeneratedSchema] enum entry (isEnum) whose name no enum in the compilation has; a [RivetGeneratedSchema]/[RivetGeneratedSchemaMetadata] attribute or an OpenAPI provenance attribute ([RivetDocumentInfo], [RivetPreservedField], [RivetOperationProvenance]...) with missing, mistyped or unparseable arguments; or a synthetic type that references itself — generation fails; re-import the OpenAPI document."
        ),
        [InvalidBuilderArgument] = new(
            Severity.Error,
            "An authored value isn't the shape its slot needs: a builder argument's JSON (metadataJson, exampleJson, schemaExamplesJson, examplesJson, referencedComponentsJson...), a [RivetDefault] or [RivetExample] value, or a JSON value in schema metadata that doesn't parse or isn't the JSON kind the keyword takes, or a .Parameter<T>() location other than path, query, header or cookie — generation fails instead of writing it into the spec as-is."
        ),
        [UnreadableFunctionsHostJson] = new(
            Severity.Error,
            "An Azure Functions project's host.json can't be read, isn't JSON, or gives extensions.http.routePrefix as something other than a string — --check stops instead of assuming the default 'api' prefix."
        ),
        [RouteConstraintContradictsType] = new(
            Severity.Error,
            "A route constraint can't hold with the type that declares its token ({id:int} over a string, min(1) on a string, two patterns, bounds that admit no value), so routing admits one thing and the handler binds another and generation fails instead of picking one for the spec."
        ),
        [CompilationFailed] = new(
            Severity.Error,
            "The C# can't be read: no MSBuild instance can be found to load the --project, the project yields no compilation, or the project or source files don't compile — generation, --check and --routes stop, listing the first compiler errors (any workspace failures are printed above)."
        ),
        [TaggedUnionComponentCollision] = new(
            Severity.Error,
            "A tagged-union variant's producer-pinned component id is already used by a different schema — generation fails, because one id cannot $ref two shapes."
        ),
        [UndefinedSecurityScheme] = new(
            Severity.Error,
            "Endpoint references a security scheme with no definition — generation fails rather than inventing security semantics."
        ),
        [DuplicateEndpoint] = new(
            Severity.Warning,
            "Two endpoints share an HTTP method + path with equivalent declarations — one representative is emitted (incompatible ones fail with RIV2012)."
        ),
        [UnknownTypeUntypedSchema] = new(
            Severity.Error,
            "A type with no schema mapping (an 'unknown' primitive whose csharpType isn't one of the any-JSON types), or a $ref to a schema component nothing defines — generation fails instead of emitting an untyped stand-in. The any-JSON types (TypeScript unknown, object, JsonElement, JsonDocument, JsonNode, JsonObject, JsonArray, JsonValue) map to {} with no diagnostic."
        ),
        [UnresolvedTypeParameter] = new(
            Severity.Error,
            "A generic type parameter reaches the OpenAPI schema unbound — generation fails instead of emitting it as object."
        ),
        [GenericTemplateMissing] = new(
            Severity.Error,
            "A generic instantiation's template is absent from the contract's type definitions — generation fails instead of emitting a free-form object."
        ),
        [BrandConflictingUnderlyingTypes] = new(
            Severity.Error,
            "Contract JSON declares one brand name two different ways — generation fails instead of letting the first declaration win."
        ),
        [ReservedHeaderParameter] = new(
            Severity.Error,
            "Header parameter named Accept, Content-Type or Authorization, or a response header named Content-Type — OpenAPI ignores these, so the spec would never carry it and clients would never send (or read) it; generation fails. Declare a response's media type with .ProducesContentType(...)."
        ),
        [DuplicateResponseStatusInIr] = new(
            Severity.Error,
            "Controller metadata or contract JSON declares the same response status more than once — generation fails instead of keeping the first declaration."
        ),
        [DuplicateSecuritySchemeDefinition] = new(
            Severity.Error,
            "Two --security values define the same scheme name."
        ),
        [ConflictingOperations] = new(
            Severity.Error,
            "Two incompatible endpoints declare the same normalized HTTP method + route — generation fails instead of writing a lossy spec."
        ),
        [EquivalentPathTemplates] = new(
            Severity.Error,
            "Two routes differ only in parameter names — OpenAPI treats them as one path, so generation fails instead of writing an invalid document."
        ),
        [ConflictingComponentExample] = new(
            Severity.Error,
            "Two examples pin the same component example id with different values, or with summaries or descriptions that contradict each other (an unstated one doesn't) — one components.examples entry can't hold both, so generation fails instead of pointing the second $ref at the first example."
        ),
        [PreservedFieldUnattachable] = new(
            Severity.Error,
            "A [RivetPreservedField] can't attach to the emitted document: its owner pointer names nothing the C# emits any more, or the owner already has that field — generation fails instead of dropping or overwriting what the source said; re-import the OpenAPI document."
        ),
        [InvalidContractJson] = new(
            Severity.Error,
            "--from contract JSON that doesn't read (not JSON, or not the contract schema's shape) or says something the IR can't hold: a tagged-union variant whose discriminatorSchema isn't a JSON object or can't carry its tag, a variant with a description, scalarMetadata or discriminatorSchema that isn't an inline object carrying the tag property, a routeTemplate token carrying routing syntax (a constraint, ?, a default or a catch-all), which would be written verbatim into the path key, or a formField param whose style isn't form, spaceDelimited, pipeDelimited, or deepObject, the styles an Encoding Object holds — generation fails."
        ),
        [InvalidSecurityOption] = new(
            Severity.Error,
            "A --security value isn't a scheme Rivet can define (bearer[:format], cookie:name, apikey:location:name, or name=<one of those>), or, with --from-openapi, isn't one scheme name — generation or import fails instead of guessing what was meant."
        ),
        [FormFieldAnnotationDropped] = new(
            Severity.Warning,
            "A --from contract's form field states something its form part can't carry: an example's summary, description, or externalValue; an example with no value (only an externalValue or a $ref); its examples when the part's schema is its input record's; or allowEmptyValue (query parameters only) — that much is dropped, and the rest reaches the spec."
        ),
        [CommittedSpecOutOfDate] = new(
            Severity.Error,
            "--verify found the committed spec missing, or different from the spec the input produces now — nothing is written, and the run exits 1."
        ),
        [InputPathNotFound] = new(
            Severity.Error,
            "An input path doesn't exist: the --from contract JSON, the --from-openapi document, a source file, or the --project file — Rivet stops before reading anything."
        ),
        [ImportAliasCycleBroken] = new(
            Severity.Warning,
            "Alias schema is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object."
        ),
        [ImportOperationMethodDropped] = new(
            Severity.Warning,
            "TRACE operation dropped — the HTTP method has no contract representation."
        ),
        [ImportDiscriminatorDropped] = new(
            Severity.Warning,
            "Discriminator with no reversible polymorphic shape (a plain object with neither oneOf nor allOf descendants, an allOf hierarchy C#'s single inheritance can't hold, a discriminator beside a component's own allOf, or a oneOf whose mapping is absent/unusable or whose siblings go beyond the discriminator) — imported without dispatch semantics; or a discriminator mapping entry that points into another file — that entry is dropped and the rest of the discriminator kept."
        ),
        [ImportAliasTargetMissing] = new(
            Severity.Warning,
            "Alias schema references a missing schema — consumers fall back to JsonElement."
        ),
        [ImportUnresolvableAliasReference] = new(
            Severity.Warning,
            "Reference to an unresolvable alias schema (cycle or missing target) — using JsonElement."
        ),
        [ImportUnresolvedSchema] = new(
            Severity.Warning,
            "Schema could not be resolved to a C# type — mapped to JsonElement."
        ),
        [ImportUnsupportedSchemaType] = new(
            Severity.Warning,
            "Unhandled JSON Schema 'type' — mapped to JsonElement."
        ),
        [ImportArrayMissingItems] = new(
            Severity.Warning,
            "Array schema without 'items' — mapped to List<JsonElement>."
        ),
        [ImportDictionaryKeyDropped] = new(
            Severity.Warning,
            "Dictionary 'propertyNames' schema has no C# dictionary-key representation — imported with string keys."
        ),
        [ImportNamedScalarAlgebraUnsupported] = new(
            Severity.Warning,
            "Named scalar uses const, schema composition or heterogeneous leaf types — existing fallback mapping retained."
        ),
        [ImportSwaggerServerNotProjected] = new(
            Severity.Warning,
            "Swagger 2 document declares a host without schemes, or schemes without a host — no server URL is invented from half of one, so no server is imported."
        ),
        [ImportEmptyParameterNameDropped] = new(
            Severity.Warning,
            "Parameter has an empty name — the invalid parameter is dropped while the operation and its other parameters are preserved."
        ),
        [ImportReservedContentTypeHeaderDropped] = new(
            Severity.Warning,
            "Reserved Content-Type header parameter is dropped — request media types are represented by requestBody.content."
        ),
        [ImportReservedAuthorizationHeaderDropped] = new(
            Severity.Warning,
            "Reserved Authorization header parameter is dropped — authentication is represented by security and security schemes."
        ),
        [ImportReservedAcceptHeaderDropped] = new(
            Severity.Warning,
            "Reserved Accept header parameter is dropped — response media types are represented by response content."
        ),
        [ImportBodyForbiddenStatusContentDropped] = new(
            Severity.Warning,
            "Response content authored on a body-forbidden status (1xx/204/205/304) is dropped; HTTP forbids a message body there; the status and its description/headers are preserved."
        ),
        [ImportSchemalessParameterDropped] = new(
            Severity.Warning,
            "Parameter declares content instead of a schema (or, off the path, neither) — Rivet parameters carry a schema, so the parameter is dropped; a path parameter declared with content drops its whole operation, since its route token would be left untyped."
        ),
        [ImportCallbackDropped] = new(
            Severity.Warning,
            "Operation or component callback dropped — Rivet contracts describe calls the API receives, not calls it makes."
        ),
        [ImportPathItemOutsidePathsDropped] = new(
            Severity.Warning,
            "Webhook or components.pathItems entry dropped — only path items under 'paths' become contract endpoints."
        ),
        [ImportEquivalentPathTemplateMerged] = new(
            Severity.Warning,
            "Path repeats an earlier path's template under other parameter names, which OpenAPI forbids — its operations merge into the earlier path and adopt its path parameter names."
        ),
        [ImportUnsatisfiableSecurity] = new(
            Severity.Warning,
            "A security alternative names a scheme components.securitySchemes never defines, so no caller can satisfy it — the alternative is dropped, and an operation left with none is dropped too rather than imported as open."
        ),
        [ImportIntegerEnumValueUnheld] = new(
            Severity.Warning,
            "Integer enum lists values its C# integer type can't hold — the values stay in the spec, but C# code using that type can't read or write them."
        ),
        [InvalidOpenApiDocument] = new(
            Severity.Error,
            "The --from-openapi document is invalid: not a JSON object, one the OpenAPI reader refuses, a security scheme missing its type or a field its type needs, a security requirement naming no scheme, a component example with both value and externalValue, a local reference that targets nothing or loops, a required field missing or mistyped, a path parameter with neither a schema nor content, or an operation whose path template has a token no path parameter declares — import fails before writing anything."
        ),
        [ImportClosedSchemaLeftOpen] = new(
            Severity.Warning,
            "A schema says additionalProperties: false but contradicts itself (another record extends it, it is an allOf composition or a closed allOf branch, it is a polymorphic variant, or it is a generic template with open instances) — its record stays open, accepting and dropping an unknown member instead of refusing it."
        ),
        [CoverageMissingImplementation] = new(
            Severity.Warning,
            "Contract endpoint has no matching implementation."
        ),
        [CoverageHttpMethodMismatch] = new(
            Severity.Warning,
            "Implementation HTTP method differs from the contract's."
        ),
        [CoverageRouteMismatch] = new(
            Severity.Warning,
            "Implementation route differs from the contract's."
        ),
        [CoverageOrphanedBinding] = new(
            Severity.Warning,
            "Contract endpoint is bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns."
        ),
        [CoverageUnbindableEnumParameter] = new(
            Severity.Error,
            "A handler binds a string-wire enum from route, query, header or form, and the host's enum parser (minimal API: exact member name; MVC: member name ignoring case; Azure Functions: a route or query value read as JSON, so no name at all) would refuse an advertised wire value."
        ),
        [CoverageInputBindingMismatch] = new(
            Severity.Error,
            "A handler's bound inputs disagree with its contract's: it binds an input the contract doesn't declare, never binds one it does, or binds one from a different source (route, query, header, body, form). An Azure Functions handler's unattributed parameters count, filled by name from the route, else the query, else a JSON body property."
        ),
        [CoverageSecurityMismatch] = new(
            Severity.Error,
            "A handler's authorization disagrees with its spec's security (the contract field's or [RivetEndpoint] action's own, else the document's default; none at all means no credentials needed): the spec requires credentials while anyone can reach the handler, or the spec admits anonymous callers while the handler requires authentication. With no security scheme declared at all, every such handler under a spec that says nothing is one RIV4008."
        ),
        [CoverageAuthorizationUnreadable] = new(
            Severity.Error,
            "Whether a handler requires authentication depends on authorization setup --check can't read statically (a requirement applied only under a condition, a policy it can't follow, a named policy with no readable registration, a convention on a builder it can't trace to its Map* call, or a Map* call on a builder it can't trace, through locals, conventions, and route-group helpers, to its MapGroup or the app), so --check reports it instead of guessing, unless [AllowAnonymous] or the rest of the setup decides the endpoint either way. Sites under one condition fall together."
        ),
    };

    /// <summary>
    /// The canonical machine-parseable line for a diagnostic, its word taken from the
    /// registry: <c>warning RIV1003: message</c> or <c>error RIV2002: message</c>. For
    /// results that are printed rather than thrown, such as --check's.
    /// </summary>
    public static string Format(string id, string message) =>
        $"{(Lookup(id).Severity == Severity.Error ? "error" : "warning")} {id}: {message}";

    /// <summary>
    /// Writes the canonical warning line to stderr: <c>warning RIV1003: message</c>.
    /// Throws <see cref="InvalidOperationException"/> on an error ID: an error stops the
    /// operation, so it is thrown with <see cref="Error"/>, never printed as a warning.
    /// </summary>
    public static void Warn(string id, string message)
    {
        Require(id, Severity.Warning);
        Console.Error.WriteLine(Format(id, message));
    }

    /// <summary>
    /// The fatal error for an error ID, its message carrying the canonical
    /// <c>error RIV2002: </c> prefix; call sites read
    /// <c>throw Diagnostics.Error(Diagnostics.X, "...")</c>. Throws
    /// <see cref="InvalidOperationException"/> on a warning ID.
    /// </summary>
    internal static RivetUserException Error(string id, string message) =>
        RivetUserException.ForError(id, message);

    /// <summary>
    /// Prefixes a collected warning string (e.g. ImportResult.Warnings) with its ID:
    /// <c>RIV3001: message</c>. Program.cs prepends "warning " when printing,
    /// producing the same canonical stderr line as <see cref="Warn"/>, so an error ID
    /// throws <see cref="InvalidOperationException"/> here as it does there.
    /// </summary>
    public static string Prefix(string id, string message)
    {
        Require(id, Severity.Warning);
        return $"{id}: {message}";
    }

    private static Entry Lookup(string id) =>
        Registry.TryGetValue(id, out var entry)
            ? entry
            : throw new InvalidOperationException($"{id} is not a registered diagnostic ID.");

    internal static void Require(string id, Severity severity)
    {
        var registered = Lookup(id).Severity;
        if (registered != severity)
        {
            throw new InvalidOperationException(
                $"{id} is registered as {registered.ToString().ToLowerInvariant()}, "
                    + $"so it can't be reported as {severity.ToString().ToLowerInvariant()}."
            );
        }
    }
}
