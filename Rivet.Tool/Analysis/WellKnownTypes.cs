using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Pre-resolves ASP.NET and infrastructure type symbols from the compilation
/// so walkers can compare via SymbolEqualityComparer instead of ToDisplayString().
/// </summary>
public sealed class WellKnownTypes
{
    // HTTP method attributes
    public readonly INamedTypeSymbol? HttpGet;
    public readonly INamedTypeSymbol? HttpPost;
    public readonly INamedTypeSymbol? HttpPut;
    public readonly INamedTypeSymbol? HttpDelete;
    public readonly INamedTypeSymbol? HttpPatch;

    // Binding attributes
    public readonly INamedTypeSymbol? Route;
    public readonly INamedTypeSymbol? FromBody;
    public readonly INamedTypeSymbol? FromForm;
    public readonly INamedTypeSymbol? FromQuery;
    public readonly INamedTypeSymbol? FromRoute;
    public readonly INamedTypeSymbol? FromHeader;
    public readonly INamedTypeSymbol? FromServices;

    // Response metadata
    public readonly INamedTypeSymbol? ProducesResponseType;
    public readonly INamedTypeSymbol? ProducesResponseTypeOfT;
    public readonly INamedTypeSymbol? RivetRequestExample;
    public readonly INamedTypeSymbol? RivetResponseExample;

    // Task wrappers (OriginalDefinition for generic matching)
    public readonly INamedTypeSymbol? TaskOfT;
    public readonly INamedTypeSymbol? Task;
    public readonly INamedTypeSymbol? ValueTaskOfT;
    public readonly INamedTypeSymbol? ValueTask;

    // MVC result types
    public readonly INamedTypeSymbol? ActionResultOfT;
    public readonly INamedTypeSymbol? ActionResult;
    public readonly INamedTypeSymbol? IActionResult;

    // Infrastructure
    public readonly INamedTypeSymbol? CancellationToken;
    public readonly INamedTypeSymbol? IFormFile;

    // Typed HTTP results — generic variants
    public readonly INamedTypeSymbol? OkOfT;
    public readonly INamedTypeSymbol? CreatedOfT;
    public readonly INamedTypeSymbol? AcceptedOfT;
    public readonly INamedTypeSymbol? BadRequestOfT;
    public readonly INamedTypeSymbol? NotFoundOfT;
    public readonly INamedTypeSymbol? ConflictOfT;
    public readonly INamedTypeSymbol? UnprocessableEntityOfT;

    // Typed HTTP results — non-generic variants
    public readonly INamedTypeSymbol? Ok;
    public readonly INamedTypeSymbol? Created;
    public readonly INamedTypeSymbol? Accepted;
    public readonly INamedTypeSymbol? NoContent;
    public readonly INamedTypeSymbol? BadRequest;
    public readonly INamedTypeSymbol? Unauthorized;
    public readonly INamedTypeSymbol? NotFound;
    public readonly INamedTypeSymbol? Conflict;
    public readonly INamedTypeSymbol? UnprocessableEntity;

    // A8: previously-unmapped typed results — Results<> branches using these were
    // silently dropped from the contract
    public readonly INamedTypeSymbol? ProblemHttpResult;
    public readonly INamedTypeSymbol? ValidationProblem;
    public readonly INamedTypeSymbol? ForbidHttpResult;
    public readonly INamedTypeSymbol? InternalServerError;
    public readonly INamedTypeSymbol? InternalServerErrorOfT;
    public readonly INamedTypeSymbol? JsonHttpResultOfT;

    /// <summary>
    /// Maps HTTP method attribute symbol → verb string ("GET", "POST", etc.).
    /// </summary>
    public readonly ImmutableDictionary<INamedTypeSymbol, string> HttpMethodAttributes;

    /// <summary>
    /// Maps typed result OriginalDefinition → HTTP status code.
    /// </summary>
    public readonly ImmutableDictionary<INamedTypeSymbol, int> TypedResultStatusCodes;

    /// <summary>
    /// Results&lt;T1, T2, ...&gt; arities 2–6.
    /// </summary>
    public readonly ImmutableHashSet<INamedTypeSymbol> ResultsArities;

