using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Tests;

/// <summary>
/// [RivetScalar] runtime truth: the attribute-local JsonConverterAttribute seam
/// makes ordinary System.Text.Json serialize/deserialize the annotated type as its
/// bare Value, so the emitted scalar contract and the runtime wire shape agree
/// without any global serializer registration
/// (acceptance:scalar-attribute-makes-stj-wire-shape-true,
/// acceptance:scalar-runtime-is-bounded).
/// </summary>
public sealed class ScalarRuntimeTests
{
    [Rivet.RivetScalar]
    public sealed record EmailScalar(string Value);

    [Rivet.RivetScalar]
    public sealed record TeamIdScalar(Guid Value);

    // No [RivetScalar]: an unannotated one-Value record stays an ordinary object.
    public sealed record PlainWrapper(string Value);

    [Fact]
    public void String_Scalar_Serializes_As_Primitive()
    {
        var json = JsonSerializer.Serialize(new EmailScalar("max@example.com"));

        Assert.Equal("\"max@example.com\"", json);
    }

    [Fact]
    public void String_Scalar_RoundTrips_Through_Stj()
    {
        var deserialized = JsonSerializer.Deserialize<EmailScalar>("\"max@example.com\"");

        Assert.Equal(new EmailScalar("max@example.com"), deserialized);
    }

    [Fact]
    public void Guid_Scalar_Serializes_As_Primitive_And_RoundTrips()
    {
        var guid = Guid.Parse("0c3f8f2e-1d5a-4b7c-9e2f-6a8d1b4c3e70");
        var json = JsonSerializer.Serialize(new TeamIdScalar(guid));

        Assert.Equal($"\"{guid}\"", json);
        Assert.Equal(new TeamIdScalar(guid), JsonSerializer.Deserialize<TeamIdScalar>(json));
    }

    [Fact]
    public void Unannotated_OneValue_Record_Remains_An_Ordinary_Object()
    {
        var json = JsonSerializer.Serialize(new PlainWrapper("x"));

        Assert.Equal("{\"Value\":\"x\"}", json);
    }

    [Fact]
    public void Scalar_Wrapper_Missing_Value_Property_Fails_Clearly()
    {
        // Attributed shape without an eligible Value property: the converter
        // factory refuses at converter-creation time instead of guessing
        // (acceptance:scalar-runtime-is-bounded,
        // planner-constraint:converter-shape-validation-at-creation).
        var exception = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new NotScalar(1))
        );

        Assert.Contains("Value", exception.Message);
    }

    [Rivet.RivetScalar]
    private sealed record NotScalar(int Amount);

    [Fact]
    public async Task Scalar_Payload_Through_Runtime_Terminal_Is_Primitive_Json()
    {
        // The live adapter path: a route whose success type is a [RivetScalar]
        // wrapper emits the bare scalar value, not {"value": ...}
        // (acceptance:scalar-live-host-proof).
        var route = Define.Get<EmailScalar>("/api/scalars/email");

        var result = await ExecuteAsync(route.Success(new EmailScalar("max@example.com")));

        Assert.Equal(
            "\"max@example.com\"",
            JsonSerializer.Serialize(JsonDocument.Parse(result.Body).RootElement)
        );
    }

    [Fact]
    public async Task Non_String_Scalar_Through_Runtime_Terminal_Is_Primitive_Json()
    {
        var guid = Guid.Parse("0c3f8f2e-1d5a-4b7c-9e2f-6a8d1b4c3e70");
        var route = Define.Get<TeamIdScalar>("/api/scalars/team");

        var result = await ExecuteAsync(route.Success(new TeamIdScalar(guid)));

        Assert.Equal(
            $"\"{guid}\"",
            JsonSerializer.Serialize(JsonDocument.Parse(result.Body).RootElement)
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
            ((MemoryStream)context.Response.Body).ToArray()
        );
    }

    private sealed record HttpObservation(int StatusCode, byte[] Body);
}
