namespace Rivet;

// Builder methods whose arguments (all or some) only Rivet.Tool reads, from the call syntax at
// generation time. At runtime they record nothing beyond what terminals and adapters validate
// (the request-body and parameter markers record that they were called, and .RequestContent its
// media type, which decide where TInput binds); they still throw once the definition is published.
// Their parameters are deliberately unused, so the local .editorconfig turns IDE0060 off for
// this file only. Parameter names are part of the Tool contract (named arguments): don't rename.
public abstract partial class RouteDefinitionBase<TSelf>
{
    private TSelf Marker() => Mutate(static state => state);

    public TSelf Returns<TResponse>(int statusCode) =>
        AddErrorResponse(statusCode, typeof(TResponse));

    public TSelf Returns<TResponse>(int statusCode, string? description) =>
        AddErrorResponse(statusCode, typeof(TResponse));

    public TSelf Returns<TResponse>(string statusKey, string? description = null) =>
        AddErrorResponse(statusKey, typeof(TResponse));

    public TSelf Returns(int statusCode) => AddErrorResponse(statusCode, null);

    public TSelf Returns(int statusCode, string? description) => AddErrorResponse(statusCode, null);

    public TSelf Returns(string statusKey, string? description = null) =>
        AddErrorResponse(statusKey, null);

    public TSelf Summary(string summary) => Marker();

    public TSelf Description(string description) => Marker();

    /// <summary>
    /// Carries the source OpenAPI response key and primary response description through
    /// generated C#. Concrete runtime status behavior remains controlled by <see cref="Status"/>.
    /// The key must be one OpenAPI can key a response by (an exact status, an nXX range, or
    /// default), as for <see cref="Returns(string, string?)"/>: generation refuses anything
    /// else as RIV1120.
    /// </summary>
    public TSelf StatusKey(string statusKey, string? description = null) =>
        Mutate(state =>
            state with
            {
                SuccessStatusKey = (
                    ResponseStatusKey.Parse(statusKey)
                    ?? throw new InvalidOperationException(
                        $"{Method} {Route}: success status key '{statusKey}' is not an exact status, nXX range, or default."
                    )
                ).Key,
            }
        );

    /// <summary>
    /// Declares the request body's media type when it is not application/json
    /// (e.g. "text/plain" for a string body). The body SCHEMA is unchanged —
    /// this overrides only the content-type key the spec declares. For raw
    /// binary bodies use .AcceptsBinary(); for forms use .FormEncoded().
    /// </summary>
    public TSelf AcceptsContentType(string contentType) =>
        Mutate(state =>
        {
            if (state.FormEncoded || state.BinaryRequestContentType is not null)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .AcceptsContentType() cannot be combined with "
                        + ".FormEncoded() or .AcceptsBinary() — those already declare the body media type."
                );
            }

