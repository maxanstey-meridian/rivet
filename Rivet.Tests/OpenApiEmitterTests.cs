using System.Text.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class OpenApiEmitterTests
{
    private static JsonDocument EmitOpenApi(
        string source,
        SecurityConfig? security = null)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, security);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument EmitOpenApiFromController(
        string source,
        SecurityConfig? security = null)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, security);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument EmitOpenApiFromModel(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        SecurityConfig? security = null)
    {
        var json = OpenApiEmitter.Emit(endpoints, definitions, brands, enums, security);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument EmitOpenApiFromJsonContract(string json)
    {
        var spec = CompilationHelper.EmitOpenApiFromJson(json);
        return JsonDocument.Parse(spec);
    }

    [Fact]
    public void Contract_Input_Route_Property_Is_Not_Repeated_In_Request_Body()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateRoleInput(Guid Id, string Role);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<UpdateRoleInput> UpdateRole =
                    Define.Put("/api/members/{id}/role")
                        .Accepts<UpdateRoleInput>()
                        .Status(204);
            }
            """;

        using var doc = EmitOpenApi(source);
        var operation = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}/role").GetProperty("put");
        var routeParameter = operation.GetProperty("parameters")[0];
        var bodySchema = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var bodyComponent = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("UpdateRoleRequest");

        Assert.Equal("string", routeParameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("uuid", routeParameter.GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal("#/components/schemas/UpdateRoleRequest", bodySchema.GetProperty("$ref").GetString());
        Assert.False(bodyComponent.GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("role", out _));
    }

    [Fact]
    public void Nullable_Contract_Input_Remains_Nullable_After_Route_Property_Is_Filtered()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateRoleInput(string Id, string Role);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<UpdateRoleInput?> UpdateRole =
                    Define.Patch("/api/members/{id}/role")
                        .Accepts<UpdateRoleInput?>();
            }
            """;

        using var doc = EmitOpenApi(source);
        var bodySchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}/role").GetProperty("patch")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var oneOf = bodySchema.GetProperty("oneOf");

        Assert.Equal("#/components/schemas/UpdateRoleRequest", oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", oneOf[1].GetProperty("type").GetString());
    }

    [Fact]
    public void JsonPropertyName_Route_Property_Is_Excluded_By_Wire_Name_Without_Renaming_Route_Param()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateRoleInput(
                [property: JsonPropertyName("member_key")] string Id,
                string Role);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<UpdateRoleInput> UpdateRole =
                    Define.Put("/api/members/{id}/role")
                        .Accepts<UpdateRoleInput>();
            }
            """;

        using var doc = EmitOpenApi(source);
        var operation = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}/role").GetProperty("put");
        var bodyComponent = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("UpdateRoleRequest");

        Assert.Equal("id", operation.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.False(bodyComponent.GetProperty("properties").TryGetProperty("member_key", out _));
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("role", out _));
    }

    [Fact]
    public void Unmatched_Route_Token_Does_Not_Filter_Unrelated_Body_Wire_Name()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateRoleInput(
                [property: JsonPropertyName("id")] string CorrelationId,
                string Role);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<UpdateRoleInput> UpdateRole =
                    Define.Put("/api/members/{id}/role")
                        .Accepts<UpdateRoleInput>();
            }
            """;

        using var doc = EmitOpenApi(source);
        var operation = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}/role").GetProperty("put");
        var bodySchema = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var bodyComponent = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("UpdateRoleInput");

        Assert.Equal("string", operation.GetProperty("parameters")[0]
            .GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/UpdateRoleInput", bodySchema.GetProperty("$ref").GetString());
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("role", out _));
    }

    [Fact]
    public void Json_Contract_Unassociated_Route_Param_Does_Not_Filter_SameNamed_Body_Property()
    {
        const string json = """
            {
              "types": [{
                "name": "UpdateRoleInput",
                "typeParameters": [],
                "properties": [
                  { "name": "id", "type": { "kind": "primitive", "type": "string" } },
                  { "name": "role", "type": { "kind": "primitive", "type": "string" } }
                ]
              }],
              "enums": [],
              "endpoints": [{
                "name": "updateRole",
                "httpMethod": "PUT",
                "routeTemplate": "/api/members/{id}/role",
                "params": [
                  { "name": "id", "type": { "kind": "primitive", "type": "string" }, "source": "route" },
                  { "name": "body", "type": { "kind": "ref", "name": "UpdateRoleInput" }, "source": "body" }
                ],
                "controllerName": "members",
                "responses": []
              }]
            }
            """;

        using var doc = EmitOpenApiFromJsonContract(json);
        var bodySchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}/role").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var bodyComponent = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("UpdateRoleInput");

        Assert.Equal("#/components/schemas/UpdateRoleInput", bodySchema.GetProperty("$ref").GetString());
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(bodyComponent.GetProperty("properties").TryGetProperty("role", out _));
    }

    [Fact]
    public void Controller_Route_And_Body_Properties_With_The_Same_Name_Remain_Independent()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record UpdateRequest(string Id, string Role);

            [ApiController]
            public sealed class MembersController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPut("/api/members/{id}")]
                public IActionResult Update(string id, [FromBody] UpdateRequest request) => Ok();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var bodySchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");

        Assert.Equal("#/components/schemas/UpdateRequest", bodySchema.GetProperty("$ref").GetString());
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("UpdateRequest").GetProperty("properties").TryGetProperty("id", out _));
    }

    [Fact]
    public void Generic_Contract_Input_Excludes_Route_Property_From_Request_Body()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateInput<T>(string Id, T Value);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<UpdateInput<string>> Update =
                    Define.Put("/api/members/{id}").Accepts<UpdateInput<string>>();
            }
            """;

        using var doc = EmitOpenApi(source);
        var bodyRef = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/members/{id}").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()!;
        var body = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(bodyRef.Split('/')[^1]);

        Assert.False(body.GetProperty("properties").TryGetProperty("id", out _));
        Assert.Equal("string", body.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public void Route_To_Body_Property_Provenance_Serializes_In_Contract_Ir()
    {
        var parameter = new TsEndpointParam(
            "id",
            new TsType.Primitive("string"),
            ParamSource.Route,
            BodyPropertyName: "member_key");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new TsTypeJsonConverter());
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var json = JsonSerializer.Serialize(parameter, options);

        Assert.Equal("member_key", JsonDocument.Parse(json).RootElement
            .GetProperty("bodyPropertyName").GetString());
    }

    [Fact]
    public void RouteFiltered_Body_Names_Avoid_Definitions_And_SameNamed_Endpoints()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType] public sealed record UpdateRequest(string Existing);
            [RivetType] public sealed record MemberInput(string Id, string Role);
            [RivetType] public sealed record TeamInput(string Id, string Name);

            [RivetContract]
            public static class MembersContract
            {
                public static readonly InputRouteDefinition<MemberInput> Update =
                    Define.Put("/members/{id}").Accepts<MemberInput>();
            }

            [RivetContract]
            public static class TeamsContract
            {
                public static readonly InputRouteDefinition<TeamInput> Update =
                    Define.Put("/teams/{id}").Accepts<TeamInput>();
            }
            """;

        using var doc = EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var memberRef = doc.RootElement.GetProperty("paths").GetProperty("/members/{id}").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();
        var teamRef = doc.RootElement.GetProperty("paths").GetProperty("/teams/{id}").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();

        Assert.Equal("#/components/schemas/MembersUpdateRequest", memberRef);
        Assert.Equal("#/components/schemas/TeamsUpdateRequest", teamRef);
        Assert.True(schemas.GetProperty("UpdateRequest").GetProperty("properties").TryGetProperty("existing", out _));
        Assert.True(schemas.GetProperty("MembersUpdateRequest").GetProperty("properties").TryGetProperty("role", out _));
        Assert.True(schemas.GetProperty("TeamsUpdateRequest").GetProperty("properties").TryGetProperty("name", out _));
    }

    [Fact]
    public void Get_Endpoint_Path_OperationId_Tags_Parameters_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GetTaskInput(string Id, string Status);

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<GetTaskInput, TaskDto>("/api/tasks/{id}")
                        .Description("Retrieve a single task by ID");
            }
            """;

        using var doc = EmitOpenApi(source);
        var root = doc.RootElement;

        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());

        var pathItem = root.GetProperty("paths").GetProperty("/api/tasks/{id}");
        var get = pathItem.GetProperty("get");

        Assert.Equal("tasks_getTask", get.GetProperty("operationId").GetString());
        Assert.Equal("Tasks", get.GetProperty("tags")[0].GetString());
        Assert.False(get.TryGetProperty("summary", out _), "Description-only should not set summary");
        Assert.Equal("Retrieve a single task by ID", get.GetProperty("description").GetString());

        // Parameters: id (path), status (query)
        var parameters = get.GetProperty("parameters");
        Assert.Equal(2, parameters.GetArrayLength());

        var idParam = parameters[0];
        Assert.Equal("id", idParam.GetProperty("name").GetString());
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());
        Assert.Equal("string", idParam.GetProperty("schema").GetProperty("type").GetString());

        var statusParam = parameters[1];
        Assert.Equal("status", statusParam.GetProperty("name").GetString());
        Assert.Equal("query", statusParam.GetProperty("in").GetString());
        Assert.Equal("string", statusParam.GetProperty("schema").GetProperty("type").GetString());

        // Response 200
        var resp200 = get.GetProperty("responses").GetProperty("200");
        Assert.Equal("Success", resp200.GetProperty("description").GetString());
        var schemaRef = resp200.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/TaskDto", schemaRef);
    }

    [Fact]
    public void Post_WithBody_RequestBody_ApplicationJson()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateTaskInput(string Title);

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define CreateTask =
                    Define.Post<CreateTaskInput, TaskDto>("/api/tasks");
            }
            """;

        using var doc = EmitOpenApi(source);
        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("post");

        var requestBody = post.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());

        var bodySchema = requestBody.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("#/components/schemas/CreateTaskInput", bodySchema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void Multi_Response_StatusCodes()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        using var doc = EmitOpenApi(source);
        var responses = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{id}")
            .GetProperty("get")
            .GetProperty("responses");

        Assert.True(responses.TryGetProperty("200", out var resp200));
        Assert.True(responses.TryGetProperty("404", out var resp404));

        // 200 response DataType → TaskDto
        Assert.Equal("#/components/schemas/TaskDto",
            resp200.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());

        // 404 response DataType → NotFoundDto
        Assert.Equal("Task not found", resp404.GetProperty("description").GetString());
        Assert.Equal("#/components/schemas/NotFoundDto",
            resp404.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Direct_Emission_Normalizes_Duplicate_Response_Statuses_First_Wins()
    {
        var endpoint = new TsEndpointDefinition(
            "getTask",
            "GET",
            "/api/tasks/{id}",
            [],
            null,
            "tasks",
            [
                new TsResponseType(200, new TsType.Primitive("string"), "first"),
                new TsResponseType(
                    200,
                    new TsType.Primitive("number"),
                    "second",
                    [new TsEndpointExample("application/json", "discarded", ComponentExampleId: "discarded", ResolvedJson: "123")]),
            ]);

        string? json = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            json = OpenApiEmitter.Emit(
                [endpoint],
                new Dictionary<string, TsTypeDefinition>(),
                new Dictionary<string, TsType.Brand>(),
                new Dictionary<string, TsType>(),
                null));

        using var document = JsonDocument.Parse(json!);
        var response = document.RootElement.GetProperty("paths").GetProperty("/api/tasks/{id}")
            .GetProperty("get").GetProperty("responses").GetProperty("200");

        Assert.Equal("first", response.GetProperty("description").GetString());
        Assert.Equal("string", response.GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("warning RIV2010:", stderr);
        Assert.DoesNotContain("discarded", json);
    }

    [Fact]
    public void Direct_Emission_Rejects_Duplicate_Primary_Security_Definition()
    {
        var primary = new Dictionary<string, object> { ["type"] = "http", ["scheme"] = "bearer" };
        var replacement = new Dictionary<string, object> { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-Key" };
        var security = new SecurityConfig(
            "bearer",
            primary,
            new Dictionary<string, Dictionary<string, object>> { ["bearer"] = replacement });

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => OpenApiEmitter.Emit(
            [],
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>(),
            security));

        Assert.Contains("error RIV2011:", exception.Message);
    }

    [Fact]
    public void Void_Response_No_Content()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}")
                        .Status(204);
            }
            """;

        using var doc = EmitOpenApi(source);
        var resp204 = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{id}")
            .GetProperty("delete")
            .GetProperty("responses")
            .GetProperty("204");

        Assert.Equal("No Content", resp204.GetProperty("description").GetString());
        Assert.False(resp204.TryGetProperty("content", out _));
    }

    [Fact]
    public void File_Upload_Multipart()
    {
        // File params are produced by the model for GET/DELETE with IFormFile properties.
        // Test via direct model construction to verify the emitter handles ParamSource.File.
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "upload", "POST", "/api/files",
                [new TsEndpointParam("document", new TsType.Primitive("File"), ParamSource.File)],
                new TsType.TypeRef("UploadResult"),
                "files",
                [new TsResponseType(201, new TsType.TypeRef("UploadResult"))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UploadResult"] = new("UploadResult", [], [new TsPropertyDefinition("url", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post");

        var requestBody = post.GetProperty("requestBody");
        var multipart = requestBody.GetProperty("content").GetProperty("multipart/form-data");
        var schema = multipart.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("document", out var fileProp));
        Assert.Equal("string", fileProp.GetProperty("type").GetString());
        Assert.Equal("binary", fileProp.GetProperty("format").GetString());

        // Response 201 DataType → UploadResult
        var resp201 = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201");
        Assert.Equal("#/components/schemas/UploadResult",
            resp201.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void RequestBody_Single_Unnamed_Inline_Example_Uses_Singular_Example()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderInput"), ParamSource.Body)],
                new TsType.TypeRef("OrderDto"),
                "orders",
                [new TsResponseType(201, new TsType.TypeRef("OrderDto"))],
                RequestExamples:
                [
                    new TsEndpointExample(
                        "application/json",
                        Json: "{\"customerId\":\"cust_123\"}"),
                ]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderInput"] = new(
                "CreateOrderInput",
                [],
                [new TsPropertyDefinition("customerId", new TsType.Primitive("string"), false)]),
            ["OrderDto"] = new(
                "OrderDto",
                [],
                [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(
            endpoints,
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var requestContent = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json");

        Assert.Equal("#/components/schemas/CreateOrderInput", requestContent.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("cust_123", requestContent.GetProperty("example").GetProperty("customerId").GetString());
        Assert.False(requestContent.TryGetProperty("examples", out _));
    }

    [Fact]
    public void Response_Named_Examples_Use_Examples_Collection()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderInput"), ParamSource.Body)],
                new TsType.TypeRef("OrderDto"),
                "orders",
                [
                    new TsResponseType(
                        201,
                        new TsType.TypeRef("OrderDto"),
                        Examples:
                        [
                            new TsEndpointExample("application/json", Name: "created", Json: "{\"id\":\"ord_123\"}"),
                            new TsEndpointExample("application/json", Name: "queued", Json: "{\"id\":\"ord_124\"}")
                        ]),
                ]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderInput"] = new(
                "CreateOrderInput",
                [],
                [new TsPropertyDefinition("customerId", new TsType.Primitive("string"), false)]),
            ["OrderDto"] = new(
                "OrderDto",
                [],
                [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(
            endpoints,
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var responseContent = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json");

        Assert.False(responseContent.TryGetProperty("example", out _));
        var examples = responseContent.GetProperty("examples");
        Assert.Equal("ord_123", examples.GetProperty("created").GetProperty("value").GetProperty("id").GetString());
        Assert.Equal("ord_124", examples.GetProperty("queued").GetProperty("value").GetProperty("id").GetString());
    }

    [Fact]
    public void Response_Single_Unnamed_Inline_Example_Uses_Singular_Example()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderInput"), ParamSource.Body)],
                new TsType.TypeRef("OrderDto"),
                "orders",
                [
                    new TsResponseType(
                        201,
                        new TsType.TypeRef("OrderDto"),
                        Examples:
                        [
                            new TsEndpointExample("application/json", Json: "{\"id\":\"ord_123\"}")
                        ]),
                ]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderInput"] = new(
                "CreateOrderInput",
                [],
                [new TsPropertyDefinition("customerId", new TsType.Primitive("string"), false)]),
            ["OrderDto"] = new(
                "OrderDto",
                [],
                [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(
            endpoints,
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var responseContent = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json");

        Assert.Equal("#/components/schemas/OrderDto", responseContent.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("ord_123", responseContent.GetProperty("example").GetProperty("id").GetString());
        Assert.False(responseContent.TryGetProperty("examples", out _));
    }

    [Fact]
    public void Response_RefBacked_Example_Uses_Component_Example_Reference()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderInput"), ParamSource.Body)],
                new TsType.TypeRef("OrderDto"),
                "orders",
                [
                    new TsResponseType(
                        201,
                        new TsType.TypeRef("OrderDto"),
                        Examples:
                        [
                            new TsEndpointExample(
                                "application/json",
                                Name: "createdFromTemplate",
                                ComponentExampleId: "order-created-template",
                                ResolvedJson: "{\"id\":\"ord_456\"}")
                        ]),
                ]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderInput"] = new(
                "CreateOrderInput",
                [],
                [new TsPropertyDefinition("customerId", new TsType.Primitive("string"), false)]),
            ["OrderDto"] = new(
                "OrderDto",
                [],
                [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(
            endpoints,
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var responseExamples = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");

        Assert.Equal(
            "#/components/examples/order-created-template",
            responseExamples.GetProperty("createdFromTemplate").GetProperty("$ref").GetString());

        var componentExample = doc.RootElement.GetProperty("components")
            .GetProperty("examples")
            .GetProperty("order-created-template");
        Assert.Equal("ord_456", componentExample.GetProperty("value").GetProperty("id").GetString());
    }

    [Fact]
    public void FormEncoded_RequestExampleJson_Emits_FormUrlEncoded_Example()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginInput(string Email, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly Define Login =
                    Define.Post<LoginInput, TokenDto>("/api/login")
                        .FormEncoded()
                        .RequestExampleJson("{\"email\":\"ada@example.com\",\"password\":\"secret\"}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var mediaType = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/login")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/x-www-form-urlencoded");

        Assert.Equal(
            "ada@example.com",
            mediaType.GetProperty("example").GetProperty("email").GetString());
        Assert.Equal(
            "secret",
            mediaType.GetProperty("example").GetProperty("password").GetString());
    }

    [Fact]
    public void Controller_ExampleAttributes_Emit_Request_And_Response_OpenApi_Metadata()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}")]
                [RivetResponseExample(
                    422,
                    "{\"title\":\"Validation failed\"}",
                    componentExampleId: "validation-problem",
                    name: "validationProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/items")
            .GetProperty("post");

        var requestContent = post.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json");
        Assert.Equal("Ada", requestContent.GetProperty("example").GetProperty("name").GetString());

        var responseExamples = post.GetProperty("responses")
            .GetProperty("422")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");
        Assert.Equal(
            "#/components/examples/validation-problem",
            responseExamples.GetProperty("validationProblem").GetProperty("$ref").GetString());

        var componentExample = doc.RootElement.GetProperty("components")
            .GetProperty("examples")
            .GetProperty("validation-problem");
        Assert.Equal("Validation failed", componentExample.GetProperty("value").GetProperty("title").GetString());
    }

    [Fact]
    public void Controller_RequestExampleAttribute_RefBacked_Request_Uses_Component_Example_Reference()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [RivetRequestExample(
                    "{\"name\":\"Ada\"}",
                    componentExampleId: "create-item",
                    name: "starter",
                    mediaType: "application/merge-patch+json")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var requestContent = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/items")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/merge-patch+json");

        var examples = requestContent.GetProperty("examples");
        Assert.Equal(
            "#/components/examples/create-item",
            examples.GetProperty("starter").GetProperty("$ref").GetString());

        var componentExample = doc.RootElement.GetProperty("components")
            .GetProperty("examples")
            .GetProperty("create-item");
        Assert.Equal("Ada", componentExample.GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public void Controller_ResponseExampleAttribute_Attaches_To_Synthesized_ActionResult_Success_Response_In_OpenApi()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ErrorDto(string Message);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                [ProducesResponseType(typeof(ErrorDto), 404)]
                [RivetResponseExample(200, "{\"id\":\"550e8400-e29b-41d4-a716-446655440000\",\"name\":\"Ada\"}", name: "ok")]
                public static Task<ActionResult<ItemDto>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var examples = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/items/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");

        Assert.Equal(
            "Ada",
            examples.GetProperty("ok").GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public void Controller_RequestExampleAttribute_Defaults_To_Multipart_For_File_Upload_Endpoints_In_OpenApi()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/uploads")]
                [ProducesResponseType(typeof(UploadResultDto), 201)]
                [RivetRequestExample("{\"title\":\"Quarterly report\"}")]
                public static Task<IActionResult> Upload(IFormFile file, [FromQuery] string title)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var multipart = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/uploads")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data");

        Assert.Equal("Quarterly report", multipart.GetProperty("example").GetProperty("title").GetString());
    }

    [Fact]
    public void Controller_QueryOnly_RequestExample_Is_Not_Emitted_Without_RequestBody()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchResultDto(string Id);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/search")]
                [ProducesResponseType(typeof(SearchResultDto[]), 200)]
                [RivetRequestExample("{\"status\":\"open\"}", name: "filter")]
                public static Task<IActionResult> Search([FromQuery] string status)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var get = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/search")
            .GetProperty("get");

        Assert.False(get.TryGetProperty("requestBody", out _));
        var schema = get.GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal(
            "#/components/schemas/SearchResultDto",
            schema.GetProperty("items").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Controller_FromForm_RequestExample_Emits_FormUrlEncoded_Content()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/login")]
                [ProducesResponseType(typeof(TokenDto), 200)]
                [RivetRequestExample("{\"email\":\"ada@example.com\",\"password\":\"secret\"}")]
                public static Task<IActionResult> Login([FromForm] LoginRequest request)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var formContent = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/login")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/x-www-form-urlencoded");

        Assert.Equal("ada@example.com", formContent.GetProperty("example").GetProperty("email").GetString());
        Assert.Equal("secret", formContent.GetProperty("example").GetProperty("password").GetString());
    }

    [Fact]
    public void RivetClient_AutoDiscovered_Method_Emits_Request_And_Response_Examples()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetClient]
            [Route("api/items")]
            public sealed class ItemsController
            {
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}", name: "starter")]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/items")
            .GetProperty("post");

        var requestExamples = post.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");
        Assert.Equal("Ada", requestExamples.GetProperty("starter").GetProperty("value").GetProperty("name").GetString());

        var responseExamples = post.GetProperty("responses")
            .GetProperty("422")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");
        Assert.Equal(
            "Validation failed",
            responseExamples.GetProperty("validationProblem").GetProperty("value").GetProperty("title").GetString());
    }

    [Fact]
    public void Controller_Multiple_ExampleAttributes_Emit_As_Examples_Collections_In_Declaration_Order()
    {
        var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}", name: "starter")]
                [RivetRequestExample("{\"name\":\"Bea\"}", name: "followUp")]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                [RivetResponseExample(422, "{\"title\":\"Already archived\"}", name: "archivedProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = EmitOpenApiFromController(source);
        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/items")
            .GetProperty("post");

        var requestExamples = post.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["starter", "followUp"], requestExamples);

        var responseExamples = post.GetProperty("responses")
            .GetProperty("422")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["validationProblem", "archivedProblem"], responseExamples);
    }

    [Fact]
    public void Multipart_RequestExampleRef_Emits_Component_Example_Reference()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadResultDto> Upload =
                    Define.Post<UploadResultDto>("/api/files")
                        .AcceptsFile()
                        .RequestExampleRef(
                            "upload-example",
                            "{\"file\":\"ignored\"}",
                            name: "upload");
            }
            """;

        using var doc = EmitOpenApi(source);
        var multipart = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data");

        Assert.Equal(
            "#/components/examples/upload-example",
            multipart.GetProperty("examples").GetProperty("upload").GetProperty("$ref").GetString());
        Assert.Equal(
            "ignored",
            doc.RootElement.GetProperty("components")
                .GetProperty("examples")
                .GetProperty("upload-example")
                .GetProperty("value")
                .GetProperty("file")
                .GetString());
    }

    [Fact]
    public void ResponseExampleJson_With_AlternateMediaType_Emits_Matching_Content()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<ProblemDto>(422)
                        .ResponseExampleJson(
                            422,
                            "{\"title\":\"Bad request\"}",
                            name: "problem",
                            mediaType: "application/problem+json");
            }
            """;

        using var doc = EmitOpenApi(source);
        var mediaType = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("422")
            .GetProperty("content")
            .GetProperty("application/problem+json");

        Assert.Equal("#/components/schemas/ProblemDto", mediaType.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal(
            "Bad request",
            mediaType.GetProperty("examples").GetProperty("problem").GetProperty("value").GetProperty("title").GetString());
    }

    [Fact]
    public void Untyped_204_ResponseExampleJson_Emits_Content_Without_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition DeleteSession =
                    Define.Delete("/api/auth/session")
                        .ResponseExampleJson(204, "{\"message\":\"deleted\"}", name: "deleted");
            }
            """;

        using var doc = EmitOpenApi(source);
        var mediaType = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/auth/session")
            .GetProperty("delete")
            .GetProperty("responses")
            .GetProperty("204")
            .GetProperty("content")
            .GetProperty("application/json");

        Assert.False(mediaType.TryGetProperty("schema", out _));
        Assert.Equal(
            "deleted",
            mediaType.GetProperty("examples").GetProperty("deleted").GetProperty("value").GetProperty("message").GetString());
    }

    [Fact]
    public void File_ResponseExampleRef_Emits_File_Content_Type_And_Component_Example()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/documents/{id}")
                        .ProducesFile("application/pdf")
                        .ResponseExampleRef(200, "document-example", "{\"href\":\"/api/documents/123\"}", name: "document");
            }
            """;

        using var doc = EmitOpenApi(source);
        var mediaType = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/documents/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/pdf");

        Assert.Equal("string", mediaType.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("binary", mediaType.GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal(
            "#/components/examples/document-example",
            mediaType.GetProperty("examples").GetProperty("document").GetProperty("$ref").GetString());
        Assert.Equal(
            "/api/documents/123",
            doc.RootElement.GetProperty("components")
                .GetProperty("examples")
                .GetProperty("document-example")
                .GetProperty("value")
                .GetProperty("href")
                .GetString());
    }

    [Fact]
    public void JsonContract_ComponentExampleId_Without_ResolvedJson_Does_Not_Emit_Empty_Example_Object()
    {
        var contractJson = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "createOrder",
                        "httpMethod": "POST",
                        "routeTemplate": "/orders",
                        "controllerName": "orders",
                        "params": [],
                        "responses": [
                            {
                                "statusCode": 202,
                                "examples": [
                                    {
                                        "mediaType": "application/json",
                                        "name": "accepted",
                                        "componentExampleId": "order-accepted"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
            """;

        using var doc = EmitOpenApiFromJsonContract(contractJson);
        var response = doc.RootElement.GetProperty("paths")
            .GetProperty("/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("202");

        Assert.False(response.TryGetProperty("content", out _));
    }

    [Fact]
    public void Unnamed_Multiple_Examples_Get_AutoKeys_And_Preserve_Count()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderRequest"), ParamSource.Body)],
                new TsType.TypeRef("OrderDto"),
                "orders",
                [
                    new TsResponseType(
                        201,
                        new TsType.TypeRef("OrderDto"),
                        Examples:
                        [
                            new TsEndpointExample("application/json", Json: "{\"id\":\"ord_123\"}"),
                            new TsEndpointExample("application/json", Json: "{\"id\":\"ord_124\"}")
                        ])
                ])
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderRequest"] = new(
                "CreateOrderRequest",
                [],
                [new TsPropertyDefinition("customerId", new TsType.Primitive("string"), false)]),
            ["OrderDto"] = new(
                "OrderDto",
                [],
                [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)])
        };

        using var doc = EmitOpenApiFromModel(
            endpoints,
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var examples = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples");

        Assert.Equal("ord_123", examples.GetProperty("example1").GetProperty("value").GetProperty("id").GetString());
        Assert.Equal("ord_124", examples.GetProperty("example2").GetProperty("value").GetProperty("id").GetString());
    }

    [Fact]
    public void Schema_Generation_Object_Properties_Required()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title, int Priority);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var taskSchema = doc.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("TaskDto");

        Assert.Equal("object", taskSchema.GetProperty("type").GetString());

        var props = taskSchema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("title").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("priority").GetProperty("type").GetString());

        var required = taskSchema.GetProperty("required");
        Assert.Equal(3, required.GetArrayLength());
    }

    [Fact]
    public void Nullable_Primitive_Emits_Type_Array_With_Null()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string? Description);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var descProp = doc.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("TaskDto")
            .GetProperty("properties")
            .GetProperty("description");

        // Nullable primitive → type array with "null" (OpenAPI 3.1)
        var typeArray = descProp.GetProperty("type");
        Assert.Equal(JsonValueKind.Array, typeArray.ValueKind);
        Assert.Equal(["string", "null"], typeArray.EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.False(descProp.TryGetProperty("nullable", out _), "3.1 must not emit nullable: true");
    }

    [Fact]
    public void Nullable_Ref_Emits_OneOf_With_Null_Branch()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string City);

            [RivetType]
            public sealed record PersonDto(string Name, AddressDto? Address);

            [RivetContract]
            public static class PeopleContract
            {
                public static readonly Define GetPerson =
                    Define.Get<PersonDto>("/api/people/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var addressProp = doc.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PersonDto")
            .GetProperty("properties")
            .GetProperty("address");

        // Nullable ref → oneOf [$ref, { type: "null" }] (OpenAPI 3.1)
        var oneOf = addressProp.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("#/components/schemas/AddressDto", oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", oneOf[1].GetProperty("type").GetString());
        Assert.False(addressProp.TryGetProperty("nullable", out _), "3.1 must not emit nullable: true");
        Assert.False(addressProp.TryGetProperty("allOf", out _), "3.1 must not use the allOf nullable wrap");
    }

    [Fact]
    public void Enrichment_Survives_As_Ref_Siblings()
    {
        // E7: OpenAPI 3.1 / JSON Schema 2020-12 permits $ref siblings — property
        // enrichment (description, deprecated, ...) must be emitted alongside the
        // $ref instead of being dropped or allOf-wrapped.
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CategoryDto(string Name);

            [RivetType]
            public sealed record ProductDto(
                string Id,
                [property: RivetDescription("Primary product category")]
                CategoryDto Category,
                [property: Obsolete]
                CategoryDto LegacyCategory);

            [RivetContract]
            public static class ProductsContract
            {
                public static readonly Define GetProduct =
                    Define.Get<ProductDto>("/api/products/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProductDto")
            .GetProperty("properties");

        var category = props.GetProperty("category");
        Assert.Equal("#/components/schemas/CategoryDto", category.GetProperty("$ref").GetString());
        Assert.Equal("Primary product category", category.GetProperty("description").GetString());

        var legacy = props.GetProperty("legacyCategory");
        Assert.Equal("#/components/schemas/CategoryDto", legacy.GetProperty("$ref").GetString());
        Assert.True(legacy.GetProperty("deprecated").GetBoolean());
    }

    [Fact]
    public void Emitted_Json_Uses_Only_OpenApi31_Nullable_Patterns()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string City);

            [RivetType]
            public sealed record PersonDto(string Name, string? Bio, int? Age, AddressDto? Address);

            [RivetContract]
            public static class PeopleContract
            {
                public static readonly Define GetPerson =
                    Define.Get<PersonDto>("/api/people/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var root = doc.RootElement;

        // Version must be 3.1.x
        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());

        var personSchema = root.GetProperty("components").GetProperty("schemas").GetProperty("PersonDto");
        var props = personSchema.GetProperty("properties");

        // Nullable primitive (Bio) → type: ["string", "null"]
        var bio = props.GetProperty("bio");
        Assert.Equal(["string", "null"], bio.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).ToArray());

        // Nullable primitive (Age) → type: ["integer", "null"]
        var age = props.GetProperty("age");
        Assert.Equal(["integer", "null"], age.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).ToArray());

        // Nullable ref (Address) → oneOf [$ref, { type: "null" }]
        var address = props.GetProperty("address");
        var addressOneOf = address.GetProperty("oneOf");
        Assert.Equal(2, addressOneOf.GetArrayLength());
        Assert.Equal("null", addressOneOf[1].GetProperty("type").GetString());
        Assert.False(address.TryGetProperty("allOf", out _), "3.1 must not use the allOf nullable wrap");

        // No 3.0-style nullable: true anywhere
        var json = root.GetRawText();
        Assert.DoesNotContain("\"nullable\"", json);
    }

    [Fact]
    public void Nullable_Inline_Schema_Gets_Type_Array_With_Null()
    {
        // Nullable array, nullable dictionary — verify the type becomes a ["T", "null"] array
        var nullableArray = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Nullable(new TsType.Array(new TsType.Primitive("string"))));
        var json = JsonSerializer.Serialize(nullableArray);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(["array", "null"], doc.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.False(doc.TryGetProperty("nullable", out _));

        var nullableDict = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Nullable(new TsType.Dictionary(new TsType.Primitive("number"))));
        var dictJson = JsonSerializer.Serialize(nullableDict);
        var dictDoc = JsonSerializer.Deserialize<JsonElement>(dictJson);

        Assert.Equal(["object", "null"], dictDoc.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.False(dictDoc.TryGetProperty("nullable", out _));
    }

    [Fact]
    public void Array_Dictionary_StringUnion_Brand_Schemas()
    {
        // Test Array, Dictionary, StringUnion, and Brand type mappings directly
        var arraySchema = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Array(new TsType.Primitive("string")));
        Assert.Equal("array", ((JsonElement)JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(arraySchema))).GetProperty("type").GetString());

        var dictSchema = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Dictionary(new TsType.Primitive("number")));
        var dictJson = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dictSchema));
        Assert.Equal("object", dictJson.GetProperty("type").GetString());
        Assert.Equal("number", dictJson.GetProperty("additionalProperties").GetProperty("type").GetString());

        var unionSchema = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.StringUnion(["Active", "Archived"]));
        var unionJson = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(unionSchema));
        Assert.Equal("string", unionJson.GetProperty("type").GetString());
        Assert.Equal("Active", unionJson.GetProperty("enum")[0].GetString());
        Assert.Equal("Archived", unionJson.GetProperty("enum")[1].GetString());

        var brandSchema = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Brand("Email", new TsType.Primitive("string")));
        var brandJson = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(brandSchema));
        Assert.Equal("#/components/schemas/Email", brandJson.GetProperty("$ref").GetString());
    }

    [Fact]
    public void Security_Default_Scheme_TopLevel()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        var security = new SecurityConfig("bearer", new Dictionary<string, object>
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
        });

        using var doc = EmitOpenApi(source, security);
        var root = doc.RootElement;

        // Top-level security
        var topSecurity = root.GetProperty("security");
        Assert.Equal(1, topSecurity.GetArrayLength());
        Assert.True(topSecurity[0].TryGetProperty("bearer", out _));

        // securitySchemes
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        var bearer = schemes.GetProperty("bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    [Fact]
    public void Security_Anonymous_Endpoint()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record StatusDto(string Status);

            [RivetContract]
            public static class HealthContract
            {
                public static readonly Define Health =
                    Define.Get<StatusDto>("/api/health")
                        .Anonymous();
            }
            """;

        var security = new SecurityConfig("bearer", new Dictionary<string, object>
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
        });

        using var doc = EmitOpenApi(source, security);
        var get = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/health")
            .GetProperty("get");

        // Anonymous endpoint → empty security array
        var opSecurity = get.GetProperty("security");
        Assert.Equal(0, opSecurity.GetArrayLength());
    }

    [Fact]
    public void Security_PerEndpoint_Secure()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define DeleteAll =
                    Define.Delete("/api/admin/tasks")
                        .Status(204)
                        .Secure("admin");
            }
            """;

        var security = new SecurityConfig("admin", new Dictionary<string, object>
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
        });

        using var doc = EmitOpenApi(source, security);
        var delete = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/admin/tasks")
            .GetProperty("delete");

        var opSecurity = delete.GetProperty("security");
        Assert.Equal(1, opSecurity.GetArrayLength());
        Assert.True(opSecurity[0].TryGetProperty("admin", out _));
    }

    [Fact]
    public void Security_PerEndpoint_Undefined_Scheme_Fails_Generation()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define DeleteAll =
                    Define.Delete("/api/admin/tasks")
                        .Status(204)
                        .Secure("admin");
            }
            """;

        var security = new SecurityConfig("bearer", new Dictionary<string, object>
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
        });

        var exception = Assert.Throws<OpenApiEmissionException>(() => EmitOpenApi(source, security));

        Assert.Contains("RIV2002", exception.Message);
        Assert.Contains("security scheme 'admin'", exception.Message);
    }

    [Fact]
    public void Security_PerEndpoint_Secure_Matching_Cli_Scheme_Is_Not_Duplicated()
    {
        // .Secure("bearer") referencing the CLI-defined scheme must reuse the CLI
        // definition — no synthesis, no diagnostic.
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define DeleteAll =
                    Define.Delete("/api/admin/tasks")
                        .Status(204)
                        .Secure("bearer");
            }
            """;

        var security = new SecurityConfig("bearer", new Dictionary<string, object>
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
        });

        JsonDocument? doc = null;
        var stderr = CompilationHelper.CaptureStdErr(() => doc = EmitOpenApi(source, security));
        using var docGuard = doc;

        // (CaptureStdErr may see unrelated warnings from parallel tests — assert only
        // that no synthesis happened for THIS scheme.)
        Assert.DoesNotContain("security scheme 'bearer'", stderr);

        var schemes = doc!.RootElement
            .GetProperty("components").GetProperty("securitySchemes");
        Assert.Single(schemes.EnumerateObject());
        Assert.True(schemes.TryGetProperty("bearer", out _));
    }

    [Fact]
    public void No_Security_Flag_No_Security_Anywhere()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var root = doc.RootElement;

        // No top-level security
        Assert.False(root.TryGetProperty("security", out _));

        // No securitySchemes
        if (root.TryGetProperty("components", out var components))
        {
            Assert.False(components.TryGetProperty("securitySchemes", out _));
        }
    }

    [Fact]
    public void Descriptions_Endpoint_Summary_Response_Description()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Get a task")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        using var doc = EmitOpenApi(source);
        var get = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{id}")
            .GetProperty("get");

        Assert.False(get.TryGetProperty("summary", out _), "Description-only should not set summary");
        Assert.Equal("Get a task", get.GetProperty("description").GetString());
        Assert.Equal("Task not found",
            get.GetProperty("responses").GetProperty("404").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("bearer", "bearer", "http", "bearer")]
    [InlineData("Bearer", "bearer", "http", "bearer")]
    [InlineData("bearer:jwt", "bearer", "http", "bearer")]
    [InlineData("cookie:sid", "cookieAuth", "apiKey", "cookie")]
    [InlineData("apikey:header:X-API-Key", "apiKeyAuth", "apiKey", "header")]
    [InlineData("admin=bearer", "admin", "http", "bearer")]
    [InlineData("session=cookie:sid", "session", "apiKey", "cookie")]
    [InlineData("internal=apikey:header:X-API-Key", "internal", "apiKey", "header")]
    [InlineData("Internal=APIKEY:HEADER:X-API-Key", "Internal", "apiKey", "header")]
    public void SecurityParser_Parse_Formats(string spec, string expectedName, string expectedType, string expectedSchemeOrIn)
    {
        var result = SecurityParser.Parse(spec);
        Assert.NotNull(result);
        Assert.Equal(expectedName, result.SchemeName);
        Assert.Equal(expectedType, result.SchemeDefinition["type"]);

        if (expectedType == "http")
        {
            Assert.Equal(expectedSchemeOrIn, result.SchemeDefinition["scheme"]);
        }
        else
        {
            Assert.Equal(expectedSchemeOrIn, result.SchemeDefinition["in"]);
        }
    }

    [Fact]
    public void SecurityParser_BearerJwt_HasFormat()
    {
        var result = SecurityParser.Parse("bearer:jwt");
        Assert.NotNull(result);
        Assert.Equal("JWT", result.SchemeDefinition["bearerFormat"]);
    }

    [Fact]
    public void SecurityParser_Cookie_HasName()
    {
        var result = SecurityParser.Parse("cookie:sid");
        Assert.NotNull(result);
        Assert.Equal("sid", result.SchemeDefinition["name"]);
    }

    [Fact]
    public void SecurityParser_ApiKey_HasName()
    {
        var result = SecurityParser.Parse("apikey:header:X-API-Key");
        Assert.NotNull(result);
        Assert.Equal("X-API-Key", result.SchemeDefinition["name"]);
    }

    [Fact]
    public void SecurityParser_Null_Returns_Null()
    {
        Assert.Null(SecurityParser.Parse(null));
        Assert.Null(SecurityParser.Parse(""));
        Assert.Null(SecurityParser.Parse("   "));
    }

    [Fact]
    public void SecurityParser_Unknown_Returns_Null()
    {
        Assert.Null(SecurityParser.Parse("oauth2"));
    }

    [Theory]
    [InlineData("admin scheme=bearer")]
    [InlineData("admin/auth=bearer")]
    [InlineData("admin:auth=bearer")]
    public void SecurityParser_Invalid_Explicit_Component_Name_Returns_Null(string spec)
    {
        Assert.Null(SecurityParser.Parse(spec));
    }

    [Theory]
    [InlineData("admin.scheme=bearer", "admin.scheme")]
    [InlineData("admin_scheme=bearer", "admin_scheme")]
    [InlineData("admin-scheme=bearer", "admin-scheme")]
    public void SecurityParser_Valid_Explicit_Component_Name_Is_Preserved(
        string spec,
        string expectedName)
    {
        Assert.Equal(expectedName, SecurityParser.Parse(spec)?.SchemeName);
    }

    [Theory]
    [InlineData("apikey:path:X-API-Key")]
    [InlineData("apikey::X-API-Key")]
    [InlineData("apikey:header:")]
    [InlineData("cookie:")]
    [InlineData("bearer:")]
    [InlineData("admin=other=bearer")]
    public void SecurityParser_Malformed_Returns_Null(string spec)
    {
        Assert.Null(SecurityParser.Parse(spec));
    }

    [Fact]
    public void SecurityParser_ParseMany_Rejects_Malformed_Explicit_Value()
    {
        var exception = Assert.Throws<SecurityConfigurationException>(() =>
            SecurityParser.ParseMany(["bearer", "oauth2"]));

        Assert.Contains("invalid --security value 'oauth2'", exception.Message);
    }

    [Fact]
    public void SecurityParser_ParseMany_Rejects_Duplicate_Scheme_Name()
    {
        var exception = Assert.Throws<SecurityConfigurationException>(() =>
            SecurityParser.ParseMany(["admin=bearer", "admin=cookie:sid"]));

        Assert.Contains("duplicate --security scheme name 'admin'", exception.Message);
    }

    [Fact]
    public void Valid_OpenApi_Structure()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Get a task")
                        .Returns<NotFoundDto>(404, "Not found");

                public static readonly Define CreateTask =
                    Define.Post<TaskDto, TaskDto>("/api/tasks");

                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}")
                        .Status(204);
            }
            """;

        using var doc = EmitOpenApi(source);
        var root = doc.RootElement;

        // Required top-level fields
        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
        Assert.True(root.TryGetProperty("info", out var info));
        Assert.True(info.TryGetProperty("title", out _));
        Assert.True(info.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("paths", out var paths));

        var validMethods = new HashSet<string> { "get", "post", "put", "delete", "patch", "options", "head", "trace" };

        foreach (var path in paths.EnumerateObject())
        {
            // Path keys must start with /
            Assert.StartsWith("/", path.Name);

            foreach (var method in path.Value.EnumerateObject())
            {
                // HTTP methods must be valid
                Assert.Contains(method.Name, validMethods);

                // Every operation must have responses
                Assert.True(method.Value.TryGetProperty("responses", out var responses),
                    $"{method.Name.ToUpperInvariant()} {path.Name} missing responses");

                foreach (var resp in responses.EnumerateObject())
                {
                    // Status codes must be numeric
                    Assert.True(int.TryParse(resp.Name, out _),
                        $"Non-numeric status code '{resp.Name}' in {method.Name.ToUpperInvariant()} {path.Name}");

                    // Each response must have a description
                    Assert.True(resp.Value.TryGetProperty("description", out _),
                        $"Response {resp.Name} missing description in {method.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }
    }

    [Fact]
    public void Generic_Response_Type_Produces_Monomorphised_Schema()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "list", "GET", "/api/tasks",
                [],
                new TsType.Generic("PagedResult", [new TsType.TypeRef("TaskDto")]),
                "tasks",
                [new TsResponseType(200, new TsType.Generic("PagedResult", [new TsType.TypeRef("TaskDto")]))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["TaskDto"] = new("TaskDto", [],
            [
                new TsPropertyDefinition("id", new TsType.Primitive("string"), false),
                new TsPropertyDefinition("title", new TsType.Primitive("string"), false),
            ]),
            ["PagedResult"] = new("PagedResult", ["T"],
            [
                new TsPropertyDefinition("items", new TsType.Array(new TsType.TypeParam("T")), false),
                new TsPropertyDefinition("totalCount", new TsType.Primitive("number"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Monomorphised schema must exist
        Assert.True(schemas.TryGetProperty("PagedResult_TaskDto", out var pagedSchema),
            "Missing monomorphised schema PagedResult_TaskDto");

        Assert.Equal("object", pagedSchema.GetProperty("type").GetString());

        // items should resolve T → TaskDto
        var items = pagedSchema.GetProperty("properties").GetProperty("items");
        Assert.Equal("array", items.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/TaskDto", items.GetProperty("items").GetProperty("$ref").GetString());

        // Response $ref should point to monomorphised name
        var respSchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("#/components/schemas/PagedResult_TaskDto", respSchema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void All_Refs_Resolve_To_Existing_Schemas()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LabelDto(string Name, string Color);

            [RivetType]
            public sealed record TaskDto(string Id, string Title, LabelDto[] Labels);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404, "Not found");
            }
            """;

        using var doc = EmitOpenApi(source);
        var root = doc.RootElement;

        // Collect all schema names
        var schemaNames = new HashSet<string>();
        if (root.TryGetProperty("components", out var components) &&
            components.TryGetProperty("schemas", out var schemas))
        {
            foreach (var schema in schemas.EnumerateObject())
            {
                schemaNames.Add(schema.Name);
            }
        }

        // Collect all $ref values recursively
        var refs = new List<string>();
        CollectRefs(root, refs);

        // Every $ref must resolve
        foreach (var refValue in refs)
        {
            Assert.StartsWith("#/components/schemas/", refValue);
            var schemaName = refValue["#/components/schemas/".Length..];
            Assert.True(schemaNames.Contains(schemaName),
                $"Broken $ref: {refValue} — schema '{schemaName}' not found in components/schemas. Available: [{string.Join(", ", schemaNames)}]");
        }
    }

    [Fact]
    public void Emitted_OpenApi_Is_Valid_Spec()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public enum Priority { Low, Medium, High }

            [RivetType]
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record UserDto(
                Guid Id,
                string Name,
                string? Email,
                int Age,
                bool IsActive,
                Priority Priority,
                AddressDto Address,
                IReadOnlyList<string> Tags,
                Dictionary<string, string> Metadata);

            [RivetType]
            public sealed record CreateUserRequest(string Name, string Email);

            [RivetType]
            public sealed record ErrorDto(string Code, string Message);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            [RivetContract]
            public static class UsersContract
            {
                public static readonly Define ListUsers =
                    Define.Get<PagedResult<UserDto>>("/api/users")
                        .Description("List all users");

                public static readonly Define GetUser =
                    Define.Get<UserDto>("/api/users/{id}")
                        .Returns<NotFoundDto>(404, "User not found");

                public static readonly Define CreateUser =
                    Define.Post<CreateUserRequest, UserDto>("/api/users")
                        .Status(201)
                        .Returns<ErrorDto>(400, "Validation error");

                public static readonly Define DeleteUser =
                    Define.Delete("/api/users/{id}")
                        .Returns<NotFoundDto>(404, "Not found");
            }

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition DownloadFile =
                    Define.Get("/api/files/{id}")
                        .ProducesFile("application/pdf")
                        .Returns<NotFoundDto>(404, "File not found");

                public static readonly RouteDefinition<byte[]> DownloadRaw =
                    Define.Get<byte[]>("/api/files/{id}/raw");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        var readResult = OpenApiDocument.Parse(json, "json");

        Assert.NotNull(readResult.Document);

        var errors = readResult.Diagnostic?.Errors ?? [];
        Assert.True(errors.Count == 0,
            $"OpenAPI validation errors:\n{string.Join("\n", errors.Select(e => $"  - {e.Message}"))}");
    }

    [Fact]
    public void Nullable_Query_Param_Not_Required()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(string Query, string? Category);

            [RivetType]
            public sealed record ResultDto(string Id);

            [RivetContract]
            public static class SearchContract
            {
                public static readonly Define Search =
                    Define.Get<SearchInput, ResultDto>("/api/search");
            }
            """;

        using var doc = EmitOpenApi(source);
        var parameters = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/search")
            .GetProperty("get")
            .GetProperty("parameters");

        // Find query and category params
        JsonElement? queryParam = null;
        JsonElement? categoryParam = null;
        foreach (var p in parameters.EnumerateArray())
        {
            if (p.GetProperty("in").GetString() == "query")
            {
                if (p.GetProperty("name").GetString() == "query")
                    queryParam = p;
                else if (p.GetProperty("name").GetString() == "category")
                    categoryParam = p;
            }
        }

        Assert.NotNull(queryParam);
        Assert.NotNull(categoryParam);
        Assert.True(queryParam.Value.GetProperty("required").GetBoolean(), "Non-nullable query param should be required");
        Assert.Equal("string", queryParam.Value.GetProperty("schema").GetProperty("type").GetString());

        Assert.False(categoryParam.Value.GetProperty("required").GetBoolean(), "Nullable query param should not be required");
        Assert.Equal(["string", "null"],
            categoryParam.Value.GetProperty("schema").GetProperty("type")
                .EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    [Fact]
    public void Void_Endpoint_Without_Status_Gets_204_Response()
    {
        // Void endpoint with no .Status() and no return type → should get default 204
        var endpoints = new List<TsEndpointDefinition>
        {
            new("doSomething", "POST", "/api/do", [], null, "test", []),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var responses = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/do")
            .GetProperty("post")
            .GetProperty("responses");

        Assert.True(responses.TryGetProperty("204", out var resp204));
        Assert.Equal("No Content", resp204.GetProperty("description").GetString());
        Assert.False(resp204.TryGetProperty("content", out _), "204 response should have no content");
    }

    [Fact]
    public void InlineObject_In_Schema()
    {
        var inlineSchema = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.InlineObject([("key", new TsType.Primitive("string")), ("value", new TsType.Primitive("number"))]));
        var json = JsonSerializer.Serialize(inlineSchema);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal("object", doc.GetProperty("type").GetString());
        var props = doc.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("key").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public void Monomorphised_Names_Use_Underscores()
    {
        // Two different generic instantiations should have distinct, delimited names
        var endpoints = new List<TsEndpointDefinition>
        {
            new("listA", "GET", "/api/a", [],
                new TsType.Generic("PagedResult", [new TsType.TypeRef("A")]),
                "test",
                [new TsResponseType(200, new TsType.Generic("PagedResult", [new TsType.TypeRef("A")]))]),
            new("listAB", "GET", "/api/ab", [],
                new TsType.Generic("PagedResult", [new TsType.TypeRef("AB")]),
                "test",
                [new TsResponseType(200, new TsType.Generic("PagedResult", [new TsType.TypeRef("AB")]))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["A"] = new("A", [], [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
            ["AB"] = new("AB", [], [new TsPropertyDefinition("id", new TsType.Primitive("string"), false)]),
            ["PagedResult"] = new("PagedResult", ["T"],
            [
                new TsPropertyDefinition("items", new TsType.Array(new TsType.TypeParam("T")), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // With underscore separator, PagedResult_A and PagedResult_AB are distinct
        Assert.True(schemas.TryGetProperty("PagedResult_A", out _));
        Assert.True(schemas.TryGetProperty("PagedResult_AB", out _));
    }

    [Fact]
    public void GetTypeNameSuffix_Distinct_Instantiations_Get_Distinct_Names()
    {
        // StringUnion inside a generic should produce a readable suffix
        var genericWithUnion = new TsType.Generic("Wrapper",
            [new TsType.StringUnion(["A", "B"])]);
        var schema = OpenApiEmitter.MapTsTypeToJsonSchema(genericWithUnion);
        var parsed = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema));
        Assert.Equal("#/components/schemas/Wrapper_AB", parsed.GetProperty("$ref").GetString());

        // E2 root: inline-object suffixes must incorporate field TYPES, not just names —
        // Wrapper<{value:string}> and Wrapper<{value:number}> are distinct instantiations
        // and must never share a component name (the old scheme collapsed both to
        // "Wrapper_Value" and silently overwrote one schema with the other).
        var wrapperOfString = new TsType.Generic("Wrapper",
            [new TsType.InlineObject([("value", new TsType.Primitive("string"))])]);
        var wrapperOfNumber = new TsType.Generic("Wrapper",
            [new TsType.InlineObject([("value", new TsType.Primitive("number"))])]);

        var refString = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(OpenApiEmitter.MapTsTypeToJsonSchema(wrapperOfString)))
            .GetProperty("$ref").GetString();
        var refNumber = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(OpenApiEmitter.MapTsTypeToJsonSchema(wrapperOfNumber)))
            .GetProperty("$ref").GetString();

        Assert.Equal("#/components/schemas/Wrapper_Value_String", refString);
        Assert.Equal("#/components/schemas/Wrapper_Value_Number", refNumber);
        Assert.NotEqual(refString, refNumber);

        // Determinism: the same instantiation always maps to the same name
        var refStringAgain = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(OpenApiEmitter.MapTsTypeToJsonSchema(
                    new TsType.Generic("Wrapper",
                        [new TsType.InlineObject([("value", new TsType.Primitive("string"))])]))))
            .GetProperty("$ref").GetString();
        Assert.Equal(refString, refStringAgain);

        // Dictionary suffix
        var genericWithDict = new TsType.Generic("Wrapper",
            [new TsType.Dictionary(new TsType.Primitive("string"))]);
        var schema3 = OpenApiEmitter.MapTsTypeToJsonSchema(genericWithDict);
        var parsed3 = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema3));
        Assert.Equal("#/components/schemas/Wrapper_RecordString", parsed3.GetProperty("$ref").GetString());
    }

    [Fact]
    public void Colliding_Generic_Instantiations_Get_Deterministic_Numeric_Suffix()
    {
        // 4+-member string unions still collapse to the "Enum" suffix — two DIFFERENT
        // unions inside the same generic would purely both name "Wrapper_Enum". The emitter
        // must disambiguate deterministically instead of silently overwriting one component.
        var unionOne = new TsType.StringUnion(["A", "B", "C", "D"]);
        var unionTwo = new TsType.StringUnion(["W", "X", "Y", "Z"]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getOne", "GET", "/api/one", [],
                new TsType.Generic("Wrapper", [unionOne]), "test",
                [new TsResponseType(200, new TsType.Generic("Wrapper", [unionOne]))]),
            new("getTwo", "GET", "/api/two", [],
                new TsType.Generic("Wrapper", [unionTwo]), "test",
                [new TsResponseType(200, new TsType.Generic("Wrapper", [unionTwo]))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Wrapper"] = new("Wrapper", ["T"],
            [
                new TsPropertyDefinition("value", new TsType.TypeParam("T"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Both instantiations exist as distinct components (first keeps the pure name,
        // second gets a deterministic numeric suffix)
        Assert.True(schemas.TryGetProperty("Wrapper_Enum", out var first));
        Assert.True(schemas.TryGetProperty("Wrapper_Enum2", out var second));

        var firstEnum = first.GetProperty("properties").GetProperty("value").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        var secondEnum = second.GetProperty("properties").GetProperty("value").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["A", "B", "C", "D"], firstEnum);
        Assert.Equal(["W", "X", "Y", "Z"], secondEnum);

        // The paths reference the right component each
        var refOne = doc.RootElement.GetProperty("paths").GetProperty("/api/one").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        var refTwo = doc.RootElement.GetProperty("paths").GetProperty("/api/two").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/Wrapper_Enum", refOne);
        Assert.Equal("#/components/schemas/Wrapper_Enum2", refTwo);
    }

    [Fact]
    public void MultipartSchema_IncludesFormFieldProperties()
    {
        // Mixed file upload: file + text fields should all appear in multipart schema
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "upload", "POST", "/api/files",
                [
                    new TsEndpointParam("document", new TsType.Primitive("File"), ParamSource.File),
                    new TsEndpointParam("title", new TsType.Primitive("string"), ParamSource.FormField),
                    new TsEndpointParam("categoryId", new TsType.Primitive("number"), ParamSource.FormField),
                ],
                new TsType.TypeRef("UploadResult"),
                "files",
                [new TsResponseType(201, new TsType.TypeRef("UploadResult"))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UploadResult"] = new("UploadResult", [], [new TsPropertyDefinition("url", new TsType.Primitive("string"), false)]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema")
            .GetProperty("properties");

        // File field
        Assert.True(schema.TryGetProperty("document", out var fileProp));
        Assert.Equal("binary", fileProp.GetProperty("format").GetString());

        // Non-file fields should also be present
        Assert.True(schema.TryGetProperty("title", out var titleProp));
        Assert.Equal("string", titleProp.GetProperty("type").GetString());

        Assert.True(schema.TryGetProperty("categoryId", out var catProp));
        Assert.Equal("number", catProp.GetProperty("type").GetString());

        // Response 201 DataType → UploadResult
        var resp201 = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201");
        Assert.Equal("#/components/schemas/UploadResult",
            resp201.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Brand_Emits_Component_Schema_With_Extension()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Email(string Value);

            [RivetType]
            public sealed record UserDto(string Name, Email Email);

            [RivetContract]
            public static class UsersContract
            {
                public static readonly Define GetUser =
                    Define.Get<UserDto>("/api/users/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Brand should be a component schema
        Assert.True(schemas.TryGetProperty("Email", out var emailSchema));
        Assert.Equal("string", emailSchema.GetProperty("type").GetString());
        Assert.Equal("Email", emailSchema.GetProperty("x-rivet-brand").GetString());

        // UserDto.email should $ref to Email
        var emailProp = schemas.GetProperty("UserDto")
            .GetProperty("properties")
            .GetProperty("email");
        Assert.Equal("#/components/schemas/Email", emailProp.GetProperty("$ref").GetString());
    }

    [Fact]
    public void FileUpload_Emits_Extensions()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "upload", "POST", "/api/files",
                [
                    new TsEndpointParam("document", new TsType.Primitive("File"), ParamSource.File),
                    new TsEndpointParam("title", new TsType.Primitive("string"), ParamSource.FormField),
                ],
                new TsType.TypeRef("UploadResult"),
                "files",
                [new TsResponseType(201, new TsType.TypeRef("UploadResult"))],
                InputTypeName: "UploadInput"),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UploadResult"] = new("UploadResult", [], [new TsPropertyDefinition("url", new TsType.Primitive("string"), false)]),
            ["UploadInput"] = new("UploadInput", [],
            [
                new TsPropertyDefinition("document", new TsType.Primitive("File"), false),
                new TsPropertyDefinition("title", new TsType.Primitive("string"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var multipartSchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");

        // Named input type emits as $ref
        Assert.Equal("#/components/schemas/UploadInput", multipartSchema.GetProperty("$ref").GetString());

        // The component schema has x-rivet-file on file properties
        var uploadSchema = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("UploadInput");
        var docProp = uploadSchema.GetProperty("properties").GetProperty("document");
        Assert.True(docProp.GetProperty("x-rivet-file").GetBoolean());
        Assert.Equal("string", docProp.GetProperty("type").GetString());
        Assert.Equal("binary", docProp.GetProperty("format").GetString());

        // Response 201 DataType → UploadResult
        var resp201 = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201");
        Assert.Equal("#/components/schemas/UploadResult",
            resp201.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Generic_Schema_Emits_Extension()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "list", "GET", "/api/tasks",
                [],
                new TsType.Generic("PagedResult", [new TsType.TypeRef("TaskDto")]),
                "tasks",
                [new TsResponseType(200, new TsType.Generic("PagedResult", [new TsType.TypeRef("TaskDto")]))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["TaskDto"] = new("TaskDto", [],
            [
                new TsPropertyDefinition("id", new TsType.Primitive("string"), false),
            ]),
            ["PagedResult"] = new("PagedResult", ["T"],
            [
                new TsPropertyDefinition("items", new TsType.Array(new TsType.TypeParam("T")), false),
                new TsPropertyDefinition("totalCount", new TsType.Primitive("number"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var monoSchema = doc.RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PagedResult_TaskDto");

        var ext = monoSchema.GetProperty("x-rivet-generic");
        Assert.Equal("PagedResult", ext.GetProperty("name").GetString());
        Assert.Equal("T", ext.GetProperty("typeParams")[0].GetString());
        Assert.Equal("TaskDto", ext.GetProperty("args").GetProperty("T").GetString());
    }

    [Fact]
    public void RequestType_IgnoredWhenBodyParamExists()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [new TsEndpointParam("body", new TsType.TypeRef("FromParams"), ParamSource.Body)],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.TypeRef("FromRequestType")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var schema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        // Should use Param (FromParams), not RequestType (FromRequestType)
        Assert.Equal("#/components/schemas/FromParams", schema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void RequestType_FormEncoded_UsesFormContentType()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                IsFormEncoded: true,
                RequestType: new TsType.TypeRef("CreateBuyerRequest")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var requestBody = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody");

        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var content = requestBody.GetProperty("content");
        Assert.True(content.TryGetProperty("application/x-www-form-urlencoded", out var formContent));
        Assert.Equal("#/components/schemas/CreateBuyerRequest",
            formContent.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public void RequestType_Generic_ProducesMonomorphisedSchema()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.Generic("Envelope", [new TsType.TypeRef("BuyerDto")])),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["BuyerDto"] = new("BuyerDto", [],
            [
                new TsPropertyDefinition("id", new TsType.Primitive("string"), false),
            ]),
            ["Envelope"] = new("Envelope", ["T"],
            [
                new TsPropertyDefinition("data", new TsType.TypeParam("T"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        // The monomorphised schema must exist
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("Envelope_BuyerDto", out var envSchema),
            "Missing monomorphised schema Envelope_BuyerDto");

        // data property should resolve T → BuyerDto
        var dataProp = envSchema.GetProperty("properties").GetProperty("data");
        Assert.Equal("#/components/schemas/BuyerDto", dataProp.GetProperty("$ref").GetString());

        // requestBody should $ref the monomorphised name
        var reqBodySchema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("#/components/schemas/Envelope_BuyerDto", reqBodySchema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void CollectGenericsFromType_WalksBrandInner()
    {
        // A Brand wrapping a Generic must produce the monomorphised schema
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "list", "GET", "/api/items",
                [],
                new TsType.Brand("Tagged", new TsType.Generic("Wrapper", [new TsType.TypeRef("ItemDto")])),
                "items",
                [new TsResponseType(200,
                    new TsType.Brand("Tagged", new TsType.Generic("Wrapper", [new TsType.TypeRef("ItemDto")])))]),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["ItemDto"] = new("ItemDto", [],
            [
                new TsPropertyDefinition("id", new TsType.Primitive("string"), false),
            ]),
            ["Wrapper"] = new("Wrapper", ["T"],
            [
                new TsPropertyDefinition("data", new TsType.TypeParam("T"), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("Wrapper_ItemDto", out _),
            "Missing monomorphised schema Wrapper_ItemDto — CollectGenericsFromType must walk Brand inner");
    }

    [Fact]
    public void RequestType_Nullable_EmitsNullableRequestBody()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.Nullable(new TsType.TypeRef("CreateBuyerRequest"))),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var schema = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        // Nullable TypeRef wraps in oneOf with an explicit null branch (3.1)
        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("#/components/schemas/CreateBuyerRequest",
            oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", oneOf[1].GetProperty("type").GetString());
    }

    [Fact]
    public void RequestType_InlineObject_WithNestedGeneric_ProducesMonomorphisedSchema()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.InlineObject([
                    ("name", new TsType.Primitive("string")),
                    ("tags", new TsType.Generic("TagList", [new TsType.TypeRef("TagDto")])),
                ])),
        };

        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["TagDto"] = new("TagDto", [],
            [
                new TsPropertyDefinition("label", new TsType.Primitive("string"), false),
            ]),
            ["TagList"] = new("TagList", ["T"],
            [
                new TsPropertyDefinition("items", new TsType.Array(new TsType.TypeParam("T")), false),
            ]),
        };

        using var doc = EmitOpenApiFromModel(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("TagList_TagDto", out var tagListSchema),
            "Missing monomorphised schema TagList_TagDto");

        var itemsProp = tagListSchema.GetProperty("properties").GetProperty("items");
        Assert.Equal("array", itemsProp.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/TagDto",
            itemsProp.GetProperty("items").GetProperty("$ref").GetString());
    }

    [Fact]
    public void RequestType_IgnoredWhenFileParamsExist()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "upload", "POST", "/api/buyers",
                [new TsEndpointParam("document", new TsType.Primitive("File"), ParamSource.File)],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.TypeRef("CreateBuyerRequest")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var requestBody = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody");

        // File upload takes priority — multipart, not application/json
        var content = requestBody.GetProperty("content");
        Assert.True(content.TryGetProperty("multipart/form-data", out _));
        Assert.False(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public void RequestType_Absent_NoRequestBody()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("list", "GET", "/api/buyers", [], null, "buyers",
                [new TsResponseType(200, null)]),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var get = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("get");

        Assert.False(get.TryGetProperty("requestBody", out _));
    }

    [Fact]
    public void RequestType_InlineObject_EmitsRequestBody()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.InlineObject([
                    ("id", new TsType.Primitive("number")),
                    ("name", new TsType.Primitive("string")),
                ])),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var requestBody = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post")
            .GetProperty("requestBody");

        Assert.True(requestBody.GetProperty("required").GetBoolean());

        var schema = requestBody.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
    }

    [Fact]
    public void RequestType_TypeRef_EmitsRequestBody()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "create", "POST", "/api/buyers",
                [],
                null, "buyers",
                [new TsResponseType(200, null)],
                RequestType: new TsType.TypeRef("CreateBuyerRequest")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("post");

        var requestBody = post.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.Equal("#/components/schemas/CreateBuyerRequest",
            requestBody.GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref").GetString());
    }

    private static void CollectRefs(JsonElement element, List<string> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        refs.Add(prop.Value.GetString()!);
                    }
                    else
                    {
                        CollectRefs(prop.Value, refs);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectRefs(item, refs);
                }
                break;
        }
    }

    // ========== Description-only does not bleed into summary ==========

    [Fact]
    public void Endpoint_Description_Only_Does_Not_Set_Summary()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/items/{id}")
                        .Description("Retrieves an item by ID");
            }
            """;

        using var doc = EmitOpenApi(source);
        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/items/{id}")
            .GetProperty("get");

        Assert.False(operation.TryGetProperty("summary", out _),
            "Description-only endpoint should not have a summary field");
        Assert.True(operation.TryGetProperty("description", out var desc),
            "Operation should have a 'description' field");
        Assert.Equal("Retrieves an item by ID", desc.GetString());
    }

    [Fact]
    public void QueryAuth_Emits_Query_Parameter_And_Extension()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("streamVideo",
                "GET",
                "/api/media/{id}/stream",
                [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
                null,
                "MediaController",
                [new TsResponseType(200, null)],
                QueryAuth: new QueryAuthMetadata("token")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/media/{id}/stream")
            .GetProperty("get");

        // Query parameter for auth token
        var parameters = operation.GetProperty("parameters");
        var tokenParam = parameters.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "token");
        Assert.Equal("query", tokenParam.GetProperty("in").GetString());
        Assert.True(tokenParam.GetProperty("required").GetBoolean());
        Assert.Equal("string", tokenParam.GetProperty("schema").GetProperty("type").GetString());

        // x-rivet-query-auth extension
        var ext = operation.GetProperty("x-rivet-query-auth");
        Assert.Equal("token", ext.GetProperty("parameterName").GetString());
    }

    [Fact]
    public void QueryAuth_Custom_ParameterName_Emits_Correctly()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("streamAudio",
                "GET",
                "/api/audio/{id}",
                [],
                null,
                "AudioController",
                [new TsResponseType(200, null)],
                QueryAuth: new QueryAuthMetadata("key")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/audio/{id}")
            .GetProperty("get");

        var parameters = operation.GetProperty("parameters");
        Assert.Equal(1, parameters.GetArrayLength());
        var keyParam = parameters[0];
        Assert.Equal("key", keyParam.GetProperty("name").GetString());
        Assert.Equal("query", keyParam.GetProperty("in").GetString());
        Assert.True(keyParam.GetProperty("required").GetBoolean());

        var ext = operation.GetProperty("x-rivet-query-auth");
        Assert.Equal("key", ext.GetProperty("parameterName").GetString());
    }

    [Fact]
    public void Non_QueryAuth_Endpoint_Has_No_Extension_Or_Extra_Param()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("getUser",
                "GET",
                "/api/users/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
                new TsType.TypeRef("UserDto"),
                "UsersController",
                [new TsResponseType(200, new TsType.TypeRef("UserDto"))]),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/users/{id}")
            .GetProperty("get");

        // Only the route param, no query auth param
        var parameters = operation.GetProperty("parameters");
        Assert.Equal(1, parameters.GetArrayLength());
        Assert.Equal("path", parameters[0].GetProperty("in").GetString());

        // No extension
        Assert.False(operation.TryGetProperty("x-rivet-query-auth", out _));
    }

    [Fact]
    public void File_Endpoint_With_ContentType_Emits_Binary_Response()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("streamVideo",
                "GET",
                "/api/media/{id}/stream",
                [],
                null,
                "MediaController",
                [new TsResponseType(200, null)],
                FileContentType: "video/mp4",
                IsFileEndpoint: true),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var resp = doc.RootElement
            .GetProperty("paths").GetProperty("/api/media/{id}/stream")
            .GetProperty("get")
            .GetProperty("responses").GetProperty("200");

        var content = resp.GetProperty("content");
        Assert.True(content.TryGetProperty("video/mp4", out var mediaContent));
        Assert.Equal("string", mediaContent.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("binary", mediaContent.GetProperty("schema").GetProperty("format").GetString());

        // Should not have application/json
        Assert.False(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public void File_Endpoint_With_QueryAuth_Emits_Both_Parameter_And_ContentType()
    {
        var endpoints = new List<TsEndpointDefinition>
        {
            new("streamVideo",
                "GET",
                "/api/media/{id}/stream",
                [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
                null,
                "MediaController",
                [new TsResponseType(200, null)],
                FileContentType: "video/mp4",
                IsFileEndpoint: true,
                QueryAuth: new QueryAuthMetadata("token")),
        };

        using var doc = EmitOpenApiFromModel(endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/media/{id}/stream")
            .GetProperty("get");

        // Auth query param present
        var tokenParam = operation.GetProperty("parameters").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "token");
        Assert.Equal("query", tokenParam.GetProperty("in").GetString());

        // Extension present
        Assert.Equal("token", operation.GetProperty("x-rivet-query-auth")
            .GetProperty("parameterName").GetString());

        // Response uses video/mp4
        var resp = operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content");
        Assert.True(resp.TryGetProperty("video/mp4", out _));
        Assert.False(resp.TryGetProperty("application/json", out _));
    }

    // ========== OpenAPI 3.1 numeric exclusiveMinimum/Maximum ==========

    [Fact]
    public void ExclusiveMinimum_Looser_Than_Minimum_Survives_Numerically()
    {
        using var doc = EmitOpenApi("""
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Dto(
                string Name,
                [property: Range(5, double.MaxValue), RivetConstraints(ExclusiveMinimum = 0)]
                double Value);

            [RivetContract]
            public static class TestContract
            {
                public static readonly RouteDefinition<Dto> GetTest = Define.Get<Dto>("/api/test");
            }
            """);

        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Dto")
            .GetProperty("properties").GetProperty("value");

        // Source means: x >= 5 AND x > 0. 3.1 expresses the conjunction losslessly
        // with numeric bounds — minimum stays 5 (the binding constraint, E12) and the
        // exclusive bound is carried as a number, never as the 3.0 boolean flag.
        Assert.Equal(5.0, props.GetProperty("minimum").GetDouble());
        var exclusiveMin = props.GetProperty("exclusiveMinimum");
        Assert.Equal(JsonValueKind.Number, exclusiveMin.ValueKind);
        Assert.Equal(0.0, exclusiveMin.GetDouble());
    }

    [Fact]
    public void ExclusiveMinimum_Tighter_Than_Minimum_Survives_Numerically()
    {
        // Mirror case: x >= 0 AND x > 5 — the effective bound is x > 5. 3.1 keeps
        // both numerically; exclusiveMinimum: 5 carries the binding constraint.
        using var doc = EmitOpenApi("""
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Dto(
                string Name,
                [property: Range(0, double.MaxValue), RivetConstraints(ExclusiveMinimum = 5)]
                double Value);

            [RivetContract]
            public static class TestContract
            {
                public static readonly RouteDefinition<Dto> GetTest = Define.Get<Dto>("/api/test");
            }
            """);

        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Dto")
            .GetProperty("properties").GetProperty("value");

        Assert.Equal(0.0, props.GetProperty("minimum").GetDouble());
        var exclusiveMin = props.GetProperty("exclusiveMinimum");
        Assert.Equal(JsonValueKind.Number, exclusiveMin.ValueKind);
        Assert.Equal(5.0, exclusiveMin.GetDouble());
    }

    [Fact]
    public void ExclusiveMaximum_Looser_Than_Maximum_Survives_Numerically()
    {
        // x <= 10 AND x < 100 — maximum: 10 is the binding constraint; 3.1 carries
        // the looser exclusive bound numerically without corrupting it.
        using var doc = EmitOpenApi("""
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Dto(
                string Name,
                [property: Range(double.MinValue, 10), RivetConstraints(ExclusiveMaximum = 100)]
                double Value);

            [RivetContract]
            public static class TestContract
            {
                public static readonly RouteDefinition<Dto> GetTest = Define.Get<Dto>("/api/test");
            }
            """);

        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Dto")
            .GetProperty("properties").GetProperty("value");

        Assert.Equal(10.0, props.GetProperty("maximum").GetDouble());
        var exclusiveMax = props.GetProperty("exclusiveMaximum");
        Assert.Equal(JsonValueKind.Number, exclusiveMax.ValueKind);
        Assert.Equal(100.0, exclusiveMax.GetDouble());
    }

    [Fact]
    public void TaggedUnion_TypeAlias_Emits_OneOf_With_Discriminator()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["DisplayState"] = new("DisplayState", [], new TsType.TaggedUnion("kind", [
                new TsType.TaggedUnionVariant("hidden", new TsType.InlineObject([
                    ("kind", new TsType.StringUnion(["hidden"])),
                    ("workspaceKey", new TsType.Nullable(new TsType.TypeRef("WorkspaceKey"))),
                ])),
                new TsType.TaggedUnionVariant("shown", new TsType.InlineObject([
                    ("kind", new TsType.StringUnion(["shown"])),
                    ("summary", new TsType.TypeRef("Summary")),
                ])),
            ])),
        };

        using var doc = EmitOpenApiFromModel(
            [],
            definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>());

        var schemas = doc.RootElement
            .GetProperty("components").GetProperty("schemas");
        var schema = schemas.GetProperty("DisplayState");

        // E11: discriminator is only meaningful over $ref'd named schemas with a mapping —
        // a discriminator on a oneOf of inline schemas is rejected/ignored by consumers.

        // 1. oneOf entries are $refs to named component schemas (no inline variants)
        Assert.True(schema.TryGetProperty("oneOf", out var oneOf));
        Assert.Equal(2, oneOf.GetArrayLength());
        var refs = oneOf.EnumerateArray()
            .Select(v => v.GetProperty("$ref").GetString())
            .ToList();
        Assert.Equal(
            ["#/components/schemas/DisplayState_Hidden", "#/components/schemas/DisplayState_Shown"],
            refs);

        // 2. the variant components exist and carry the variant shapes
        var hidden = schemas.GetProperty("DisplayState_Hidden");
        Assert.Equal("object", hidden.GetProperty("type").GetString());
        Assert.True(hidden.GetProperty("properties").TryGetProperty("kind", out _));
        Assert.True(hidden.GetProperty("properties").TryGetProperty("workspaceKey", out _));

        var shown = schemas.GetProperty("DisplayState_Shown");
        Assert.Equal("object", shown.GetProperty("type").GetString());
        Assert.True(shown.GetProperty("properties").TryGetProperty("kind", out _));
        Assert.True(shown.GetProperty("properties").TryGetProperty("summary", out _));

        // 3. discriminator has propertyName plus a complete tag → $ref mapping
        var discriminator = schema.GetProperty("discriminator");
        Assert.Equal("kind", discriminator.GetProperty("propertyName").GetString());
        var mapping = discriminator.GetProperty("mapping");
        Assert.Equal("#/components/schemas/DisplayState_Hidden", mapping.GetProperty("hidden").GetString());
        Assert.Equal("#/components/schemas/DisplayState_Shown", mapping.GetProperty("shown").GetString());
    }

    [Fact]
    public void Global_Tags_Array_Declares_All_Operation_Tags()
    {
        // W4: operations reference tags — the global tags array must declare them
        // (operation-tag-defined; docs-UI consumers group/order by it).
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define Purge =
                    Define.Delete("/api/admin/cache").Status(204);
            }
            """;

        using var doc = EmitOpenApi(source);
        var tags = doc.RootElement.GetProperty("tags");

        var names = tags.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["Admin", "Tasks"], names);
    }

    [Fact]
    public void Generic_Instance_With_Missing_Template_Emits_Fallback_Component_And_Diagnostic()
    {
        // GAP-1 / E6 family: a generic instantiation whose template is absent from the
        // contract's type definitions (the rivet-php golden shape) used to emit
        // $ref: #/components/schemas/Collection_ProductDto with no matching component
        // and no diagnostic — a dangling reference every consumer rejects.
        const string contractJson = """
            {
                "types": [
                    {
                        "name": "ProductDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "id", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": [
                    {
                        "name": "paginated",
                        "httpMethod": "GET",
                        "routeTemplate": "/products/paginated",
                        "controllerName": "product",
                        "params": [],
                        "returnType": {
                            "kind": "generic",
                            "name": "Collection",
                            "typeArgs": [ { "kind": "ref", "name": "ProductDto" } ]
                        },
                        "responses": [
                            {
                                "statusCode": 200,
                                "dataType": {
                                    "kind": "generic",
                                    "name": "Collection",
                                    "typeArgs": [ { "kind": "ref", "name": "ProductDto" } ]
                                }
                            }
                        ]
                    }
                ]
            }
            """;

        var spec = string.Empty;
        var stderr = CompilationHelper.CaptureStdErr(
            () => spec = CompilationHelper.EmitOpenApiFromJson(contractJson));

        // 1. Loud, named diagnostic — never a silent drop.
        Assert.Contains("generic template 'Collection'", stderr);
        Assert.Contains("Collection_ProductDto", stderr);

        using var doc = JsonDocument.Parse(spec);

        // 2. The operation still references the instantiation by name...
        var responseSchema = doc.RootElement
            .GetProperty("paths").GetProperty("/products/paginated").GetProperty("get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/Collection_ProductDto",
            responseSchema.GetProperty("$ref").GetString());

        // 3. ...and the $ref target exists as a valid (free-form object) fallback component.
        var fallback = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Collection_ProductDto");
        Assert.Equal("object", fallback.GetProperty("type").GetString());
    }

    [Fact]
    public void FromContract_Inline_Brands_Land_In_Component_Schemas()
    {
        // BUG-1 (FABLE_GAPS §3): the contract JSON carries brands only as inline
        // kind:"brand" nodes (the TS lowerer emits them that way); the --from path
        // dropped them while MapTsTypeToJsonSchema still $ref'd each brand by name —
        // a dangling reference every consumer rejects.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "contract-ts-brands.json"));
        var spec = CompilationHelper.EmitOpenApiFromJson(json);
        using var doc = JsonDocument.Parse(spec);

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Brand component schemas exist with the x-rivet-brand extension
        var email = schemas.GetProperty("Email");
        Assert.Equal("string", email.GetProperty("type").GetString());
        Assert.Equal("Email", email.GetProperty("x-rivet-brand").GetString());

        var userId = schemas.GetProperty("UserId");
        Assert.Equal("string", userId.GetProperty("type").GetString());
        Assert.Equal("UserId", userId.GetProperty("x-rivet-brand").GetString());

        // The property and param $refs point at those components
        Assert.Equal("#/components/schemas/Email",
            schemas.GetProperty("UserDto").GetProperty("properties").GetProperty("email")
                .GetProperty("$ref").GetString());

        var routeParamSchema = doc.RootElement
            .GetProperty("paths").GetProperty("/api/users/{id}").GetProperty("get")
            .GetProperty("parameters")[0].GetProperty("schema");
        Assert.Equal("#/components/schemas/UserId", routeParamSchema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void FromContract_Multipart_With_Undefined_InputType_Builds_Inline_Schema_And_Diagnostic()
    {
        // BUG-2 (FABLE_GAPS §3): the TS lowerer decomposes the multipart input into
        // params and never ships the input type definition, but the emitter $ref'd
        // ep.InputTypeName with no existence check — a schema nobody defines. The fix
        // mirrors the E6 generic fallback: warn loudly and build the multipart request
        // schema inline from the endpoint's params.
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "contract-ts-multipart.json"));

        var spec = string.Empty;
        var stderr = CompilationHelper.CaptureStdErr(
            () => spec = CompilationHelper.EmitOpenApiFromJson(json));

        // 1. Loud, named diagnostic — never a silent dangling $ref.
        Assert.Contains("multipart input type 'UploadAvatarInput'", stderr);
        Assert.Contains("users.uploadAvatar", stderr);

        using var doc = JsonDocument.Parse(spec);
        var schema = doc.RootElement
            .GetProperty("paths").GetProperty("/api/users/{id}/avatar").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");

        // 2. No $ref — the schema is built inline from the endpoint params.
        Assert.False(schema.TryGetProperty("$ref", out _),
            "Undefined multipart input type must not emit a dangling $ref");
        Assert.Equal("object", schema.GetProperty("type").GetString());

        // 3. Resolved schema properties: file part → string/binary, scalar parts → mapped schemas.
        var props = schema.GetProperty("properties");
        var file = props.GetProperty("file");
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
        Assert.True(file.GetProperty("x-rivet-file").GetBoolean());
        Assert.Equal("string", props.GetProperty("title").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("caption").GetProperty("type").GetString());

        // 4. required honours isOptional: caption (isOptional: true) is excluded.
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(["file", "title"], required.OrderBy(x => x).ToList());

        // 5. The declared input-type name survives for the importer.
        Assert.Equal("UploadAvatarInput", schema.GetProperty("x-rivet-input-type").GetString());
    }
}
