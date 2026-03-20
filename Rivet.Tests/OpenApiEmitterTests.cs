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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, security);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument EmitOpenApiFromModel(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType.StringUnion> enums,
        SecurityConfig? security = null)
    {
        var json = OpenApiEmitter.Emit(endpoints, definitions, brands, enums, security);
        return JsonDocument.Parse(json);
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

        Assert.Equal("3.0.3", root.GetProperty("openapi").GetString());

        var pathItem = root.GetProperty("paths").GetProperty("/api/tasks/{id}");
        var get = pathItem.GetProperty("get");

        Assert.Equal("tasks_getTask", get.GetProperty("operationId").GetString());
        Assert.Equal("Tasks", get.GetProperty("tags")[0].GetString());
        Assert.Equal("Retrieve a single task by ID", get.GetProperty("summary").GetString());

        // Parameters: id (path), status (query)
        var parameters = get.GetProperty("parameters");
        Assert.Equal(2, parameters.GetArrayLength());

        var idParam = parameters[0];
        Assert.Equal("id", idParam.GetProperty("name").GetString());
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());

        var statusParam = parameters[1];
        Assert.Equal("status", statusParam.GetProperty("name").GetString());
        Assert.Equal("query", statusParam.GetProperty("in").GetString());

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

        Assert.Equal("Task not found", resp404.GetProperty("description").GetString());
        Assert.Equal("#/components/schemas/NotFoundDto",
            resp404.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
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
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType.StringUnion>());

        var post = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post");

        var requestBody = post.GetProperty("requestBody");
        var multipart = requestBody.GetProperty("content").GetProperty("multipart/form-data");
        var schema = multipart.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("document", out var fileProp));
        Assert.Equal("binary", fileProp.GetProperty("format").GetString());
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
        Assert.Equal("number", props.GetProperty("priority").GetProperty("type").GetString());

        var required = taskSchema.GetProperty("required");
        Assert.Equal(3, required.GetArrayLength());
    }

    [Fact]
    public void Nullable_Primitive_Emits_Type_With_Nullable_True()
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

        // Nullable primitive → type + nullable: true (OpenAPI 3.0)
        Assert.Equal("string", descProp.GetProperty("type").GetString());
        Assert.True(descProp.GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public void Nullable_Ref_Emits_AllOf_With_Nullable_True()
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

        // Nullable ref → allOf [$ref] + nullable: true (OpenAPI 3.0)
        var allOf = addressProp.GetProperty("allOf");
        Assert.Equal(1, allOf.GetArrayLength());
        Assert.Equal("#/components/schemas/AddressDto", allOf[0].GetProperty("$ref").GetString());
        Assert.True(addressProp.GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public void Emitted_Json_Contains_No_OpenApi31_Nullable_Patterns()
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
        var json = doc.RootElement.GetRawText();

        // No 3.1-style type arrays like ["string", "null"]
        Assert.DoesNotContain("\"null\"", json);
        // No 3.1-style { "type": "null" } in oneOf
        Assert.DoesNotContain("\"type\": \"null\"", json);
        // Version must be 3.0.x
        Assert.Contains("\"openapi\": \"3.0.3\"", json);
        // Must use nullable: true instead
        Assert.Contains("\"nullable\": true", json);
    }

    [Fact]
    public void Nullable_Inline_Schema_Gets_Nullable_Property()
    {
        // Nullable array, nullable dictionary — verify nullable is added directly
        var nullableArray = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Nullable(new TsType.Array(new TsType.Primitive("string"))));
        var json = JsonSerializer.Serialize(nullableArray);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal("array", doc.GetProperty("type").GetString());
        Assert.True(doc.GetProperty("nullable").GetBoolean());

        var nullableDict = OpenApiEmitter.MapTsTypeToJsonSchema(
            new TsType.Nullable(new TsType.Dictionary(new TsType.Primitive("number"))));
        var dictJson = JsonSerializer.Serialize(nullableDict);
        var dictDoc = JsonSerializer.Deserialize<JsonElement>(dictJson);

        Assert.Equal("object", dictDoc.GetProperty("type").GetString());
        Assert.True(dictDoc.GetProperty("nullable").GetBoolean());
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
        Assert.Equal("string", brandJson.GetProperty("type").GetString());
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

        using var doc = EmitOpenApi(source);
        var delete = doc.RootElement.GetProperty("paths")
            .GetProperty("/api/admin/tasks")
            .GetProperty("delete");

        var opSecurity = delete.GetProperty("security");
        Assert.Equal(1, opSecurity.GetArrayLength());
        Assert.True(opSecurity[0].TryGetProperty("admin", out _));
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

        Assert.Equal("Get a task", get.GetProperty("summary").GetString());
        Assert.Equal("Task not found",
            get.GetProperty("responses").GetProperty("404").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("bearer", "bearer", "http", "bearer")]
    [InlineData("bearer:jwt", "bearer", "http", "bearer")]
    [InlineData("cookie:sid", "cookieAuth", "apiKey", "cookie")]
    [InlineData("apikey:header:X-API-Key", "apiKeyAuth", "apiKey", "header")]
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
        Assert.Equal("3.0.3", root.GetProperty("openapi").GetString());
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
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType.StringUnion>());

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Monomorphised schema must exist
        Assert.True(schemas.TryGetProperty("PagedResultTaskDto", out var pagedSchema),
            "Missing monomorphised schema PagedResultTaskDto");

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
        Assert.Equal("#/components/schemas/PagedResultTaskDto", respSchema.GetProperty("$ref").GetString());
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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        var readResult = OpenApiDocument.Parse(json, "json");

        Assert.NotNull(readResult.Document);

        var errors = readResult.Diagnostic?.Errors ?? [];
        Assert.True(errors.Count == 0,
            $"OpenAPI validation errors:\n{string.Join("\n", errors.Select(e => $"  - {e.Message}"))}");
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
}
