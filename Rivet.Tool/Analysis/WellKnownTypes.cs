using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Pre-resolves ASP.NET, Rivet and infrastructure type symbols from the compilation
/// so walkers compare by symbol instead of by name.
/// </summary>
public sealed class WellKnownTypes(Compilation c)
{
    private const string Mvc = "Microsoft.AspNetCore.Mvc.";

    /// <summary>The compilation these symbols were resolved from.</summary>
    public Compilation Compilation { get; } = c;
    private const string HttpResults = "Microsoft.AspNetCore.Http.HttpResults.";
    private const string Authorization = "Microsoft.AspNetCore.Authorization.";

    private static readonly (string MetadataName, string Verb)[] _httpMethodTable =
    [
        (Mvc + "HttpGetAttribute", "GET"),
        (Mvc + "HttpPostAttribute", "POST"),
        (Mvc + "HttpPutAttribute", "PUT"),
        (Mvc + "HttpDeleteAttribute", "DELETE"),
        (Mvc + "HttpPatchAttribute", "PATCH"),
        (Mvc + "HttpHeadAttribute", "HEAD"),
        (Mvc + "HttpOptionsAttribute", "OPTIONS"),
    ];

    // Only genuinely fixed statuses live here. ProblemHttpResult (a Results.Problem
    // branch can carry any status) and JsonHttpResult<T> (Results.Json selects its
    // own status) are not entries — a Results<...> branch using them is an
    // unresolved contract.
    private static readonly (string MetadataName, int Status)[] _typedResultTable =
    [
        (HttpResults + "Ok`1", 200),
        (HttpResults + "Ok", 200),
        (HttpResults + "Created`1", 201),
        (HttpResults + "Created", 201),
        (HttpResults + "Accepted`1", 202),
        (HttpResults + "Accepted", 202),
        (HttpResults + "NoContent", 204),
        (HttpResults + "BadRequest`1", 400),
        (HttpResults + "BadRequest", 400),
        (HttpResults + "UnauthorizedHttpResult", 401),
        (HttpResults + "NotFound`1", 404),
        (HttpResults + "NotFound", 404),
        (HttpResults + "Conflict`1", 409),
        (HttpResults + "Conflict", 409),
        (HttpResults + "UnprocessableEntity`1", 422),
        (HttpResults + "UnprocessableEntity", 422),
        (HttpResults + "ValidationProblem", 400),
        (HttpResults + "ForbidHttpResult", 403),
        (HttpResults + "InternalServerError", 500),
        (HttpResults + "InternalServerError`1", 500),
    ];

    // Binding and response attributes
    public INamedTypeSymbol? Route { get; } = c.GetTypeByMetadataName(Mvc + "RouteAttribute");
    public INamedTypeSymbol? FromBody { get; } = c.GetTypeByMetadataName(Mvc + "FromBodyAttribute");
    public INamedTypeSymbol? FromForm { get; } = c.GetTypeByMetadataName(Mvc + "FromFormAttribute");
    public INamedTypeSymbol? FromQuery { get; } =
        c.GetTypeByMetadataName(Mvc + "FromQueryAttribute");
    public INamedTypeSymbol? FromRoute { get; } =
        c.GetTypeByMetadataName(Mvc + "FromRouteAttribute");
    public INamedTypeSymbol? FromHeader { get; } =
        c.GetTypeByMetadataName(Mvc + "FromHeaderAttribute");
    public INamedTypeSymbol? FromServices { get; } =
        c.GetTypeByMetadataName(Mvc + "FromServicesAttribute");
    public INamedTypeSymbol? ModelBinder { get; } =
        c.GetTypeByMetadataName(Mvc + "ModelBinderAttribute");

