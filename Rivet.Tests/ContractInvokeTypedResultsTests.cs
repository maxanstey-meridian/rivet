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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            route.Invoke<Ok<ItemDto>, NotFound>(
                () => Task.FromResult<Results<Ok<ItemDto>, NotFound>>(TypedResults.NotFound())));

        Assert.Contains("without a payload", exception.Message);
    }

    [Fact]
    public async Task Invoke_WithWrongPayloadType_Throws()
    {
        var route = Define.Get<ItemDto>("/api/items/{id}")
            .Returns<NotFoundDto>(StatusCodes.Status404NotFound, "Not found");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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

    public sealed record ItemDto(string Id, string Name);

    public sealed record ErrorDto(string Message);

    public sealed record NotFoundDto(string Message);

    public sealed record CreateItemRequest(string Name);

    public sealed record UpdateItemRequest(string Name);
}
