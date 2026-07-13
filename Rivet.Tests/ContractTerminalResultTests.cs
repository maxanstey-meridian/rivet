using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Tests;

public sealed class ContractTerminalResultTests
{
    [Fact]
    public async Task BoundSuccess_ReturnsDeclaredResult()
    {
        var route = Define
            .Post<CreateItemRequest, ItemDto>("/api/items")
            .Status(StatusCodes.Status201Created)
            .Returns<ErrorDto>(StatusCodes.Status409Conflict, "Conflict");

        var request = new CreateItemRequest("Widget");
        var response = route.Bind(request).Success(new ItemDto("item_1", request.Name));
        var result = await ExecuteAsync(response);

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        Assert.Equal("Widget", Deserialize<ItemDto>(result).Name);
    }

    [Fact]
    public async Task Error_ReturnsDeclaredResult()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var result = await ExecuteAsync(
            route.Error(StatusCodes.Status404NotFound, new NotFoundDto("Missing item"))
        );

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Missing item", Deserialize<NotFoundDto>(result).Message);
    }

    [Fact]
    public async Task Success_VoidContract_ReturnsNoContent()
    {
        var route = Define
            .Delete("/api/items/{id}")
            .Status(StatusCodes.Status204NoContent)
            .Returns(StatusCodes.Status404NotFound, "Not found");

        var result = await ExecuteAsync(route.Success());

        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task Success_VoidDeleteContract_WithoutExplicitStatus_DefaultsTo204()
    {
        // A1 repro: no .Status(204) — the runtime default for void DELETE must itself be 204.
        var route = Define
            .Delete("/api/items/{id}")
            .Returns(StatusCodes.Status404NotFound, "Not found");

        Assert.Equal(StatusCodes.Status204NoContent, route.SuccessStatusCode);

        var result = await ExecuteAsync(route.Success());

        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
        Assert.Empty(result.Body);
    }

    [Fact]
    public void Returns_DuplicateStatus_Throws()
    {
        // R1 guard: duplicate .Returns for the same status used to be appended silently and
        // become ambiguous during terminal response resolution.
        // The builder must reject the duplicate registration immediately instead.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define
                .Get<ItemDto>("/api/items/{id}")
                .Returns<ErrorDto>(StatusCodes.Status404NotFound)
                .Returns<NotFoundDto>(StatusCodes.Status404NotFound)
        );

        Assert.Contains("404", exception.Message);
        Assert.Contains("already declared", exception.Message);
    }

    [Fact]
    public void Returns_DuplicateStatus_UntypedOverload_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define
                .Delete("/api/items/{id}")
                .Returns(StatusCodes.Status404NotFound)
                .Returns(StatusCodes.Status404NotFound, "Not found")
        );

        Assert.Contains("404", exception.Message);
        Assert.Contains("already declared", exception.Message);
    }

    [Fact]
    public void Returns_Rejects_Existing_Success_Status()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define
                .Get<ItemDto>("/api/items/{id}")
                .Status(StatusCodes.Status202Accepted)
                .Returns<ErrorDto>(StatusCodes.Status202Accepted)
        );

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Status_Rejects_Existing_Returns_Status()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define
                .Get<ItemDto>("/api/items/{id}")
                .Returns<ErrorDto>(StatusCodes.Status202Accepted)
                .Status(StatusCodes.Status202Accepted)
        );

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Accepts_Preserves_Explicit_Status_State()
    {
        var route = Define
            .Put("/api/items/{id}")
            .Status(StatusCodes.Status202Accepted)
            .Accepts<UpdateItemRequest>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Status(StatusCodes.Status203NonAuthoritative)
        );

        Assert.Contains("Status already set", exception.Message);
    }

    [Fact]
    public void Accepts_Preserves_Success_Status_Collision_Check()
    {
        var route = Define
            .Put("/api/items/{id}")
            .Status(StatusCodes.Status202Accepted)
            .Accepts<UpdateItemRequest>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Returns<ErrorDto>(StatusCodes.Status202Accepted)
        );

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Returns_DefaultSuccessStatus_Can_Precede_Status_Override()
    {
        var route = Define
            .Post<ItemDto>("/api/items")
            .Returns<ErrorDto>(StatusCodes.Status201Created)
            .Status(StatusCodes.Status202Accepted);

        Assert.Equal(StatusCodes.Status202Accepted, route.SuccessStatusCode);
        Assert.Equal(
            StatusCodes.Status201Created,
            Assert.Single(route.RouteErrorResponses!).StatusCode
        );
    }

    [Fact]
    public void Returns_DefaultSuccessStatus_Without_Override_Throws_On_Publish()
    {
        var route = Define
            .Post<ItemDto>("/api/items")
            .Returns<ErrorDto>(StatusCodes.Status201Created);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Success(new ItemDto("1", "item"))
        );

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public async Task BoundInputOnlyContract_Error_ReturnsDeclaredResult()
    {
        var route = Define
            .Put("/api/items/{id}")
            .Accepts<UpdateItemRequest>()
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var result = await ExecuteAsync(
            route
                .Bind(new UpdateItemRequest("Widget"))
                .Error(StatusCodes.Status404NotFound, new NotFoundDto("Missing item"))
        );

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Missing item", Deserialize<NotFoundDto>(result).Message);
    }

    [Fact]
    public void Error_WithUndeclaredStatus_Throws()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Error(StatusCodes.Status409Conflict, new ErrorDto("duplicate"))
        );

        Assert.Contains("undeclared status code 409", exception.Message);
    }

    [Fact]
    public void Error_WithPayloadWhereContractDeclaresNoPayload_Throws()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns(StatusCodes.Status404NotFound, "Not found");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Error(StatusCodes.Status404NotFound, new NotFoundDto("Missing item"))
        );

        Assert.Contains("declares no payload", exception.Message);
    }

    [Fact]
    public void Error_WithoutPayloadWhereContractDeclaresPayload_Throws()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Error(StatusCodes.Status404NotFound)
        );

        Assert.Contains("no payload was supplied", exception.Message);
    }

    [Fact]
    public void Error_WithWrongPayloadType_Throws()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Error(StatusCodes.Status404NotFound, new ErrorDto("wrong"))
        );

        Assert.Contains(typeof(NotFoundDto).FullName!, exception.Message);
        Assert.Contains(typeof(ErrorDto).FullName!, exception.Message);
    }

    [Fact]
    public async Task Error_HasNoResultUnionCeiling()
    {
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<ErrorDto>(StatusCodes.Status400BadRequest)
            .Returns<ErrorDto>(StatusCodes.Status401Unauthorized)
            .Returns<ErrorDto>(StatusCodes.Status403Forbidden)
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound)
            .Returns<ErrorDto>(StatusCodes.Status409Conflict);

        var result = await ExecuteAsync(
            route.Error(StatusCodes.Status404NotFound, new NotFoundDto("Missing item"))
        );

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("Missing item", Deserialize<NotFoundDto>(result).Message);
    }

    [Fact]
    public void R3_Terminal_FreezesDefinition_MutationThrows()
    {
        // R3: a terminal publishes the definition too —
        // a later builder call on the shared static must throw, not mutate.
        var route = Define
            .Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        _ = route.Success(new ItemDto("item_1", "Widget"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            route.Returns<ErrorDto>(StatusCodes.Status409Conflict)
        );
        Assert.Contains("immutable once published", ex.Message);
    }

    private static async Task<HttpObservation> ExecuteAsync(RivetResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        await result.ToResult().ExecuteAsync(context);

        return new HttpObservation(
            context.Response.StatusCode,
            ((MemoryStream)context.Response.Body).ToArray()
        );
    }

    private static T Deserialize<T>(HttpObservation observation) =>
        JsonSerializer.Deserialize<T>(observation.Body, JsonSerializerOptions.Web)!;

    private sealed record HttpObservation(int StatusCode, byte[] Body);

    public sealed record ItemDto(string Id, string Name);

    public sealed record ErrorDto(string Message);

    public sealed record NotFoundDto(string Message);

    public sealed record CreateItemRequest(string Name);

    public sealed record UpdateItemRequest(string Name);
}