    public WellKnownTypes(Compilation compilation)
    {
        // HTTP method attributes
        HttpGet = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.HttpGetAttribute");
        HttpPost = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.HttpPostAttribute");
        HttpPut = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.HttpPutAttribute");
        HttpDelete = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"
        );
        HttpPatch = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.HttpPatchAttribute"
        );

        // Binding attributes
        Route = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.RouteAttribute");
        FromBody = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.FromBodyAttribute");
        FromForm = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.FromFormAttribute");
        FromQuery = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.FromQueryAttribute"
        );
        FromRoute = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.FromRouteAttribute"
        );
        FromHeader = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.FromHeaderAttribute"
        );
        FromServices = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.FromServicesAttribute"
        );

        // Response metadata
        ProducesResponseType = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute"
        );
        // A7: the .NET 7+ generic [ProducesResponseType<T>] is a distinct symbol
        ProducesResponseTypeOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute`1"
        );
        RivetRequestExample = compilation.GetTypeByMetadataName(
            "Rivet.RivetRequestExampleAttribute"
        );
        RivetResponseExample = compilation.GetTypeByMetadataName(
            "Rivet.RivetResponseExampleAttribute"
        );

        // Task wrappers
        TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        ValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");

        // MVC result types
        ActionResultOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ActionResult`1"
        );
        ActionResult = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ActionResult");
        IActionResult = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.IActionResult");

        // Infrastructure
        CancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        IFormFile = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.IFormFile");

        // Typed HTTP results — generic
        OkOfT = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Ok`1");
        CreatedOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Created`1"
        );
        AcceptedOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Accepted`1"
        );
        BadRequestOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.BadRequest`1"
        );
        NotFoundOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.NotFound`1"
        );
        ConflictOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Conflict`1"
        );
        UnprocessableEntityOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.UnprocessableEntity`1"
        );

        // Typed HTTP results — non-generic
        Ok = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Ok");
        Created = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Created"
        );
        Accepted = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Accepted"
        );
        NoContent = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.NoContent"
        );
        BadRequest = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.BadRequest"
        );
        Unauthorized = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult"
        );
        NotFound = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.NotFound"
        );
        Conflict = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.Conflict"
        );
        UnprocessableEntity = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.UnprocessableEntity"
        );

        // A8: typed results that were missing from the mapping table
        ProblemHttpResult = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult"
        );
        ValidationProblem = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.ValidationProblem"
        );
        ForbidHttpResult = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult"
        );
        InternalServerError = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.InternalServerError"
        );
        InternalServerErrorOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.InternalServerError`1"
        );
        JsonHttpResultOfT = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult`1"
        );

        // Build convenience dictionaries
        HttpMethodAttributes = BuildHttpMethodAttributes();
        TypedResultStatusCodes = BuildTypedResultStatusCodes();
        ResultsArities = BuildResultsArities(compilation);
    }

    private ImmutableDictionary<INamedTypeSymbol, string> BuildHttpMethodAttributes()
    {
        var builder = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, string>(
            SymbolEqualityComparer.Default
        );
        TryAdd(builder, HttpGet, "GET");
        TryAdd(builder, HttpPost, "POST");
        TryAdd(builder, HttpPut, "PUT");
        TryAdd(builder, HttpDelete, "DELETE");
        TryAdd(builder, HttpPatch, "PATCH");
        return builder.ToImmutable();
    }

    private ImmutableDictionary<INamedTypeSymbol, int> BuildTypedResultStatusCodes()
    {
        var builder = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, int>(
            SymbolEqualityComparer.Default
        );
        TryAdd(builder, OkOfT, 200);
        TryAdd(builder, Ok, 200);
        TryAdd(builder, CreatedOfT, 201);
        TryAdd(builder, Created, 201);
        TryAdd(builder, AcceptedOfT, 202);
        TryAdd(builder, Accepted, 202);
        TryAdd(builder, NoContent, 204);
        TryAdd(builder, BadRequestOfT, 400);
        TryAdd(builder, BadRequest, 400);
        TryAdd(builder, Unauthorized, 401);
        TryAdd(builder, NotFoundOfT, 404);
        TryAdd(builder, NotFound, 404);
        TryAdd(builder, ConflictOfT, 409);
        TryAdd(builder, Conflict, 409);
        TryAdd(builder, UnprocessableEntityOfT, 422);
        TryAdd(builder, UnprocessableEntity, 422);
        // A8: ProblemHttpResult defaults to 500, ValidationProblem is a 400
        // ValidationProblemDetails, ForbidHttpResult is 403, InternalServerError[<T>]
        // is 500 (.NET 9), JsonHttpResult<T> is a 200 with a JSON body
        TryAdd(builder, ProblemHttpResult, 500);
        TryAdd(builder, ValidationProblem, 400);
        TryAdd(builder, ForbidHttpResult, 403);
        TryAdd(builder, InternalServerError, 500);
        TryAdd(builder, InternalServerErrorOfT, 500);
        TryAdd(builder, JsonHttpResultOfT, 200);
        return builder.ToImmutable();
    }

    private static ImmutableHashSet<INamedTypeSymbol> BuildResultsArities(Compilation compilation)
    {
        var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(
            SymbolEqualityComparer.Default
        );
        for (var arity = 2; arity <= 6; arity++)
        {
            var symbol = compilation.GetTypeByMetadataName(
                $"Microsoft.AspNetCore.Http.HttpResults.Results`{arity}"
            );
            if (symbol is not null)
            {
                builder.Add(symbol);
            }
        }
        return builder.ToImmutable();
    }

    private static void TryAdd<T>(
        ImmutableDictionary<INamedTypeSymbol, T>.Builder builder,
        INamedTypeSymbol? symbol,
        T value
    )
    {
        if (symbol is not null)
        {
            builder.Add(symbol, value);
        }
    }
}