            return state;
        });

    /// <summary>
    /// Declares a response header on the given status code. Emitting Location/ETag/... is
    /// handler code. <paramref name="required"/> promises the header is always present, and
    /// the adapters hold the handler to it: a response at that status written without the
    /// header is a <see cref="RivetContractViolationException"/>. The other arguments are
    /// spec-only.
    /// </summary>
    public TSelf WithResponseHeader(
        int statusCode,
        string name,
        string? description = null,
        bool required = false
    ) => AddRequiredHeader(statusCode.ToString(), name, required);

    public TSelf WithResponseHeader<THeader>(
        int statusCode,
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) => AddRequiredHeader(statusCode.ToString(), name, required);

    public TSelf WithResponseHeaderKey(
        string statusKey,
        string name,
        string? description = null,
        bool required = false
    ) => AddRequiredHeader(statusKey, name, required);

    public TSelf WithResponseHeaderKey<THeader>(
        string statusKey,
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) => AddRequiredHeader(statusKey, name, required);

    /// <summary>
    /// Declares a response header on the endpoint's success status.
    /// See <see cref="WithResponseHeader(int, string, string?, bool)"/>.
    /// </summary>
    public TSelf WithResponseHeader(
        string name,
        string? description = null,
        bool required = false
    ) => AddRequiredHeader(null, name, required);

    public TSelf WithResponseHeader<THeader>(
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) => AddRequiredHeader(null, name, required);

    public TSelf RequestExampleJson(
        string? json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null,
        string? summary = null,
        string? description = null
    ) => Marker();

    public TSelf RequestExampleRef(
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    ) => Marker();

    public TSelf ResponseExampleJson(
        int statusCode,
        string? json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null,
        string? summary = null,
        string? description = null
    ) => Marker();

    public TSelf ResponseExampleJson(
        string statusKey,
        string? json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null,
        string? summary = null,
        string? description = null
    ) => Marker();

    public TSelf ResponseExampleRef(
        int statusCode,
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    ) => Marker();

    public TSelf ResponseExampleRef(
        string statusKey,
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    ) => Marker();

    /// <summary>
    /// No credentials required (<c>security: []</c>). Stands alone: an endpoint that also
    /// accepts credentials uses <see cref="Secure"/> plus <see cref="OrAnonymous"/>.
    /// </summary>
    public TSelf Anonymous() => Marker();

    /// <summary>
    /// Adds one way to authenticate: the named scheme, with the scopes it must grant. Each
    /// call is an alternative (OpenAPI ORs them); <see cref="AndSecure"/> adds schemes the
    /// same alternative also needs.
    /// </summary>
    public TSelf Secure(string scheme, params string[] scopes) => Marker();

    /// <summary>
    /// Adds a scheme, with its scopes, to the alternative the previous <see cref="Secure"/>
    /// started: the caller must satisfy both.
    /// </summary>
    public TSelf AndSecure(string scheme, params string[] scopes) => Marker();

    /// <summary>
    /// Adds an alternative that needs no credentials (an empty requirement, <c>{}</c>):
    /// authentication is optional.
    /// </summary>
    public TSelf OrAnonymous() => Marker();

    public TSelf RequestContent<T>(
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null
    ) => AddRequestContentType(mediaType);

    public TSelf RequestContent(string mediaType) => AddRequestContentType(mediaType);

    private TSelf AddRequestContentType(string mediaType) =>
        Mutate(state =>
            state with
            {
                RequestContentTypes = state.RequestContentTypes.Add(mediaType),
            }
        );

    public TSelf RequestBinaryContent(string mediaType) =>
        Mutate(static state => state with { RequestBinaryContentDeclared = true });

    public TSelf RequestBodyRequired(bool required) =>
        Mutate(static state => state with { RequestBodyDeclared = true });

    public TSelf RequestBody() => Mutate(static state => state with { RequestBodyDeclared = true });

    public TSelf Parameter<T>(
        string name,
        string location,
        bool required,
        string? schemaType = null,
        string? format = null,
        string? metadataJson = null,
        string? schemaRef = null
    ) => Mutate(static state => state with { ParametersDeclared = true });

    /// <summary>
    /// Opts this endpoint into query-based authentication, where the auth token is passed
    /// as a query parameter instead of a header. Primarily intended for media players
    /// (ExoPlayer, HLS.js) that cannot inject custom headers on segment requests.
    /// </summary>
    public TSelf QueryAuth(string parameterName = "token") => Marker();

    // The runtime keeps the payload type and media type for terminal validation; the
    // schema arguments are spec-only.
    public TSelf ResponseContent<T>(
        int statusCode,
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null,
        string? schemaDescription = null
    ) => AddResponseContent(statusCode.ToString(), mediaType, typeof(T), isBinary: false);

    public TSelf ResponseContent<T>(
        string statusKey,
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null,
        string? schemaDescription = null
    ) => AddResponseContent(statusKey, mediaType, typeof(T), isBinary: false);
}
