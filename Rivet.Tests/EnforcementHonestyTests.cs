using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Tests;

/// <summary>
/// Pins the P1 enforcement-honesty tier: the extra-field leak (derived/upcast
/// instances), body-on-void, content-type conformance, file-endpoint terminals,
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
            Task.FromResult(route.Success(new ItemWithSecrets("1", "Widget", "hunter2", true)))
        );

        Assert.Contains("ItemWithSecrets", exception.Message);
        Assert.Contains("runtime payload type", exception.Message);
    }

    [Fact]
    public async Task UpcastDerivedInstance_InOkOfDeclaredType_Throws()
    {
        // The sneakier variant: Ok<ItemBase> holding an ItemWithSecrets instance.
        // The generic argument matches the declaration; the VALUE still leaks.
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            Task.FromResult(
                route.Success((ItemBase)new ItemWithSecrets("1", "Widget", "hunter2", true))
            )
        );

        Assert.Contains("ItemWithSecrets", exception.Message);
    }

    [Fact]
    public async Task ExactInstance_OfDeclaredType_Passes()
    {
        var route = Define.Get<ItemBase>("/api/items/{id}");

        var result = await ExecuteAsync(route.Success(new ItemBase("1", "Widget")));

        Assert.Equal("Widget", Deserialize<ItemBase>(result).Name);
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record Shape(string Id);

    public sealed record Circle(string Id, double Radius) : Shape(Id);

    public record Animal(string Name);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(Dog), "dog")]
    public record PolymorphicAnimal(string Name);

    public sealed record Dog(string Name, string Breed) : PolymorphicAnimal(Name);

    [Fact]
    public async Task DerivedInstance_WhereJsonPolymorphicTypeDeclared_Passes()
    {
        // [JsonPolymorphic] means the spec declares the hierarchy (oneOf) and STJ
        // serializes the discriminated contract — derived instances ARE the contract.
        var route = Define.Get<PolymorphicAnimal>("/api/animals/{id}");

        var result = await ExecuteAsync(route.Success((PolymorphicAnimal)new Dog("Rex", "Lab")));

        Assert.IsType<Dog>(Deserialize<PolymorphicAnimal>(result));
    }

    [Fact]
    public async Task ImplementingInstance_WhereAbstractTypeDeclared_Passes()
    {
        // Abstract declared types can only ever be satisfied by a subtype.
        var route = Define.Get<Shape>("/api/shapes/{id}");

        var result = await ExecuteAsync(route.Success((Shape)new Circle("1", 2.0)));

        Assert.IsType<Circle>(Deserialize<Shape>(result));
    }

    [Fact]
    public async Task DerivedInstance_OnErrorBranch_Throws()
    {
        // The guard applies to declared error payloads too, not just the success type.
        var route = Define
            .Get<ItemBase>("/api/items/{id}")
            .Returns<Animal>(StatusCodes.Status404NotFound);

        var exception = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            Task.FromResult(
                route.Error(StatusCodes.Status404NotFound, (Animal)new LoudAnimal("Rex", "WOOF"))
            )
        );

        Assert.Contains("LoudAnimal", exception.Message);
    }

    public sealed record LoudAnimal(string Name, string Sound) : Animal(Name);

    // ========== Body on void declaration ==========

    [Fact]
    public void VoidContract_HasNoContentBearingSuccessTerminal()
    {
        // A void contract exposes only Success(); a content-bearing success cannot be formed.
        var route = Define.Delete("/api/items/{id}").Status(StatusCodes.Status200OK);

        Assert.DoesNotContain(
            route.GetType().GetMethods(),
            method => method.Name == nameof(route.Success) && method.GetParameters().Length != 0
        );
    }

    [Fact]
    public async Task NoContent_OnVoidContract_Passes()
    {
        var route = Define.Delete("/api/items/{id}");

        var result = await ExecuteAsync(route.Success());

        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
        Assert.Empty(result.Body);
    }

    // ========== Content type on JSON declarations ==========

    [Fact]
    public void NonStringPayload_WithTextContentType_Throws()
    {
        var route = Define.Get<ItemBase>("/api/items/{id}").ProducesContentType("text/csv");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Success(new ItemBase("1", "Widget"))
        );

        Assert.Contains("text/csv", exception.Message);
        Assert.Contains("not string", exception.Message);
    }

    [Fact]
    public async Task JsonPayload_WithExplicitJsonContentType_Passes()
    {
        var route = Define
            .Get<ItemBase>("/api/items/{id}")
            .ProducesContentType("application/json; charset=utf-8");

        var result = await ExecuteAsync(route.Success(new ItemBase("1", "Widget")));

        Assert.Equal("Widget", Deserialize<ItemBase>(result).Name);
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
    }

    // ========== File endpoint terminals ==========

    [Fact]
    public async Task File_MatchingContentType_Passes()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var result = await ExecuteAsync(route.File(new byte[] { 0xFF, 0xD8 }));

        Assert.Equal("image/jpeg", result.ContentType);
    }

    [Fact]
    public async Task File_ContentTypeIsOwnedByContract()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var result = await ExecuteAsync(route.File(new byte[] { 0x25 }));

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.NotEqual("application/pdf", result.ContentType);
    }

    [Fact]
    public void FileRoute_HasNoJsonSuccessTerminal()
    {
        // A file route exposes File(), not Success(payload), so JSON cannot be returned.
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        Assert.DoesNotContain(route.GetType().GetMethods(), method => method.Name == "Success");
    }

    [Fact]
    public void File_UndeclaredErrorStatus_Throws()
    {
        var route = Define.File("/api/items/{id}/photo").ContentType("image/jpeg");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Error(StatusCodes.Status404NotFound)
        );

        Assert.Contains("undeclared status code 404", exception.Message);
    }

    [Fact]
    public async Task File_DeclaredErrorStatus_WithDeclaredPayload_Passes()
    {
        var route = Define
            .File("/api/items/{id}/photo")
            .ContentType("image/jpeg")
            .Returns<Animal>(StatusCodes.Status404NotFound, "No photo");

        var result = await ExecuteAsync(
            route.Error(StatusCodes.Status404NotFound, new Animal("missing"))
        );

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("missing", Deserialize<Animal>(result).Name);
    }

    [Fact]
    public async Task BoundFile_WithInput_UsesDeclaredContentType()
    {
        var route = Define.File<PhotoRequest>("/api/photos").ContentType("image/png");

        var result = await ExecuteAsync(
            route.Bind(new PhotoRequest("p1")).File(new byte[] { 0x00 })
        );

        Assert.Equal("image/png", result.ContentType);
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
            Task.FromResult(route.Success((ItemBase)new ItemWithSecrets("1", "W", "s", false)))
        );

        Assert.True(
            typeof(InvalidOperationException).IsAssignableFrom(
                typeof(RivetContractViolationException)
            )
        );
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
            ((MemoryStream)context.Response.Body).ToArray(),
            context.Response.ContentType
        );
    }

    private static T Deserialize<T>(HttpObservation observation) =>
        JsonSerializer.Deserialize<T>(observation.Body, JsonSerializerOptions.Web)!;

    private sealed record HttpObservation(int StatusCode, byte[] Body, string? ContentType);
}
