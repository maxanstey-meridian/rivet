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
    public void OneOf_Schema_Produces_Union_Wrapper()
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
        Assert.Contains(result.Files, f => f.FileName.Contains("Shape"));
        Assert.Contains(result.Files, f => f.FileName.Contains("Circle"));
        Assert.Contains(result.Files, f => f.FileName.Contains("Square"));

        var content = result.Files.First(f => f.FileName.Contains("Shape")).Content;
        Assert.Contains("Circle? AsCircle", content);
        Assert.Contains("Square? AsSquare", content);
    }

    // ========== Contract output tests (v1 static class + RouteDefinition fields) ==========

    [Fact]
    public void Contract_Output_Is_Static_Class_With_RouteDefinition_Fields()
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
        Assert.Contains("public static readonly RouteDefinition<TaskDto> List", content);
        Assert.Contains("Define.Get<TaskDto>(\"/api/tasks\")", content);
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

        Assert.Contains("RouteDefinition<CreateTaskRequest, TaskDto> CreateTask", content);
        Assert.Contains("Define.Post<CreateTaskRequest, TaskDto>(\"/api/tasks\")", content);
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
        Assert.Contains("public static readonly RouteDefinition DeleteTask", content);
        Assert.Contains("Define.Delete(\"/api/tasks/{id}\")", content);
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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

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
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

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
        var (_, walker) = CompilationHelper.DiscoverAndWalk(CompileGeneratedFiles(result));
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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

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
        Assert.Contains("Define.Get<", content);
        Assert.Contains("Define.Post<", content);
        Assert.Contains("Define.Put<", content);
        Assert.Contains("Define.Patch<", content);
        Assert.Contains("Define.Delete(", content);
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

        Assert.Contains("RouteDefinition<AttachFileRequest, AttachmentDto> Attach", content);
        Assert.Contains("Define.Post<AttachFileRequest, AttachmentDto>(\"/api/tasks/{taskId}/attachments\")", content);
    }

    [Fact]
    public void Multipart_RoundTrip_Produces_File_Param()
    {
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompileGeneratedFiles(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

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
        Assert.Contains("RouteDefinition<UploadRequest, UploadResult>", contract);
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
            Assert.Contains("RouteDefinition", file.Content);
            // Must NOT have v2 patterns
            Assert.DoesNotContain("abstract class", file.Content);
            Assert.DoesNotContain("ControllerBase", file.Content);
            Assert.DoesNotContain("[HttpGet", file.Content);
            Assert.DoesNotContain("[ProducesResponseType", file.Content);
        }
    }

    [Fact]
    public void Importer_Output_Uses_RouteDefinition_Not_Bare_Define()
    {
        // Fields should be RouteDefinition<T> not Define, so Invoke is available
        var result = Import(LoadFixture(), "TaskBoard.Contracts");
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("public static readonly RouteDefinition<", content);
        Assert.DoesNotContain("public static readonly Define ", content);
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

    // ========== Union ref name sanitization ==========

    [Fact]
    public void OneOf_With_Hyphenated_Ref_Names_Sanitized()
    {
        var spec = BuildSpec(schemas: """
            "my-shape": {
                "oneOf": [
                    { "$ref": "#/components/schemas/my-circle" },
                    { "$ref": "#/components/schemas/my-square" }
                ]
            },
            "my-circle": {
                "type": "object",
                "properties": { "radius": { "type": "number" } },
                "required": ["radius"]
            },
            "my-square": {
                "type": "object",
                "properties": { "side": { "type": "number" } },
                "required": ["side"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "MyShape.cs");

        // Ref names should be PascalCased, not raw "my-circle"
        Assert.Contains("MyCircle? AsMyCircle", content);
        Assert.Contains("MySquare? AsMySquare", content);
        Assert.DoesNotContain("my-circle", content);
        Assert.DoesNotContain("my-square", content);
    }

    [Fact]
    public void AnyOf_With_Dotted_Ref_Names_Sanitized()
    {
        var spec = BuildSpec(schemas: """
            "shape.union": {
                "anyOf": [
                    { "$ref": "#/components/schemas/geo.circle" },
                    { "$ref": "#/components/schemas/geo.square" }
                ]
            },
            "geo.circle": {
                "type": "object",
                "properties": { "radius": { "type": "number" } },
                "required": ["radius"]
            },
            "geo.square": {
                "type": "object",
                "properties": { "side": { "type": "number" } },
                "required": ["side"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "ShapeUnion.cs");

        Assert.Contains("GeoCircle? AsGeoCircle", content);
        Assert.Contains("GeoSquare? AsGeoSquare", content);
    }

    [Fact]
    public void Sanitized_Union_Refs_Compile()
    {
        var spec = BuildSpec(schemas: """
            "result-union": {
                "oneOf": [
                    { "$ref": "#/components/schemas/success-response" },
                    { "$ref": "#/components/schemas/error-response" }
                ]
            },
            "success-response": {
                "type": "object",
                "properties": { "data": { "type": "string" } },
                "required": ["data"]
            },
            "error-response": {
                "type": "object",
                "properties": { "message": { "type": "string" } },
                "required": ["message"]
            }
            """);

        var result = Import(spec);
        var errors = CompileGeneratedFiles(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== allOf ref name sanitization ==========

    [Fact]
    public void AllOf_With_Hyphenated_Ref_Names_Sanitized()
    {
        var spec = BuildSpec(schemas: """
            "base-address": {
                "type": "object",
                "properties": { "street": { "type": "string" } },
                "required": ["street"]
            },
            "extended-address": {
                "allOf": [
                    { "$ref": "#/components/schemas/base-address" },
                    { "type": "object", "properties": { "zip": { "type": "string" } }, "required": ["zip"] }
                ]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "ExtendedAddress.cs");

        Assert.Contains("string Street", content);
        Assert.Contains("string Zip", content);
        // No hyphens in the output
        Assert.DoesNotContain("base-address", content);
    }

    [Fact]
    public void AllOf_Nested_Hyphenated_Refs_Compile()
    {
        // allOf referencing another allOf with hyphenated names — 3 levels deep
        var spec = BuildSpec(schemas: """
            "api-base": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
            },
            "api-response-single": {
                "allOf": [
                    { "$ref": "#/components/schemas/api-base" },
                    { "type": "object", "properties": { "result": { "type": "string" } }, "required": ["result"] }
                ]
            },
            "device-api-response": {
                "allOf": [
                    { "$ref": "#/components/schemas/api-response-single" },
                    { "type": "object", "properties": { "device": { "type": "string" } }, "required": ["device"] }
                ]
            }
            """);

        var result = Import(spec);

        var errors = CompileGeneratedFiles(result).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        var content = FindFile(result, "DeviceApiResponse.cs");
        Assert.Contains("string Id", content);
        Assert.Contains("string Result", content);
        Assert.Contains("string Device", content);
    }

    // ========== $ref requestBody resolution ==========

    [Fact]
    public void RequestBody_Ref_Resolves_To_Input_Type()
    {
        var spec = """
            {
                "openapi": "3.1.0",
                "info": { "title": "API", "version": "1.0.0" },
                "components": {
                    "schemas": {
                        "BrandPayload": {
                            "type": "object",
                            "properties": { "name": { "type": "string" } },
                            "required": ["name"]
                        },
                        "BrandResult": {
                            "type": "object",
                            "properties": { "id": { "type": "string" } },
                            "required": ["id"]
                        }
                    },
                    "requestBodies": {
                        "BrandRequest": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/BrandPayload" }
                                }
                            }
                        }
                    }
                },
                "paths": {
                    "/api/brands": {
                        "post": {
                            "operationId": "brands_create",
                            "tags": ["Brands"],
                            "requestBody": {
                                "$ref": "#/components/requestBodies/BrandRequest"
                            },
                            "responses": {
                                "201": {
                                    "description": "Created",
                                    "content": {
                                        "application/json": {
                                            "schema": { "$ref": "#/components/schemas/BrandResult" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """;

        var result = Import(spec);
        var content = FindFile(result, "BrandsContract.cs");

        Assert.Contains("RouteDefinition<BrandPayload, BrandResult>", content);
        Assert.Contains("Define.Post<BrandPayload, BrandResult>", content);
    }

    [Fact]
    public void RequestBody_Ref_With_Inline_Schema()
    {
        var spec = """
            {
                "openapi": "3.1.0",
                "info": { "title": "API", "version": "1.0.0" },
                "components": {
                    "requestBodies": {
                        "SettingsBody": {
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "type": "object",
                                        "properties": { "theme": { "type": "string" } },
                                        "required": ["theme"]
                                    }
                                }
                            }
                        }
                    }
                },
                "paths": {
                    "/api/settings": {
                        "put": {
                            "operationId": "settings_update",
                            "tags": ["Settings"],
                            "requestBody": {
                                "$ref": "#/components/requestBodies/SettingsBody"
                            },
                            "responses": {
                                "204": { "description": "No Content" }
                            }
                        }
                    }
                }
            }
            """;

        var result = Import(spec);
        var content = FindFile(result, "SettingsContract.cs");

        Assert.Contains("InputRouteDefinition<UpdateRequest>", content);
        Assert.Contains(".Accepts<UpdateRequest>()", content);
    }

    [Fact]
    public void Unresolvable_RequestBody_Ref_Produces_No_Input()
    {
        var spec = """
            {
                "openapi": "3.1.0",
                "info": { "title": "API", "version": "1.0.0" },
                "paths": {
                    "/api/things": {
                        "post": {
                            "operationId": "things_create",
                            "tags": ["Things"],
                            "requestBody": {
                                "$ref": "#/components/requestBodies/DoesNotExist"
                            },
                            "responses": {
                                "201": { "description": "Created" }
                            }
                        }
                    }
                }
            }
            """;

        var result = Import(spec);
        var content = FindFile(result, "ThingsContract.cs");

        // Unresolvable ref → no input type, just bare RouteDefinition
        Assert.DoesNotContain("InputRouteDefinition", content);
        Assert.Contains("public static readonly RouteDefinition Create", content);
    }

    // ========== Empty allOf record skipping ==========

    [Fact]
    public void AllOf_With_Primitive_Ref_Skips_Empty_Record()
    {
        // allOf referencing a primitive-like schema (no properties) should not emit an empty record
        var spec = BuildSpec(schemas: """
            "StringAlias": {
                "type": "string"
            },
            "Wrapper": {
                "allOf": [
                    { "$ref": "#/components/schemas/StringAlias" }
                ]
            }
            """);

        var result = Import(spec);

        // Wrapper should not be emitted as an empty record
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("Wrapper.cs"));
    }

    [Fact]
    public void AllOf_With_Object_Ref_Still_Emits_Record()
    {
        // allOf referencing an object schema should still produce a record with flattened properties
        var spec = BuildSpec(schemas: """
            "Base": {
                "type": "object",
                "properties": { "name": { "type": "string" } },
                "required": ["name"]
            },
            "Extended": {
                "allOf": [
                    { "$ref": "#/components/schemas/Base" },
                    { "type": "object", "properties": { "extra": { "type": "string" } }, "required": ["extra"] }
                ]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "Extended.cs");

        Assert.Contains("string Name", content);
        Assert.Contains("string Extra", content);
    }

    // ========== */* content type fallback ==========

    [Fact]
    public void Wildcard_Content_Type_Resolves_Input_Type()
    {
        var spec = BuildSpec(
            schemas: """
                "PodSpec": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                },
                "PodResult": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/pods": {
                    "post": {
                        "operationId": "pods_create",
                        "tags": ["Pods"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "*/*": {
                                    "schema": { "$ref": "#/components/schemas/PodSpec" }
                                }
                            }
                        },
                        "responses": {
                            "201": {
                                "description": "Created",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/PodResult" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "PodsContract.cs");

        Assert.Contains("RouteDefinition<PodSpec, PodResult>", content);
        Assert.Contains("Define.Post<PodSpec, PodResult>", content);
    }

    [Fact]
    public void Wildcard_Content_Type_Resolves_Output_Type()
    {
        var spec = BuildSpec(
            schemas: """
                "StatusDto": {
                    "type": "object",
                    "properties": { "ok": { "type": "boolean" } },
                    "required": ["ok"]
                }
                """,
            paths: """
                "/api/status": {
                    "get": {
                        "operationId": "status_check",
                        "tags": ["Status"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "*/*": {
                                        "schema": { "$ref": "#/components/schemas/StatusDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "StatusContract.cs");

        Assert.Contains("RouteDefinition<StatusDto>", content);
        Assert.Contains("Define.Get<StatusDto>", content);
    }

    [Fact]
    public void Wildcard_Content_Type_Resolves_Error_Response()
    {
        var spec = BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "ErrorDto": {
                    "type": "object",
                    "properties": { "message": { "type": "string" } },
                    "required": ["message"]
                }
                """,
            paths: """
                "/api/items/{id}": {
                    "get": {
                        "operationId": "items_get",
                        "tags": ["Items"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            },
                            "404": {
                                "description": "Not found",
                                "content": {
                                    "*/*": {
                                        "schema": { "$ref": "#/components/schemas/ErrorDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "ItemsContract.cs");

        Assert.Contains(".Returns<ErrorDto>(404, \"Not found\")", content);
    }

    [Fact]
    public void Json_Content_Type_Takes_Priority_Over_Wildcard()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "GenericDto": {
                    "type": "object",
                    "properties": { "data": { "type": "string" } },
                    "required": ["data"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    },
                                    "*/*": {
                                        "schema": { "$ref": "#/components/schemas/GenericDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "TasksContract.cs");

        // application/json should win
        Assert.Contains("RouteDefinition<TaskDto>", content);
        Assert.DoesNotContain("GenericDto", content);
    }

    // ========== 4XX/5XX wildcard status codes ==========

    [Fact]
    public void Wildcard_4XX_Status_Code_Maps_To_400()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "ClientErrorDto": {
                    "type": "object",
                    "properties": { "error": { "type": "string" } },
                    "required": ["error"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    }
                                }
                            },
                            "4XX": {
                                "description": "Client error",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ClientErrorDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        Assert.Contains(".Returns<ClientErrorDto>(400, \"Client error\")",
            FindFile(Import(spec), "TasksContract.cs"));
    }

    [Fact]
    public void Wildcard_5XX_Status_Code_Maps_To_500()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "ServerErrorDto": {
                    "type": "object",
                    "properties": { "error": { "type": "string" } },
                    "required": ["error"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/TaskDto" }
                                    }
                                }
                            },
                            "5XX": {
                                "description": "Server error",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ServerErrorDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        Assert.Contains(".Returns<ServerErrorDto>(500, \"Server error\")",
            FindFile(Import(spec), "TasksContract.cs"));
    }

    // ========== InputRouteDefinition<T> (input-only endpoints) ==========

    [Fact]
    public void Input_Only_Endpoint_Produces_InputRouteDefinition()
    {
        var spec = BuildSpec(
            schemas: """
                "UpdateSettingsRequest": {
                    "type": "object",
                    "properties": { "theme": { "type": "string" } },
                    "required": ["theme"]
                }
                """,
            paths: """
                "/api/settings": {
                    "put": {
                        "operationId": "settings_update",
                        "tags": ["Settings"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/UpdateSettingsRequest" }
                                }
                            }
                        },
                        "responses": {
                            "204": { "description": "No Content" }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "SettingsContract.cs");

        Assert.Contains("InputRouteDefinition<UpdateSettingsRequest> Update", content);
        Assert.Contains("Define.Put(\"/api/settings\")", content);
        Assert.Contains(".Accepts<UpdateSettingsRequest>()", content);
        Assert.Contains(".Status(204)", content);
        // Must NOT have type args on Define.Put
        Assert.DoesNotContain("Define.Put<", content);
    }

    [Fact]
    public void Input_Only_Endpoint_With_Wildcard_Content_Type()
    {
        var spec = BuildSpec(
            schemas: """
                "PodSpec": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                }
                """,
            paths: """
                "/api/pods": {
                    "post": {
                        "operationId": "pods_create",
                        "tags": ["Pods"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "*/*": {
                                    "schema": { "$ref": "#/components/schemas/PodSpec" }
                                }
                            }
                        },
                        "responses": {
                            "201": { "description": "Created" }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "PodsContract.cs");

        Assert.Contains("InputRouteDefinition<PodSpec>", content);
        Assert.Contains(".Accepts<PodSpec>()", content);
    }

    [Fact]
    public void Input_Only_Endpoint_Compiles_And_Survives_RoundTrip()
    {
        var spec = BuildSpec(
            schemas: """
                "UpdateRequest": {
                    "type": "object",
                    "properties": { "value": { "type": "string" } },
                    "required": ["value"]
                }
                """,
            paths: """
                "/api/config": {
                    "put": {
                        "operationId": "config_update",
                        "tags": ["Config"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/UpdateRequest" }
                                }
                            }
                        },
                        "responses": {
                            "204": { "description": "No Content" }
                        }
                    }
                }
                """);

        var result = Import(spec);

        // Compiles
        var compilation = CompileGeneratedFiles(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        // Survives Roslyn walk
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

        Assert.Single(endpoints);
        var endpoint = endpoints[0];
        Assert.Equal("PUT", endpoint.HttpMethod);
        Assert.Equal("/api/config", endpoint.RouteTemplate);
        Assert.Contains(endpoint.Params, p => p.Source == ParamSource.Body);
        Assert.Null(endpoint.ReturnType);
    }

    // ========== Unsupported content type markers ==========

    [Fact]
    public void Octet_Stream_Body_Resolves_To_IFormFile()
    {
        var spec = BuildSpec(
            paths: """
                "/api/upload": {
                    "post": {
                        "operationId": "upload_create",
                        "tags": ["Upload"],
                        "requestBody": {
                            "content": {
                                "application/octet-stream": {
                                    "schema": { "type": "string", "format": "binary" }
                                }
                            }
                        },
                        "responses": {
                            "201": { "description": "Created" }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "UploadContract.cs");

        // application/octet-stream with format: binary → IFormFile input type
        Assert.Contains("InputRouteDefinition<IFormFile>", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.DoesNotContain("rivet:unsupported", content);
    }

    [Fact]
    public void Unsupported_Response_Content_Type_Emits_Marker_Comment()
    {
        var spec = BuildSpec(
            paths: """
                "/api/avatar": {
                    "get": {
                        "operationId": "avatar_get",
                        "tags": ["Avatar"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "image/png": {
                                        "schema": { "type": "string", "format": "binary" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "AvatarContract.cs");

        Assert.Contains(".ProducesFile(\"image/png\")", content);
        Assert.Contains("public static readonly RouteDefinition Get", content);
    }

    [Fact]
    public void Unsupported_Error_Content_Type_Emits_Marker_Comment()
    {
        var spec = BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items/{id}": {
                    "get": {
                        "operationId": "items_get",
                        "tags": ["Items"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            },
                            "404": {
                                "description": "Not found",
                                "content": {
                                    "text/plain": {
                                        "schema": { "type": "string" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var content = FindFile(Import(spec), "ItemsContract.cs");

        Assert.Contains("[rivet:unsupported error status=404 content-type=text/plain]", content);
        // The typed 200 response should still work
        Assert.Contains("RouteDefinition<ItemDto>", content);
    }

    [Fact]
    public void Supported_Content_Types_Do_Not_Emit_Markers()
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
                        "responses": {
                            "200": {
                                "description": "OK",
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

        Assert.DoesNotContain("[rivet:unsupported", content);
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

    // ========== Schema name dedup $ref resolution ==========

    [Fact]
    public void Deduped_Schema_Name_Resolved_In_Refs()
    {
        // foo_bar and FooBar both PascalCase to FooBar — the second gets deduped to FooBar_2.
        // A $ref to foo_bar must resolve to FooBar_2 (the deduped name), not FooBar.
        var spec = BuildSpec(
            schemas: """
                "FooBar": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "foo_bar": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                },
                "Consumer": {
                    "type": "object",
                    "properties": {
                        "original": { "$ref": "#/components/schemas/FooBar" },
                        "snake": { "$ref": "#/components/schemas/foo_bar" }
                    },
                    "required": ["original", "snake"]
                }
                """,
            paths: """
                "/api/test": {
                    "get": {
                        "operationId": "test_get",
                        "tags": ["Test"],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Consumer" } } }
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var consumer = FindFile(result, "Consumer.cs");

        // original → FooBar, snake → FooBar_2 (deduped)
        Assert.Contains("FooBar Original", consumer);
        Assert.Contains("FooBar_2 Snake", consumer);

        // Both types should be generated as separate records
        var fooBar = FindFile(result, "FooBar.cs");
        Assert.Contains("string Id", fooBar);
        var fooBar2 = result.Files.FirstOrDefault(f => f.FileName.EndsWith("FooBar_2.cs"));
        Assert.NotNull(fooBar2);
        Assert.Contains("string Name", fooBar2.Content);

        // Must compile
        var compilation = CompilationHelper.CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray());
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== POST param-only input wiring ==========

    [Fact]
    public void Post_With_Query_Params_Wires_Input_Type()
    {
        // POST with query params (no body) should wire the input type
        // so it survives round-trip, even though the params become body params.
        var spec = BuildSpec(
            paths: """
                "/api/search": {
                    "post": {
                        "operationId": "items_search",
                        "tags": ["Items"],
                        "parameters": [
                            { "name": "query", "in": "query", "required": true, "schema": { "type": "string" } },
                            { "name": "limit", "in": "query", "required": false, "schema": { "type": "integer", "format": "int32" } }
                        ],
                        "responses": {
                            "200": { "description": "OK" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var contract = FindFile(result, "ItemsContract.cs");

        // Input type should be wired (via .Accepts<T>() for input-only endpoints)
        Assert.Contains("SearchInput", contract);
        Assert.Contains("Accepts<SearchInput>", contract);

        // The synthetic input type should exist with the params
        var inputFile = result.Files.FirstOrDefault(f => f.Content.Contains("SearchInput"));
        Assert.NotNull(inputFile);
        Assert.Contains("string Query", inputFile.Content);
        Assert.Contains("int? Limit", inputFile.Content);
    }

    [Fact]
    public void Post_With_Body_Still_Uses_Body()
    {
        // POST with a proper request body should still wire TInput as body (not query).
        // Ensures the isParamOnlyInput fix doesn't break normal POST endpoints.
        var spec = BuildSpec(
            schemas: """
                "CreateRequest": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                },
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "post": {
                        "operationId": "items_create",
                        "tags": ["Items"],
                        "requestBody": {
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/CreateRequest" }
                                }
                            }
                        },
                        "responses": {
                            "201": {
                                "description": "Created",
                                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ItemDto" } } }
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var contract = FindFile(result, "ItemsContract.cs");

        // Should wire input as type arg (not .Accepts<T>())
        Assert.Contains("RouteDefinition<CreateRequest, ItemDto>", contract);
        Assert.Contains("Define.Post<CreateRequest, ItemDto>", contract);
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
