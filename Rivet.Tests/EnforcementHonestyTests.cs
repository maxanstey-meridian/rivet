using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Tests;

/// <summary>
/// Pins the P1 enforcement-honesty tier: the extra-field leak (derived/upcast
/// instances), body-on-void, content-type conformance, file-endpoint Invoke,
/// and the structured contract-violation envelope. Each test is the runtime
/// counterpart of a "not enforced" row from the FABLE_GAPS §4.1 table.
/// </summary>
public sealed class EnforcementHonestyTests
{
    // ========== Extra-field leak: runtime type vs declared type ==========

    public record ItemBase(string Id, string Name);

    public sealed record ItemWithSecrets(string Id, string Name, string Secret, bool IsAdmin)
        : ItemBase(Id, Name);

    [Fact]
    public async Task DerivedInstance_WhereConcreteTypeDeclared_Throws()
    {
        // The classic leak: Ok<Derived> passes IsAssignableFrom, STJ serializes
        // Secret/IsAdmin to the wire. Must now be a loud contract violation.
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemWithSecrets>>(() =>
                Task.FromResult(
                    TypedResults.Ok(new ItemWithSecrets("1", "Widget", "hunter2", true))
                )
            )
        );

        Assert.Contains("ItemWithSecrets", exception.Message);
        Assert.Contains("runtime type", exception.Message);
    }

    [Fact]
    public async Task UpcastDerivedInstance_InOkOfDeclaredType_Throws()
    {
        // The sneakier variant: Ok<ItemBase> holding an ItemWithSecrets instance.
        // The generic argument matches the declaration; the VALUE still leaks.
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemBase>>(() =>
                Task.FromResult(
                    TypedResults.Ok<ItemBase>(new ItemWithSecrets("1", "Widget", "hunter2", true))
                )
            )
        );

        Assert.Contains("ItemWithSecrets", exception.Message);
    }

    [Fact]
    public async Task ExactInstance_OfDeclaredType_Passes()
    {
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var result = await route.Invoke<Ok<ItemBase>>(() =>
            Task.FromResult(TypedResults.Ok(new ItemBase("1", "Widget")))
        );

        Assert.Equal("Widget", Assert.IsType<Ok<ItemBase>>(result).Value!.Name);
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record Shape(string Id);

    public sealed record Circle(string Id, double Radius) : Shape(Id);

    public record Animal(string Name);

    [JsonPolymorphic]
    public record PolymorphicAnimal(string Name);

    public sealed record Dog(string Name, string Breed) : PolymorphicAnimal(Name);

    [Fact]
    public async Task DerivedInstance_WhereJsonPolymorphicTypeDeclared_Passes()
    {
        // [JsonPolymorphic] means the spec declares the hierarchy (oneOf) and STJ
        // serializes the discriminated contract — derived instances ARE the contract.
        var route = Define.Get<PolymorphicAnimal>("/api/animals/{id}");

        var result = await route.Invoke<Ok<PolymorphicAnimal>>(() =>
            Task.FromResult(TypedResults.Ok<PolymorphicAnimal>(new Dog("Rex", "Lab")))
        );

        Assert.IsType<Dog>(Assert.IsType<Ok<PolymorphicAnimal>>(result).Value);
    }

    [Fact]
    public async Task ImplementingInstance_WhereAbstractTypeDeclared_Passes()
    {
        // Abstract declared types can only ever be satisfied by a subtype.
        var route = Define.Get<Shape>("/api/shapes/{id}");

        var result = await route.Invoke<Ok<Shape>>(() =>
            Task.FromResult(TypedResults.Ok<Shape>(new Circle("1", 2.0)))
        );

        Assert.IsType<Circle>(Assert.IsType<Ok<Shape>>(result).Value);
    }

    [Fact]
    public async Task DerivedInstance_OnErrorBranch_Throws()
    {
        // The guard applies to declared error payloads too, not just the success type.
        var route = Define
            .Get<ItemBase>("/api/items/{id}")
            .Returns<Animal>(StatusCodes.Status404NotFound);

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemBase>, NotFound<Animal>>(() =>
                Task.FromResult<Results<Ok<ItemBase>, NotFound<Animal>>>(
                    TypedResults.NotFound<Animal>(new LoudAnimal("Rex", "WOOF"))
                )
            )
        );

        Assert.Contains("LoudAnimal", exception.Message);
    }

    public sealed record LoudAnimal(string Name, string Sound) : Animal(Name);

    // ========== Body on void declaration ==========

    [Fact]
    public async Task ContentResult_OnVoidContract_Throws()
    {
        // The :93-102/:134-139 hole: ContentHttpResult is not IValueHttpResult, so a
        // text/plain body on a void contract sailed through with a matching status.
        var route = Define.Delete("/api/items/{id}").Status(StatusCodes.Status200OK);

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<ContentHttpResult>(() =>
                Task.FromResult(
                    TypedResults.Text("leaked body", statusCode: StatusCodes.Status200OK)
                )
            )
        );

        Assert.Contains("content-bearing", exception.Message);
    }

    [Fact]
    public async Task NoContent_OnVoidContract_Passes()
    {
        var route = Define.Delete("/api/items/{id}");

        var result = await route.Invoke<NoContent>(() => Task.FromResult(TypedResults.NoContent()));

        Assert.IsType<NoContent>(result);
    }

    // ========== Content type on JSON declarations ==========

    [Fact]
    public async Task JsonPayload_WithNonJsonContentType_Throws()
    {
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<JsonHttpResult<ItemBase>>(() =>
                Task.FromResult(
                    TypedResults.Json(
                        new ItemBase("1", "Widget"),
                        contentType: "text/csv",
                        statusCode: StatusCodes.Status200OK
                    )
                )
            )
        );

        Assert.Contains("text/csv", exception.Message);
        Assert.Contains("JSON payload", exception.Message);
    }

    [Fact]
    public async Task JsonPayload_WithExplicitJsonContentType_Passes()
    {
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var result = await route.Invoke<JsonHttpResult<ItemBase>>(() =>
            Task.FromResult(
                TypedResults.Json(
                    new ItemBase("1", "Widget"),
                    contentType: "application/json; charset=utf-8",
                    statusCode: StatusCodes.Status200OK
                )
            )
        );

        Assert.Equal("Widget", Assert.IsType<JsonHttpResult<ItemBase>>(result).Value!.Name);
    }

    // ========== File endpoint Invoke ==========

    [Fact]
    public async Task FileInvoke_MatchingContentType_Passes()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var result = await route.Invoke<FileContentHttpResult>(() =>
            Task.FromResult(TypedResults.File(new byte[] { 0xFF, 0xD8 }, "image/jpeg"))
        );

        Assert.Equal("image/jpeg", Assert.IsType<FileContentHttpResult>(result).ContentType);
    }

    [Fact]
    public async Task FileInvoke_WrongContentType_Throws()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<FileContentHttpResult>(() =>
                Task.FromResult(TypedResults.File(new byte[] { 0x25 }, "application/pdf"))
            )
        );

        Assert.Contains("image/jpeg", exception.Message);
        Assert.Contains("application/pdf", exception.Message);
    }

    [Fact]
    public async Task FileInvoke_JsonResultOnSuccessStatus_Throws()
    {
        // The curl-proved hole: JSON served 200 on an image/jpeg contract.
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemBase>>(() =>
                Task.FromResult(TypedResults.Ok(new ItemBase("1", "Widget")))
            )
        );

        Assert.Contains("JSON payload result", exception.Message);
    }

    [Fact]
    public async Task FileInvoke_UndeclaredErrorStatus_Throws()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<NotFound>(() => Task.FromResult(TypedResults.NotFound()))
        );

        Assert.Contains("undeclared status code 404", exception.Message);
    }

    [Fact]
    public async Task FileInvoke_DeclaredErrorStatus_WithDeclaredPayload_Passes()
    {
        var route = Define
            .File("/api/items/{id}/photo")
            .ContentType("image/jpeg")
            .Returns<Animal>(StatusCodes.Status404NotFound, "No photo");

        var result = await route.Invoke<NotFound<Animal>>(() =>
            Task.FromResult(TypedResults.NotFound(new Animal("missing")))
        );

        Assert.IsType<NotFound<Animal>>(result);
    }

    [Fact]
    public async Task FileInvoke_WithInput_ValidatesLikeParameterless()
    {
        var route = Define.File<PhotoRequest>("/api/photos").ContentType("image/png");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<FileContentHttpResult>(
                new PhotoRequest("p1"),
                request => Task.FromResult(TypedResults.File(new byte[] { 0x00 }, "image/jpeg"))
            )
        );

        Assert.Contains("image/png", exception.Message);
    }

    public sealed record PhotoRequest(string Id);

    // ========== The failure envelope ==========

    [Fact]
    public async Task ViolationHandler_EmitsStructuredEnvelope()
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        httpContext.Response.Body = new MemoryStream();

        var handled = await new RivetContractViolationHandler().TryHandleAsync(
            httpContext,
            new RivetContractViolationException("Route '/x' returned undeclared status code 418."),
            CancellationToken.None
        );

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        httpContext.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.Equal("contract_violation", body.RootElement.GetProperty("code").GetString());
        Assert.Contains("418", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ViolationHandler_IgnoresOtherExceptions()
    {
        var handled = await new RivetContractViolationHandler().TryHandleAsync(
            new DefaultHttpContext(),
            new InvalidOperationException("not ours"),
            CancellationToken.None
        );

        Assert.False(handled);
    }

    [Fact]
    public async Task ViolationException_IsAnInvalidOperationException()
    {
        // Back-compat promise: pre-existing catch blocks and test assertions keep working.
        var route = Define.Get<ItemBase>("/api/items/{id}");

        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            route.Invoke<Ok<ItemBase>>(() =>
                Task.FromResult(
                    TypedResults.Ok<ItemBase>(new ItemWithSecrets("1", "W", "s", false))
                )
            )
        );

        Assert.True(
            typeof(InvalidOperationException).IsAssignableFrom(
                typeof(RivetContractViolationException)
            )
        );
    }
}