    // Implemented by [ApiController]: marks an API controller, whose unattributed complex
    // parameters MVC infers as [FromBody].
    public INamedTypeSymbol? ApiBehaviorMetadata { get; } =
        c.GetTypeByMetadataName(Mvc + "Infrastructure.IApiBehaviorMetadata");
    public INamedTypeSymbol? AsParameters { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Http.AsParametersAttribute");
    public INamedTypeSymbol? Consumes { get; } = c.GetTypeByMetadataName(Mvc + "ConsumesAttribute");
    public INamedTypeSymbol? ProducesResponseType { get; } =
        c.GetTypeByMetadataName(Mvc + "ProducesResponseTypeAttribute");

    // The .NET 7+ generic [ProducesResponseType<T>] is a distinct symbol.
    public INamedTypeSymbol? ProducesResponseTypeOfT { get; } =
        c.GetTypeByMetadataName(Mvc + "ProducesResponseTypeAttribute`1");
    public INamedTypeSymbol? Produces { get; } = c.GetTypeByMetadataName(Mvc + "ProducesAttribute");
    public INamedTypeSymbol? RivetRequestExample { get; } =
        RivetType(c, "RivetRequestExampleAttribute");
    public INamedTypeSymbol? RivetResponseExample { get; } =
        RivetType(c, "RivetResponseExampleAttribute");
    public INamedTypeSymbol? RivetRequestBody { get; } = RivetType(c, "RivetRequestBodyAttribute");
    public INamedTypeSymbol? ProducesFile { get; } = RivetType(c, "ProducesFileAttribute");

    // Member and type metadata attributes
    public INamedTypeSymbol? RivetFormat { get; } = RivetType(c, "RivetFormatAttribute");
    public INamedTypeSymbol? RivetSchemaType { get; } = RivetType(c, "RivetSchemaTypeAttribute");
    public INamedTypeSymbol? RivetSchemaRef { get; } = RivetType(c, "RivetSchemaRefAttribute");
    public INamedTypeSymbol? RivetDefault { get; } = RivetType(c, "RivetDefaultAttribute");
    public INamedTypeSymbol? RivetConstraints { get; } = RivetType(c, "RivetConstraintsAttribute");
    public INamedTypeSymbol? RivetDescription { get; } = RivetType(c, "RivetDescriptionAttribute");
    public INamedTypeSymbol? RivetExample { get; } = RivetType(c, "RivetExampleAttribute");
    public INamedTypeSymbol? RivetReadOnly { get; } = RivetType(c, "RivetReadOnlyAttribute");
    public INamedTypeSymbol? RivetWriteOnly { get; } = RivetType(c, "RivetWriteOnlyAttribute");
    public INamedTypeSymbol? RivetOptional { get; } = RivetType(c, "RivetOptionalAttribute");
    public INamedTypeSymbol? RivetHeader { get; } = RivetType(c, "RivetHeaderAttribute");
    public INamedTypeSymbol? RivetScalar { get; } = RivetType(c, "RivetScalarAttribute");
    public INamedTypeSymbol? RivetUnion { get; } = RivetType(c, "RivetUnionAttribute");
    public INamedTypeSymbol? RivetGeneratedType { get; } =
        RivetType(c, "RivetGeneratedTypeAttribute");
    public INamedTypeSymbol? RivetGeneratedSchema { get; } =
        RivetType(c, "RivetGeneratedSchemaAttribute");
    public INamedTypeSymbol? RivetGeneratedSchemaMetadata { get; } =
        RivetType(c, "RivetGeneratedSchemaMetadataAttribute");
    public INamedTypeSymbol? Obsolete { get; } =
        c.GetTypeByMetadataName("System.ObsoleteAttribute");
    public INamedTypeSymbol? Flags { get; } = c.GetTypeByMetadataName("System.FlagsAttribute");

    // System.Text.Json
    public INamedTypeSymbol? JsonPropertyName { get; } = Stj(c, "JsonPropertyNameAttribute");
    public INamedTypeSymbol? JsonIgnore { get; } = Stj(c, "JsonIgnoreAttribute");
    public INamedTypeSymbol? JsonExtensionData { get; } = Stj(c, "JsonExtensionDataAttribute");
    public INamedTypeSymbol? JsonInclude { get; } = Stj(c, "JsonIncludeAttribute");
    public INamedTypeSymbol? JsonConstructor { get; } = Stj(c, "JsonConstructorAttribute");
    public INamedTypeSymbol? JsonPolymorphic { get; } = Stj(c, "JsonPolymorphicAttribute");
    public INamedTypeSymbol? JsonDerivedType { get; } = Stj(c, "JsonDerivedTypeAttribute");
    public INamedTypeSymbol? JsonConverter { get; } = Stj(c, "JsonConverterAttribute");
    public INamedTypeSymbol? JsonNumberHandling { get; } = Stj(c, "JsonNumberHandlingAttribute");
    public INamedTypeSymbol? JsonStringEnumMemberName { get; } =
        Stj(c, "JsonStringEnumMemberNameAttribute");
    public INamedTypeSymbol? JsonUnmappedMemberHandling { get; } =
        Stj(c, "JsonUnmappedMemberHandlingAttribute");
    public INamedTypeSymbol? JsonRequired { get; } = Stj(c, "JsonRequiredAttribute");

    // A [JsonExtensionData] bag is a JsonObject or an IDictionary<string, object> or
    // IDictionary<string, JsonElement>.
    public INamedTypeSymbol? JsonElement { get; } =
        c.GetTypeByMetadataName("System.Text.Json.JsonElement");
    public INamedTypeSymbol? IDictionaryOfTKeyTValue { get; } =
        c.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2");

    // DataAnnotations
    public INamedTypeSymbol? MinLength { get; } = DataAnnotation(c, "MinLengthAttribute");
    public INamedTypeSymbol? MaxLength { get; } = DataAnnotation(c, "MaxLengthAttribute");
    public INamedTypeSymbol? Length { get; } = DataAnnotation(c, "LengthAttribute");
    public INamedTypeSymbol? StringLength { get; } = DataAnnotation(c, "StringLengthAttribute");
    public INamedTypeSymbol? Range { get; } = DataAnnotation(c, "RangeAttribute");
    public INamedTypeSymbol? RegularExpression { get; } =
        DataAnnotation(c, "RegularExpressionAttribute");
    public INamedTypeSymbol? EmailAddress { get; } = DataAnnotation(c, "EmailAddressAttribute");
    public INamedTypeSymbol? Url { get; } = DataAnnotation(c, "UrlAttribute");

    // Task wrappers (OriginalDefinition for generic matching)
    public INamedTypeSymbol? TaskOfT { get; } =
        c.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
    public INamedTypeSymbol? Task { get; } = c.GetTypeByMetadataName("System.Threading.Tasks.Task");
    public INamedTypeSymbol? ValueTaskOfT { get; } =
        c.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
    public INamedTypeSymbol? ValueTask { get; } =
        c.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");

    // MVC result types
    public INamedTypeSymbol? ActionResultOfT { get; } =
        c.GetTypeByMetadataName(Mvc + "ActionResult`1");
    public INamedTypeSymbol? ActionResult { get; } = c.GetTypeByMetadataName(Mvc + "ActionResult");
    public INamedTypeSymbol? IActionResult { get; } =
        c.GetTypeByMetadataName(Mvc + "IActionResult");
    public INamedTypeSymbol? IResult { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Http.IResult");
    public INamedTypeSymbol? IFormFile { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Http.IFormFile");

    // Contract builder chain and coverage analysis
    public INamedTypeSymbol? Define { get; } = RivetType(c, "Define");
    public INamedTypeSymbol? RouteDefinitionBase { get; } = RivetType(c, "RouteDefinitionBase`1");
    public INamedTypeSymbol? RouteDefinition { get; } = RivetType(c, "RouteDefinition");
    public INamedTypeSymbol? RouteDefinitionOfT { get; } = RivetType(c, "RouteDefinition`1");
    public INamedTypeSymbol? RouteDefinitionOfTInputTOutput { get; } =
        RivetType(c, "RouteDefinition`2");
    public INamedTypeSymbol? InputRouteDefinitionOfT { get; } =
        RivetType(c, "InputRouteDefinition`1");
    public INamedTypeSymbol? FileRouteDefinition { get; } = RivetType(c, "FileRouteDefinition");
    public INamedTypeSymbol? FileRouteDefinitionOfT { get; } =
        RivetType(c, "FileRouteDefinition`1");
    public INamedTypeSymbol? BoundRouteDefinition { get; } = RivetType(c, "BoundRouteDefinition");
    public INamedTypeSymbol? BoundRouteDefinitionOfT { get; } =
        RivetType(c, "BoundRouteDefinition`1");
    public INamedTypeSymbol? BoundFileRouteDefinition { get; } =
        RivetType(c, "BoundFileRouteDefinition");
    public INamedTypeSymbol? EndpointRouteBuilderExtensions { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions");
    public INamedTypeSymbol? Function { get; } =
        c.GetTypeByMetadataName("Microsoft.Azure.Functions.Worker.FunctionAttribute");
    public INamedTypeSymbol? HttpTrigger { get; } =
        c.GetTypeByMetadataName("Microsoft.Azure.Functions.Worker.HttpTriggerAttribute");

    // Contract security on controllers (K.3)
    public INamedTypeSymbol? RivetSecure { get; } = RivetType(c, "RivetSecureAttribute");
    public INamedTypeSymbol? RivetAndSecure { get; } = RivetType(c, "RivetAndSecureAttribute");
    public INamedTypeSymbol? RivetOrAnonymous { get; } = RivetType(c, "RivetOrAnonymousAttribute");
    public INamedTypeSymbol? RivetAnonymous { get; } = RivetType(c, "RivetAnonymousAttribute");

    // ASP.NET authorization (K.5): the attributes and the metadata interfaces they implement,
    // the options and builder a fallback policy is set on, the static classes declaring
    // AddAuthorization/AddAuthorizationBuilder and AddAuthorizationCore, the endpoint
    // builders' RequireAuthorization/AllowAnonymous extensions, and MapGroup's builder. They
    // span assemblies: Microsoft.AspNetCore.Authorization (attributes, options, builder,
    // AddAuthorizationCore), Microsoft.AspNetCore.Metadata (the two interfaces) and
    // Microsoft.AspNetCore.Authorization.Policy (AddAuthorization and the endpoint extensions).
    public INamedTypeSymbol? Authorize { get; } =
        c.GetTypeByMetadataName(Authorization + "AuthorizeAttribute");
    public INamedTypeSymbol? AllowAnonymous { get; } =
        c.GetTypeByMetadataName(Authorization + "AllowAnonymousAttribute");
    public INamedTypeSymbol? IAuthorizeData { get; } =
        c.GetTypeByMetadataName(Authorization + "IAuthorizeData");
    public INamedTypeSymbol? IAllowAnonymous { get; } =
        c.GetTypeByMetadataName(Authorization + "IAllowAnonymous");
    public INamedTypeSymbol? AuthorizationOptions { get; } =
        c.GetTypeByMetadataName(Authorization + "AuthorizationOptions");
    public INamedTypeSymbol? AuthorizationBuilder { get; } =
        c.GetTypeByMetadataName(Authorization + "AuthorizationBuilder");
    public INamedTypeSymbol? AuthorizationEndpointConventionBuilderExtensions { get; } =
        c.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions"
        );
    public INamedTypeSymbol? PolicyServiceCollectionExtensions { get; } =
        c.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions"
        );
    public INamedTypeSymbol? AuthorizationServiceCollectionExtensions { get; } =
        c.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.AuthorizationServiceCollectionExtensions"
        );
    public INamedTypeSymbol? RouteGroupBuilder { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Routing.RouteGroupBuilder");
    public INamedTypeSymbol? FunctionsAuthorizationLevel { get; } =
        c.GetTypeByMetadataName("Microsoft.Azure.Functions.Worker.AuthorizationLevel");

    // ...and what K.5 reads authorization from beyond them: an MVC AuthorizeFilter and the
    // FilterCollection it's added to, MapControllers()' declaring class and the convention
    // builder it returns, a minimal API's RouteHandlerBuilder, and a policy, built or
    // passed whole.
    public INamedTypeSymbol? AuthorizeFilter { get; } =
        c.GetTypeByMetadataName(Mvc + "Authorization.AuthorizeFilter");
    public INamedTypeSymbol? FilterCollection { get; } =
        c.GetTypeByMetadataName(Mvc + "Filters.FilterCollection");
    public INamedTypeSymbol? ControllerEndpointRouteBuilderExtensions { get; } =
        c.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions"
        );
    public INamedTypeSymbol? ControllerActionEndpointConventionBuilder { get; } =
        c.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Builder.ControllerActionEndpointConventionBuilder"
        );
    public INamedTypeSymbol? RouteHandlerBuilder { get; } =
        c.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.RouteHandlerBuilder");
    public INamedTypeSymbol? AuthorizationPolicy { get; } =
        c.GetTypeByMetadataName(Authorization + "AuthorizationPolicy");
    public INamedTypeSymbol? AuthorizationPolicyBuilder { get; } =
        c.GetTypeByMetadataName(Authorization + "AuthorizationPolicyBuilder");

