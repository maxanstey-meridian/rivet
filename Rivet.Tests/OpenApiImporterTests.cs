using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class OpenApiImporterTests
{
    // ========== SchemaMapper Tests ==========

    [Fact]
    public void Primitive_Types_String_Int_Long_Double_Float_Bool()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "TestDto.cs");

        Assert.Contains("string Name", content);
        Assert.Contains("long Count", content);
        Assert.Contains("long BigCount", content);
        Assert.Contains("double Score", content);
        Assert.Contains("float Rating", content);
        Assert.Contains("bool Active", content);
    }

    [Fact]
    public void DateTime_Format_Maps_To_DateTime()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "EventDto": {
                "type": "object",
                "properties": {
                    "createdAt": { "type": "string", "format": "date-time" }
                },
                "required": ["createdAt"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        Assert.Contains("DateTime CreatedAt", CompilationHelper.FindFile(result, "EventDto.cs"));
    }

    [Fact]
    public void Guid_Format_Maps_To_Guid()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "ItemDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string", "format": "uuid" }
                },
                "required": ["id"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        Assert.Contains("Guid Id", CompilationHelper.FindFile(result, "ItemDto.cs"));
    }

    [Fact]
    public void String_Enum_Maps_To_CSharp_Enum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "string",
                "enum": ["low", "medium", "high", "critical"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("public enum Priority", content);
        Assert.Contains("Low", content);
        Assert.Contains("Medium", content);
        Assert.Contains("High", content);
        Assert.Contains("Critical", content);
    }

    [Fact]
    public void Branded_Format_Maps_To_Value_Object()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Email": {
                "type": "string",
                "format": "email"
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Email.cs");

        Assert.Contains("public sealed record Email(string Value)", content);
        Assert.Contains("public override string ToString() => Value;", content);
        Assert.Contains("Domain/Email.cs", result.Files.First(f => f.FileName.Contains("Email")).FileName);
    }

    [Fact]
    public void Array_Maps_To_List()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "TagList": {
                "type": "object",
                "properties": {
                    "tags": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["tags"]
            }
            """, title: "API");

        Assert.Contains("List<string> Tags", CompilationHelper.FindFile(CompilationHelper.Import(spec), "TagList.cs"));
    }

    [Fact]
    public void Dictionary_Maps_To_Dictionary()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "MetadataDto": {
                "type": "object",
                "properties": {
                    "values": { "type": "object", "additionalProperties": { "type": "string" } }
                },
                "required": ["values"]
            }
            """, title: "API");

        Assert.Contains("Dictionary<string, string> Values", CompilationHelper.FindFile(CompilationHelper.Import(spec), "MetadataDto.cs"));
    }

    [Fact]
    public void Nullable_Type_Array_Maps_To_Nullable()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "description": { "type": ["string", "null"] }
                },
                "required": ["description"]
            }
            """, title: "API");

        Assert.Contains("string? Description", CompilationHelper.FindFile(CompilationHelper.Import(spec), "TaskDto.cs"));
    }

    [Fact]
    public void Object_With_Properties_Maps_To_Sealed_Record()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string" },
                    "title": { "type": "string" }
                },
                "required": ["id", "title"]
            }
            """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TaskDto.cs");
        Assert.Contains("[RivetType]", content);
        Assert.Contains("public sealed record TaskDto(", content);
    }

    [Fact]
    public void Ref_Resolution_Uses_Named_Type()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        Assert.Contains("LabelDto Label", CompilationHelper.FindFile(CompilationHelper.Import(spec), "TaskDto.cs"));
    }

    [Fact]
    public void Required_Vs_Optional_Properties()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "id": { "type": "string" },
                    "description": { "type": "string" }
                },
                "required": ["id"]
            }
            """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TaskDto.cs");
        Assert.Contains("string Id", content);
        Assert.Contains("string? Description", content);
        Assert.DoesNotContain("string? Id", content);
    }

    [Fact]
    public void OneOf_Schema_Produces_Union_Wrapper()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "TasksContract.cs");

        Assert.Contains("[RivetContract]", content);
        Assert.Contains("public static class TasksContract", content);
        Assert.Contains("public static readonly RouteDefinition<TaskDto> List", content);
        Assert.Contains("Define.Get<TaskDto>(\"/api/tasks\")", content);
        Assert.Contains(".Summary(\"List all tasks\")", content);
    }

    [Fact]
    public void Post_With_RequestBody_Has_Input_And_Output_Types()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs");

        Assert.Contains("RouteDefinition<CreateTaskRequest, TaskDto> CreateTask", content);
        Assert.Contains("Define.Post<CreateTaskRequest, TaskDto>(\"/api/tasks\")", content);
        // 201 is the default for POST — no .Status() emitted
        Assert.DoesNotContain(".Status(", content);
    }

    [Fact]
    public void Error_Responses_Produce_Returns_Call()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        Assert.Contains(".Returns<NotFoundDto>(404, \"Task not found\")",
            CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs"));
    }

    [Fact]
    public void Void_Endpoint_No_Output_Type()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs");
        Assert.Contains("public static readonly RouteDefinition DeleteTask", content);
        Assert.Contains("Define.Delete(\"/api/tasks/{id}\")", content);
        // 204 is the default for DELETE — no .Status() emitted
        Assert.DoesNotContain(".Status(", content);
    }

    [Fact]
    public void Anonymous_Endpoint()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/health": {
                    "get": {
                        "operationId": "health_check",
                        "tags": ["Health"],
                        "security": [],
                        "responses": { "200": { "description": "OK" } }
                    }
                }
                """, title: "API");

        Assert.Contains(".Anonymous()", CompilationHelper.FindFile(CompilationHelper.Import(spec), "HealthContract.cs"));
    }

    [Fact]
    public void Secured_Endpoint()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/admin": {
                    "delete": {
                        "operationId": "admin_deleteAll",
                        "tags": ["Admin"],
                        "security": [{ "admin": [] }],
                        "responses": { "204": { "description": "No Content" } }
                    }
                }
                """, title: "API");

        Assert.Contains(".Secure(\"admin\")", CompilationHelper.FindFile(CompilationHelper.Import(spec), "AdminContract.cs"));
    }

    [Fact]
    public void Tag_Grouping_Produces_Separate_Contracts()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "TaskDto": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] },
                "MemberDto": { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }
                """,
            paths: """
                "/api/tasks": { "get": { "operationId": "tasks_list", "tags": ["Tasks"], "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/TaskDto" } } } } } } },
                "/api/members": { "get": { "operationId": "members_list", "tags": ["Members"], "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/MemberDto" } } } } } } }
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        Assert.Contains(result.Files, f => f.FileName == "Contracts/TasksContract.cs");
        Assert.Contains(result.Files, f => f.FileName == "Contracts/MembersContract.cs");
    }

    [Fact]
    public void No_Tag_Uses_DefaultContract()
    {
        var spec = CompilationHelper.BuildSpec(paths: """
            "/api/health": { "get": { "operationId": "healthCheck", "responses": { "200": { "description": "OK" } } } }
            """, title: "API");

        Assert.Contains(CompilationHelper.Import(spec).Files, f => f.FileName == "Contracts/DefaultContract.cs");
    }

    [Fact]
    public void OperationId_Stripped_Of_Tag_Prefix()
    {
        var spec = CompilationHelper.BuildSpec(paths: """
            "/api/tasks": { "get": { "operationId": "tasks_listAllTasks", "tags": ["Tasks"], "responses": { "200": { "description": "OK" } } } }
            """, title: "API");

        Assert.Contains("ListAllTasks", CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs"));
    }

    // ========== Fixture-based round-trip tests ==========

    private static string LoadFixture(string name = "openapi-import.json")
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    }

    private static JsonObject CreateFixtureSliceDocument(string fixtureName)
    {
        var root = JsonNode.Parse(LoadFixture(fixtureName))!.AsObject();

        return new JsonObject
        {
            ["openapi"] = root["openapi"]!.DeepClone(),
            ["info"] = root["info"]!.DeepClone(),
            ["paths"] = new JsonObject(),
        };
    }

    private static JsonObject GetOrAddComponents(JsonObject document)
    {
        document["components"] ??= new JsonObject();
        return (JsonObject)document["components"]!;
    }

    private static void CopyComponentEntries(
        JsonObject sourceDocument,
        JsonObject targetDocument,
        string sectionName,
        params string[] keys)
    {
        var sourceSection = sourceDocument["components"]?[sectionName] as JsonObject;
        Assert.NotNull(sourceSection);

        var components = GetOrAddComponents(targetDocument);
        components[sectionName] ??= new JsonObject();
        var targetSection = (JsonObject)components[sectionName]!;

        foreach (var key in keys)
        {
            targetSection[key] = sourceSection[key]!.DeepClone();
        }
    }

    private static string BuildTwilioCreateAccountFixtureSpec()
    {
        var source = JsonNode.Parse(LoadFixture("openapi-twilio.json"))!.AsObject();
        var document = CreateFixtureSliceDocument("openapi-twilio.json");
        var paths = (JsonObject)document["paths"]!;
        var sourcePath = source["paths"]?["/2010-04-01/Accounts.json"] as JsonObject;

        Assert.NotNull(sourcePath);
        paths["/2010-04-01/Accounts.json"] = new JsonObject
        {
            ["post"] = sourcePath["post"]!.DeepClone(),
        };

        return document.ToJsonString();
    }

    private static string BuildGitHubUpdateBudgetFixtureSpec()
    {
        var source = JsonNode.Parse(LoadFixture("openapi-github.json"))!.AsObject();
        var document = CreateFixtureSliceDocument("openapi-github.json");
        var paths = (JsonObject)document["paths"]!;
        var sourcePath = source["paths"]?["/organizations/{org}/settings/billing/budgets/{budget_id}"] as JsonObject;

        Assert.NotNull(sourcePath);

        var patch = sourcePath["patch"]!.DeepClone()!.AsObject();
        patch["responses"] = new JsonObject
        {
            ["404"] = patch["responses"]!["404"]!.DeepClone(),
        };

        paths["/organizations/{org}/settings/billing/budgets/{budget_id}"] = new JsonObject
        {
            ["patch"] = patch,
        };

        CopyComponentEntries(source, document, "parameters", "org", "budget");
        CopyComponentEntries(source, document, "schemas", "basic-error");

        return document.ToJsonString();
    }

    private static string BuildGitHubDeleteBudgetFixtureSpec()
    {
        var source = JsonNode.Parse(LoadFixture("openapi-github.json"))!.AsObject();
        var document = CreateFixtureSliceDocument("openapi-github.json");
        var paths = (JsonObject)document["paths"]!;
        var sourcePath = source["paths"]?["/organizations/{org}/settings/billing/budgets/{budget_id}"] as JsonObject;

        Assert.NotNull(sourcePath);

        var delete = sourcePath["delete"]!.DeepClone()!.AsObject();
        delete["responses"] = new JsonObject
        {
            ["200"] = delete["responses"]!["200"]!.DeepClone(),
        };

        paths["/organizations/{org}/settings/billing/budgets/{budget_id}"] = new JsonObject
        {
            ["delete"] = delete,
        };

        CopyComponentEntries(source, document, "parameters", "org", "budget");
        CopyComponentEntries(source, document, "responses", "delete-budget");
        CopyComponentEntries(source, document, "examples", "delete-budget");
        CopyComponentEntries(source, document, "schemas", "delete-budget");

        return document.ToJsonString();
    }

    private static string BuildGitHubSetActionsCacheRetentionLimitFixtureSpec()
    {
        var source = JsonNode.Parse(LoadFixture("openapi-github.json"))!.AsObject();
        var document = CreateFixtureSliceDocument("openapi-github.json");
        var paths = (JsonObject)document["paths"]!;
        var sourcePath = source["paths"]?["/enterprises/{enterprise}/actions/cache/retention-limit"] as JsonObject;

        Assert.NotNull(sourcePath);

        var put = sourcePath["put"]!.DeepClone()!.AsObject();
        put["responses"] = new JsonObject
        {
            ["204"] = put["responses"]!["204"]!.DeepClone(),
        };

        paths["/enterprises/{enterprise}/actions/cache/retention-limit"] = new JsonObject
        {
            ["put"] = put,
        };

        CopyComponentEntries(source, document, "parameters", "enterprise");
        CopyComponentEntries(source, document, "schemas", "actions-cache-retention-limit-for-enterprise");
        CopyComponentEntries(source, document, "examples", "actions-cache-retention-limit");

        return document.ToJsonString();
    }

    private static string BuildGitHubUpdateImportFixtureSpec()
    {
        var source = JsonNode.Parse(LoadFixture("openapi-github.json"))!.AsObject();
        var document = CreateFixtureSliceDocument("openapi-github.json");
        var paths = (JsonObject)document["paths"]!;
        var sourcePath = source["paths"]?["/repos/{owner}/{repo}/import"] as JsonObject;

        Assert.NotNull(sourcePath);

        var patch = sourcePath["patch"]!.DeepClone()!.AsObject();
        patch["responses"] = new JsonObject
        {
            ["204"] = new JsonObject
            {
                ["description"] = "No Content",
            },
        };

        paths["/repos/{owner}/{repo}/import"] = new JsonObject
        {
            ["patch"] = patch,
        };

        CopyComponentEntries(source, document, "parameters", "owner", "repo");

        return document.ToJsonString();
    }

    private static (ImportResult Result, IReadOnlyList<TsEndpointDefinition> Endpoints) ImportSpecAndWalkContracts(
        string spec,
        string ns = "Test")
    {
        var result = CompilationHelper.Import(spec, ns);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return (result, endpoints);
    }


    [Fact]
    public void Fixture_Generated_CSharp_Compiles()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        Assert.Empty(result.Warnings);

        var errors = CompilationHelper.CompileImportResult(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Fixture_Contracts_Survive_Roslyn_RoundTrip()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

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
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompilationHelper.CompileImportResult(result);
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
        var priorityEnum = (TsType.StringUnion)walker.Enums["Priority"];
        Assert.Contains("low", priorityEnum.Members);
        Assert.Contains("critical", priorityEnum.Members);

        Assert.True(walker.Brands.ContainsKey("Email"));
        Assert.True(walker.Brands.ContainsKey("Website"));
    }

    [Fact]
    public void Fixture_TaskDto_Properties_Match_OpenAPI_Schema()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var (_, walker) = CompilationHelper.DiscoverAndWalk(CompilationHelper.CompileImportResult(result));
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
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

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
    public void Twilio_CreateAccount_FormUrlEncoded_RequestExample_Survives_Import_RoundTrip()
    {
        var (_, endpoints) = ImportSpecAndWalkContracts(BuildTwilioCreateAccountFixtureSpec(), "Twilio.Contracts");

        var endpoint = endpoints.Single(e =>
            e.HttpMethod == "POST" &&
            e.RouteTemplate == "/2010-04-01/Accounts.json");

        Assert.True(endpoint.IsFormEncoded);

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("application/x-www-form-urlencoded", requestExample.MediaType);
        Assert.Equal("create", requestExample.Name);
        Assert.Equal("""{"FriendlyName":"friendly_name"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);
    }

    [Fact]
    public void GitHub_UpdateBudgetOrg_404_Named_ResponseExamples_Survive_Import_RoundTrip()
    {
        var (_, endpoints) = ImportSpecAndWalkContracts(BuildGitHubUpdateBudgetFixtureSpec(), "GitHub.Contracts");

        var endpoint = endpoints.Single(e =>
            e.HttpMethod == "PATCH" &&
            e.RouteTemplate == "/organizations/{org}/settings/billing/budgets/{budget_id}");

        var response = endpoint.Responses.Single(r => r.StatusCode == 404);
        Assert.NotNull(response.Examples);
        Assert.Collection(
            response.Examples!,
            first =>
            {
                Assert.Equal("budget-not-found", first.Name);
                Assert.Equal("application/json", first.MediaType);
                Assert.Equal("""{"message":"Budget with ID 550e8400-e29b-41d4-a716-446655440000 not found.","documentation_url":"https://docs.github.com/rest/billing/budgets#update-a-budget"}""", first.Json);
                Assert.Null(first.ComponentExampleId);
                Assert.Null(first.ResolvedJson);
            },
            second =>
            {
                Assert.Equal("feature-not-enabled", second.Name);
                Assert.Equal("application/json", second.MediaType);
                Assert.Equal("""{"message":"Not Found","documentation_url":"https://docs.github.com/rest/billing/budgets#update-a-budget"}""", second.Json);
                Assert.Null(second.ComponentExampleId);
                Assert.Null(second.ResolvedJson);
            });
    }

    [Fact]
    public void GitHub_DeleteBudgetOrg_RefBacked_ResponseExample_Preserves_Ref_And_ResolvedJson()
    {
        var (_, endpoints) = ImportSpecAndWalkContracts(BuildGitHubDeleteBudgetFixtureSpec(), "GitHub.Contracts");

        var endpoint = endpoints.Single(e =>
            e.HttpMethod == "DELETE" &&
            e.RouteTemplate == "/organizations/{org}/settings/billing/budgets/{budget_id}");

        var response = endpoint.Responses.Single(r => r.StatusCode == 200);
        var example = Assert.Single(response.Examples!);

        Assert.Equal("default", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("delete-budget", example.ComponentExampleId);
        Assert.Equal("""{"message":"Budget successfully deleted.","budget_id":"2c1feb79-3947-4dc8-a16e-80cbd732cc0b"}""", example.ResolvedJson);
    }

    [Fact]
    public void GitHub_SetActionsCacheRetentionLimit_Request_RefExample_Preserves_Ref_And_ResolvedJson()
    {
        var (_, endpoints) = ImportSpecAndWalkContracts(
            BuildGitHubSetActionsCacheRetentionLimitFixtureSpec(),
            "GitHub.Contracts");

        var endpoint = endpoints.Single(e =>
            e.HttpMethod == "PUT" &&
            e.RouteTemplate == "/enterprises/{enterprise}/actions/cache/retention-limit");

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("selected_actions", requestExample.Name);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Null(requestExample.Json);
        Assert.Equal("actions-cache-retention-limit", requestExample.ComponentExampleId);
        Assert.Equal("""{"max_cache_retention_days":80}""", requestExample.ResolvedJson);
    }

    [Fact]
    public void GitHub_UpdateImport_Exampleless_RequestEntry_Is_Explicit_Not_Silent()
    {
        var spec = BuildGitHubUpdateImportFixtureSpec();
        var result = CompilationHelper.Import(spec, "GitHub.Contracts");
        var content = CompilationHelper.FindFile(result, "MigrationsContract.cs");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var endpoint = endpoints.Single(e =>
            e.HttpMethod == "PATCH" &&
            e.RouteTemplate == "/repos/{owner}/{repo}/import");

        Assert.NotNull(endpoint.RequestExamples);
        Assert.Collection(
            endpoint.RequestExamples!,
            first => Assert.Equal("example-1", first.Name),
            second => Assert.Equal("example-2", second.Name));

        Assert.Contains(
            "[rivet:unsupported request-example media-type=application/json name=example-3 reason=missing-value]",
            content);
    }

    [Fact]
    public void Request_Example_Survives_For_Unsupported_Request_Content_Type()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/messages": {
                    "post": {
                        "operationId": "messages_create",
                        "tags": ["Messages"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "text/plain": {
                                    "example": "hello world"
                                }
                            }
                        },
                        "responses": {
                            "204": {
                                "description": "Created"
                            }
                        }
                    }
                }
                """,
            title: "API");

        var (result, endpoints) = ImportSpecAndWalkContracts(spec);
        var contract = CompilationHelper.FindFile(result, "MessagesContract.cs");
        var endpoint = Assert.Single(endpoints);

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("text/plain", requestExample.MediaType);
        Assert.Null(requestExample.Name);
        Assert.Equal("\"hello world\"", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);

        Assert.Contains("[rivet:unsupported body content-type=text/plain]", contract);
        Assert.Contains(
            ".RequestExampleJson(\"\\\"hello world\\\"\", mediaType: \"text/plain\")",
            contract);
    }

    [Fact]
    public void Request_Unresolved_Ref_Example_Is_Explicit_Not_Silent()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "API", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "QueueRequest": {
                    "type": "object",
                    "properties": {
                      "mode": { "type": "string" }
                    },
                    "required": ["mode"]
                  }
                }
              },
              "paths": {
                "/api/imports": {
                  "post": {
                    "operationId": "imports_queue",
                    "tags": ["Imports"],
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "#/components/schemas/QueueRequest" },
                          "examples": {
                            "validRequest": {
                              "value": { "mode": "fast" }
                            },
                            "missingRef": {
                              "$ref": "#/components/examples/missing-request"
                            }
                          }
                        }
                      }
                    },
                    "responses": {
                      "202": {
                        "description": "Queued"
                      }
                    }
                  }
                }
              }
            }
            """;

        var (result, endpoints) = ImportSpecAndWalkContracts(spec, "Test");
        var contract = CompilationHelper.FindFile(result, "ImportsContract.cs");
        var endpoint = Assert.Single(endpoints);

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("validRequest", requestExample.Name);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"mode":"fast"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);

        Assert.Contains(
            ".RequestExampleJson(\"{\\\"mode\\\":\\\"fast\\\"}\", mediaType: \"application/json\", name: \"validRequest\")",
            contract);
        Assert.Contains(
            "[rivet:unsupported request-example media-type=application/json name=missingRef component-example-id=missing-request reason=unresolved-ref]",
            contract);
    }

    [Fact]
    public void Response_Invalid_Example_Entries_Are_Explicit_Not_Silent()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "API", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "ProblemDto": {
                    "type": "object",
                    "properties": {
                      "message": { "type": "string" }
                    },
                    "required": ["message"]
                  }
                }
              },
              "paths": {
                "/api/orders/{id}": {
                  "get": {
                    "operationId": "orders_get",
                    "tags": ["Orders"],
                    "responses": {
                      "422": {
                        "description": "Problem response",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/ProblemDto" },
                            "examples": {
                              "validProblem": {
                                "value": { "message": "Validation failed" }
                              },
                              "missingValue": {
                                "summary": "No payload"
                              },
                              "missingRef": {
                                "$ref": "#/components/examples/missing-problem"
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var (result, endpoints) = ImportSpecAndWalkContracts(spec, "Test");
        var contract = CompilationHelper.FindFile(result, "OrdersContract.cs");
        var endpoint = Assert.Single(endpoints);

        var response = endpoint.Responses.Single(item => item.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("validProblem", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"message":"Validation failed"}""", example.Json);
        Assert.Null(example.ComponentExampleId);
        Assert.Null(example.ResolvedJson);

        Assert.Contains(
            ".ResponseExampleJson(422, \"{\\\"message\\\":\\\"Validation failed\\\"}\", mediaType: \"application/json\"",
            contract);
        Assert.Contains("name: \"validProblem\"", contract);

        Assert.Contains(
            "[rivet:unsupported response-example status=422 media-type=application/json name=missingValue reason=missing-value]",
            contract);
        Assert.Contains(
            "[rivet:unsupported response-example status=422 media-type=application/json name=missingRef component-example-id=missing-problem reason=unresolved-ref]",
            contract);
    }

    [Fact]
    public void Singular_MediaType_Example_On_Request_And_Response_Survives_Import_RoundTrip()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "CreateWidgetRequest": {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string" }
                    },
                    "required": ["name"]
                },
                "WidgetResponse": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" },
                        "name": { "type": "string" }
                    },
                    "required": ["id", "name"]
                }
                """,
            paths: """
                "/api/widgets": {
                    "post": {
                        "operationId": "widgets_create",
                        "tags": ["Widgets"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/CreateWidgetRequest" },
                                    "example": { "name": "starter-widget" }
                                }
                            }
                        },
                        "responses": {
                            "201": {
                                "description": "Created",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/WidgetResponse" },
                                        "example": { "id": "wid_123", "name": "starter-widget" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var (result, endpoints) = ImportSpecAndWalkContracts(spec);
        var contract = CompilationHelper.FindFile(result, "WidgetsContract.cs");
        var endpoint = Assert.Single(endpoints);

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Null(requestExample.Name);
        Assert.Equal("""{"name":"starter-widget"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);

        var response = endpoint.Responses.Single(r => r.StatusCode == 201);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("application/json", responseExample.MediaType);
        Assert.Null(responseExample.Name);
        Assert.Equal("""{"id":"wid_123","name":"starter-widget"}""", responseExample.Json);
        Assert.Null(responseExample.ComponentExampleId);
        Assert.Null(responseExample.ResolvedJson);

        Assert.Contains(
            ".RequestExampleJson(\"{\\\"name\\\":\\\"starter-widget\\\"}\", mediaType: \"application/json\")",
            contract);
        Assert.Contains(
            ".ResponseExampleJson(201, \"{\\\"id\\\":\\\"wid_123\\\",\\\"name\\\":\\\"starter-widget\\\"}\", mediaType: \"application/json\")",
            contract);
    }

    [Fact]
    public void Examples_Survive_When_Request_Has_No_Schema_And_Response_Content_Type_Is_Unsupported()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/previews": {
                    "post": {
                        "operationId": "previews_queue",
                        "tags": ["Previews"],
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "example": { "mode": "fast" }
                                }
                            }
                        },
                        "responses": {
                            "202": {
                                "description": "Queued",
                                "content": {
                                    "text/plain": {
                                        "example": "queued"
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var (result, endpoints) = ImportSpecAndWalkContracts(spec);
        var contract = CompilationHelper.FindFile(result, "PreviewsContract.cs");
        var endpoint = Assert.Single(endpoints);

        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"mode":"fast"}""", requestExample.Json);

        var response = endpoint.Responses.Single(r => r.StatusCode == 202);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("text/plain", responseExample.MediaType);
        Assert.Equal("\"queued\"", responseExample.Json);

        Assert.Contains("[rivet:unsupported body content-type=application/json]", contract);
        Assert.Contains("[rivet:unsupported response status=202 content-type=text/plain]", contract);
        Assert.Contains(
            ".RequestExampleJson(\"{\\\"mode\\\":\\\"fast\\\"}\", mediaType: \"application/json\")",
            contract);
        Assert.Contains(
            ".ResponseExampleJson(202, \"\\\"queued\\\"\", mediaType: \"text/plain\")",
            contract);
    }

    [Fact]
    public void Example_Bearing_Unsupported_Error_Response_Remains_Reachable_After_Import_RoundTrip()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" }
                    },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/tasks/{id}": {
                    "get": {
                        "operationId": "tasks_get",
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
                            "404": {
                                "description": "Not found",
                                "content": {
                                    "text/plain": {
                                        "example": "missing"
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var (result, endpoints) = ImportSpecAndWalkContracts(spec);
        var contract = CompilationHelper.FindFile(result, "TasksContract.cs");
        var endpoint = Assert.Single(endpoints);

        var response = endpoint.Responses.Single(r => r.StatusCode == 404);
        Assert.Null(response.DataType);
        Assert.Equal("Not found", response.Description);

        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("text/plain", responseExample.MediaType);
        Assert.Equal("\"missing\"", responseExample.Json);

        Assert.Contains(".Returns(404, \"Not found\")", contract);
        Assert.Contains(
            ".ResponseExampleJson(404, \"\\\"missing\\\"\", mediaType: \"text/plain\")",
            contract);
    }

    [Fact]
    public void Example_Bearing_Default_Success_Response_Without_Type_Forces_Status_Declaration()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/previews/{id}": {
                    "get": {
                        "operationId": "previews_get",
                        "tags": ["Previews"],
                        "responses": {
                            "200": {
                                "description": "Preview text",
                                "content": {
                                    "text/plain": {
                                        "example": "preview"
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var (result, endpoints) = ImportSpecAndWalkContracts(spec);
        var contract = CompilationHelper.FindFile(result, "PreviewsContract.cs");
        var endpoint = Assert.Single(endpoints);

        var response = Assert.Single(endpoint.Responses);
        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.DataType);

        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("text/plain", responseExample.MediaType);
        Assert.Equal("\"preview\"", responseExample.Json);

        Assert.Contains(".Status(200)", contract);
        Assert.Contains(
            ".ResponseExampleJson(200, \"\\\"preview\\\"\", mediaType: \"text/plain\")",
            contract);
    }

    [Fact]
    public void Fixture_Covers_All_Supported_Type_Mappings()
    {
        var content = CompilationHelper.FindFile(CompilationHelper.Import(LoadFixture(), "Test"), "TaskDto.cs");

        Assert.Contains("Guid Id", content);
        Assert.Contains("string Title", content);
        Assert.Contains("string? Description", content);
        Assert.Contains("Priority Priority", content);
        Assert.Contains("double Score", content);
        Assert.Contains("float Rating", content);
        Assert.Contains("long ViewCount", content);
        Assert.Contains("long TotalBytes", content);
        Assert.Contains("bool IsArchived", content);
        Assert.Contains("DateTime CreatedAt", content);
        Assert.Contains("Email AssigneeEmail", content);
        Assert.Contains("List<LabelDto> Labels", content);
        Assert.Contains("Dictionary<string, string> Metadata", content);

        Assert.Contains("Priority?", CompilationHelper.FindFile(CompilationHelper.Import(LoadFixture(), "Test"), "PatchTaskRequest.cs"));
    }

    [Fact]
    public void Fixture_Covers_All_HTTP_Methods()
    {
        var content = CompilationHelper.FindFile(CompilationHelper.Import(LoadFixture(), "Test"), "TasksContract.cs");
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
        var result = CompilationHelper.Import(LoadFixture(), "Test");
        var content = CompilationHelper.FindFile(result, "AttachFileRequest.cs");

        Assert.Contains("IFormFile File", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.Contains("string? Description", content);
    }

    [Fact]
    public void Multipart_Endpoint_Uses_Request_As_InputType()
    {
        var content = CompilationHelper.FindFile(CompilationHelper.Import(LoadFixture(), "Test"), "TasksContract.cs");

        Assert.Contains("RouteDefinition<AttachFileRequest, AttachmentDto> Attach", content);
        Assert.Contains("Define.Post<AttachFileRequest, AttachmentDto>(\"/api/tasks/{taskId}/attachments\")", content);
    }

    [Fact]
    public void Multipart_RoundTrip_Produces_File_Param()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var attach = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks/{taskId}/attachments");
        Assert.Contains(attach.Params, p => p.Source == ParamSource.File && p.Name == "file");
    }

    [Fact]
    public void Standalone_Multipart_Binary_Maps_To_IFormFile()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        var record = CompilationHelper.FindFile(result, "UploadRequest.cs");
        Assert.Contains("IFormFile Document", record);
        Assert.Contains("using Microsoft.AspNetCore.Http;", record);

        var contract = CompilationHelper.FindFile(result, "UploadsContract.cs");
        Assert.Contains("RouteDefinition<UploadRequest, UploadResult>", contract);
    }

    // ========== Drift detection ==========

    [Fact]
    public void Importer_Output_Is_V1_Static_Class_With_Typed_Builder_Fields()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");

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
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");
        var content = CompilationHelper.FindFile(result, "TasksContract.cs");

        Assert.Contains("public static readonly RouteDefinition<", content);
        Assert.DoesNotContain("public static readonly Define ", content);
    }

    [Fact]
    public void Importer_Security_And_Description_Preserved_In_Builder_Chain()
    {
        var result = CompilationHelper.Import(LoadFixture(), "TaskBoard.Contracts");

        // Health endpoint has security: [] → .Anonymous()
        Assert.Contains(".Anonymous()", CompilationHelper.FindFile(result, "HealthContract.cs"));

        // Members invite has security: [{"admin": []}] → .Secure("admin")
        Assert.Contains(".Secure(\"admin\")", CompilationHelper.FindFile(result, "MembersContract.cs"));

        // Tasks list has global security → .Secure("bearer")
        Assert.Contains(".Secure(\"bearer\")", CompilationHelper.FindFile(result, "TasksContract.cs"));

        // Summary present
        Assert.Contains(".Summary(\"List all tasks\")", CompilationHelper.FindFile(result, "TasksContract.cs"));
    }

    // ========== Union ref name sanitization ==========

    [Fact]
    public void OneOf_With_Hyphenated_Ref_Names_Sanitized()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "MyShape.cs");

        // Ref names should be PascalCased, not raw "my-circle"
        Assert.Contains("MyCircle? AsMyCircle", content);
        Assert.Contains("MySquare? AsMySquare", content);
        Assert.DoesNotContain("my-circle", content);
        Assert.DoesNotContain("my-square", content);
    }

    [Fact]
    public void AnyOf_With_Dotted_Ref_Names_Sanitized()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ShapeUnion.cs");

        Assert.Contains("GeoCircle? AsGeoCircle", content);
        Assert.Contains("GeoSquare? AsGeoSquare", content);
    }

    [Fact]
    public void Sanitized_Union_Refs_Compile()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var errors = CompilationHelper.CompileImportResult(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== allOf ref name sanitization ==========

    [Fact]
    public void AllOf_With_Hyphenated_Ref_Names_Sanitized()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ExtendedAddress.cs");

        Assert.Contains("string Street", content);
        Assert.Contains("string Zip", content);
        // No hyphens in the output
        Assert.DoesNotContain("base-address", content);
    }

    [Fact]
    public void AllOf_Nested_Hyphenated_Refs_Compile()
    {
        // allOf referencing another allOf with hyphenated names — 3 levels deep
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        var errors = CompilationHelper.CompileImportResult(result).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        var content = CompilationHelper.FindFile(result, "DeviceApiResponse.cs");
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

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "BrandsContract.cs");

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

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "SettingsContract.cs");

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

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ThingsContract.cs");

        // Unresolvable ref → no input type, just bare RouteDefinition
        Assert.DoesNotContain("InputRouteDefinition", content);
        Assert.Contains("public static readonly RouteDefinition Create", content);
    }

    // ========== Empty allOf record skipping ==========

    [Fact]
    public void AllOf_With_Primitive_Ref_Skips_Empty_Record()
    {
        // allOf referencing a primitive-like schema (no properties) should not emit an empty record
        var spec = CompilationHelper.BuildSpec(schemas: """
            "StringAlias": {
                "type": "string"
            },
            "Wrapper": {
                "allOf": [
                    { "$ref": "#/components/schemas/StringAlias" }
                ]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // Wrapper should not be emitted as an empty record
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("Wrapper.cs"));
    }

    [Fact]
    public void AllOf_With_Object_Ref_Still_Emits_Record()
    {
        // allOf referencing an object schema should still produce a record with flattened properties
        var spec = CompilationHelper.BuildSpec(schemas: """
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
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Extended.cs");

        Assert.Contains("string Name", content);
        Assert.Contains("string Extra", content);
    }

    // ========== */* content type fallback ==========

    [Fact]
    public void Wildcard_Content_Type_Resolves_Input_Type()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "PodsContract.cs");

        Assert.Contains("RouteDefinition<PodSpec, PodResult>", content);
        Assert.Contains("Define.Post<PodSpec, PodResult>", content);
    }

    [Fact]
    public void Wildcard_Content_Type_Resolves_Output_Type()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "StatusContract.cs");

        Assert.Contains("RouteDefinition<StatusDto>", content);
        Assert.Contains("Define.Get<StatusDto>", content);
    }

    [Fact]
    public void Wildcard_Content_Type_Resolves_Error_Response()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "ItemsContract.cs");

        Assert.Contains(".Returns<ErrorDto>(404, \"Not found\")", content);
    }

    [Fact]
    public void Json_Content_Type_Takes_Priority_Over_Wildcard()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs");

        // application/json should win
        Assert.Contains("RouteDefinition<TaskDto>", content);
        Assert.DoesNotContain("GenericDto", content);
    }

    // ========== 4XX/5XX wildcard status codes ==========

    [Fact]
    public void Wildcard_4XX_Status_Code_Maps_To_400()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        Assert.Contains(".Returns<ClientErrorDto>(400, \"Client error\")",
            CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs"));
    }

    [Fact]
    public void Wildcard_5XX_Status_Code_Maps_To_500()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        Assert.Contains(".Returns<ServerErrorDto>(500, \"Server error\")",
            CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs"));
    }

    [Fact]
    public void Wildcard_And_Default_ResponseExamples_Map_To_400_And_500()
    {
        var spec = CompilationHelper.BuildSpec(
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
                            "4XX": {
                                "description": "Client error",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ClientErrorDto" },
                                        "examples": {
                                            "clientProblem": {
                                                "value": { "error": "Bad request" }
                                            }
                                        }
                                    }
                                }
                            },
                            "default": {
                                "description": "Server error",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ServerErrorDto" },
                                        "examples": {
                                            "serverProblem": {
                                                "value": { "error": "Unexpected failure" }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);

        var clientError = endpoint.Responses.Single(response => response.StatusCode == 400);
        var clientExample = Assert.Single(clientError.Examples!);
        Assert.Equal("clientProblem", clientExample.Name);
        Assert.Equal("""{"error":"Bad request"}""", clientExample.Json);

        var serverError = endpoint.Responses.Single(response => response.StatusCode == 500);
        var serverExample = Assert.Single(serverError.Examples!);
        Assert.Equal("serverProblem", serverExample.Name);
        Assert.Equal("""{"error":"Unexpected failure"}""", serverExample.Json);
    }

    // ========== InputRouteDefinition<T> (input-only endpoints) ==========

    [Fact]
    public void Input_Only_Endpoint_Produces_InputRouteDefinition()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "SettingsContract.cs");

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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "PodsContract.cs");

        Assert.Contains("InputRouteDefinition<PodSpec>", content);
        Assert.Contains(".Accepts<PodSpec>()", content);
    }

    [Fact]
    public void Input_Only_Endpoint_Compiles_And_Survives_RoundTrip()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);

        // Compiles
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        // Survives Roslyn walk
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "UploadContract.cs");

        // application/octet-stream with format: binary → IFormFile input type
        Assert.Contains("InputRouteDefinition<IFormFile>", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.DoesNotContain("rivet:unsupported", content);
    }

    [Fact]
    public void Unsupported_Response_Content_Type_Emits_Marker_Comment()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "AvatarContract.cs");

        Assert.Contains("Define.File", content);
        Assert.Contains(".ContentType(\"image/png\")", content);
        Assert.Contains("public static readonly FileRouteDefinition Get", content);
    }

    [Fact]
    public void Unsupported_Error_Content_Type_Emits_Marker_Comment()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "ItemsContract.cs");

        Assert.Contains("[rivet:unsupported error status=404 content-type=text/plain]", content);
        // The typed 200 response should still work
        Assert.Contains("RouteDefinition<ItemDto>", content);
    }

    [Fact]
    public void Supported_Content_Types_Do_Not_Emit_Markers()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "TasksContract.cs");

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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        var consumer = CompilationHelper.FindFile(result, "Consumer.cs");

        // original → FooBar, snake → FooBar_2 (deduped)
        Assert.Contains("FooBar Original", consumer);
        Assert.Contains("FooBar_2 Snake", consumer);

        // Both types should be generated as separate records
        var fooBar = CompilationHelper.FindFile(result, "FooBar.cs");
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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "ItemsContract.cs");

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
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "ItemsContract.cs");

        // Should wire input as type arg (not .Accepts<T>())
        Assert.Contains("RouteDefinition<CreateRequest, ItemDto>", contract);
        Assert.Contains("Define.Post<CreateRequest, ItemDto>", contract);
    }

    // ========== Header/Cookie parameter synthesis ==========

    [Fact]
    public void Header_Parameter_Included_In_Synthesized_Input_Record()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" }
                    },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "ListItems",
                        "parameters": [
                            {
                                "name": "X-Tenant-Id",
                                "in": "header",
                                "required": true,
                                "schema": { "type": "string" }
                            },
                            {
                                "name": "X-Trace-Id",
                                "in": "header",
                                "required": false,
                                "schema": { "type": "string" }
                            }
                        ],
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": {
                                            "type": "array",
                                            "items": { "$ref": "#/components/schemas/ItemDto" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var inputContent = CompilationHelper.FindFile(result, "ListItemsInput.cs");

        Assert.Contains("string XTenantId", inputContent);
        Assert.Contains("string? XTraceId", inputContent);
    }

    [Fact]
    public void Cookie_Parameter_Included_In_Synthesized_Input_Record()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ProfileDto": {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string" }
                    },
                    "required": ["name"]
                }
                """,
            paths: """
                "/api/profile": {
                    "get": {
                        "operationId": "GetProfile",
                        "parameters": [
                            {
                                "name": "session_id",
                                "in": "cookie",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ProfileDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var inputContent = CompilationHelper.FindFile(result, "GetProfileInput.cs");

        Assert.Contains("string SessionId", inputContent);
    }

    // ========== HasMappedSchema dedup guard ==========

    [Fact]
    public void Param_Input_Record_Reuses_Existing_Schema_When_Name_Matches()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ListItemsInput": {
                    "type": "object",
                    "properties": {
                        "page": { "type": "integer" },
                        "limit": { "type": "integer" }
                    },
                    "required": ["page", "limit"]
                },
                "ItemDto": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" }
                    },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "ListItems",
                        "parameters": [
                            {
                                "name": "page",
                                "in": "query",
                                "required": true,
                                "schema": { "type": "integer" }
                            }
                        ],
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": {
                                            "type": "array",
                                            "items": { "$ref": "#/components/schemas/ItemDto" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);

        // Should use the existing schema, not generate a duplicate
        var inputFiles = result.Files.Where(f => f.FileName.Contains("ListItemsInput")).ToList();
        Assert.Single(inputFiles);

        // The existing schema has both page and limit; should compile
        var content = inputFiles[0].Content;
        Assert.Contains("long Page", content);
        Assert.Contains("long Limit", content);
    }

    // ========== IsStringEnum type inference from values ==========

    [Fact]
    public void Enum_Without_Type_Field_Inferred_From_String_Values()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "StatusDto": {
                    "enum": ["active", "inactive", "pending"]
                },
                "ItemDto": {
                    "type": "object",
                    "properties": {
                        "status": { "$ref": "#/components/schemas/StatusDto" }
                    },
                    "required": ["status"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "ListItems",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": {
                                            "type": "array",
                                            "items": { "$ref": "#/components/schemas/ItemDto" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var enumContent = CompilationHelper.FindFile(result, "StatusDto.cs");

        Assert.Contains("public enum StatusDto", enumContent);
        Assert.Contains("Active", enumContent);
        Assert.Contains("Inactive", enumContent);
        Assert.Contains("Pending", enumContent);

        // Original names preserved via [JsonStringEnumMemberName]
        Assert.Contains("[JsonStringEnumMemberName(\"active\")]", enumContent);

        // Should compile
        var compilation = CompilationHelper.CompileImportResult(result);
        var diags = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(diags);
    }

    // ========== allOf/anyOf schema descriptions ==========

    [Fact]
    public void AllOf_Schema_Description_Emitted_As_RivetDescription()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "BaseDto": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" }
                    },
                    "required": ["id"]
                },
                "ExtendedDto": {
                    "description": "An extended data transfer object",
                    "allOf": [
                        { "$ref": "#/components/schemas/BaseDto" },
                        {
                            "type": "object",
                            "properties": {
                                "name": { "type": "string" }
                            },
                            "required": ["name"]
                        }
                    ]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "GetItem",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ExtendedDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ExtendedDto.cs");

        Assert.Contains("[RivetDescription(\"An extended data transfer object\")]", content);
    }

    [Fact]
    public void AnyOf_Schema_Description_Survives_Import()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "FlexibleValueDto": {
                    "description": "A value that can be a string or number",
                    "anyOf": [
                        { "type": "string" },
                        { "type": "number" }
                    ]
                }
                """,
            paths: """
                "/api/values": {
                    "get": {
                        "operationId": "GetValue",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/FlexibleValueDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "FlexibleValueDto.cs");

        Assert.Contains("[RivetDescription(\"A value that can be a string or number\")]", content);
    }

    // ========== GAP-6: Multipart $ref without x-rivet-input-type ==========

    [Fact]
    public void Multipart_Ref_Without_Extension_Resolves_Type_Name()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "UploadPayload": {
                    "type": "object",
                    "properties": {
                        "file": { "type": "string", "format": "binary" },
                        "title": { "type": "string" }
                    },
                    "required": ["file", "title"]
                },
                "UploadResult": {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string" }
                    },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/uploads": {
                    "post": {
                        "operationId": "Upload",
                        "requestBody": {
                            "required": true,
                            "content": {
                                "multipart/form-data": {
                                    "schema": { "$ref": "#/components/schemas/UploadPayload" }
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
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "Contract.cs");

        // The input type should be "UploadPayload", not "UploadRequest" (synthesized name)
        Assert.Contains("UploadPayload", contract);
        Assert.DoesNotContain("UploadRequest", contract);
    }

    // ========== BUG-1: minLength: 0 must survive import ==========

    [Fact]
    public void MinLength_Zero_Preserved_Through_Import()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": {
                        "name": {
                            "type": "string",
                            "minLength": 0,
                            "maxLength": 255
                        }
                    },
                    "required": ["name"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "GetItem",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemDto.cs");

        // minLength: 0 should be preserved as MinLength = 0 in the constraint attribute
        Assert.Contains("MinLength = 0", content);
        Assert.Contains("MaxLength = 255", content);
    }

    // ========== oneOf schema description ==========

    [Fact]
    public void OneOf_Schema_Description_Survives_Import()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "Shape": {
                    "description": "A geometric shape",
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

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Shape.cs");

        Assert.Contains("[RivetDescription(\"A geometric shape\")]", content);
    }

    // ========== Summary and Description imported separately ==========

    [Fact]
    public void Summary_And_Description_Imported_Separately()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "items_list",
                        "tags": ["Items"],
                        "summary": "List items",
                        "description": "Returns all items with pagination support",
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemsContract.cs");

        Assert.Contains(".Summary(\"List items\")", content);
        Assert.Contains(".Description(\"Returns all items with pagination support\")", content);
    }

    [Fact]
    public void Summary_Only_Does_Not_Emit_Description_Call()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "items_list",
                        "tags": ["Items"],
                        "summary": "List items",
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemsContract.cs");

        Assert.Contains(".Summary(\"List items\")", content);
        Assert.DoesNotContain(".Description(", content);
    }

    [Fact]
    public void Description_Only_Does_Not_Emit_Summary_Call()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "items_list",
                        "tags": ["Items"],
                        "description": "Returns all items with pagination support",
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemsContract.cs");

        Assert.DoesNotContain(".Summary(", content);
        Assert.Contains(".Description(\"Returns all items with pagination support\")", content);
    }

    // ========== Integer Enum Tests ==========

    [Fact]
    public void IsIntEnum_Recognises_Integer_Type_With_Enum_Values()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "UserRole": {
                "type": "integer",
                "enum": [1, 2, 3]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "UserRole.cs");

        Assert.Contains("public enum UserRole", content);
    }

    [Fact]
    public void IsIntEnum_Recognises_Untyped_Numeric_Enum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Severity": {
                "enum": [10, 20, 30]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Severity.cs");

        Assert.Contains("public enum Severity", content);
        Assert.Contains("Value10", content);
        Assert.Contains("Value20", content);
        Assert.Contains("Value30", content);
    }

    [Fact]
    public void IntEnum_WouldGenerateType_Enables_Ref_Resolution()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "StatusCode": {
                "type": "integer",
                "enum": [1, 2, 3]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "status": { "$ref": "#/components/schemas/StatusCode" }
                },
                "required": ["status"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        var enumContent = CompilationHelper.FindFile(result, "StatusCode.cs");
        Assert.Contains("public enum StatusCode", enumContent);

        var dtoContent = CompilationHelper.FindFile(result, "OrderDto.cs");
        Assert.Contains("StatusCode Status", dtoContent);
        Assert.DoesNotContain("long Status", dtoContent);
    }

    [Fact]
    public void IntEnum_Members_Have_Explicit_Values()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("Value0 = 0,", content);
        Assert.Contains("Value1 = 1,", content);
        Assert.Contains("Value2 = 2", content);
        Assert.DoesNotContain("JsonStringEnumMemberName", content);
    }

    [Fact]
    public void IntEnum_Output_Compiles()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "StatusCode": {
                "type": "integer",
                "enum": [1, 2, 3]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "status": { "$ref": "#/components/schemas/StatusCode" }
                },
                "required": ["status"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Negative_Values_Produce_Valid_Identifiers()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Offset": {
                "type": "integer",
                "enum": [-1, 0, 1]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Offset.cs");

        Assert.Contains("ValueNeg1 = -1,", content);
        Assert.Contains("Value0 = 0,", content);
        Assert.Contains("Value1 = 1", content);

        // Must compile — invalid identifiers would cause CS errors
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Duplicate_Values_Are_Deduplicated()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Status": {
                "type": "integer",
                "enum": [1, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Status.cs");

        Assert.Contains("Value1 = 1,", content);
        Assert.Contains("Value1_2 = 1,", content);
        Assert.Contains("Value2 = 2", content);

        // Must compile — duplicate member names would cause CS errors
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Inline_Integer_Enum_On_Property_Synthesizes_Enum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "TaskDto": {
                "type": "object",
                "properties": {
                    "status": { "type": "integer", "enum": [0, 1, 2] }
                },
                "required": ["status"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var dtoContent = CompilationHelper.FindFile(result, "TaskDto.cs");

        // Property type should be the synthesized enum, not long
        Assert.DoesNotContain("long Status", dtoContent);

        // The synthesized enum file should exist with explicit values
        var enumFile = result.Files.FirstOrDefault(f =>
            f.Content.Contains("public enum") && f.Content.Contains("= 0,"));
        Assert.NotNull(enumFile);
        Assert.Contains("Value0 = 0,", enumFile.Content);
        Assert.Contains("Value1 = 1,", enumFile.Content);
        Assert.Contains("Value2 = 2", enumFile.Content);
    }

    [Fact]
    public void Single_Value_IntEnum_Falls_Through_To_Long()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Constant": {
                "type": "integer",
                "enum": [42]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "code": { "$ref": "#/components/schemas/Constant" }
                },
                "required": ["code"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // No enum file should be generated for a single-value enum
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("Constant.cs"));

        // The DTO property should fall through to long
        var dtoContent = CompilationHelper.FindFile(result, "OrderDto.cs");
        Assert.Contains("long Code", dtoContent);
    }

    [Fact]
    public void Single_Value_Untyped_IntEnum_Falls_Through()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "SingleVal": {
                "enum": [42]
            },
            "ItemDto": {
                "type": "object",
                "properties": {
                    "val": { "$ref": "#/components/schemas/SingleVal" }
                },
                "required": ["val"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // No enum file should be generated
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("SingleVal.cs"));

        // Untyped single-value falls through to string in ResolveFallbackType
        var dtoContent = CompilationHelper.FindFile(result, "ItemDto.cs");
        Assert.Contains("string Val", dtoContent);
    }

    [Fact]
    public void Float_Enum_Values_Not_Classified_As_IntEnum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "MixedVals": {
                "type": "integer",
                "enum": [1, 2, 3.5]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "code": { "$ref": "#/components/schemas/MixedVals" }
                },
                "required": ["code"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // No enum file — float value disqualifies it as an int enum
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("MixedVals.cs"));

        // The DTO property should fall through to long
        var dtoContent = CompilationHelper.FindFile(result, "OrderDto.cs");
        Assert.Contains("long Code", dtoContent);
    }

    [Fact]
    public void Untyped_Float_Enum_Values_Not_Classified_As_IntEnum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "FloatVals": {
                "enum": [1.5, 2.5]
            },
            "ItemDto": {
                "type": "object",
                "properties": {
                    "val": { "$ref": "#/components/schemas/FloatVals" }
                },
                "required": ["val"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // No named enum file for FloatVals — WouldGenerateType returns false
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("FloatVals.cs"));

        // Falls through to string-backed inline enum via ResolveFallbackType
        var dtoContent = CompilationHelper.FindFile(result, "ItemDto.cs");
        Assert.Contains("ItemDtoVal Val", dtoContent);
        Assert.DoesNotContain("long Val", dtoContent);
    }

    [Fact]
    public void IntEnum_Value_Exceeding_Int32_Range_Falls_Through_To_Long()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "BigVals": {
                "type": "integer",
                "enum": [1, 2147483648]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "code": { "$ref": "#/components/schemas/BigVals" }
                },
                "required": ["code"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // Value exceeds int.MaxValue — should not generate an int enum
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("BigVals.cs"));

        var dtoContent = CompilationHelper.FindFile(result, "OrderDto.cs");
        Assert.Contains("long Code", dtoContent);
    }

    [Fact]
    public void Two_Value_IntEnum_Is_Classified_As_Enum()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Toggle": {
                "type": "integer",
                "enum": [0, 1]
            },
            "FlagDto": {
                "type": "object",
                "properties": {
                    "enabled": { "$ref": "#/components/schemas/Toggle" }
                },
                "required": ["enabled"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);

        // Two values is the minimum for Count: > 1 — enum SHOULD be generated
        var enumContent = CompilationHelper.FindFile(result, "Toggle.cs");
        Assert.Contains("public enum Toggle", enumContent);
        Assert.Contains("Value0 = 0,", enumContent);
        Assert.Contains("Value1 = 1", enumContent);

        // DTO should reference the enum type, not long
        var dtoContent = CompilationHelper.FindFile(result, "FlagDto.cs");
        Assert.Contains("Toggle Enabled", dtoContent);
        Assert.DoesNotContain("long Enabled", dtoContent);
    }

    [Fact]
    public void IntEnum_With_XEnumVarnames_Uses_Custom_Names()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2],
                "x-enum-varnames": ["Low", "Medium", "High"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("Low = 0,", content);
        Assert.Contains("Medium = 1,", content);
        Assert.Contains("High = 2", content);
        Assert.DoesNotContain("Value0", content);
        Assert.DoesNotContain("Value1", content);
        Assert.DoesNotContain("Value2", content);
    }

    [Fact]
    public void IntEnum_With_XEnumVarnames_Compiles()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2],
                "x-enum-varnames": ["Low", "Medium", "High"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_XEnumVarnames_Are_Sanitised()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1],
                "x-enum-varnames": ["low_priority", "high-priority"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("LowPriority = 0,", content);
        Assert.Contains("HighPriority = 1", content);
    }

    [Fact]
    public void IntEnum_XEnumVarnames_Count_Mismatch_Falls_Back()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2],
                "x-enum-varnames": ["Low", "High"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("Value0 = 0,", content);
        Assert.Contains("Value1 = 1,", content);
        Assert.Contains("Value2 = 2", content);
        Assert.DoesNotContain("Low", content);
        Assert.DoesNotContain("High", content);
    }

    [Fact]
    public void IntEnum_Without_XEnumVarnames_Uses_ValueN_Fallback()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("Value0 = 0,", content);
        Assert.Contains("Value1 = 1,", content);
        Assert.Contains("Value2 = 2", content);
    }

    [Fact]
    public void StringEnum_Does_Not_Have_JsonNumberEnumConverter()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Color": {
                "type": "string",
                "enum": ["red", "green", "blue"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Color.cs");

        Assert.DoesNotContain("JsonNumberEnumConverter", content);
    }

    [Fact]
    public void IntEnum_With_JsonConverter_Compiles()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "StatusCode": {
                "type": "integer",
                "enum": [1, 2, 3]
            },
            "OrderDto": {
                "type": "object",
                "properties": {
                    "status": { "$ref": "#/components/schemas/StatusCode" }
                },
                "required": ["status"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var enumContent = CompilationHelper.FindFile(result, "StatusCode.cs");
        Assert.Contains("[JsonConverter(typeof(JsonNumberEnumConverter<StatusCode>))]", enumContent);

        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Serialises_As_Number()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("[JsonConverter(typeof(JsonNumberEnumConverter<Priority>))]", content);
        Assert.Contains("using System.Text.Json.Serialization;", content);
    }

    [Fact]
    public void IntEnum_Has_JsonNumberEnumConverter_Attribute()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "StatusCode": {
                "type": "integer",
                "enum": [1, 2, 3]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "StatusCode.cs");

        // Type-level attribute is emitted; note that options.Converters takes precedence
        // over type-level [JsonConverter] per .NET converter precedence rules —
        // property-level attributes would be needed to fully override options converters.
        Assert.Contains("[JsonConverter(typeof(JsonNumberEnumConverter<StatusCode>))]", content);
    }

    [Fact]
    public void IntEnum_Deserialises_From_Number()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        // JsonNumberEnumConverter handles both serialisation and deserialisation
        Assert.Contains("[JsonConverter(typeof(JsonNumberEnumConverter<Priority>))]", content);
    }

    [Fact]
    public void IntEnum_Untyped_With_XEnumVarnames_Uses_Custom_Names()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Severity": {
                "enum": [10, 20, 30],
                "x-enum-varnames": ["Info", "Warning", "Error"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Severity.cs");

        Assert.Contains("Info = 10,", content);
        Assert.Contains("Warning = 20,", content);
        Assert.Contains("Error = 30", content);
        Assert.DoesNotContain("Value10", content);
        Assert.DoesNotContain("Value20", content);
        Assert.DoesNotContain("Value30", content);
    }

    [Fact]
    public void IntEnum_XEnumVarnames_Duplicate_After_Sanitisation_Are_Deduplicated()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1],
                "x-enum-varnames": ["low_val", "lowVal"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        Assert.Contains("LowVal = 0,", content);
        Assert.Contains("LowVal_2 = 1", content);

        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Serialisation_RoundTrip_Produces_Number()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);

        using var ms = new MemoryStream();
        var emitResult = ((Microsoft.CodeAnalysis.CSharp.CSharpCompilation)compilation).Emit(ms);
        Assert.True(emitResult.Success);
        ms.Seek(0, SeekOrigin.Begin);

        var asm = System.Reflection.Assembly.Load(ms.ToArray());
        var enumType = asm.GetType("Test.Priority")!;
        Assert.NotNull(enumType);

        // Value1 = 1
        var enumValue = Enum.ToObject(enumType, 1);

        // The [JsonConverter(typeof(JsonNumberEnumConverter<Priority>))] attribute
        // ensures the enum serialises as a number (the type-level default).
        // Note: options.Converters takes precedence over type-level [JsonConverter]
        // per .NET converter precedence rules — property-level attributes are needed
        // to override options converters.
        var json = System.Text.Json.JsonSerializer.Serialize(enumValue, enumType);

        Assert.Equal("1", json);

        // Round-trip: deserialise back
        var deserialized = System.Text.Json.JsonSerializer.Deserialize(json, enumType);
        Assert.Equal(enumValue, deserialized);
    }

    [Fact]
    public void WriteEnum_StringBacked_Preserves_JsonStringEnumMemberName()
    {
        var enumDef = new GeneratedEnum("Status", [
            new GeneratedEnumMember("Active", "active"),
            new GeneratedEnumMember("Inactive", "inactive"),
            new GeneratedEnumMember("Archived", null),
        ]);

        var output = CSharpWriter.WriteEnum(enumDef, "Test");

        Assert.Contains("[JsonStringEnumMemberName(\"active\")]", output);
        Assert.Contains("[JsonStringEnumMemberName(\"inactive\")]", output);
        Assert.DoesNotContain("[JsonStringEnumMemberName(\"Archived\")]", output);
        Assert.DoesNotContain("= ", output);
    }

    [Fact]
    public void IntEnum_Dedup_Suffixed_Name_Does_Not_Collide_With_Natural_Name()
    {
        // varnames: Foo, Foo, Foo_2
        // Old logic: Foo (ok), Foo_2 (deduped from Foo), Foo_2 (natural) → collision!
        // New logic: Foo, Foo_2, Foo_2_2 — all unique
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Priority": {
                "type": "integer",
                "enum": [0, 1, 2],
                "x-enum-varnames": ["Foo", "Foo", "Foo_2"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Priority.cs");

        // All three members must have distinct names
        Assert.Contains("Foo = 0,", content);
        Assert.Contains("Foo_2 = 1,", content);
        Assert.Contains("Foo_2_2 = 2", content);

        // Must compile — CS0102 would indicate collision
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Dedup_Skips_Past_Previously_Emitted_Natural_Name()
    {
        // varnames: A, A_2, A — natural A_2 is emitted second,
        // so when the third "A" dedupes, suffix _2 is already taken → must skip to _3
        // Old dict logic: A, A_2, A_2 — collision (dict doesn't know A_2 was emitted naturally)
        // New HashSet logic: A, A_2, A_3 — correct (while loop skips taken _2)
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Level": {
                "type": "integer",
                "enum": [0, 1, 2],
                "x-enum-varnames": ["A", "A_2", "A"]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Level.cs");

        Assert.Contains("A = 0,", content);
        Assert.Contains("A_2 = 1,", content);
        Assert.Contains("A_3 = 2", content);

        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void IntEnum_Triple_Duplicate_Values_Produce_Unique_Names()
    {
        var spec = CompilationHelper.BuildSpec(schemas: """
            "Status": {
                "type": "integer",
                "enum": [1, 1, 1]
            }
            """, title: "API");

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "Status.cs");

        Assert.Contains("Value1 = 1,", content);
        Assert.Contains("Value1_2 = 1,", content);
        Assert.Contains("Value1_3 = 1", content);

        // Must compile — duplicate member names would cause CS0102
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    // ========== QueryAuth + File Endpoint Import Tests ==========

    [Fact]
    public void QueryAuth_Extension_Emits_QueryAuth_Call()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/media/{id}/stream": {
                    "get": {
                        "operationId": "media_streamVideo",
                        "tags": ["Media"],
                        "parameters": [
                            {
                                "name": "id",
                                "in": "path",
                                "required": true,
                                "schema": { "type": "string" }
                            },
                            {
                                "name": "token",
                                "in": "query",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "x-rivet-query-auth": { "parameterName": "token" },
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "video/mp4": {
                                        "schema": { "type": "string", "format": "binary" }
                                    }
                                }
                            }
                        }
                    }
                }
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "MediaContract.cs");

        Assert.Contains("Define.File", content);
        Assert.Contains(".QueryAuth()", content);
        Assert.Contains(".ContentType(\"video/mp4\")", content);
        Assert.Contains("FileRouteDefinition", content);
        // Token param should not appear as an input field
        Assert.DoesNotContain("Token", content);
    }

    [Fact]
    public void QueryAuth_Custom_ParameterName_Emits_With_Argument()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/audio/{id}": {
                    "get": {
                        "operationId": "audio_stream",
                        "tags": ["Audio"],
                        "parameters": [
                            {
                                "name": "id",
                                "in": "path",
                                "required": true,
                                "schema": { "type": "string" }
                            },
                            {
                                "name": "key",
                                "in": "query",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "x-rivet-query-auth": { "parameterName": "key" },
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "audio/mpeg": {
                                        "schema": { "type": "string", "format": "binary" }
                                    }
                                }
                            }
                        }
                    }
                }
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "AudioContract.cs");

        Assert.Contains(".QueryAuth(\"key\")", content);
        // Key param should not appear as an input field
        Assert.DoesNotContain("Key", content.Split("Define.File")[0]);
    }

    [Fact]
    public void No_QueryAuth_Extension_Does_Not_Emit_QueryAuth()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "UserDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                }
                """,
            paths: """
                "/api/users/{id}": {
                    "get": {
                        "operationId": "users_get",
                        "tags": ["Users"],
                        "parameters": [
                            {
                                "name": "id",
                                "in": "path",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/UserDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "UsersContract.cs");

        Assert.DoesNotContain("QueryAuth", content);
        Assert.DoesNotContain("Define.File", content);
        Assert.Contains("Define.Get", content);
    }

    [Fact]
    public void Binary_Response_Without_QueryAuth_Emits_DefineFile()
    {
        var spec = CompilationHelper.BuildSpec(
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
                """, title: "API");

        var content = CompilationHelper.FindFile(CompilationHelper.Import(spec), "AvatarContract.cs");

        Assert.Contains("Define.File", content);
        Assert.Contains("FileRouteDefinition", content);
        Assert.Contains(".ContentType(\"image/png\")", content);
        Assert.DoesNotContain("QueryAuth", content);
        Assert.DoesNotContain("ProducesFile", content);
    }

    [Fact]
    public void QueryAuth_RoundTrip_Preserves_Metadata()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
                "/api/media/{id}/stream": {
                    "get": {
                        "operationId": "media_streamVideo",
                        "tags": ["Media"],
                        "parameters": [
                            {
                                "name": "id",
                                "in": "path",
                                "required": true,
                                "schema": { "type": "string" }
                            },
                            {
                                "name": "token",
                                "in": "query",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "x-rivet-query-auth": { "parameterName": "token" },
                        "responses": {
                            "200": {
                                "description": "OK",
                                "content": {
                                    "video/mp4": {
                                        "schema": { "type": "string", "format": "binary" }
                                    }
                                }
                            }
                        }
                    }
                }
                """, title: "API");

        // Import → compile → walk → assert QueryAuth survives
        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var ep = Assert.Single(endpoints);
        Assert.True(ep.IsFileEndpoint);
        Assert.NotNull(ep.QueryAuth);
        Assert.Equal("token", ep.QueryAuth!.ParameterName);
        Assert.Equal("video/mp4", ep.FileContentType);
    }
}
