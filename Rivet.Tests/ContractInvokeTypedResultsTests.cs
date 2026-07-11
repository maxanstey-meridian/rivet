using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http;
using Rivet;

namespace Rivet.Tests;

public sealed class ContractInvokeTypedResultsTests
{
    [Fact]
    public async Task Invoke_WithTypedResultsSuccessBranch_ReturnsNativeResult()
    {
        var route = Define.Post<CreateItemRequest, ItemDto>("/api/items")
            .Status(StatusCodes.Status201Created)
            .Returns<ErrorDto>(StatusCodes.Status409Conflict, "Conflict");

        var result = await route.Invoke<Created<ItemDto>, Conflict<ErrorDto>>(
            new CreateItemRequest("Widget"),
            request => Task.FromResult<Results<Created<ItemDto>, Conflict<ErrorDto>>>(
                TypedResults.Created($"/api/items/{request.Name}", new ItemDto("item_1", request.Name))));

        var branch = Assert.IsType<Created<ItemDto>>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, branch.StatusCode);
        Assert.NotNull(branch.Value);
        Assert.Equal("Widget", branch.Value.Name);
    }

    [Fact]
    public async Task Invoke_WithTypedResultsErrorBranch_ReturnsNativeResult()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var result = await route.Invoke<Ok<ItemDto>, NotFound<NotFoundDto>>(
            () => Task.FromResult<Results<Ok<ItemDto>, NotFound<NotFoundDto>>>(
                TypedResults.NotFound(new NotFoundDto("Missing item"))));

        var branch = Assert.IsType<NotFound<NotFoundDto>>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, branch.StatusCode);
        Assert.NotNull(branch.Value);
        Assert.Equal("Missing item", branch.Value.Message);
    }

    [Fact]
    public async Task Invoke_VoidContract_WithTypedResultsNoContentBranch_ReturnsNativeResult()
    {
        var route = Define.Delete("/api/items/{id}")
            .Status(StatusCodes.Status204NoContent)
            .Returns(StatusCodes.Status404NotFound, "Not found");

        var result = await route.Invoke<NoContent, NotFound>(
            () => Task.FromResult<Results<NoContent, NotFound>>(TypedResults.NoContent()));

        Assert.IsType<NoContent>(result.Result);
    }

    [Fact]
    public async Task Invoke_VoidDeleteContract_WithoutExplicitStatus_DefaultsTo204_AndNoContentDoesNotThrow()
    {
        // A1 repro: no .Status(204) — the runtime default for void DELETE must itself be 204,
        // so a handler returning TypedResults.NoContent() (the status the contract advertises)
        // must validate cleanly instead of throwing at request time.
        var route = Define.Delete("/api/items/{id}")
            .Returns(StatusCodes.Status404NotFound, "Not found");

        Assert.Equal(StatusCodes.Status204NoContent, route.SuccessStatusCode);

        var result = await route.Invoke<NoContent, NotFound>(
            () => Task.FromResult<Results<NoContent, NotFound>>(TypedResults.NoContent()));

        var branch = Assert.IsType<NoContent>(result.Result);
        Assert.Equal(StatusCodes.Status204NoContent, branch.StatusCode);
    }

    [Fact]
    public void Returns_DuplicateStatus_Throws()
    {
        // R1 guard: duplicate .Returns for the same status used to be appended silently and
        // blow up inside TypedResultValidator (SingleOrDefault) on the first matching response.
        // The builder must reject the duplicate registration immediately instead.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<ItemDto>("/api/items/{id}")
                .Returns<ErrorDto>(StatusCodes.Status404NotFound)
                .Returns<NotFoundDto>(StatusCodes.Status404NotFound));

        Assert.Contains("404", exception.Message);
        Assert.Contains("already declared", exception.Message);
    }

    [Fact]
    public void Returns_DuplicateStatus_UntypedOverload_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define.Delete("/api/items/{id}")
                .Returns(StatusCodes.Status404NotFound)
                .Returns(StatusCodes.Status404NotFound, "Not found"));

        Assert.Contains("404", exception.Message);
        Assert.Contains("already declared", exception.Message);
    }

    [Fact]
    public void Returns_Rejects_Existing_Success_Status()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<ItemDto>("/api/items/{id}")
                .Status(StatusCodes.Status202Accepted)
                .Returns<ErrorDto>(StatusCodes.Status202Accepted));

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Status_Rejects_Existing_Returns_Status()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<ItemDto>("/api/items/{id}")
                .Returns<ErrorDto>(StatusCodes.Status202Accepted)
                .Status(StatusCodes.Status202Accepted));

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Accepts_Preserves_Explicit_Status_State()
    {
        var route = Define.Put("/api/items/{id}")
            .Status(StatusCodes.Status202Accepted)
            .Accepts<UpdateItemRequest>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Status(StatusCodes.Status203NonAuthoritative));

        Assert.Contains("Status already set", exception.Message);
    }

    [Fact]
    public void Accepts_Preserves_Success_Status_Collision_Check()
    {
        var route = Define.Put("/api/items/{id}")
            .Status(StatusCodes.Status202Accepted)
            .Accepts<UpdateItemRequest>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Returns<ErrorDto>(StatusCodes.Status202Accepted));

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public void Returns_DefaultSuccessStatus_Can_Precede_Status_Override()
    {
        var route = Define.Post<ItemDto>("/api/items")
            .Returns<ErrorDto>(StatusCodes.Status201Created)
            .Status(StatusCodes.Status202Accepted);

        Assert.Equal(StatusCodes.Status202Accepted, route.SuccessStatusCode);
        Assert.Equal(StatusCodes.Status201Created, Assert.Single(route.RouteErrorResponses!).StatusCode);
    }

    [Fact]
    public async Task Returns_DefaultSuccessStatus_Without_Override_Throws_On_Publish()
    {
        var route = Define.Post<ItemDto>("/api/items")
            .Returns<ErrorDto>(StatusCodes.Status201Created);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            route.Invoke(() => Task.FromResult(new ItemDto("1", "item"))));

        Assert.Contains("success and error responses cannot share", exception.Message);
    }

    [Fact]
    public async Task Invoke_InputOnlyContract_WithTypedResultsErrorBranch_ReturnsNativeResult()
    {
        var route = Define.Put("/api/items/{id}")
            .Accepts<UpdateItemRequest>()
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var result = await route.Invoke<NoContent, NotFound<NotFoundDto>>(
            new UpdateItemRequest("Widget"),
            _ => Task.FromResult<Results<NoContent, NotFound<NotFoundDto>>>(
                TypedResults.NotFound(new NotFoundDto("Missing item"))));

        var branch = Assert.IsType<NotFound<NotFoundDto>>(result.Result);
        Assert.NotNull(branch.Value);
        Assert.Equal("Missing item", branch.Value.Message);
    }

    [Fact]
    public async Task Invoke_WithUndeclaredStatus_Throws()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemDto>, Conflict<ErrorDto>>(
                () => Task.FromResult<Results<Ok<ItemDto>, Conflict<ErrorDto>>>(
                    TypedResults.Conflict(new ErrorDto("duplicate")))));

        Assert.Contains("undeclared status code 409", exception.Message);
    }

    [Fact]
    public async Task Invoke_WithPayloadWhereContractDeclaresNoPayload_Throws()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns(StatusCodes.Status404NotFound, "Not found");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemDto>, NotFound<NotFoundDto>>(
                () => Task.FromResult<Results<Ok<ItemDto>, NotFound<NotFoundDto>>>(
                    TypedResults.NotFound(new NotFoundDto("Missing item")))));

        Assert.Contains("declares no payload", exception.Message);
    }

    [Fact]
    public async Task Invoke_WithoutPayloadWhereContractDeclaresPayload_Throws()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemDto>, NotFound>(
                () => Task.FromResult<Results<Ok<ItemDto>, NotFound>>(TypedResults.NotFound())));

        Assert.Contains("without a payload", exception.Message);
    }

    [Fact]
    public async Task Invoke_WithWrongPayloadType_Throws()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemDto>, NotFound<ErrorDto>>(
                () => Task.FromResult<Results<Ok<ItemDto>, NotFound<ErrorDto>>>(
                    TypedResults.NotFound(new ErrorDto("wrong")))));

        Assert.Contains("declares 'Rivet.Tests.ContractInvokeTypedResultsTests+NotFoundDto'", exception.Message);
    }

    [Fact]
    public async Task Invoke_SupportsSixResultUnion()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<ErrorDto>(StatusCodes.Status400BadRequest)
            .Returns<ErrorDto>(StatusCodes.Status401Unauthorized)
            .Returns<ErrorDto>(StatusCodes.Status403Forbidden)
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound)
            .Returns<ErrorDto>(StatusCodes.Status409Conflict);

        var result = await route.Invoke<
            Ok<ItemDto>,
            BadRequest<ErrorDto>,
            UnauthorizedHttpResult,
            ForbidHttpResult,
            NotFound<NotFoundDto>,
            Conflict<ErrorDto>>(
            () => Task.FromResult<
                Results<
                    Ok<ItemDto>,
                    BadRequest<ErrorDto>,
                    UnauthorizedHttpResult,
                    ForbidHttpResult,
                    NotFound<NotFoundDto>,
                    Conflict<ErrorDto>>>(
                TypedResults.NotFound(new NotFoundDto("Missing item"))));

        var branch = Assert.IsType<NotFound<NotFoundDto>>(result.Result);
        Assert.NotNull(branch.Value);
        Assert.Equal("Missing item", branch.Value.Message);
    }

    [Fact]
    public async Task R3_TypedResultsInvoke_FreezesDefinition_MutationThrows()
    {
        // R3: the typed-results Invoke path publishes the definition too —
        // a later builder call on the shared static must throw, not mutate.
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        await route.Invoke<Ok<ItemDto>, NotFound<NotFoundDto>>(
            () => Task.FromResult<Results<Ok<ItemDto>, NotFound<NotFoundDto>>>(
                TypedResults.Ok(new ItemDto("item_1", "Widget"))));

        var ex = Assert.Throws<InvalidOperationException>(
            () => route.Returns<ErrorDto>(StatusCodes.Status409Conflict));
        Assert.Contains("immutable once published", ex.Message);
    }

    public sealed record ItemDto(string Id, string Name);

    public sealed record ErrorDto(string Message);

    public sealed record NotFoundDto(string Message);

    public sealed record CreateItemRequest(string Name);

    public sealed record UpdateItemRequest(string Name);
}
