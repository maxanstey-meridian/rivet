using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractEmitterTests
{
    [Fact]
    public void Empty_Contract_Has_Three_Top_Level_Arrays()
    {
        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.StringUnion>(),
            []);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("types").ValueKind);
        Assert.Equal(0, root.GetProperty("types").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("enums").ValueKind);
        Assert.Equal(0, root.GetProperty("enums").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("endpoints").ValueKind);
        Assert.Equal(0, root.GetProperty("endpoints").GetArrayLength());
    }

    [Fact]
    public void Types_Contains_Serialized_TypeDefinition()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UserDto"] = new("UserDto", [], [
                new("id", new TsType.Primitive("number"), false),
                new("name", new TsType.Primitive("string"), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType.StringUnion>(), []);
        using var doc = JsonDocument.Parse(json);
        var types = doc.RootElement.GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        var t = types[0];
        Assert.Equal("UserDto", t.GetProperty("name").GetString());
        var props = t.GetProperty("properties");
        Assert.Equal(2, props.GetArrayLength());
        Assert.Equal("primitive", props[0].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("number", props[0].GetProperty("type").GetProperty("type").GetString());
    }

    [Fact]
    public void Enums_Projected_With_Name_And_Values()
    {
        var enums = new Dictionary<string, TsType.StringUnion>
        {
            ["Status"] = new(["Active", "Inactive", "Pending"]),
        };

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), enums, []);
        using var doc = JsonDocument.Parse(json);
        var enumArr = doc.RootElement.GetProperty("enums");

        Assert.Equal(1, enumArr.GetArrayLength());
        Assert.Equal("Status", enumArr[0].GetProperty("name").GetString());
        var values = enumArr[0].GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal("Active", values[0].GetString());
        Assert.Equal("Inactive", values[1].GetString());
        Assert.Equal("Pending", values[2].GetString());
    }

    [Fact]
    public void Endpoint_Serializes_Core_Fields_As_CamelCase()
    {
        var endpoint = new TsEndpointDefinition(
            "getUser",
            "GET",
            "/api/users/{id}",
            [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
            new TsType.TypeRef("UserDto"),
            "UsersController",
            [new TsResponseType(200, new TsType.TypeRef("UserDto"))]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType.StringUnion>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        Assert.Equal("getUser", ep.GetProperty("name").GetString());
        Assert.Equal("GET", ep.GetProperty("httpMethod").GetString());
        Assert.Equal("/api/users/{id}", ep.GetProperty("routeTemplate").GetString());
        Assert.Equal("UsersController", ep.GetProperty("controllerName").GetString());
        Assert.Equal("ref", ep.GetProperty("returnType").GetProperty("kind").GetString());
    }

    [Fact]
    public void ParamSource_Serializes_As_CamelCase_String()
    {
        var endpoint = new TsEndpointDefinition(
            "upload",
            "POST",
            "/api/upload",
            [
                new TsEndpointParam("data", new TsType.TypeRef("Payload"), ParamSource.Body),
                new TsEndpointParam("file", new TsType.Primitive("unknown"), ParamSource.File),
                new TsEndpointParam("field", new TsType.Primitive("string"), ParamSource.FormField),
            ],
            null,
            "UploadController",
            [new TsResponseType(204, null)]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType.StringUnion>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var pars = doc.RootElement.GetProperty("endpoints")[0].GetProperty("params");

        Assert.Equal("body", pars[0].GetProperty("source").GetString());
        Assert.Equal("file", pars[1].GetProperty("source").GetString());
        Assert.Equal("formField", pars[2].GetProperty("source").GetString());
    }

    [Fact]
    public void Null_And_Default_Fields_Omitted()
    {
        var endpoint = new TsEndpointDefinition(
            "list",
            "GET",
            "/api/items",
            [],
            null,
            "ItemsController",
            [new TsResponseType(200, null)]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType.StringUnion>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        Assert.False(ep.TryGetProperty("returnType", out _));
        Assert.False(ep.TryGetProperty("summary", out _));
        Assert.False(ep.TryGetProperty("description", out _));
        Assert.False(ep.TryGetProperty("security", out _));
        Assert.False(ep.TryGetProperty("fileContentType", out _));
        Assert.False(ep.TryGetProperty("inputTypeName", out _));
        Assert.False(ep.TryGetProperty("isFormEncoded", out _));
    }

    [Fact]
    public void Responses_Serialize_With_StatusCode_And_DataType()
    {
        var endpoint = new TsEndpointDefinition(
            "getUser",
            "GET",
            "/api/users/{id}",
            [],
            new TsType.TypeRef("UserDto"),
            "UsersController",
            [
                new TsResponseType(200, new TsType.TypeRef("UserDto")),
                new TsResponseType(404, null, "Not found"),
            ]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType.StringUnion>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var responses = doc.RootElement.GetProperty("endpoints")[0].GetProperty("responses");

        Assert.Equal(2, responses.GetArrayLength());
        Assert.Equal(200, responses[0].GetProperty("statusCode").GetInt32());
        Assert.Equal("ref", responses[0].GetProperty("dataType").GetProperty("kind").GetString());
        Assert.Equal(404, responses[1].GetProperty("statusCode").GetInt32());
        Assert.False(responses[1].TryGetProperty("dataType", out _));
    }

    [Fact]
    public void Security_Serializes_As_CamelCase()
    {
        var endpoint = new TsEndpointDefinition(
            "secure",
            "GET",
            "/api/secure",
            [],
            null,
            "SecureController",
            [],
            Security: new EndpointSecurity(false, "Bearer"));

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType.StringUnion>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var sec = doc.RootElement.GetProperty("endpoints")[0].GetProperty("security");

        Assert.False(sec.GetProperty("isAnonymous").GetBoolean());
        Assert.Equal("Bearer", sec.GetProperty("scheme").GetString());
    }

    [Fact]
    public void Full_Contract_Contains_All_Sections()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UserDto"] = new("UserDto", [], [
                new("id", new TsType.Primitive("number"), false),
                new("status", new TsType.TypeRef("Status"), false),
            ]),
        };

        var enums = new Dictionary<string, TsType.StringUnion>
        {
            ["Status"] = new(["Active", "Inactive"]),
        };

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getUser", "GET", "/api/users/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                new TsType.TypeRef("UserDto"),
                "UsersController",
                [new TsResponseType(200, new TsType.TypeRef("UserDto"))]),
        };

        var json = ContractEmitter.Emit(definitions, enums, endpoints);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("types").GetArrayLength());
        Assert.Equal("UserDto", root.GetProperty("types")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("enums").GetArrayLength());
        Assert.Equal("Status", root.GetProperty("enums")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("endpoints").GetArrayLength());
        Assert.Equal("getUser", root.GetProperty("endpoints")[0].GetProperty("name").GetString());
    }
}
