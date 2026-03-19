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
        var content = FindFile(result, "EventDto.cs");

        Assert.Contains("DateTime CreatedAt", content);
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
        var content = FindFile(result, "ItemDto.cs");

        Assert.Contains("Guid Id", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TagList.cs");

        Assert.Contains("List<string> Tags", content);
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

        var result = Import(spec);
        var content = FindFile(result, "MetadataDto.cs");

        Assert.Contains("Dictionary<string, string> Values", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TaskDto.cs");

        Assert.Contains("string? Description", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TaskDto.cs");

        Assert.Contains("[RivetType]", content);
        Assert.Contains("public sealed record TaskDto(", content);
    }

    [Fact]
    public void Ref_Resolution_Uses_Named_Type()
    {
        var spec = BuildSpec(schemas: """
            "LabelDto": {
                "type": "object",
                "properties": {
                    "name": { "type": "string" }
                },
                "required": ["name"]
            },
            "TaskDto": {
                "type": "object",
                "properties": {
                    "label": { "$ref": "#/components/schemas/LabelDto" }
                },
                "required": ["label"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "TaskDto.cs");

        Assert.Contains("LabelDto Label", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TaskDto.cs");

        Assert.Contains("string Id", content);
        Assert.Contains("string? Description", content);
        // Id should NOT be nullable
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
        // Circle and Square should still be generated
        Assert.Contains(result.Files, f => f.FileName.Contains("Circle"));
        Assert.Contains(result.Files, f => f.FileName.Contains("Square"));
    }

    // ========== ContractBuilder Tests ==========

    [Fact]
    public void Get_Endpoint_Produces_Correct_Contract()
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
                "/api/tasks/{id}": {
                    "get": {
                        "operationId": "tasks_getTask",
                        "tags": ["Tasks"],
                        "summary": "Retrieve a single task",
                        "parameters": [
                            { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                        ],
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

        Assert.Contains("Endpoint.Get<TaskDto>(\"/api/tasks/{id}\")", content);
        Assert.Contains(".Description(\"Retrieve a single task\")", content);
        Assert.Contains("public static readonly Endpoint GetTask", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("Endpoint.Post<CreateTaskRequest, TaskDto>(\"/api/tasks\")", content);
        Assert.Contains(".Status(201)", content);
    }

    [Fact]
    public void Non_200_Success_Status_Emits_Status_Call()
    {
        var spec = BuildSpec(
            paths: """
                "/api/tasks": {
                    "post": {
                        "operationId": "tasks_create",
                        "tags": ["Tasks"],
                        "responses": {
                            "201": { "description": "Created" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains(".Status(201)", content);
    }

    [Fact]
    public void Error_Responses_With_Schema_Produce_Returns_Call()
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

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains(".Returns<NotFoundDto>(404, \"Task not found\")", content);
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

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("Endpoint.Delete(\"/api/tasks/{id}\")", content);
        Assert.Contains(".Status(204)", content);
    }

    [Fact]
    public void Route_Parameters_Preserved()
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
                "/api/projects/{projectId}/tasks/{taskId}": {
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
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("\"/api/projects/{projectId}/tasks/{taskId}\"", content);
    }

    [Fact]
    public void Anonymous_Endpoint_Empty_Security_Array()
    {
        var spec = BuildSpec(
            paths: """
                "/api/health": {
                    "get": {
                        "operationId": "health_check",
                        "tags": ["Health"],
                        "security": [],
                        "responses": {
                            "200": { "description": "OK" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "HealthContract.cs");

        Assert.Contains(".Anonymous()", content);
    }

    [Fact]
    public void Secured_Endpoint_With_Scheme()
    {
        var spec = BuildSpec(
            paths: """
                "/api/admin": {
                    "delete": {
                        "operationId": "admin_deleteAll",
                        "tags": ["Admin"],
                        "security": [{ "admin": [] }],
                        "responses": {
                            "204": { "description": "No Content" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "AdminContract.cs");

        Assert.Contains(".Secure(\"admin\")", content);
    }

    [Fact]
    public void Tag_Grouping_Produces_Separate_Contracts()
    {
        var spec = BuildSpec(
            schemas: """
                "TaskDto": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                },
                "MemberDto": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                }
                """,
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
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
                },
                "/api/members": {
                    "get": {
                        "operationId": "members_list",
                        "tags": ["Members"],
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/MemberDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

        var result = Import(spec);

        Assert.Contains(result.Files, f => f.FileName == "Contracts/TasksContract.cs");
        Assert.Contains(result.Files, f => f.FileName == "Contracts/MembersContract.cs");
    }

    [Fact]
    public void No_Tag_Uses_DefaultContract()
    {
        var spec = BuildSpec(
            paths: """
                "/api/health": {
                    "get": {
                        "operationId": "healthCheck",
                        "responses": {
                            "200": { "description": "OK" }
                        }
                    }
                }
                """);

        var result = Import(spec);

        Assert.Contains(result.Files, f => f.FileName == "Contracts/DefaultContract.cs");
    }

    [Fact]
    public void OperationId_Stripped_Of_Tag_Prefix_And_PascalCased()
    {
        var spec = BuildSpec(
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_listAllTasks",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": { "description": "Success" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("public static readonly Endpoint ListAllTasks", content);
    }

    // ========== CSharpWriter Tests ==========

    [Fact]
    public void Record_Output_Has_RivetType_Sealed_PrimaryConstructor()
    {
        var spec = BuildSpec(schemas: """
            "PersonDto": {
                "type": "object",
                "properties": {
                    "firstName": { "type": "string" },
                    "age": { "type": "integer" }
                },
                "required": ["firstName", "age"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "PersonDto.cs");

        Assert.Contains("using Rivet;", content);
        Assert.Contains("[RivetType]", content);
        Assert.Contains("public sealed record PersonDto(", content);
        Assert.Contains("string FirstName", content);
        Assert.Contains("int Age", content);
    }

    [Fact]
    public void Enum_Output_PascalCased_Members()
    {
        var spec = BuildSpec(schemas: """
            "Status": {
                "type": "string",
                "enum": ["active", "inactive", "pending"]
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "Status.cs");

        Assert.Contains("public enum Status", content);
        Assert.Contains("Active", content);
        Assert.Contains("Inactive", content);
        Assert.Contains("Pending", content);
    }

    [Fact]
    public void Brand_Output_SingleProperty_Record_With_ToString()
    {
        var spec = BuildSpec(schemas: """
            "Uri": {
                "type": "string",
                "format": "uri"
            }
            """);

        var result = Import(spec);
        var content = FindFile(result, "Uri.cs");

        Assert.Contains("public sealed record Uri(string Value)", content);
        Assert.Contains("public override string ToString() => Value;", content);
    }

    [Fact]
    public void Contract_Output_Has_RivetContract_StaticClass_EndpointChains()
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

        Assert.Contains("using Rivet;", content);
        Assert.Contains("using Endpoint = Rivet.Endpoint;", content);
        Assert.Contains("[RivetContract]", content);
        Assert.Contains("public static class TasksContract", content);
        Assert.Contains("public static readonly Endpoint List", content);
    }

    // ========== End-to-End Tests ==========

    [Fact]
    public void Full_Spec_Produces_Multiple_Valid_Files()
    {
        var spec = """
            {
                "openapi": "3.1.0",
                "info": { "title": "TaskBoard API", "version": "1.0.0" },
                "paths": {
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
                                            "schema": {
                                                "type": "array",
                                                "items": { "$ref": "#/components/schemas/TaskDto" }
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        "post": {
                            "operationId": "tasks_create",
                            "tags": ["Tasks"],
                            "summary": "Create a new task",
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
                    },
                    "/api/members": {
                        "get": {
                            "operationId": "members_list",
                            "tags": ["Members"],
                            "responses": {
                                "200": {
                                    "description": "Success",
                                    "content": {
                                        "application/json": {
                                            "schema": { "$ref": "#/components/schemas/MemberDto" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "TaskDto": {
                            "type": "object",
                            "properties": {
                                "id": { "type": "string", "format": "uuid" },
                                "title": { "type": "string" },
                                "description": { "type": ["string", "null"] },
                                "priority": { "$ref": "#/components/schemas/Priority" }
                            },
                            "required": ["id", "title", "priority"]
                        },
                        "CreateTaskRequest": {
                            "type": "object",
                            "properties": {
                                "title": { "type": "string" },
                                "priority": { "$ref": "#/components/schemas/Priority" }
                            },
                            "required": ["title", "priority"]
                        },
                        "MemberDto": {
                            "type": "object",
                            "properties": {
                                "name": { "type": "string" },
                                "email": { "$ref": "#/components/schemas/Email" }
                            },
                            "required": ["name", "email"]
                        },
                        "Priority": {
                            "type": "string",
                            "enum": ["low", "medium", "high"]
                        },
                        "Email": {
                            "type": "string",
                            "format": "email"
                        }
                    }
                }
            }
            """;

        var result = Import(spec, "TaskBoard.Contracts");

        // Types
        Assert.Contains(result.Files, f => f.FileName == "Types/TaskDto.cs");
        Assert.Contains(result.Files, f => f.FileName == "Types/CreateTaskRequest.cs");
        Assert.Contains(result.Files, f => f.FileName == "Types/MemberDto.cs");
        Assert.Contains(result.Files, f => f.FileName == "Types/Priority.cs");
        Assert.Contains(result.Files, f => f.FileName == "Domain/Email.cs");

        // Contracts
        Assert.Contains(result.Files, f => f.FileName == "Contracts/TasksContract.cs");
        Assert.Contains(result.Files, f => f.FileName == "Contracts/MembersContract.cs");

        // Namespace
        foreach (var file in result.Files)
        {
            Assert.Contains("TaskBoard.Contracts", file.Content);
        }

        // Verify contract content
        var tasksContract = FindFile(result, "TasksContract.cs");
        Assert.Contains("Endpoint.Get<List<TaskDto>>(\"/api/tasks\")", tasksContract);
        Assert.Contains("Endpoint.Post<CreateTaskRequest, TaskDto>(\"/api/tasks\")", tasksContract);
        Assert.Contains(".Status(201)", tasksContract);
    }

    [Fact]
    public void Warnings_Collected_For_Unsupported_Features()
    {
        var spec = BuildSpec(schemas: """
            "ValidDto": {
                "type": "object",
                "properties": { "name": { "type": "string" } },
                "required": ["name"]
            },
            "ComposedType": {
                "allOf": [
                    { "$ref": "#/components/schemas/ValidDto" },
                    { "type": "object", "properties": { "extra": { "type": "string" } } }
                ]
            }
            """);

        var result = Import(spec);

        Assert.Contains(result.Warnings, w => w.Contains("allOf") && w.Contains("ComposedType"));
        // ValidDto should still be generated
        Assert.Contains(result.Files, f => f.FileName.Contains("ValidDto"));
        // ComposedType should NOT be generated
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("ComposedType"));
    }

    [Fact]
    public void Global_Security_Scheme_Applied_To_Endpoints()
    {
        var spec = BuildSpec(
            paths: """
                "/api/tasks": {
                    "get": {
                        "operationId": "tasks_list",
                        "tags": ["Tasks"],
                        "responses": {
                            "200": { "description": "Success" }
                        }
                    }
                }
                """);

        var result = Import(spec, security: "bearer");
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains(".Secure(\"bearer\")", content);
    }

    [Fact]
    public void Spec_Level_Security_Detected_As_Default()
    {
        var spec = """
            {
                "openapi": "3.1.0",
                "info": { "title": "API", "version": "1.0.0" },
                "security": [{ "bearerAuth": [] }],
                "paths": {
                    "/api/data": {
                        "get": {
                            "operationId": "data_get",
                            "tags": ["Data"],
                            "responses": {
                                "200": { "description": "OK" }
                            }
                        }
                    }
                },
                "components": {
                    "securitySchemes": {
                        "bearerAuth": { "type": "http", "scheme": "bearer" }
                    }
                }
            }
            """;

        var result = Import(spec);
        var content = FindFile(result, "DataContract.cs");

        Assert.Contains(".Secure(\"bearerAuth\")", content);
    }

    // ========== Fixture-based round-trip tests ==========

    private static string LoadFixture()
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openapi-import.json"));
    }

    /// <summary>
    /// Compiles multiple generated files as separate syntax trees (file-scoped namespaces
    /// require one per tree).
    /// </summary>
    private static Compilation CompileGeneratedFiles(ImportResult result)
    {
        return CompilationHelper.CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray());
    }

    [Fact]
    public void Fixture_Generated_CSharp_Compiles()
    {
        var json = LoadFixture();
        var result = Import(json, "TaskBoard.Contracts");

        Assert.Empty(result.Warnings);

        var compilation = CompileGeneratedFiles(result);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Fixture_Contracts_Survive_Roslyn_RoundTrip()
    {
        var json = LoadFixture();
        var result = Import(json, "TaskBoard.Contracts");

        var compilation = CompileGeneratedFiles(result);

        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        // 3 tags: Tasks (6 operations), Members (2), Health (1) = 9 endpoints total
        Assert.Equal(9, endpoints.Count);

        // Verify HTTP methods
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PATCH" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/members");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/members");
    }

    [Fact]
    public void Fixture_Types_Survive_Roslyn_RoundTrip()
    {
        var json = LoadFixture();
        var result = Import(json, "TaskBoard.Contracts");

        var compilation = CompileGeneratedFiles(result);

        var walker = TypeWalker.Create(compilation);

        // Records should be discovered
        Assert.True(walker.Definitions.ContainsKey("TaskDto"));
        Assert.True(walker.Definitions.ContainsKey("LabelDto"));
        Assert.True(walker.Definitions.ContainsKey("CreateTaskRequest"));
        Assert.True(walker.Definitions.ContainsKey("NotFoundDto"));
        Assert.True(walker.Definitions.ContainsKey("ValidationErrorDto"));
        Assert.True(walker.Definitions.ContainsKey("MemberDto"));
        Assert.True(walker.Definitions.ContainsKey("HealthDto"));

        // Enum should be discovered
        Assert.True(walker.Enums.ContainsKey("Priority"));
        var priority = walker.Enums["Priority"];
        Assert.Contains("Low", priority.Members);
        Assert.Contains("Critical", priority.Members);

        // Brands should be discovered
        Assert.True(walker.Brands.ContainsKey("Email"));
        Assert.True(walker.Brands.ContainsKey("Website"));
    }

    [Fact]
    public void Fixture_TaskDto_Properties_Match_OpenAPI_Schema()
    {
        var json = LoadFixture();
        var result = Import(json, "TaskBoard.Contracts");

        var compilation = CompileGeneratedFiles(result);

        var walker = TypeWalker.Create(compilation);
        var taskDto = walker.Definitions["TaskDto"];

        // Verify all property types survived the round-trip
        AssertProperty(taskDto, "id", "string");       // Guid → string in TS
        AssertProperty(taskDto, "title", "string");
        AssertProperty(taskDto, "priority", null);      // enum ref
        AssertProperty(taskDto, "score", "number");     // double → number
        AssertProperty(taskDto, "rating", "number");    // float → number
        AssertProperty(taskDto, "viewCount", "number"); // int → number
        AssertProperty(taskDto, "totalBytes", "number"); // long → number
        AssertProperty(taskDto, "isArchived", "boolean");
        AssertProperty(taskDto, "createdAt", "string"); // DateTime → string in TS

        // description should be optional (nullable in OpenAPI, optional in TS)
        var descProp = taskDto.Properties.FirstOrDefault(p => p.Name == "description");
        Assert.NotNull(descProp);
        Assert.True(descProp.IsOptional || descProp.Type is TsType.Nullable);
    }

    [Fact]
    public void Fixture_Endpoint_Responses_And_Security_Survive_RoundTrip()
    {
        var json = LoadFixture();
        var result = Import(json, "TaskBoard.Contracts");

        var compilation = CompileGeneratedFiles(result);

        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        // POST /api/tasks → 201 + 422 error
        var createTask = endpoints.First(e =>
            e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(createTask.Responses, r => r.StatusCode == 201);
        Assert.Contains(createTask.Responses, r => r.StatusCode == 422);

        // GET /api/tasks/{taskId} → 200 + 404 error
        var getTask = endpoints.First(e =>
            e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(getTask.Responses, r => r.StatusCode == 200);
        Assert.Contains(getTask.Responses, r => r.StatusCode == 404);

        // DELETE /api/tasks/{taskId} → 204 (void)
        var deleteTask = endpoints.First(e =>
            e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{taskId}");
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 204);

        // GET /api/health → anonymous
        var health = endpoints.First(e => e.RouteTemplate == "/api/health");
        Assert.NotNull(health.Security);
        Assert.True(health.Security.IsAnonymous);

        // POST /api/members → secured with "admin"
        var invite = endpoints.First(e =>
            e.HttpMethod == "POST" && e.RouteTemplate == "/api/members");
        Assert.NotNull(invite.Security);
        Assert.Equal("admin", invite.Security.Scheme);
    }

    [Fact]
    public void Fixture_Covers_All_Supported_Type_Mappings()
    {
        // Verify the fixture exercises every type from the supported subset table
        var json = LoadFixture();
        var content = FindFile(Import(json, "Test"), "TaskDto.cs");

        Assert.Contains("Guid Id", content);                          // string + uuid
        Assert.Contains("string Title", content);                     // string
        Assert.Contains("string? Description", content);              // nullable string
        Assert.Contains("Priority Priority", content);                // $ref → enum
        Assert.Contains("double Score", content);                     // number
        Assert.Contains("float Rating", content);                     // number + float
        Assert.Contains("int ViewCount", content);                    // integer
        Assert.Contains("long TotalBytes", content);                  // integer + int64
        Assert.Contains("bool IsArchived", content);                  // boolean
        Assert.Contains("DateTime CreatedAt", content);               // string + date-time
        Assert.Contains("Email AssigneeEmail", content);              // $ref → brand (email)
        Assert.Contains("List<LabelDto> Labels", content);            // array + $ref
        Assert.Contains("Dictionary<string, string> Metadata", content); // object + additionalProperties

        // Nullable ref via oneOf (PatchTaskRequest.priority)
        var patchContent = FindFile(Import(json, "Test"), "PatchTaskRequest.cs");
        Assert.Contains("Priority?", patchContent);
    }

    [Fact]
    public void Fixture_Covers_All_HTTP_Methods()
    {
        var json = LoadFixture();
        var result = Import(json, "Test");
        var content = FindFile(result, "TasksContract.cs");

        Assert.Contains("Endpoint.Get<", content);    // GET /api/tasks
        Assert.Contains("Endpoint.Post<", content);   // POST /api/tasks
        Assert.Contains("Endpoint.Put<", content);    // PUT /api/tasks/{taskId}
        Assert.Contains("Endpoint.Patch<", content);  // PATCH /api/tasks/{taskId}
        Assert.Contains("Endpoint.Delete(", content); // DELETE /api/tasks/{taskId}
    }

    private static void AssertProperty(
        TsTypeDefinition def,
        string name,
        string? expectedPrimitive)
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