    /// <summary>HTTP method attribute symbol → verb ("GET", "POST", …).</summary>
    public ImmutableDictionary<INamedTypeSymbol, string> HttpMethodAttributes { get; } =
        Resolve(c, _httpMethodTable);

    /// <summary>Typed result OriginalDefinition → fixed HTTP status code.</summary>
    public ImmutableDictionary<INamedTypeSymbol, int> TypedResultStatusCodes { get; } =
        Resolve(c, _typedResultTable);

    /// <summary>Results&lt;T1, T2, ...&gt; arities 2–6.</summary>
    public ImmutableHashSet<INamedTypeSymbol> ResultsArities { get; } =
        Enumerable
            .Range(2, 5)
            .Select(arity => c.GetTypeByMetadataName($"{HttpResults}Results`{arity}"))
            .OfType<INamedTypeSymbol>()
            .ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    private static INamedTypeSymbol? RivetType(Compilation c, string name) =>
        c.GetTypeByMetadataName("Rivet." + name);

    private static INamedTypeSymbol? Stj(Compilation c, string name) =>
        c.GetTypeByMetadataName("System.Text.Json.Serialization." + name);

    private static INamedTypeSymbol? DataAnnotation(Compilation c, string name) =>
        c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations." + name);

    private static ImmutableDictionary<INamedTypeSymbol, T> Resolve<T>(
        Compilation compilation,
        (string MetadataName, T Value)[] table
    )
    {
        var builder = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, T>(
            SymbolEqualityComparer.Default
        );
        foreach (var (metadataName, value) in table)
        {
            if (compilation.GetTypeByMetadataName(metadataName) is { } symbol)
            {
                builder.Add(symbol, value);
            }
        }
        return builder.ToImmutable();
    }
}
