using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class OpenApiImporterTests
{
    private static ImportResult Import(string json, string ns = "Test", string? security = null)
    {
        return OpenApiImporter.Import(json, new ImportOptions(ns, security));
    }

    private static string FindFile(ImportResult result, string fileName)
    {
        var file = result.Files.FirstOrDefault(f => f.FileName.EndsWith(fileName));
        Assert.NotNull(file);
        return file.Content;
    }

    // ========== SchemaMapper Tests ==========

    [Fact]
    public void Primitive_Types_String_Int_Long_Double_Float_Bool()
    {
        var spec = BuildSpec(schemas: """
            "TestDto": {
                "type": "object",
                "properties": {
                    "name": { "type": "string" },
                    "count": { "type": "integer" },
                    "bigCount": { "type": "integer", "format": "int64" },
                    "score": { "type": "number" },
                    "rating": { "type": "number", "format": "float" },
                    "active": { "type": "boolean" }
                },
                "required": ["name", "count", "bigCount", "score", "rating", "active"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "TestDto.cs");

        Assert.Contains("string Name", content);
        Assert.Contains("int Count", content);
        Assert.Contains("long BigCount", content);
        Assert.Contains("double Score", content);
        Assert.Contains("float Rating", content);
        Assert.Contains("bool Active", content);
    }

    [Fact]
    public void DateTime_Format_Maps_To_DateTime()
    {
        var spec = BuildSpec(schemas: """
            "EventDto": {
                "type": "object",
                "properties": {
                    "createdAt": { "type": "string", "format": "date-time" }
                },
                "required": ["createdAt"]
            }
            """);

        var result = Import(spec);
        Assert.Contains("DateTime CreatedAt", FindFile(result, "EventDto.cs"));
    }

    [Fact]
    public void Guid_Format_Maps_To_Guid()
    {
        var spec = BuildSpec(schemas: """
            "ItemDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string", "format": "uuid" }
                },
                "required": ["id"]
            }
            """);

        var result = Import(spec);
        Assert.Contains("Guid Id", FindFile(result, "ItemDto.cs"));
    }

    [Fact]
    public void String_Enum_Maps_To_CSharp_Enum()
    {
        var spec = BuildSpec(schemas: """
            "Priority": {
                "type": "string",
                "enum": ["low", "medium", "high", "critical"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "Priority.cs");

        Assert.Contains("public enum Priority", content);
        Assert.Contains("Low", content);
        Assert.Contains("Medium", content);
        Assert.Contains("High", content);
        Assert.Contains("Critical", content);
    }

    [Fact]
    public void Branded_Format_Maps_To_Value_Object()
    {
        var spec = BuildSpec(schemas: """
            "Email": {
                "type": "string",
                "format": "email"
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "Email.cs");

        Assert.Contains("public sealed record Email(string Value)", content);
        Assert.Contains("public override string ToString() => Value;", content);
        Assert.Contains("Domain/Email.cs", result.Files.First(f => f.FileName.Contains("Email")).FileName);
    }

    [Fact]
    public void Array_Maps_To_List()
    {
        var spec = BuildSpec(schemas: """
            "TagList": {
                "type": "object",
                "properties": {
                    "tags": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["tags"]
            }
            """);

        Assert.Contains("List<string> Tags", FindFile(Import(spec), "TagList.cs"));
    }

    [Fact]
    public void Dictionary_Maps_To_Dictionary()
    {
        var spec = BuildSpec(schemas: """
            "MetadataDto": {
                "type": "object",
                "properties": {
                    "values": { "type": "object", "additionalProperties": { "type": "string" } }
                },
                "required": ["values"]
            }
            """);

        Assert.Contains("Dictionary<string, string> Values", FindFile(Import(spec), "MetadataDto.cs"));
    }

    [Fact]
    public void Nullable_Type_Array_Maps_To_Nullable()
    {
        var spec = BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "description": { "type": ["string", "null"] }
                },
                "required": ["description"]
            }
            """);

        Assert.Contains("string? Description", FindFile(Import(spec), "TaskDto.cs"));
    }

    [Fact]
    public void Object_With_Properties_Maps_To_Sealed_Record()
    {
        var spec = BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string" },
                    "title": { "type": "string" }
                },
                "required": ["id", "title"]
            }
            """);

        var content = FindFile(Import(spec), "TaskDto.cs");
        Assert.Contains("[RivetType]", content);
        Assert.Contains("public sealed record TaskDto(", content);
    }

    [Fact]
    public void Ref_Resolution_Uses_Named_Type()
    {
        var spec = BuildSpec(schemas: """
            "LabelDto": {
                "type": "object",
                "properties": { "name": { "type": "string" } },
                "required": ["name"]
            },
            "TaskDto": {
                "type": "object",
                "properties": { "label": { "$ref": "#/components/schemas/LabelDto" } },
                "required": ["label"]
            }
            """);

        Assert.Contains("LabelDto Label", FindFile(Import(spec), "TaskDto.cs"));
    }

    [Fact]
    public void Required_Vs_Optional_Properties()
    {
        var spec = BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string" },
                    "description": { "type": "string" }
                },
                "required": ["id"]
            }
            """);

        var content = FindFile(Import(spec), "TaskDto.cs");
        Assert.Contains("string Id", content);
        Assert.Contains("string? Description", content);
        Assert.DoesNotContain("string? Id", content);
    }

    [Fact]
    public void Unsupported_OneOf_Schema_Produces_Warning()
    {
        var spec = BuildSpec(schemas: """
            "Shape": {
                "oneOf": [
                    { "$ref": "#/components/schemas/Circle" },
                    { "$ref": "#/components/schemas/Square" }
                ]
            },
            "Circle": {
                "type": "object",
                "properties": { "radius": { "type": "number" } },
                "required": ["radius"]
            },
            "Square": {
                "type": "object",
                "properties": { "side": { "type": "number" } },
                "required": ["side"]
            }
            """);

        var result = Import(spec);
        Assert.Contains(result.Warnings, w => w.Contains("oneOf") && w.Contains("Shape"));
        Assert.Contains(result.Files, f => f.FileName.Contains("Circle"));
        Assert.Contains(result.Files, f => f.FileName.Contains("Square"));
    }

    // ========== Contract output tests (v1 static class + EndpointBuilder fields) ==========

    [Fact]
    public void Contract_Output_Is_Static_Class_With_EndpointBuilder_Fields()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
                        "summary": "List all tasks",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("[RivetContract]", content);
        Assert.Contains("public static class TasksContract", content);
        Assert.Contains("public static readonly EndpointBuilder<TaskDto> List", content);
        Assert.Contains("Endpoint.Get<TaskDto>(\"/api/tasks\")", content);
        Assert.Contains(".Description(\"List all tasks\")", content);
    }

    [Fact]
    public void Post_With_RequestBody_Has_Input_And_Output_Types()
    {
        var spec = BuildSpec(
            schemas: """
                "CreateTaskRequest": {
                    "type": "object",
                    "properties": { "title": { "type": "string" } },
                    "required": ["title"]
                },
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "post": {
                        "operationId": "tasks_createTask",
                        "tags": ["Tasks"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/CreateTaskRequest" }
                                }
                            }
                        },
                        "responses": {
                            "201": {
                                "description": "Created",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "TasksContract.cs");

        Assert.Contains("EndpointBuilder<CreateTaskRequest, TaskDto> CreateTask", content);
        Assert.Contains("Endpoint.Post<CreateTaskRequest, TaskDto>(\"/api/tasks\")", content);
        Assert.Contains(".Status(201)", content);
    }

    [Fact]
    public void Error_Responses_Produce_Returns_Call()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "NotFoundDto": {
                    "type": "object",
                    "properties": { "message": { "type": "string" } },
                    "required": ["message"]
                }
                """,
            paths: """
                "/api/tasks/{id}": {
                    "get": {
                        "operationId": "tasks_getTask",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    }
                                }
                            },
                            "404": {
                                "description": "Task not found",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/NotFoundDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        Assert.Contains(".Returns<NotFoundDto>(404, \"Task not found\")",
            FindFile(Import(spec), "TasksContract.cs"));
    }

    [Fact]
    public void Void_Endpoint_No_Output_Type()
    {
        var spec = BuildSpec(
            paths: """
                "/api/tasks/{id}": {
                    "delete": {
                        "operationId": "tasks_deleteTask",
                        "tags": ["Tasks"],
                        "responses": {
                            "204": { "description": "No Content" }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "TasksContract.cs");
        Assert.Contains("public static readonly EndpointBuilder DeleteTask", content);
        Assert.Contains("Endpoint.Delete(\"/api/tasks/{id}\")", content);
        Assert.Contains(".Status(204)", content);
    }

    [Fact]
    public void Anonymous_Endpoint()
    {
        var spec = BuildSpec(
            paths: """
                "/api/health": {
                    "get": {
                        "operationId": "health_check",
                        "tags": ["Health"],
                        "security": [],
                        "responses": { "200": { "description": "OK" } }
                    }
                }
                """);

        Assert.Contains(".Anonymous()", FindFile(Import(spec), "HealthContract.cs"));
    }

    [Fact]
    public void Secured_Endpoint()
    {
        var spec = BuildSpec(
            paths: """
                "/api/admin": {
                    "delete": {
                        "operationId": "admin_deleteAll",
                        "tags": ["Admin"],
                        "security": [{ "admin": [] }],
                        "responses": { "204": { "description": "No Content" } }
                    }
                }
                """);

        Assert.Contains(".Secure(\"admin\")", FindFile(Import(spec), "AdminContract.cs"));
    }

    [Fact]
    public void Tag_Grouping_Produces_Separate_Contracts()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] },
                "MemberDto": { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }
                """,
            paths: """
                "/api/tasks": { "get": { "operationId": "tasks_list", "tags": ["Tasks"], "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/TaskDto" } } } } } } },
                "/api/members": { "get": { "operationId": "members_list", "tags": ["Members"], "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/MemberDto" } } } } } } }
                """);

        var result = Import(spec);
        Assert.Contains(result.Files, f => f.FileName == "Contracts/TasksContract.cs");
        Assert.Contains(result.Files, f => f.FileName == "Contracts/MembersContract.cs");
    }

    [Fact]
    public void No_Tag_Uses_DefaultContract()
    {
        var spec = BuildSpec(paths: """
            "/api/health": { "get": { "operationId": "healthCheck", "responses": { "200": { "description": "OK" } } } }
            """);

        Assert.Contains(Import(spec).Files, f => f.FileName == "Contracts/DefaultContract.cs");
    }

    [Fact]
    public void OperationId_Stripped_Of_Tag_Prefix()
    {
        var spec = BuildSpec(paths: """
            "/api/tasks": { "get": { "operationId": "tasks_listAllTasks", "tags": ["Tasks"], "responses": { "200": { "description": "OK" } } } }
            """);

        Assert.Contains("ListAllTasks", FindFile(Import(spec), "TasksContract.cs"));
    }

    // ========== Fixture-based round-trip tests ==========

    private static string LoadFixture()
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openapi-import.json"));
    }

    private static Compilation CompileGeneratedFiles(ImportResult result)
    {
        return CompilationHelper.CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray());
    }

    [Fact]
    public void Fixture_Generated_CSharp_Compiles()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        Assert.Empty(result.Warnings);

        var errors = CompileGeneratedFiles(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Fixture_Contracts_Survive_Roslyn_RoundTrip()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        Assert.Equal(10, endpoints.Count);
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PATCH" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks/{taskId}/attachments");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/members");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/members");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
    }

    [Fact]
    public void Fixture_Types_Survive_Roslyn_RoundTrip()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);

        Assert.True(walker.Definitions.ContainsKey("TaskDto"));
        Assert.True(walker.Definitions.ContainsKey("LabelDto"));
        Assert.True(walker.Definitions.ContainsKey("CreateTaskRequest"));
        Assert.True(walker.Definitions.ContainsKey("AttachFileRequest"));
        Assert.True(walker.Definitions.ContainsKey("AttachmentDto"));
        Assert.True(walker.Definitions.ContainsKey("NotFoundDto"));
        Assert.True(walker.Definitions.ContainsKey("ValidationErrorDto"));
        Assert.True(walker.Definitions.ContainsKey("MemberDto"));
        Assert.True(walker.Definitions.ContainsKey("HealthDto"));

        Assert.True(walker.Enums.ContainsKey("Priority"));
        Assert.Contains("Low", walker.Enums["Priority"].Members);
        Assert.Contains("Critical", walker.Enums["Priority"].Members);

        Assert.True(walker.Brands.ContainsKey("Email"));
        Assert.True(walker.Brands.ContainsKey("Website"));
    }

    [Fact]
    public void Fixture_TaskDto_Properties_Match_OpenAPI_Schema()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var walker = TypeWalker.Create(CompileGeneratedFiles(result));
        var taskDto = walker.Definitions["TaskDto"];

        AssertProperty(taskDto, "id", "string");
        AssertProperty(taskDto, "title", "string");
        AssertProperty(taskDto, "priority", null);
        AssertProperty(taskDto, "score", "number");
        AssertProperty(taskDto, "rating", "number");
        AssertProperty(taskDto, "viewCount", "number");
        AssertProperty(taskDto, "totalBytes", "number");
        AssertProperty(taskDto, "isArchived", "boolean");
        AssertProperty(taskDto, "createdAt", "string");

        var descProp = taskDto.Properties.FirstOrDefault(p => p.Name == "description");
        Assert.NotNull(descProp);
        Assert.True(descProp.IsOptional || descProp.Type is TsType.Nullable);
    }

    [Fact]
    public void Fixture_Endpoint_Responses_Survive_RoundTrip()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        var createTask = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(createTask.Responses, r => r.StatusCode == 201);
        Assert.Contains(createTask.Responses, r => r.StatusCode == 422);

        var getTask = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(getTask.Responses, r => r.StatusCode == 200);
        Assert.Contains(getTask.Responses, r => r.StatusCode == 404);

        var deleteTask = endpoints.First(e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 204);
    }

    [Fact]
    public void Fixture_Covers_All_Supported_Type_Mappings()
    {
        var content = FindFile(Import(LoadFixture(), "Test"), "TaskDto.cs");

        Assert.Contains("Guid Id", content);
        Assert.Contains("string Title", content);
        Assert.Contains("string? Description", content);
        Assert.Contains("Priority Priority", content);
        Assert.Contains("double Score", content);
        Assert.Contains("float Rating", content);
        Assert.Contains("int ViewCount", content);
        Assert.Contains("long TotalBytes", content);
        Assert.Contains("bool IsArchived", content);
        Assert.Contains("DateTime CreatedAt", content);
        Assert.Contains("Email AssigneeEmail", content);
        Assert.Contains("List<LabelDto> Labels", content);
        Assert.Contains("Dictionary<string, string> Metadata", content);

        Assert.Contains("Priority?", FindFile(Import(LoadFixture(), "Test"), "PatchTaskRequest.cs"));
    }

    [Fact]
    public void Fixture_Covers_All_HTTP_Methods()
    {
        var content = FindFile(Import(LoadFixture(), "Test"), "TasksContract.cs");
        Assert.Contains("Endpoint.Get<", content);
        Assert.Contains("Endpoint.Post<", content);
        Assert.Contains("Endpoint.Put<", content);
        Assert.Contains("Endpoint.Patch<", content);
        Assert.Contains("Endpoint.Delete(", content);
    }

    // ========== File upload (multipart/form-data) tests ==========

    [Fact]
    public void Multipart_FormData_Binary_Property_Maps_To_IFormFile()
    {
        var result = Import(LoadFixture(), "Test");
        var content = FindFile(result, "AttachFileRequest.cs");

        Assert.Contains("IFormFile File", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.Contains("string? Description", content);
    }

    [Fact]
    public void Multipart_Endpoint_Uses_Request_As_InputType()
    {
        var content = FindFile(Import(LoadFixture(), "Test"), "TasksContract.cs");

        Assert.Contains("EndpointBuilder<AttachFileRequest, AttachmentDto> Attach", content);
        Assert.Contains("Endpoint.Post<AttachFileRequest, AttachmentDto>(\"/api/tasks/{taskId}/attachments\")", content);
    }

    [Fact]
    public void Multipart_RoundTrip_Produces_File_Param()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        var attach = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks/{taskId}/attachments");
        Assert.Contains(attach.Params, p => p.Source == ParamSource.File && p.Name == "file");
    }

    [Fact]
    public void Standalone_Multipart_Binary_Maps_To_IFormFile()
    {
        var spec = BuildSpec(
            schemas: """
                "UploadRequest": {
                    "type": "object",
                    "properties": {
                        "document": { "type": "string", "format": "binary" }
                    },
                    "required": ["document"]
                },
                "UploadResult": {
                    "type": "object",
                    "properties": { "url": { "type": "string" } },
                    "required": ["url"]
                }
                """,
            paths: """
                "/api/uploads": {
                    "post": {
                        "operationId": "uploads_create",
                        "tags": ["Uploads"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "multipart/form-data": {
                                    "schema": { "$ref": "#/components/schemas/UploadRequest" }
                                }
                            }
                        },
                        "responses": {
                            "201": {
                                "description": "Created",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/UploadResult" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var record = FindFile(result, "UploadRequest.cs");
        Assert.Contains("IFormFile Document", record);
        Assert.Contains("using Microsoft.AspNetCore.Http;", record);

        var contract = FindFile(result, "UploadsContract.cs");
        Assert.Contains("EndpointBuilder<UploadRequest, UploadResult>", contract);
    }

    // ========== Drift detection ==========

    [Fact]
    public void Importer_Output_Is_V1_Static_Class_With_Typed_Builder_Fields()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");

        foreach (var file in result.Files.Where(f => f.FileName.StartsWith("Contracts/")))
        {
            Assert.Contains("public static class", file.Content);
            Assert.Contains("[RivetContract]", file.Content);
            Assert.Contains("EndpointBuilder", file.Content);
            // Must NOT have v2 patterns
            Assert.DoesNotContain("abstract class", file.Content);
            Assert.DoesNotContain("ControllerBase", file.Content);
            Assert.DoesNotContain("[HttpGet", file.Content);
            Assert.DoesNotContain("[ProducesResponseType", file.Content);
        }
    }

    [Fact]
    public void Importer_Output_Uses_EndpointBuilder_Not_Bare_Endpoint()
    {
        // Fields should be EndpointBuilder<T> not Endpoint, so Invoke is available
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("public static readonly EndpointBuilder<", content);
        Assert.DoesNotContain("public static readonly Endpoint ", content);
    }

    [Fact]
    public void Importer_Security_And_Description_Preserved_In_Builder_Chain()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");

        // Health endpoint has security: [] → .Anonymous()
        Assert.Contains(".Anonymous()", FindFile(result, "HealthContract.cs"));

        // Members invite has security: [{"admin": []}] → .Secure("admin")
        Assert.Contains(".Secure(\"admin\")", FindFile(result, "MembersContract.cs"));

        // Tasks list has global security → .Secure("bearer")
        Assert.Contains(".Secure(\"bearer\")", FindFile(result, "TasksContract.cs"));

        // Descriptions present
        Assert.Contains(".Description(\"List all tasks\")", FindFile(result, "TasksContract.cs"));
    }

    private static void AssertProperty(TsTypeDefinition def, string name, string? expectedPrimitive)
    {
        var prop = def.Properties.FirstOrDefault(p => p.Name == name);
        Assert.True(prop is not null, $"Property '{name}' not found on {def.Name}");

        if (expectedPrimitive is not null)
        {
            var innerType = prop.Type is TsType.Nullable n ? n.Inner : prop.Type;
            Assert.True(
                innerType is TsType.Primitive p && p.Name == expectedPrimitive,
                $"Property '{name}' expected primitive '{expectedPrimitive}' but got {innerType}");
        }
    }

    // ========== Helpers ==========

    private static string BuildSpec(string? schemas = null, string? paths = null)
    {
        var schemasBlock = schemas is not null
            ? $"\"components\": {{ \"schemas\": {{ {schemas} }} }},"
            : "";

        var pathsBlock = paths is not null
            ? $"\"paths\": {{ {paths} }}"
            : "\"paths\": {}";

        return $$"""
            {
                "openapi": "3.1.0",
                "info": { "title": "API", "version": "1.0.0" },
                {{schemasBlock}}
                {{pathsBlock}}
            }
            """;
    }
}
