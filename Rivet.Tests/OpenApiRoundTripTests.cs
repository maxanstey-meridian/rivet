using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Tests that C# → OpenAPI → Import → C# → Roslyn walk produces equivalent endpoints.
/// </summary>
public sealed class OpenApiRoundTripTests
{
    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) RoundTrip(
        string csharpSource, string? security = null)
    {
        // Forward: C# → OpenAPI JSON
        var compilation = CompilationHelper.CreateCompilation(csharpSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var securityConfig = security is not null ? SecurityParser.Parse(security) : null;
        var openApiJson = OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);

        // Reverse: OpenAPI → import → compile → walk
        var importResult = OpenApiImporter.Import(
            openApiJson, new ImportOptions("RoundTrip", security));
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (reDiscovered, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);
        var reEndpoints = CompilationHelper.WalkContracts(recompilation, reDiscovered, rewalker);

        return (reEndpoints, rewalker);
    }

    [Fact]
    public void Simple_CRUD_Survives_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record CreateTaskRequest(string Title);

            [RivetType]
            public sealed record UpdateTaskRequest(string Title, string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define ListTasks =
                    Define.Get<TaskDto>("/api/tasks");

                public static readonly Define CreateTask =
                    Define.Post<CreateTaskRequest, TaskDto>("/api/tasks")
                        .Status(201);

                public static readonly Define UpdateTask =
                    Define.Put<UpdateTaskRequest, TaskDto>("/api/tasks/{id}");

                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}")
                        .Status(204);
            }
            """;

        var (endpoints, walker) = RoundTrip(source);

        Assert.Equal(4, endpoints.Count);
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/tasks/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{id}");

        // Return types survive
        var listTasks = endpoints.First(e => e.HttpMethod == "GET");
        Assert.True(listTasks.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {listTasks.ReturnType}");

        var createTask = endpoints.First(e => e.HttpMethod == "POST");
        Assert.True(createTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {createTask.ReturnType}");
        Assert.Contains(createTask.Responses, r => r.StatusCode == 201);
        Assert.True(createTask.Responses.First(r => r.StatusCode == 201).DataType is TsType.TypeRef { Name: "TaskDto" },
            "201 response should carry TaskDto DataType");

        var updateTask = endpoints.First(e => e.HttpMethod == "PUT");
        Assert.True(updateTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {updateTask.ReturnType}");
        // PUT has a path param
        var putIdParam = Assert.Single(updateTask.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", putIdParam.Name);

        var deleteTask = endpoints.First(e => e.HttpMethod == "DELETE");
        Assert.Null(deleteTask.ReturnType);
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 204);

        // Property types on TaskDto survive
        var taskDef = walker.Definitions["TaskDto"];
        var idProp = taskDef.Properties.First(p => p.Name == "id");
        Assert.True(idProp.Type is TsType.Primitive { Name: "string" },
            $"Expected Primitive(string) but got {idProp.Type}");
        var titleProp = taskDef.Properties.First(p => p.Name == "title");
        Assert.True(titleProp.Type is TsType.Primitive { Name: "string" },
            $"Expected Primitive(string) but got {titleProp.Type}");
    }

    [Fact]
    public void Types_Survive_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            public enum Priority { Low, Medium, High }

            [RivetType]
            public sealed record TaskDto(string Id, Priority Priority);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);

        Assert.True(walker.Definitions.ContainsKey("TaskDto"));
        Assert.True(walker.Enums.ContainsKey("Priority"));
        Assert.Contains("Low", walker.Enums["Priority"].Members);
        Assert.Contains("Medium", walker.Enums["Priority"].Members);
        Assert.Contains("High", walker.Enums["Priority"].Members);

        // Property types on TaskDto survive
        var taskDef = walker.Definitions["TaskDto"];
        var idProp = taskDef.Properties.First(p => p.Name == "id");
        Assert.True(idProp.Type is TsType.Primitive { Name: "string" },
            $"Expected Primitive(string) but got {idProp.Type}");
        var priorityProp = taskDef.Properties.First(p => p.Name == "priority");
        Assert.True(priorityProp.Type is TsType.TypeRef { Name: "Priority" },
            $"Expected TypeRef(Priority) but got {priorityProp.Type}");

        // Endpoint return type survives
        var getTask = endpoints.First(e => e.HttpMethod == "GET");
        Assert.True(getTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {getTask.ReturnType}");
    }

    [Fact]
    public void Nullable_Types_Survive_RoundTrip()
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

        var (_, walker) = RoundTrip(source);
        var person = walker.Definitions["PersonDto"];

        var bio = person.Properties.First(p => p.Name == "bio");
        Assert.True(bio.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } });

        var age = person.Properties.First(p => p.Name == "age");
        Assert.True(age.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } });

        var address = person.Properties.First(p => p.Name == "address");
        Assert.True(address.Type is TsType.Nullable { Inner: TsType.TypeRef { Name: "AddressDto" } });
    }

    [Fact]
    public void Array_And_Dictionary_Types_Survive_RoundTrip()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LabelDto(string Name);

            [RivetType]
            public sealed record TaskDto(
                List<string> Tags,
                List<LabelDto> Labels,
                Dictionary<string, string> Metadata);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        var (_, walker) = RoundTrip(source);
        var task = walker.Definitions["TaskDto"];

        var tags = task.Properties.First(p => p.Name == "tags");
        Assert.True(tags.Type is TsType.Array { Element: TsType.Primitive { Name: "string" } });

        var labels = task.Properties.First(p => p.Name == "labels");
        Assert.True(labels.Type is TsType.Array { Element: TsType.TypeRef { Name: "LabelDto" } });

        var metadata = task.Properties.First(p => p.Name == "metadata");
        Assert.True(metadata.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "string" } });
    }

    [Fact]
    public void Multi_Response_Endpoints_Survive_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetType]
            public sealed record ValidationErrorDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404, "Not found")
                        .Returns<ValidationErrorDto>(422, "Validation failed");
            }
            """;

        var (endpoints, _) = RoundTrip(source);
        var getTask = endpoints.First(e => e.HttpMethod == "GET");

        Assert.Contains(getTask.Responses, r => r.StatusCode == 200);
        Assert.Contains(getTask.Responses, r => r.StatusCode == 404);
        Assert.Contains(getTask.Responses, r => r.StatusCode == 422);

        // DataType on each response survives
        var ok = getTask.Responses.First(r => r.StatusCode == 200);
        Assert.True(ok.DataType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected 200 DataType TypeRef(TaskDto) but got {ok.DataType}");

        var notFound = getTask.Responses.First(r => r.StatusCode == 404);
        Assert.True(notFound.DataType is TsType.TypeRef { Name: "NotFoundDto" },
            $"Expected 404 DataType TypeRef(NotFoundDto) but got {notFound.DataType}");

        var validation = getTask.Responses.First(r => r.StatusCode == 422);
        Assert.True(validation.DataType is TsType.TypeRef { Name: "ValidationErrorDto" },
            $"Expected 422 DataType TypeRef(ValidationErrorDto) but got {validation.DataType}");

        // Endpoint return type is the primary (200) type
        Assert.True(getTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected ReturnType TypeRef(TaskDto) but got {getTask.ReturnType}");
    }

    [Fact]
    public void Security_Survives_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record StatusDto(string Status);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }

            [RivetContract]
            public static class HealthContract
            {
                public static readonly Define Check =
                    Define.Get<StatusDto>("/api/health")
                        .Anonymous();
            }

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define Purge =
                    Define.Delete("/api/admin")
                        .Status(204)
                        .Secure("admin");
            }
            """;

        var (endpoints, _) = RoundTrip(source, "bearer");

        // Anonymous endpoint
        var health = endpoints.First(e => e.RouteTemplate == "/api/health");
        Assert.NotNull(health.Security);
        Assert.True(health.Security.IsAnonymous);

        // Override endpoint
        var admin = endpoints.First(e => e.RouteTemplate == "/api/admin");
        Assert.NotNull(admin.Security);
        Assert.Equal("admin", admin.Security.Scheme);

        // Default-security endpoint inherits global bearer after round-trip
        var task = endpoints.First(e => e.RouteTemplate == "/api/tasks/{id}");
        Assert.NotNull(task.Security);
        Assert.Equal("bearer", task.Security.Scheme);

        // Return types survive through security round-trip
        Assert.True(task.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {task.ReturnType}");
        Assert.True(health.ReturnType is TsType.TypeRef { Name: "StatusDto" },
            $"Expected TypeRef(StatusDto) but got {health.ReturnType}");

        // Delete with .Status(204) has no return type
        Assert.Null(admin.ReturnType);
        Assert.Contains(admin.Responses, r => r.StatusCode == 204);
    }

    [Fact]
    public void Descriptions_Survive_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Retrieve a task by ID");
            }
            """;

        var (endpoints, _) = RoundTrip(source);
        var getTask = endpoints.First(e => e.HttpMethod == "GET");

        Assert.Equal("Retrieve a task by ID", getTask.Description);
    }

    [Fact]
    public void Void_Endpoint_Survives_RoundTrip()
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

        var (endpoints, _) = RoundTrip(source);

        Assert.Single(endpoints);
        var deleteTask = endpoints[0];
        Assert.Equal("DELETE", deleteTask.HttpMethod);
        Assert.Null(deleteTask.ReturnType);
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 204);
    }

    [Fact]
    public void Path_Parameter_Types_Survive_RoundTrip()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GetTaskInput(Guid Id);

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<GetTaskInput, TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, _) = RoundTrip(source);

        Assert.Single(endpoints);
        var getTask = endpoints[0];

        // Route param should exist with correct type and Guid format
        var idParam = Assert.Single(getTask.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", idParam.Name);
        Assert.True(idParam.Type is TsType.Primitive { Name: "string", Format: "uuid" },
            $"Expected Primitive(string, uuid) but got {idParam.Type}");

        // Return type survives
        Assert.True(getTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {getTask.ReturnType}");
    }

    [Fact]
    public void Query_Parameter_Types_Survive_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(string Query, int Limit);

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define SearchTasks =
                    Define.Get<SearchInput, TaskDto>("/api/tasks");
            }
            """;

        var (endpoints, _) = RoundTrip(source);

        Assert.Single(endpoints);
        var search = endpoints[0];

        // Both query params should have correct types
        Assert.Equal(2, search.Params.Count);

        var queryParam = Assert.Single(search.Params, p => p.Name == "query");
        Assert.Equal(ParamSource.Query, queryParam.Source);
        Assert.True(queryParam.Type is TsType.Primitive { Name: "string" },
            $"Expected Primitive(string) but got {queryParam.Type}");

        var limitParam = Assert.Single(search.Params, p => p.Name == "limit");
        Assert.Equal(ParamSource.Query, limitParam.Source);
        Assert.True(limitParam.Type is TsType.Primitive { Name: "number", Format: "int32" },
            $"Expected Primitive(number, int32) but got {limitParam.Type}");

        // Return type survives
        Assert.True(search.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"Expected TypeRef(TaskDto) but got {search.ReturnType}");
    }

    [Fact]
    public void Brand_Survives_RoundTrip()
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

        var (_, walker) = RoundTrip(source);

        // Brand should survive as a brand, not collapse to plain string
        Assert.True(walker.Brands.ContainsKey("Email"),
            $"Expected brand 'Email' but got brands: [{string.Join(", ", walker.Brands.Keys)}]");

        // UserDto should reference Email as a Brand or TypeRef (not plain string)
        var user = walker.Definitions["UserDto"];
        var emailProp = user.Properties.First(p => p.Name == "email");
        Assert.True(
            emailProp.Type is TsType.Brand { Name: "Email" } or TsType.TypeRef { Name: "Email" },
            $"Expected Brand(Email) or TypeRef(Email) but got {emailProp.Type}");
    }

    [Fact]
    public void FileUpload_InputTypeName_Survives_RoundTrip()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files")
                        .Status(201);
            }
            """;

        // Forward: C# → OpenAPI
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Verify the extension is in the JSON
        var jsonDoc = System.Text.Json.JsonDocument.Parse(openApiJson);
        var multipartSchema = jsonDoc.RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");
        Assert.Equal("UploadInput", multipartSchema.GetProperty("x-rivet-input-type").GetString());

        // Reverse: OpenAPI → import → compile → walk
        var importResult = OpenApiImporter.Import(
            openApiJson, new ImportOptions("RoundTrip"));

        // The input type name should be "UploadInput" (not "UploadRequest")
        var contractFile = importResult.Files.First(f => f.FileName.Contains("Contract"));
        Assert.Contains("UploadInput", contractFile.Content);

        // Full recompile → walk: verify the record is named UploadInput and the endpoint references it
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (reDiscovered, reWalker) = CompilationHelper.DiscoverAndWalk(recompilation);
        var reEndpoints = CompilationHelper.WalkContracts(recompilation, reDiscovered, reWalker);

        var upload = Assert.Single(reEndpoints, e => e.HttpMethod == "POST");
        // With required multipart properties, IFormFile resolves correctly and the
        // walker decomposes into File+FormField params (the correct behavior).
        Assert.Equal("UploadInput", upload.InputTypeName);
        var fileParam = Assert.Single(upload.Params, p => p.Source == ParamSource.File);
        Assert.Equal("document", fileParam.Name);
        var formParam = Assert.Single(upload.Params, p => p.Source == ParamSource.FormField);
        Assert.Equal("title", formParam.Name);
    }

    [Fact]
    public void Generic_Survives_RoundTrip()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define ListTasks =
                    Define.Get<PagedResult<TaskDto>>("/api/tasks");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);

        // The generic template should survive
        Assert.True(walker.Definitions.ContainsKey("PagedResult"),
            $"Expected generic 'PagedResult' but got definitions: [{string.Join(", ", walker.Definitions.Keys)}]");

        var pagedResult = walker.Definitions["PagedResult"];
        Assert.True(pagedResult.TypeParameters.Count > 0,
            "PagedResult should have type parameters");
        Assert.Equal("T", pagedResult.TypeParameters[0]);

        // Template properties should use type parameter T, not concrete type
        var itemsProp = pagedResult.Properties.First(p => p.Name == "items");
        Assert.True(itemsProp.Type is TsType.Array { Element: TsType.TypeParam { Name: "T" } },
            $"Expected Array(TypeParam(T)) but got {itemsProp.Type}");
        var countProp = pagedResult.Properties.First(p => p.Name == "totalCount");
        Assert.IsType<TsType.Primitive>(countProp.Type);

        // The endpoint return type should be Generic, not a flat TypeRef
        var listEndpoint = endpoints.First(e => e.HttpMethod == "GET");
        Assert.IsType<TsType.Generic>(listEndpoint.ReturnType);
        var generic = (TsType.Generic)listEndpoint.ReturnType!;
        Assert.Equal("PagedResult", generic.Name);
        Assert.Single(generic.TypeArguments);
        Assert.IsType<TsType.TypeRef>(generic.TypeArguments[0]);
        Assert.Equal("TaskDto", ((TsType.TypeRef)generic.TypeArguments[0]).Name);
    }

    [Fact]
    public void Generic_Multiple_Instantiations_Survive_RoundTrip()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record UserDto(string Id, string Name);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define ListTasks =
                    Define.Get<PagedResult<TaskDto>>("/api/tasks");
            }

            [RivetContract]
            public static class UsersContract
            {
                public static readonly Define ListUsers =
                    Define.Get<PagedResult<UserDto>>("/api/users");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);

        // Only one PagedResult template should exist (not PagedResultTaskDto + PagedResultUserDto)
        Assert.True(walker.Definitions.ContainsKey("PagedResult"));
        var pagedResult = walker.Definitions["PagedResult"];
        Assert.True(pagedResult.TypeParameters.Count > 0);

        // Both endpoints should use Generic return types
        var taskEndpoint = endpoints.First(e => e.RouteTemplate == "/api/tasks");
        Assert.IsType<TsType.Generic>(taskEndpoint.ReturnType);

        var userEndpoint = endpoints.First(e => e.RouteTemplate == "/api/users");
        Assert.IsType<TsType.Generic>(userEndpoint.ReturnType);
    }

    [Fact]
    public void Double_RoundTrip_Is_Stable()
    {
        // C# → OpenAPI → C# → OpenAPI → C# should produce equivalent endpoints both times.
        // Tests idempotency of the extension-based round-trip.
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Email(string Value);

            public enum Priority { Low, Medium, High }

            [RivetType]
            public sealed record TaskDto(string Id, string Title, Email AuthorEmail, Priority Priority);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define ListTasks =
                    Define.Get<PagedResult<TaskDto>>("/api/tasks")
                        .Description("List all tasks");

                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}")
                        .Status(204);
            }
            """;

        // First round-trip
        var (firstEndpoints, firstWalker) = RoundTrip(source);

        // Second round-trip: feed the first round-trip's output back through
        var firstCompilation = CompilationHelper.CreateCompilation(source);
        var (firstDisc, firstWlk) = CompilationHelper.DiscoverAndWalk(firstCompilation);
        var firstEps = CompilationHelper.WalkContracts(firstCompilation, firstDisc, firstWlk);
        var firstJson = OpenApiEmitter.Emit(
            firstEps, firstWlk.Definitions, firstWlk.Brands, firstWlk.Enums, null);
        var firstImport = OpenApiImporter.Import(firstJson, new ImportOptions("RoundTrip"));
        var secondCompilation = CompilationHelper.CreateCompilationFromMultiple(
            firstImport.Files.Select(f => f.Content).ToArray());
        var (secondDisc, secondWlk) = CompilationHelper.DiscoverAndWalk(secondCompilation);
        var secondEps = CompilationHelper.WalkContracts(secondCompilation, secondDisc, secondWlk);
        var secondJson = OpenApiEmitter.Emit(
            secondEps, secondWlk.Definitions, secondWlk.Brands, secondWlk.Enums, null);
        var secondImport = OpenApiImporter.Import(secondJson, new ImportOptions("RoundTrip"));
        var thirdCompilation = CompilationHelper.CreateCompilationFromMultiple(
            secondImport.Files.Select(f => f.Content).ToArray());
        var (thirdDisc, thirdWlk) = CompilationHelper.DiscoverAndWalk(thirdCompilation);
        var thirdEps = CompilationHelper.WalkContracts(thirdCompilation, thirdDisc, thirdWlk);

        // Same number of endpoints
        Assert.Equal(firstEndpoints.Count, thirdEps.Count);

        // Same routes, methods, param counts, and return types
        foreach (var ep1 in firstEndpoints)
        {
            var ep3 = thirdEps.FirstOrDefault(e =>
                e.HttpMethod == ep1.HttpMethod && e.RouteTemplate == ep1.RouteTemplate);
            Assert.NotNull(ep3);
            Assert.Equal(ep1.Params.Count, ep3.Params.Count);
            Assert.Equal(ep1.Description, ep3.Description);

            // Return type shape preserved with structural match
            if (ep1.ReturnType is TsType.Generic g1)
            {
                Assert.True(ep3.ReturnType is TsType.Generic g3
                    && g3.Name == g1.Name
                    && g3.TypeArguments.Count == g1.TypeArguments.Count,
                    $"Expected Generic({g1.Name}) but got {ep3.ReturnType}");
            }
            else if (ep1.ReturnType is TsType.TypeRef r1)
            {
                Assert.True(ep3.ReturnType is TsType.TypeRef r3 && r3.Name == r1.Name,
                    $"Expected TypeRef({r1.Name}) but got {ep3.ReturnType}");
            }
            else if (ep1.ReturnType is null)
            {
                Assert.Null(ep3.ReturnType);
            }

            // Response counts match
            Assert.Equal(ep1.Responses.Count, ep3.Responses.Count);
        }

        // Brand survived both round-trips
        Assert.True(thirdWlk.Brands.ContainsKey("Email"),
            $"Brand 'Email' lost after double round-trip. Brands: [{string.Join(", ", thirdWlk.Brands.Keys)}]");

        // Generic survived both round-trips
        Assert.True(thirdWlk.Definitions.ContainsKey("PagedResult"));
        Assert.True(thirdWlk.Definitions["PagedResult"].TypeParameters.Count > 0,
            "PagedResult lost type parameters after double round-trip");

        // Enum survived
        Assert.True(thirdWlk.Enums.ContainsKey("Priority"));

        // TaskDto property types survive double round-trip
        var taskDef = thirdWlk.Definitions["TaskDto"];
        Assert.True(taskDef.Properties.First(p => p.Name == "id").Type is TsType.Primitive { Name: "string" });
        Assert.True(taskDef.Properties.First(p => p.Name == "title").Type is TsType.Primitive { Name: "string" });
        Assert.True(
            taskDef.Properties.First(p => p.Name == "authorEmail").Type
                is TsType.Brand { Name: "Email" } or TsType.TypeRef { Name: "Email" },
            $"Expected Brand/TypeRef(Email) but got {taskDef.Properties.First(p => p.Name == "authorEmail").Type}");
        Assert.True(taskDef.Properties.First(p => p.Name == "priority").Type is TsType.TypeRef { Name: "Priority" },
            $"Expected TypeRef(Priority) but got {taskDef.Properties.First(p => p.Name == "priority").Type}");

        // OpenAPI JSON idempotency: compare schema names and path sets
        // (full string equality may differ due to dictionary iteration order)
        var firstDoc = JsonSerializer.Deserialize<JsonElement>(firstJson);
        var secondDoc = JsonSerializer.Deserialize<JsonElement>(secondJson);
        var firstSchemas = firstDoc.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        var secondSchemas = secondDoc.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(firstSchemas, secondSchemas);

        var firstPaths = firstDoc.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        var secondPaths = secondDoc.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(firstPaths, secondPaths);
    }

    [Fact]
    public void Brand_Refs_Resolve_In_Emitted_Spec()
    {
        // Verify that brand $refs point to existing component schemas
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

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Validate: parse as OpenAPI and check for errors
        var readResult = Microsoft.OpenApi.OpenApiDocument.Parse(json, "json");
        Assert.NotNull(readResult.Document);
        var errors = readResult.Diagnostic?.Errors ?? [];
        Assert.True(errors.Count == 0,
            $"OpenAPI validation errors:\n{string.Join("\n", errors.Select(e => $"  - {e.Message}"))}");

        // Verify brand appears as component schema
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("Email", out _));

        // Collect all $refs and verify they resolve
        var refs = new List<string>();
        CollectRefs(doc.RootElement, refs);
        var schemaNames = new HashSet<string>();
        foreach (var s in schemas.EnumerateObject())
        {
            schemaNames.Add(s.Name);
        }

        foreach (var refValue in refs)
        {
            var schemaName = refValue["#/components/schemas/".Length..];
            Assert.True(schemaNames.Contains(schemaName),
                $"Broken $ref: {refValue} — not in schemas [{string.Join(", ", schemaNames)}]");
        }
    }

    private static void CollectRefs(System.Text.Json.JsonElement element, List<string> refs)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        refs.Add(prop.Value.GetString()!);
                    }
                    else
                    {
                        CollectRefs(prop.Value, refs);
                    }
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectRefs(item, refs);
                }
                break;
        }
    }

    // ========== x-rivet-csharp-type round-trips ==========

    [Fact]
    public void CSharpType_DateTimeOffset_Survives_RoundTrip()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record EventDto(string Name, DateTimeOffset CreatedAt, DateTime UpdatedAt);

            [RivetContract]
            public static class EventsContract
            {
                public static readonly Define GetEvent =
                    Define.Get<EventDto>("/api/events/{id}");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);
        var ep = Assert.Single(endpoints);
        Assert.NotNull(ep.ReturnType);

        // EventDto should have distinct types for CreatedAt and UpdatedAt
        var def = walker.Definitions["EventDto"];
        var createdAt = def.Properties.First(p => p.Name == "createdAt");
        var updatedAt = def.Properties.First(p => p.Name == "updatedAt");

        // Both are string in TS, but the C# types should differ after round-trip
        Assert.IsType<TsType.Primitive>(createdAt.Type);
        Assert.IsType<TsType.Primitive>(updatedAt.Type);

        // The key assertion: DateTimeOffset roundtrips as DateTimeOffset, not DateTime
        var createdPrim = (TsType.Primitive)createdAt.Type;
        var updatedPrim = (TsType.Primitive)updatedAt.Type;
        Assert.Equal("date-time", createdPrim.Format);
        Assert.Equal("date-time", updatedPrim.Format);
        Assert.Equal("DateTimeOffset", createdPrim.CSharpType);
        Assert.Null(updatedPrim.CSharpType); // DateTime has no CSharpType — it's the default
    }

    [Fact]
    public void CSharpType_UnsignedIntegers_Survive_RoundTrip()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MetricsDto(int Count, uint Flags, long Total, ulong BigTotal);

            [RivetContract]
            public static class MetricsContract
            {
                public static readonly Define GetMetrics =
                    Define.Get<MetricsDto>("/api/metrics");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);
        var def = walker.Definitions["MetricsDto"];

        var count = (TsType.Primitive)def.Properties.First(p => p.Name == "count").Type;
        var flags = (TsType.Primitive)def.Properties.First(p => p.Name == "flags").Type;
        var total = (TsType.Primitive)def.Properties.First(p => p.Name == "total").Type;
        var bigTotal = (TsType.Primitive)def.Properties.First(p => p.Name == "bigTotal").Type;

        Assert.Null(count.CSharpType); // int is default for int32
        Assert.Equal("uint", flags.CSharpType);
        Assert.Null(total.CSharpType); // long is default for int64
        Assert.Equal("ulong", bigTotal.CSharpType);
    }

    [Fact]
    public void CSharpType_SmallIntegers_Survive_RoundTrip()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SensorDto(short Temperature, byte Channel, sbyte Offset, ushort Voltage);

            [RivetContract]
            public static class SensorsContract
            {
                public static readonly Define GetSensor =
                    Define.Get<SensorDto>("/api/sensors/{id}");
            }
            """;

        var (endpoints, walker) = RoundTrip(source);
        var def = walker.Definitions["SensorDto"];

        var temp = (TsType.Primitive)def.Properties.First(p => p.Name == "temperature").Type;
        var channel = (TsType.Primitive)def.Properties.First(p => p.Name == "channel").Type;
        var offset = (TsType.Primitive)def.Properties.First(p => p.Name == "offset").Type;
        var voltage = (TsType.Primitive)def.Properties.First(p => p.Name == "voltage").Type;

        Assert.Equal("short", temp.CSharpType);
        Assert.Equal("byte", channel.CSharpType);
        Assert.Equal("sbyte", offset.CSharpType);
        Assert.Equal("ushort", voltage.CSharpType);
    }

    [Fact]
    public void CSharpType_EmittedInOpenApiJson()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MixedDto(uint Flags, DateTimeOffset Timestamp, int Normal);

            [RivetContract]
            public static class MixedContract
            {
                public static readonly Define Get =
                    Define.Get<MixedDto>("/api/mixed");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        var schema = doc.GetProperty("components").GetProperty("schemas").GetProperty("MixedDto");
        var props = schema.GetProperty("properties");

        // Flags has x-rivet-csharp-type
        var flags = props.GetProperty("flags");
        Assert.Equal("integer", flags.GetProperty("type").GetString());
        Assert.Equal("uint32", flags.GetProperty("format").GetString());
        Assert.Equal("uint", flags.GetProperty("x-rivet-csharp-type").GetString());

        // Timestamp has x-rivet-csharp-type
        var timestamp = props.GetProperty("timestamp");
        Assert.Equal("string", timestamp.GetProperty("type").GetString());
        Assert.Equal("date-time", timestamp.GetProperty("format").GetString());
        Assert.Equal("DateTimeOffset", timestamp.GetProperty("x-rivet-csharp-type").GetString());

        // Normal does NOT have x-rivet-csharp-type (int is the default for int32)
        var normal = props.GetProperty("normal");
        Assert.Equal("integer", normal.GetProperty("type").GetString());
        Assert.False(normal.TryGetProperty("x-rivet-csharp-type", out _),
            "int should not emit x-rivet-csharp-type — it's the default for int32");
    }

    [Fact]
    public void CSharpType_AllLossyTypes_Double_RoundTrip_Stable()
    {
        // Every type that was previously lossy should now survive two full round-trips
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AllTypesDto(
                int Int32Val,
                uint UInt32Val,
                long Int64Val,
                ulong UInt64Val,
                short Int16Val,
                ushort UInt16Val,
                byte ByteVal,
                sbyte SByteVal,
                float FloatVal,
                double DoubleVal,
                decimal DecimalVal,
                DateTime DateTimeVal,
                DateTimeOffset DateTimeOffsetVal,
                DateOnly DateOnlyVal,
                Guid GuidVal,
                string StringVal,
                bool BoolVal);

            [RivetContract]
            public static class AllTypesContract
            {
                public static readonly Define Get =
                    Define.Get<AllTypesDto>("/api/all-types");
            }
            """;

        // First round-trip
        var (firstEndpoints, firstWalker) = RoundTrip(source);
        var firstDef = firstWalker.Definitions["AllTypesDto"];

        // Second round-trip from the first round-trip's output
        var compilation = CompilationHelper.CreateCompilation(source);
        var (disc, wlk) = CompilationHelper.DiscoverAndWalk(compilation);
        var eps = CompilationHelper.WalkContracts(compilation, disc, wlk);
        var json1 = OpenApiEmitter.Emit(eps, wlk.Definitions, wlk.Brands, wlk.Enums, null);
        var import1 = OpenApiImporter.Import(json1, new ImportOptions("RoundTrip"));
        var recomp1 = CompilationHelper.CreateCompilationFromMultiple(
            import1.Files.Select(f => f.Content).ToArray());
        var (disc2, wlk2) = CompilationHelper.DiscoverAndWalk(recomp1);
        var eps2 = CompilationHelper.WalkContracts(recomp1, disc2, wlk2);
        var json2 = OpenApiEmitter.Emit(eps2, wlk2.Definitions, wlk2.Brands, wlk2.Enums, null);
        var import2 = OpenApiImporter.Import(json2, new ImportOptions("RoundTrip"));
        var recomp2 = CompilationHelper.CreateCompilationFromMultiple(
            import2.Files.Select(f => f.Content).ToArray());
        var (disc3, wlk3) = CompilationHelper.DiscoverAndWalk(recomp2);
        var secondDef = wlk3.Definitions["AllTypesDto"];

        // Every property type should match between first and second round-trip
        Assert.Equal(firstDef.Properties.Count, secondDef.Properties.Count);
        for (var i = 0; i < firstDef.Properties.Count; i++)
        {
            var p1 = (TsType.Primitive)firstDef.Properties[i].Type;
            var p2 = (TsType.Primitive)secondDef.Properties[i].Type;
            Assert.Equal(p1.Name, p2.Name);
            Assert.Equal(p1.Format, p2.Format);
            Assert.Equal(p1.CSharpType, p2.CSharpType);
        }
    }

    // ========== Comprehensive belt-and-braces round-trip ==========

    [Fact]
    public void Comprehensive_RoundTrip_AllFeatures()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            // --- String enum ---
            public enum Status { Active, Inactive, Archived }

            // --- Branded value object ---
            [RivetType]
            public sealed record Email(string Value);

            // --- Generic type ---
            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            // --- All primitives + deprecated property ---
            [RivetType]
            public sealed record KitchenSinkDto(
                string Name,
                int IntVal,
                uint UintVal,
                long LongVal,
                ulong UlongVal,
                short ShortVal,
                ushort UshortVal,
                byte ByteVal,
                sbyte SbyteVal,
                float FloatVal,
                double DoubleVal,
                decimal DecimalVal,
                bool BoolVal,
                DateTime DateTimeVal,
                DateTimeOffset DateTimeOffsetVal,
                DateOnly DateOnlyVal,
                Guid GuidVal,
                string? NullableString,
                int? NullableInt,
                bool? NullableBool,
                Guid? NullableGuid,
                List<string> Tags,
                List<int> Scores,
                Dictionary<string, string> Metadata,
                Dictionary<string, int> Counts,
                Email AuthorEmail,
                Status CurrentStatus,
                [property: Obsolete] string LegacyField);

            // --- Nested reference type ---
            [RivetType]
            public sealed record AddressDto(string City, string Country);

            // --- Type with nested ref + nullable ref ---
            [RivetType]
            public sealed record UserDto(string Id, string Name, AddressDto Address, AddressDto? SecondaryAddress);

            // --- Error response type ---
            [RivetType]
            public sealed record NotFoundError(string Message);

            // --- File upload input ---
            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            // --- File upload result ---
            [RivetType]
            public sealed record UploadResult(string Url);

            // --- Search input (query params) ---
            [RivetType]
            public sealed record SearchInput(string Query, int Limit);

            // --- Contract with all endpoint patterns ---
            [RivetContract]
            public static class ItemsContract
            {
                // GET with output, path param, description
                public static readonly Define GetItem =
                    Define.Get<KitchenSinkDto>("/api/items/{id}")
                        .Description("Get a single item by ID");

                // GET with query params, generic return
                public static readonly Define SearchItems =
                    Define.Get<SearchInput, PagedResult<KitchenSinkDto>>("/api/items");

                // POST with body input + output + status override
                public static readonly Define CreateItem =
                    Define.Post<KitchenSinkDto, KitchenSinkDto>("/api/items")
                        .Status(201);

                // DELETE void + 204 status
                public static readonly Define DeleteItem =
                    Define.Delete("/api/items/{id}")
                        .Status(204);

                // GET with multi-response (200 + 404)
                public static readonly Define GetUser =
                    Define.Get<UserDto>("/api/users/{id}")
                        .Returns<NotFoundError>(404, "User not found");

                // POST file upload
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files")
                        .Status(201);
            }

            // --- Separate contract for security + anonymous ---
            [RivetContract]
            public static class HealthContract
            {
                public static readonly Define Check =
                    Define.Get("/api/health")
                        .Anonymous();
            }

            [RivetContract]
            public static class AdminContract
            {
                public static readonly Define Purge =
                    Define.Delete("/api/admin")
                        .Status(204)
                        .Secure("admin");
            }

            // --- Second instantiation of generic for multi-instantiation test ---
            [RivetContract]
            public static class UsersContract
            {
                public static readonly Define ListUsers =
                    Define.Get<PagedResult<UserDto>>("/api/users");
            }
            """;

        var (endpoints, walker) = RoundTrip(source, "bearer");

        // --- Endpoint count (6 Items + 1 Health + 1 Admin + 1 Users) ---
        Assert.Equal(9, endpoints.Count);

        // --- HTTP methods + routes ---
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/items");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/files");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/admin");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");

        // --- KitchenSinkDto properties ---
        Assert.True(walker.Definitions.ContainsKey("KitchenSinkDto"));
        var sink = walker.Definitions["KitchenSinkDto"];
        // All primitives + nullable + collections + brand ref + enum ref + deprecated = 28 properties
        Assert.Equal(28, sink.Properties.Count);

        // Spot-check primitive types
        var nameP = sink.Properties.First(p => p.Name == "name");
        Assert.True(nameP.Type is TsType.Primitive { Name: "string" });

        var intP = sink.Properties.First(p => p.Name == "intVal");
        Assert.True(intP.Type is TsType.Primitive { Name: "number", Format: "int32" });

        var longP = sink.Properties.First(p => p.Name == "longVal");
        Assert.True(longP.Type is TsType.Primitive { Name: "number", Format: "int64" });

        var floatP = sink.Properties.First(p => p.Name == "floatVal");
        Assert.True(floatP.Type is TsType.Primitive { Name: "number", Format: "float" });

        var doubleP = sink.Properties.First(p => p.Name == "doubleVal");
        Assert.True(doubleP.Type is TsType.Primitive { Name: "number", Format: "double" });

        var decimalP = sink.Properties.First(p => p.Name == "decimalVal");
        Assert.True(decimalP.Type is TsType.Primitive { Name: "number", Format: "decimal" });

        var boolP = sink.Properties.First(p => p.Name == "boolVal");
        Assert.True(boolP.Type is TsType.Primitive { Name: "boolean" });

        var dateTimeP = sink.Properties.First(p => p.Name == "dateTimeVal");
        Assert.True(dateTimeP.Type is TsType.Primitive { Name: "string", Format: "date-time" });

        var dtoP = sink.Properties.First(p => p.Name == "dateTimeOffsetVal");
        Assert.True(dtoP.Type is TsType.Primitive { Name: "string", Format: "date-time" });

        var dateOnlyP = sink.Properties.First(p => p.Name == "dateOnlyVal");
        Assert.True(dateOnlyP.Type is TsType.Primitive { Name: "string", Format: "date" });

        var guidP = sink.Properties.First(p => p.Name == "guidVal");
        Assert.True(guidP.Type is TsType.Primitive { Name: "string", Format: "uuid" });

        // Nullable types
        var nullStr = sink.Properties.First(p => p.Name == "nullableString");
        Assert.True(nullStr.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } });

        var nullInt = sink.Properties.First(p => p.Name == "nullableInt");
        Assert.True(nullInt.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } });

        var nullBool = sink.Properties.First(p => p.Name == "nullableBool");
        Assert.True(nullBool.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "boolean" } });

        var nullGuid = sink.Properties.First(p => p.Name == "nullableGuid");
        Assert.True(nullGuid.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } });

        // Arrays
        var tags = sink.Properties.First(p => p.Name == "tags");
        Assert.True(tags.Type is TsType.Array { Element: TsType.Primitive { Name: "string" } });

        var scores = sink.Properties.First(p => p.Name == "scores");
        Assert.True(scores.Type is TsType.Array { Element: TsType.Primitive { Name: "number" } });

        // Dictionaries
        var meta = sink.Properties.First(p => p.Name == "metadata");
        Assert.True(meta.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "string" } });

        var counts = sink.Properties.First(p => p.Name == "counts");
        Assert.True(counts.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "number" } });

        // Brand reference
        var emailRef = sink.Properties.First(p => p.Name == "authorEmail");
        Assert.True(
            emailRef.Type is TsType.Brand { Name: "Email" } or TsType.TypeRef { Name: "Email" },
            $"Expected brand/ref Email but got {emailRef.Type}");

        // Enum reference
        var statusRef = sink.Properties.First(p => p.Name == "currentStatus");
        Assert.True(statusRef.Type is TsType.TypeRef { Name: "Status" },
            $"Expected TypeRef(Status) but got {statusRef.Type}");

        // Deprecated property
        var legacy = sink.Properties.First(p => p.Name == "legacyField");
        Assert.True(legacy.IsDeprecated, "legacyField should be deprecated after round-trip");

        // --- Enum survived ---
        Assert.True(walker.Enums.ContainsKey("Status"));
        Assert.Contains("Active", walker.Enums["Status"].Members);
        Assert.Contains("Inactive", walker.Enums["Status"].Members);
        Assert.Contains("Archived", walker.Enums["Status"].Members);

        // --- Brand survived ---
        Assert.True(walker.Brands.ContainsKey("Email"),
            $"Expected brand 'Email' but got: [{string.Join(", ", walker.Brands.Keys)}]");

        // --- Generic template survived ---
        Assert.True(walker.Definitions.ContainsKey("PagedResult"));
        var paged = walker.Definitions["PagedResult"];
        Assert.True(paged.TypeParameters.Count > 0, "PagedResult should have type parameters");
        Assert.Equal("T", paged.TypeParameters[0]);
        var itemsProp = paged.Properties.First(p => p.Name == "items");
        Assert.True(itemsProp.Type is TsType.Array { Element: TsType.TypeParam { Name: "T" } },
            $"Expected Array(TypeParam(T)) but got {itemsProp.Type}");

        // --- Multiple generic instantiations ---
        var searchEp = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items");
        Assert.IsType<TsType.Generic>(searchEp.ReturnType);
        var searchGeneric = (TsType.Generic)searchEp.ReturnType!;
        Assert.Equal("PagedResult", searchGeneric.Name);

        var usersEp = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.IsType<TsType.Generic>(usersEp.ReturnType);
        var usersGeneric = (TsType.Generic)usersEp.ReturnType!;
        Assert.Equal("PagedResult", usersGeneric.Name);

        // --- Description survived ---
        var getItem = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items/{id}");
        Assert.Equal("Get a single item by ID", getItem.Description);

        // --- Path parameter ---
        var routeParam = getItem.Params.FirstOrDefault(p => p.Source == ParamSource.Route);
        Assert.NotNull(routeParam);
        Assert.Equal("id", routeParam.Name);

        // --- Query parameters ---
        var queryEp = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items");
        Assert.True(queryEp.Params.Count >= 2, "Search endpoint should have query params");
        Assert.Contains(queryEp.Params, p => p.Name == "query" && p.Source == ParamSource.Query);
        Assert.Contains(queryEp.Params, p => p.Name == "limit" && p.Source == ParamSource.Query);

        // --- Void endpoint (DELETE 204) ---
        var deleteEp = endpoints.First(e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/items/{id}");
        Assert.Null(deleteEp.ReturnType);
        Assert.Contains(deleteEp.Responses, r => r.StatusCode == 204);

        // --- Status code override (POST 201) ---
        var createEp = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/items");
        Assert.Contains(createEp.Responses, r => r.StatusCode == 201);

        // --- Multi-response (200 + 404) ---
        var getUserEp = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{id}");
        Assert.Contains(getUserEp.Responses, r => r.StatusCode == 200);
        Assert.Contains(getUserEp.Responses, r => r.StatusCode == 404);

        // --- File upload ---
        var uploadEp = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/files");
        Assert.Contains(uploadEp.Params, p => p.Source == ParamSource.File);
        Assert.Contains(uploadEp.Params, p => p.Source == ParamSource.FormField);

        // --- Security: anonymous ---
        var healthEp = endpoints.First(e => e.RouteTemplate == "/api/health");
        Assert.NotNull(healthEp.Security);
        Assert.True(healthEp.Security.IsAnonymous);

        // --- Security: override scheme ---
        var adminEp = endpoints.First(e => e.RouteTemplate == "/api/admin");
        Assert.NotNull(adminEp.Security);
        Assert.Equal("admin", adminEp.Security.Scheme);

        // --- Security: default bearer ---
        Assert.NotNull(getItem.Security);
        Assert.Equal("bearer", getItem.Security.Scheme);

        // --- Nested type survived ---
        Assert.True(walker.Definitions.ContainsKey("AddressDto"));
        Assert.True(walker.Definitions.ContainsKey("UserDto"));
        var user = walker.Definitions["UserDto"];
        var addrProp = user.Properties.First(p => p.Name == "address");
        Assert.True(addrProp.Type is TsType.TypeRef { Name: "AddressDto" });
        var secAddr = user.Properties.First(p => p.Name == "secondaryAddress");
        Assert.True(secAddr.Type is TsType.Nullable { Inner: TsType.TypeRef { Name: "AddressDto" } });

        // --- NotFoundError survived for multi-response ---
        Assert.True(walker.Definitions.ContainsKey("NotFoundError"));
    }

    // ========== Double round-trip: maximally expressive contract ==========

    /// <summary>
    /// The ultimate lossless round-trip test.
    /// Starts from the most expressive C# contract possible, goes through:
    ///   C# → OpenAPI (json0)
    ///   json0 → import → compile → walk → OpenAPI (json1)   [round 1]
    ///   json1 → import → compile → walk → OpenAPI (json2)   [round 2]
    /// Asserts:
    ///   1. json1 ≡ json2 (structural equality — schemas, paths, properties, extensions)
    ///   2. Deep property-level fidelity on the final C# model (types, formats, CSharpType, deprecated, etc.)
    /// </summary>
    [Fact]
    public void MaximalContract_DoublRoundTrip_IsLossless()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            // --- Enum ---
            public enum Priority { Low, Medium, High, Critical }

            // --- Branded value objects ---
            [RivetType]
            public sealed record Email(string Value);

            [RivetType]
            public sealed record Uprn(string Value);

            [RivetType]
            public sealed record Quantity(int Value);

            // --- Generic type ---
            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            // --- All primitives + deprecated + nullable variants ---
            [RivetType]
            public sealed record KitchenSinkDto(
                // Primitives
                string Name,
                int IntVal,
                uint UintVal,
                long LongVal,
                ulong UlongVal,
                short ShortVal,
                ushort UshortVal,
                byte ByteVal,
                sbyte SbyteVal,
                float FloatVal,
                double DoubleVal,
                decimal DecimalVal,
                bool BoolVal,
                DateTime DateTimeVal,
                DateTimeOffset DateTimeOffsetVal,
                DateOnly DateOnlyVal,
                Guid GuidVal,
                // Nullable primitives
                string? NullableString,
                int? NullableInt,
                bool? NullableBool,
                Guid? NullableGuid,
                DateTime? NullableDateTime,
                DateTimeOffset? NullableDateTimeOffset,
                // Collections
                List<string> Tags,
                List<int> Scores,
                Dictionary<string, string> Metadata,
                Dictionary<string, int> Counts,
                List<Guid> IdList,
                // Brand + enum refs
                Email AuthorEmail,
                Quantity ItemQuantity,
                Priority CurrentPriority,
                // Nested type refs
                AddressDto HomeAddress,
                AddressDto? WorkAddress,
                // Deprecated
                [property: Obsolete] string LegacyField);

            // --- Nested type ---
            [RivetType]
            public sealed record AddressDto(string Line1, string? Line2, string City, string PostCode);

            // --- Error types for multi-response ---
            [RivetType]
            public sealed record NotFoundError(string Message);

            [RivetType]
            public sealed record ValidationError(string Message, Dictionary<string, string> Errors);

            // --- Input types ---
            [RivetType]
            public sealed record CreateItemInput(
                string Name, Email AuthorEmail, Priority CurrentPriority, AddressDto HomeAddress);

            [RivetType]
            public sealed record SearchInput(string Query, int Limit, int? Offset);

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title, int? PageCount);

            [RivetType]
            public sealed record UploadResult(string Url, Guid FileId);

            // --- Second type for generic multi-instantiation ---
            [RivetType]
            public sealed record UserDto(string Id, string Name, Email Email, Uprn? Uprn);

            // === Contracts ===

            [RivetContract]
            public static class ItemsContract
            {
                // GET — output only, path param, description
                public static readonly Define GetItem =
                    Define.Get<KitchenSinkDto>("/api/items/{id}")
                        .Description("Retrieve a single item by its unique ID");

                // GET — query params, generic return
                public static readonly Define SearchItems =
                    Define.Get<SearchInput, PagedResult<KitchenSinkDto>>("/api/items");

                // POST — body input + output + status override + multi-response
                public static readonly Define CreateItem =
                    Define.Post<CreateItemInput, KitchenSinkDto>("/api/items")
                        .Status(201)
                        .Returns<ValidationError>(422, "Validation failed");

                // PUT — input + output, path param
                public static readonly Define UpdateItem =
                    Define.Put<CreateItemInput, KitchenSinkDto>("/api/items/{id}");

                // DELETE — void + 204 + multi-response
                public static readonly Define DeleteItem =
                    Define.Delete("/api/items/{id}")
                        .Status(204)
                        .Returns<NotFoundError>(404, "Item not found");

                // PATCH — input + void
                public static readonly Define PatchItem =
                    Define.Patch<CreateItemInput>("/api/items/{id}")
                        .Status(204);
            }

            [RivetContract]
            public static class UsersContract
            {
                // GET with generic (second instantiation of PagedResult<T>)
                public static readonly Define ListUsers =
                    Define.Get<PagedResult<UserDto>>("/api/users")
                        .Description("List all users with pagination");

                // GET with multi-response
                public static readonly Define GetUser =
                    Define.Get<UserDto>("/api/users/{userId}")
                        .Returns<NotFoundError>(404, "User not found");
            }

            [RivetContract]
            public static class FilesContract
            {
                // POST — file upload with FormField
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files")
                        .Status(201);
            }

            [RivetContract]
            public static class HealthContract
            {
                // GET — anonymous void
                public static readonly Define Check =
                    Define.Get("/api/health")
                        .Anonymous()
                        .Description("Health check endpoint");
            }

            [RivetContract]
            public static class AdminContract
            {
                // DELETE — custom security, void
                public static readonly Define Purge =
                    Define.Delete("/api/admin/cache")
                        .Status(204)
                        .Secure("admin");
            }
            """;

        const string security = "bearer";

        // ───── Pipeline: C# → OpenAPI → C# → OpenAPI → C# → OpenAPI ─────

        // Step 0: Original C# → OpenAPI
        var comp0 = CompilationHelper.CreateCompilation(source);
        var (disc0, wlk0) = CompilationHelper.DiscoverAndWalk(comp0);
        var eps0 = CompilationHelper.WalkContracts(comp0, disc0, wlk0);
        var secCfg = SecurityParser.Parse(security);
        var json0 = OpenApiEmitter.Emit(eps0, wlk0.Definitions, wlk0.Brands, wlk0.Enums, secCfg);

        // Step 1 (round 1): OpenAPI → import → compile → walk → OpenAPI
        var import1 = OpenApiImporter.Import(json0, new ImportOptions("RoundTrip", security));

        var comp1 = CompilationHelper.CreateCompilationFromMultiple(
            import1.Files.Select(f => f.Content).ToArray());
        var (disc1, wlk1) = CompilationHelper.DiscoverAndWalk(comp1);
        var eps1 = CompilationHelper.WalkContracts(comp1, disc1, wlk1);
        var json1 = OpenApiEmitter.Emit(eps1, wlk1.Definitions, wlk1.Brands, wlk1.Enums, secCfg);

        // Step 2 (round 2): OpenAPI → import → compile → walk → OpenAPI
        var import2 = OpenApiImporter.Import(json1, new ImportOptions("RoundTrip", security));
        var comp2 = CompilationHelper.CreateCompilationFromMultiple(
            import2.Files.Select(f => f.Content).ToArray());
        var (disc2, wlk2) = CompilationHelper.DiscoverAndWalk(comp2);
        var eps2 = CompilationHelper.WalkContracts(comp2, disc2, wlk2);
        var json2 = OpenApiEmitter.Emit(eps2, wlk2.Definitions, wlk2.Brands, wlk2.Enums, secCfg);

        // ───── Assertion group 1: OpenAPI JSON idempotency (json1 ≡ json2) ─────

        var doc1 = JsonSerializer.Deserialize<JsonElement>(json1);
        var doc2 = JsonSerializer.Deserialize<JsonElement>(json2);

        // Same schema names
        var schemas1 = doc1.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        var schemas2 = doc2.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(schemas1, schemas2);

        // Same path sets
        var paths1 = doc1.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        var paths2 = doc2.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(paths1, paths2);

        // Deep structural comparison: every schema property and extension must match
        foreach (var schemaName in schemas1)
        {
            var s1 = doc1.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
            var s2 = doc2.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
            Assert.Equal(
                JsonSerializer.Serialize(s1, new JsonSerializerOptions { WriteIndented = true }),
                JsonSerializer.Serialize(s2, new JsonSerializerOptions { WriteIndented = true }));
        }

        // Deep path comparison: every operation must match
        foreach (var pathName in paths1)
        {
            var p1 = doc1.GetProperty("paths").GetProperty(pathName);
            var p2 = doc2.GetProperty("paths").GetProperty(pathName);
            Assert.Equal(
                JsonSerializer.Serialize(p1, new JsonSerializerOptions { WriteIndented = true }),
                JsonSerializer.Serialize(p2, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ───── Assertion group 2: Endpoint count and routes ─────

        // 6 Items + 2 Users + 1 Files + 1 Health + 1 Admin = 11
        Assert.Equal(11, eps2.Count);

        Assert.Contains(eps2, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(eps2, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items");
        Assert.Contains(eps2, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/items");
        Assert.Contains(eps2, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(eps2, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(eps2, e => e.HttpMethod == "PATCH" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(eps2, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.Contains(eps2, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Contains(eps2, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/files");
        Assert.Contains(eps2, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
        Assert.Contains(eps2, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/admin/cache");

        // ───── Assertion group 3: KitchenSinkDto property types (x-rivet-csharp-type fidelity) ─────

        Assert.True(wlk2.Definitions.ContainsKey("KitchenSinkDto"));
        var sink = wlk2.Definitions["KitchenSinkDto"];

        void AssertPrimitive(string propName, string tsName, string? format, string? csharpType)
        {
            var prop = sink.Properties.First(p => p.Name == propName);
            var prim = Assert.IsType<TsType.Primitive>(prop.Type);
            Assert.Equal(tsName, prim.Name);
            Assert.Equal(format, prim.Format);
            Assert.Equal(csharpType, prim.CSharpType);
        }

        void AssertNullablePrimitive(string propName, string tsName, string? format, string? csharpType)
        {
            var prop = sink.Properties.First(p => p.Name == propName);
            Assert.IsType<TsType.Nullable>(prop.Type);
            var inner = Assert.IsType<TsType.Primitive>(((TsType.Nullable)prop.Type).Inner);
            Assert.Equal(tsName, inner.Name);
            Assert.Equal(format, inner.Format);
            Assert.Equal(csharpType, inner.CSharpType);
        }

        // Plain primitives — default C# types have null CSharpType
        AssertPrimitive("name", "string", null, null);
        AssertPrimitive("intVal", "number", "int32", null);
        AssertPrimitive("longVal", "number", "int64", null);
        AssertPrimitive("floatVal", "number", "float", null);
        AssertPrimitive("doubleVal", "number", "double", null);
        AssertPrimitive("boolVal", "boolean", null, null);
        AssertPrimitive("dateTimeVal", "string", "date-time", null);
        AssertPrimitive("dateOnlyVal", "string", "date", null);
        AssertPrimitive("guidVal", "string", "uuid", null);

        // x-rivet-csharp-type primitives — non-default C# types survive via extension
        AssertPrimitive("uintVal", "number", "uint32", "uint");
        AssertPrimitive("ulongVal", "number", "uint64", "ulong");
        AssertPrimitive("shortVal", "number", "int16", "short");
        AssertPrimitive("ushortVal", "number", "uint16", "ushort");
        AssertPrimitive("byteVal", "number", "uint8", "byte");
        AssertPrimitive("sbyteVal", "number", "int8", "sbyte");
        AssertPrimitive("decimalVal", "number", "decimal", null);
        AssertPrimitive("dateTimeOffsetVal", "string", "date-time", "DateTimeOffset");

        // Nullable primitives
        AssertNullablePrimitive("nullableString", "string", null, null);
        AssertNullablePrimitive("nullableInt", "number", "int32", null);
        AssertNullablePrimitive("nullableBool", "boolean", null, null);
        AssertNullablePrimitive("nullableGuid", "string", "uuid", null);
        AssertNullablePrimitive("nullableDateTime", "string", "date-time", null);
        AssertNullablePrimitive("nullableDateTimeOffset", "string", "date-time", "DateTimeOffset");

        // Arrays
        var tags = sink.Properties.First(p => p.Name == "tags");
        Assert.True(tags.Type is TsType.Array { Element: TsType.Primitive { Name: "string" } });

        var scores = sink.Properties.First(p => p.Name == "scores");
        Assert.True(scores.Type is TsType.Array { Element: TsType.Primitive { Name: "number" } });

        var idList = sink.Properties.First(p => p.Name == "idList");
        Assert.True(idList.Type is TsType.Array { Element: TsType.Primitive { Name: "string", Format: "uuid" } });

        // Dictionaries
        var meta = sink.Properties.First(p => p.Name == "metadata");
        Assert.True(meta.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "string" } });

        var counts = sink.Properties.First(p => p.Name == "counts");
        Assert.True(counts.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "number" } });

        // Brand references
        var emailRef = sink.Properties.First(p => p.Name == "authorEmail");
        Assert.True(
            emailRef.Type is TsType.Brand { Name: "Email" } or TsType.TypeRef { Name: "Email" },
            $"Expected Email brand/ref but got {emailRef.Type}");

        var qtyRef = sink.Properties.First(p => p.Name == "itemQuantity");
        Assert.True(
            qtyRef.Type is TsType.Brand { Name: "Quantity" } or TsType.TypeRef { Name: "Quantity" },
            $"Expected Quantity brand/ref but got {qtyRef.Type}");

        // Enum reference
        var priorityRef = sink.Properties.First(p => p.Name == "currentPriority");
        Assert.True(priorityRef.Type is TsType.TypeRef { Name: "Priority" },
            $"Expected TypeRef(Priority) but got {priorityRef.Type}");

        // Nested type refs
        var homeAddr = sink.Properties.First(p => p.Name == "homeAddress");
        Assert.True(homeAddr.Type is TsType.TypeRef { Name: "AddressDto" });

        var workAddr = sink.Properties.First(p => p.Name == "workAddress");
        Assert.True(workAddr.Type is TsType.Nullable { Inner: TsType.TypeRef { Name: "AddressDto" } });

        // Deprecated
        var legacy = sink.Properties.First(p => p.Name == "legacyField");
        Assert.True(legacy.IsDeprecated, "legacyField should survive as deprecated after 2 round-trips");

        // ───── Assertion group 4: Brands survived ─────

        Assert.True(wlk2.Brands.ContainsKey("Email"),
            $"Email brand lost. Brands: [{string.Join(", ", wlk2.Brands.Keys)}]");
        Assert.True(wlk2.Brands.ContainsKey("Uprn"),
            $"Uprn brand lost. Brands: [{string.Join(", ", wlk2.Brands.Keys)}]");
        Assert.True(wlk2.Brands.ContainsKey("Quantity"),
            $"Quantity brand lost. Brands: [{string.Join(", ", wlk2.Brands.Keys)}]");

        // ───── Assertion group 5: Enum survived with all members ─────

        Assert.True(wlk2.Enums.ContainsKey("Priority"));
        var prioEnum = wlk2.Enums["Priority"];
        Assert.Contains("Low", prioEnum.Members);
        Assert.Contains("Medium", prioEnum.Members);
        Assert.Contains("High", prioEnum.Members);
        Assert.Contains("Critical", prioEnum.Members);

        // ───── Assertion group 6: Generic template survived ─────

        Assert.True(wlk2.Definitions.ContainsKey("PagedResult"));
        var paged = wlk2.Definitions["PagedResult"];
        Assert.True(paged.TypeParameters.Count > 0, "PagedResult should retain type parameters");
        Assert.Equal("T", paged.TypeParameters[0]);
        var itemsProp = paged.Properties.First(p => p.Name == "items");
        Assert.True(itemsProp.Type is TsType.Array { Element: TsType.TypeParam { Name: "T" } },
            $"Expected Array(TypeParam(T)) but got {itemsProp.Type}");

        // Both generic instantiations survived
        var searchEp = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items");
        Assert.IsType<TsType.Generic>(searchEp.ReturnType);
        Assert.Equal("PagedResult", ((TsType.Generic)searchEp.ReturnType!).Name);

        var usersListEp = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.IsType<TsType.Generic>(usersListEp.ReturnType);
        Assert.Equal("PagedResult", ((TsType.Generic)usersListEp.ReturnType!).Name);

        // ───── Assertion group 7: Descriptions survived ─────

        var getItem = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/items/{id}");
        Assert.Equal("Retrieve a single item by its unique ID", getItem.Description);

        var listUsers = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.Equal("List all users with pagination", listUsers.Description);

        var healthCheck = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
        Assert.Equal("Health check endpoint", healthCheck.Description);

        // ───── Assertion group 8: Security survived ─────

        // Anonymous
        Assert.NotNull(healthCheck.Security);
        Assert.True(healthCheck.Security.IsAnonymous);

        // Custom scheme
        var adminPurge = eps2.First(e => e.RouteTemplate == "/api/admin/cache");
        Assert.NotNull(adminPurge.Security);
        Assert.Equal("admin", adminPurge.Security.Scheme);

        // Default bearer
        Assert.NotNull(getItem.Security);
        Assert.Equal("bearer", getItem.Security.Scheme);

        // ───── Assertion group 9: HTTP verbs + status codes + multi-response ─────

        // DELETE 204 + 404
        var deleteItem = eps2.First(e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/items/{id}");
        Assert.Null(deleteItem.ReturnType);
        Assert.Contains(deleteItem.Responses, r => r.StatusCode == 204);
        Assert.Contains(deleteItem.Responses, r => r.StatusCode == 404);

        // POST 201 + 422
        var createItem = eps2.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/items");
        Assert.Contains(createItem.Responses, r => r.StatusCode == 201);
        Assert.Contains(createItem.Responses, r => r.StatusCode == 422);

        // PATCH 204
        var patchItem = eps2.First(e => e.HttpMethod == "PATCH" && e.RouteTemplate == "/api/items/{id}");
        Assert.Contains(patchItem.Responses, r => r.StatusCode == 204);

        // GET user multi-response (200 + 404)
        var getUser = eps2.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Contains(getUser.Responses, r => r.StatusCode == 200);
        Assert.Contains(getUser.Responses, r => r.StatusCode == 404);

        // ───── Assertion group 10: Parameters (types + nullability) ─────

        // Path params
        var getItemParam = Assert.Single(getItem.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", getItemParam.Name);

        var getUserParam = Assert.Single(getUser.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("userId", getUserParam.Name);

        // Query params — types and nullability must survive
        var searchParams = searchEp.Params.Where(p => p.Source == ParamSource.Query).ToList();
        Assert.True(searchParams.Count >= 3, $"Search should have query + limit + offset, got {searchParams.Count}");

        var queryParam = searchParams.First(p => p.Name == "query");
        Assert.True(queryParam.Type is TsType.Primitive { Name: "string" },
            $"query param should be string, got {queryParam.Type}");

        var limitParam = searchParams.First(p => p.Name == "limit");
        Assert.True(limitParam.Type is TsType.Primitive { Name: "number" },
            $"limit param should be number, got {limitParam.Type}");

        // CRITICAL: nullable query param must survive as Nullable
        var offsetParam = searchParams.First(p => p.Name == "offset");
        Assert.True(offsetParam.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } },
            $"offset param should be Nullable(number) — optional query param must survive as nullable, got {offsetParam.Type}");

        // File upload: File + FormField params with types
        var uploadEp = eps2.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/files");
        Assert.Contains(uploadEp.Params, p => p.Source == ParamSource.File && p.Name == "document");

        var titleFormField = Assert.Single(uploadEp.Params, p => p.Source == ParamSource.FormField && p.Name == "title");
        Assert.True(titleFormField.Type is TsType.Primitive { Name: "string" },
            $"title FormField should be string, got {titleFormField.Type}");

        // CRITICAL: nullable FormField must survive as Nullable
        var pageCountField = Assert.Single(uploadEp.Params, p => p.Source == ParamSource.FormField && p.Name == "pageCount");
        Assert.True(pageCountField.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } },
            $"pageCount FormField should be Nullable(number) — optional form field must survive as nullable, got {pageCountField.Type}");

        // ───── Assertion group 11: Nested types survived ─────

        Assert.True(wlk2.Definitions.ContainsKey("AddressDto"));
        var addr = wlk2.Definitions["AddressDto"];
        Assert.Equal(4, addr.Properties.Count);
        Assert.Contains(addr.Properties, p => p.Name == "line1");
        Assert.Contains(addr.Properties, p => p.Name == "city");
        var line2 = addr.Properties.First(p => p.Name == "line2");
        Assert.IsType<TsType.Nullable>(line2.Type);

        Assert.True(wlk2.Definitions.ContainsKey("UserDto"));
        var user = wlk2.Definitions["UserDto"];
        var uprnProp = user.Properties.First(p => p.Name == "uprn");
        Assert.True(uprnProp.Type is TsType.Nullable,
            $"Uprn should be nullable but got {uprnProp.Type}");

        Assert.True(wlk2.Definitions.ContainsKey("NotFoundError"));
        Assert.True(wlk2.Definitions.ContainsKey("ValidationError"));
        var valErr = wlk2.Definitions["ValidationError"];
        var errorsDict = valErr.Properties.First(p => p.Name == "errors");
        Assert.True(errorsDict.Type is TsType.Dictionary { Value: TsType.Primitive { Name: "string" } });

        // ───── Assertion group 12: All $refs resolve ─────

        var allRefs = new List<string>();
        CollectRefs(doc2, allRefs);
        var allSchemaNames = new HashSet<string>(schemas2);
        foreach (var refValue in allRefs)
        {
            if (refValue.StartsWith("#/components/schemas/"))
            {
                var schemaName = refValue["#/components/schemas/".Length..];
                Assert.True(allSchemaNames.Contains(schemaName),
                    $"Broken $ref after double round-trip: {refValue}");
            }
        }
    }

    // ========== Isolated bug-detection tests ==========

    /// <summary>
    /// Verifies that optional query params (required: false) produce nullable C# types
    /// in the generated import code, and that nullability survives the full round-trip.
    /// Bug: ContractBuilder.ResolveParamInputType stores IsRequired but CSharpWriter ignores it.
    /// </summary>
    [Fact]
    public void OptionalQueryParam_SurvivesImport_AsNullable()
    {
        // Hand-crafted OpenAPI with an optional query param
        var openApiJson = """
            {
                "openapi": "3.1.0",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/items": {
                        "get": {
                            "operationId": "searchItems",
                            "parameters": [
                                {
                                    "name": "query",
                                    "in": "query",
                                    "required": true,
                                    "schema": { "type": "string" }
                                },
                                {
                                    "name": "limit",
                                    "in": "query",
                                    "required": false,
                                    "schema": { "type": "integer", "format": "int32" }
                                },
                                {
                                    "name": "cursor",
                                    "in": "query",
                                    "required": false,
                                    "schema": { "type": "string" }
                                }
                            ],
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": {
                                        "application/json": {
                                            "schema": {
                                                "$ref": "#/components/schemas/ItemDto"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "ItemDto": {
                            "type": "object",
                            "properties": {
                                "id": { "type": "string" }
                            },
                            "required": ["id"]
                        }
                    }
                }
            }
            """;

        var importResult = OpenApiImporter.Import(openApiJson, new ImportOptions("Test"));

        // Find the generated input type — should contain nullable params
        var inputFile = importResult.Files.First(f =>
            f.Content.Contains("SearchItemsInput") || f.Content.Contains("limit"));
        var content = inputFile.Content;

        // "limit" is required:false → must be int? not int
        Assert.Contains("int?", content);
        // "cursor" is required:false → must be string? not string
        Assert.Contains("string?", content);

        // Full compile + walk: verify the nullable types survive into TsType model
        var compilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var searchEp = endpoints.First(e => e.HttpMethod == "GET");
        var queryParams = searchEp.Params.Where(p => p.Source == ParamSource.Query).ToList();

        // Required param stays non-nullable
        var queryParam = queryParams.First(p => p.Name == "query");
        Assert.True(queryParam.Type is TsType.Primitive { Name: "string" },
            $"Required query param should be string, got {queryParam.Type}");

        // Optional params must be nullable
        var limitParam = queryParams.First(p => p.Name == "limit");
        Assert.True(limitParam.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } },
            $"Optional limit should be Nullable(number), got {limitParam.Type}");

        var cursorParam = queryParams.First(p => p.Name == "cursor");
        Assert.True(cursorParam.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } },
            $"Optional cursor should be Nullable(string), got {cursorParam.Type}");
    }

    /// <summary>
    /// Verifies that nullable fields inside inline objects (InlineObject TsType)
    /// are NOT marked as required in the emitted OpenAPI schema.
    /// Bug: BuildInlineObjectSchema unconditionally adds all fields to required[].
    /// </summary>
    [Fact]
    public void InlineObject_NullableFields_NotMarkedRequired()
    {
        // Create an endpoint with an InlineObject return type that has nullable fields
        var inlineType = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("description", new TsType.Nullable(new TsType.Primitive("string"))),
            ("count", new TsType.Primitive("number", "int32")),
            ("limit", new TsType.Nullable(new TsType.Primitive("number", "int32"))),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                Name: "getStatus",
                HttpMethod: "GET",
                RouteTemplate: "/api/status",
                Params: [],
                ReturnType: inlineType,
                ControllerName: "Status",
                Responses: [new(200, inlineType, null)],
                Description: null,
                Security: null,
                InputTypeName: null)
        };

        var json = OpenApiEmitter.Emit(
            endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType.StringUnion>(),
            null);

        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var responseSchema = doc.GetProperty("paths")
            .GetProperty("/api/status")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        // Check the required array — should only contain non-nullable fields
        var requiredArray = responseSchema.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        Assert.Contains("name", requiredArray);
        Assert.Contains("count", requiredArray);
        Assert.DoesNotContain("description", requiredArray);
        Assert.DoesNotContain("limit", requiredArray);
    }

    /// <summary>
    /// JsonObject, JsonArray, and JsonNode must emit correct x-rivet-csharp-type extensions
    /// and import back as the correct C# types (not Dictionary/List/JsonElement).
    /// </summary>
    [Fact]
    public void JsonDynamicTypes_EmitAndImport_Lossless()
    {
        // Build the TsType model directly — avoids Roslyn compilation needing System.Text.Json.Nodes
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["DynamicDto"] = new("DynamicDto", [], [
                new("condition", new TsType.Dictionary(new TsType.Primitive("unknown", CSharpType: "JsonObject")), false),
                new("items", new TsType.Array(new TsType.Primitive("unknown", CSharpType: "JsonArray")), false),
                new("payload", new TsType.Primitive("unknown", CSharpType: "JsonNode"), false),
                new("raw", new TsType.Primitive("unknown"), false),
                new("nullableCondition", new TsType.Nullable(new TsType.Dictionary(new TsType.Primitive("unknown", CSharpType: "JsonObject"))), true),
                new("nullablePayload", new TsType.Nullable(new TsType.Primitive("unknown", CSharpType: "JsonNode")), true),
            ]),
        };

        var endpoints = new List<TsEndpointDefinition>
        {
            new("get", "GET", "/api/dynamic/{id}",
                [new("id", new TsType.Primitive("string"), ParamSource.Route)],
                new TsType.TypeRef("DynamicDto"), "dynamic",
                [new(200, new TsType.TypeRef("DynamicDto"), null)]),
        };

        // Emit OpenAPI
        var json = OpenApiEmitter.Emit(endpoints, definitions,
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType.StringUnion>(), null);

        // Verify OpenAPI JSON has x-rivet-csharp-type on the right schemas
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var props = doc.GetProperty("components").GetProperty("schemas")
            .GetProperty("DynamicDto").GetProperty("properties");

        // JsonObject → type: object + x-rivet-csharp-type: JsonObject
        var condition = props.GetProperty("condition");
        Assert.Equal("object", condition.GetProperty("type").GetString());
        Assert.Equal("JsonObject", condition.GetProperty("x-rivet-csharp-type").GetString());

        // JsonArray → type: array + x-rivet-csharp-type: JsonArray
        var items = props.GetProperty("items");
        Assert.Equal("array", items.GetProperty("type").GetString());
        Assert.Equal("JsonArray", items.GetProperty("x-rivet-csharp-type").GetString());

        // JsonNode → x-rivet-csharp-type: JsonNode (no type field)
        var payload = props.GetProperty("payload");
        Assert.Equal("JsonNode", payload.GetProperty("x-rivet-csharp-type").GetString());
        Assert.False(payload.TryGetProperty("type", out _), "JsonNode should not have a type field");

        // JsonElement → bare {} (no x-rivet-csharp-type)
        var raw = props.GetProperty("raw");
        Assert.False(raw.TryGetProperty("x-rivet-csharp-type", out _),
            "JsonElement is the default — should not have x-rivet-csharp-type");

        // Nullable JsonObject → nullable + x-rivet-csharp-type
        var nullCond = props.GetProperty("nullableCondition");
        Assert.True(nullCond.GetProperty("nullable").GetBoolean());
        Assert.Equal("JsonObject", nullCond.GetProperty("x-rivet-csharp-type").GetString());

        // Import: OpenAPI → C# — verify correct types
        var importResult = OpenApiImporter.Import(json, new ImportOptions("Test"));
        var dtoFile = importResult.Files.First(f => f.Content.Contains("DynamicDto"));

        Assert.Contains("System.Text.Json.Nodes.JsonObject Condition", dtoFile.Content);
        Assert.Contains("System.Text.Json.Nodes.JsonArray Items", dtoFile.Content);
        Assert.Contains("System.Text.Json.Nodes.JsonNode Payload", dtoFile.Content);
        Assert.Contains("System.Text.Json.JsonElement Raw", dtoFile.Content);
        Assert.Contains("System.Text.Json.Nodes.JsonObject? NullableCondition", dtoFile.Content);
        Assert.Contains("System.Text.Json.Nodes.JsonNode? NullablePayload", dtoFile.Content);
    }

    /// <summary>
    /// Nullable properties must NOT be in the required array.
    /// required + nullable is semantically contradictory and breaks most OpenAPI consumers.
    /// </summary>
    [Fact]
    public void NullableProperties_NotInRequiredArray()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PersonDto(
                string Name,
                string? Bio,
                int Age,
                int? Score,
                Guid Id,
                Guid? OptionalRef,
                DateTime CreatedAt,
                DateTime? DeletedAt);

            [RivetContract]
            public static class PeopleContract
            {
                public static readonly Define Get =
                    Define.Get<PersonDto>("/api/people/{id}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var schema = doc.GetProperty("components").GetProperty("schemas").GetProperty("PersonDto");
        var required = schema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var props = schema.GetProperty("properties");

        // Non-nullable fields MUST be in required
        Assert.Contains("name", required);
        Assert.Contains("age", required);
        Assert.Contains("id", required);
        Assert.Contains("createdAt", required);

        // Nullable fields MUST NOT be in required
        Assert.DoesNotContain("bio", required);
        Assert.DoesNotContain("score", required);
        Assert.DoesNotContain("optionalRef", required);
        Assert.DoesNotContain("deletedAt", required);

        // Nullable fields should still have nullable: true
        Assert.True(props.GetProperty("bio").GetProperty("nullable").GetBoolean());
        Assert.True(props.GetProperty("score").GetProperty("nullable").GetBoolean());
        Assert.True(props.GetProperty("optionalRef").GetProperty("nullable").GetBoolean());
        Assert.True(props.GetProperty("deletedAt").GetProperty("nullable").GetBoolean());
    }

    /// <summary>
    /// CancellationToken must not leak into generated endpoints as a parameter —
    /// not as a query param, not as a form field, not anywhere.
    /// </summary>
    [Fact]
    public void CancellationToken_StrippedFromEndpoints()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResult(Guid Id);

            [Route("api/files")]
            public sealed class FilesController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(UploadResult), 201)]
                public Task<IActionResult> Upload(
                    IFormFile file,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var ep = Assert.Single(endpoints);

        // Only file param should exist — ct must be stripped
        Assert.Single(ep.Params);
        Assert.Equal(ParamSource.File, ep.Params[0].Source);
        Assert.Equal("file", ep.Params[0].Name);

        // Verify ct does not appear anywhere in the OpenAPI output
        var typeFileMap = new Dictionary<string, string>();
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        Assert.DoesNotContain("\"ct\"", json);
        Assert.DoesNotContain("CancellationToken", json);
    }

    // ========== Comprehensive C# → OpenAPI → C# round-trip ==========

    /// <summary>
    /// Belt-and-braces C# → OpenAPI → C# round-trip test.
    /// Starts from a maximally expressive C# contract covering every feature,
    /// round-trips through OpenAPI, and deeply asserts the resulting C# model:
    /// endpoints, params, return types, type definitions, brands, enums, generics,
    /// nullable fields, collections, dictionaries, file uploads, multi-response,
    /// security, descriptions, deprecated fields, and status code overrides.
    /// </summary>
    [Fact]
    public void Comprehensive_CSharp_To_OpenApi_To_CSharp_ModelFidelity()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            // --- Enum ---
            public enum Priority { Low, Medium, High, Critical }

            // --- Branded value objects ---
            [RivetType]
            public sealed record Email(string Value);

            [RivetType]
            public sealed record Quantity(int Value);

            // --- Generic ---
            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

            // --- Types ---
            [RivetType]
            public sealed record AddressDto(string Line1, string? Line2, string City, string PostCode);

            [RivetType]
            public sealed record TaskDto(
                Guid Id,
                string Title,
                int Count,
                long BigNumber,
                decimal Price,
                double Rate,
                bool IsActive,
                DateTime CreatedAt,
                DateTimeOffset UpdatedAt,
                DateOnly DueDate,
                string? Description,
                int? OptionalCount,
                Guid? OptionalId,
                Email AuthorEmail,
                Quantity ItemQuantity,
                Priority CurrentPriority,
                AddressDto HomeAddress,
                AddressDto? WorkAddress,
                List<string> Tags,
                List<Guid> IdList,
                Dictionary<string, string> Metadata,
                Dictionary<string, int> Counts,
                [property: Obsolete] string LegacyField);

            [RivetType]
            public sealed record NotFoundError(string Message);

            [RivetType]
            public sealed record ValidationError(string Message, Dictionary<string, string> Errors);

            [RivetType]
            public sealed record CreateTaskInput(string Title, Email AuthorEmail, Priority CurrentPriority, AddressDto Address);

            [RivetType]
            public sealed record SearchInput(string Query, int Limit, int? Offset);

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url, Guid FileId);

            [RivetType]
            public sealed record UserDto(string Id, string Name, Email Email);

            // === Contracts ===

            [RivetContract]
            public static class TasksContract
            {
                // GET — output only, path param
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Retrieve a single task");

                // GET — query params, generic return
                public static readonly Define SearchTasks =
                    Define.Get<SearchInput, PagedResult<TaskDto>>("/api/tasks");

                // POST — body + output + status + multi-response
                public static readonly Define CreateTask =
                    Define.Post<CreateTaskInput, TaskDto>("/api/tasks")
                        .Status(201)
                        .Returns<ValidationError>(422, "Validation failed");

                // PUT — input + output, path param
                public static readonly Define UpdateTask =
                    Define.Put<CreateTaskInput, TaskDto>("/api/tasks/{id}");

                // DELETE — void + 204 + error response
                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}")
                        .Status(204)
                        .Returns<NotFoundError>(404, "Task not found");

                // PATCH — input + void
                public static readonly Define PatchTask =
                    Define.Patch<CreateTaskInput>("/api/tasks/{id}")
                        .Status(204);
            }

            [RivetContract]
            public static class UsersContract
            {
                // GET — second instantiation of PagedResult<T>
                public static readonly Define ListUsers =
                    Define.Get<PagedResult<UserDto>>("/api/users")
                        .Description("List all users");

                // GET — multi-response
                public static readonly Define GetUser =
                    Define.Get<UserDto>("/api/users/{userId}")
                        .Returns<NotFoundError>(404, "User not found");
            }

            [RivetContract]
            public static class FilesContract
            {
                // POST — file upload
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files")
                        .Status(201);
            }

            [RivetContract]
            public static class HealthContract
            {
                // GET — anonymous void
                public static readonly Define Check =
                    Define.Get("/api/health")
                        .Anonymous()
                        .Description("Health check");
            }
            """;

        var (endpoints, walker) = RoundTrip(source, "Bearer");

        // ===== Type definitions survived =====

        Assert.True(walker.Definitions.ContainsKey("TaskDto"), "TaskDto should survive");
        Assert.True(walker.Definitions.ContainsKey("AddressDto"), "AddressDto should survive");
        Assert.True(walker.Definitions.ContainsKey("NotFoundError"), "NotFoundError should survive");
        Assert.True(walker.Definitions.ContainsKey("ValidationError"), "ValidationError should survive");
        Assert.True(walker.Definitions.ContainsKey("CreateTaskInput"), "CreateTaskInput should survive");
        Assert.True(walker.Definitions.ContainsKey("UploadResult"), "UploadResult should survive");
        Assert.True(walker.Definitions.ContainsKey("UserDto"), "UserDto should survive");

        // ===== Brands survived =====

        Assert.True(walker.Brands.ContainsKey("Email"), "Email brand should survive");
        Assert.True(walker.Brands["Email"].Inner is TsType.Primitive { Name: "string" },
            $"Email brand inner should be string, got {walker.Brands["Email"].Inner}");
        Assert.True(walker.Brands.ContainsKey("Quantity"), "Quantity brand should survive");
        Assert.True(walker.Brands["Quantity"].Inner is TsType.Primitive { Name: "number" },
            $"Quantity brand inner should be number, got {walker.Brands["Quantity"].Inner}");

        // ===== Enum survived =====

        Assert.True(walker.Enums.ContainsKey("Priority"), "Priority enum should survive");
        var priorityMembers = walker.Enums["Priority"].Members;
        Assert.Equal(4, priorityMembers.Count);
        Assert.Contains("Low", priorityMembers);
        Assert.Contains("Critical", priorityMembers);

        // ===== TaskDto property types =====

        var taskDef = walker.Definitions["TaskDto"];

        void AssertProp(string name, Func<TsType, bool> check, string expected)
        {
            var prop = taskDef.Properties.FirstOrDefault(p => p.Name == name);
            Assert.True(prop is not null, $"TaskDto should have property '{name}'");
            Assert.True(check(prop.Type), $"TaskDto.{name} expected {expected}, got {prop.Type}");
        }

        // Primitives
        AssertProp("id", t => t is TsType.Primitive { Name: "string", Format: "uuid" }, "string(uuid)");
        AssertProp("title", t => t is TsType.Primitive { Name: "string" }, "string");
        AssertProp("count", t => t is TsType.Primitive { Name: "number", Format: "int32" }, "number(int32)");
        AssertProp("isActive", t => t is TsType.Primitive { Name: "boolean" }, "boolean");
        AssertProp("createdAt", t => t is TsType.Primitive { Name: "string", Format: "date-time" }, "string(date-time)");
        AssertProp("dueDate", t => t is TsType.Primitive { Name: "string", Format: "date" }, "string(date)");

        // Nullable primitives
        AssertProp("description", t => t is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } }, "string | null");
        AssertProp("optionalCount", t => t is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } }, "number | null");
        AssertProp("optionalId", t => t is TsType.Nullable { Inner: TsType.Primitive { Name: "string", Format: "uuid" } }, "string(uuid) | null");

        // Brand refs
        AssertProp("authorEmail", t => t is TsType.Brand { Name: "Email" } or TsType.TypeRef { Name: "Email" }, "Email");
        AssertProp("itemQuantity", t => t is TsType.Brand { Name: "Quantity" } or TsType.TypeRef { Name: "Quantity" }, "Quantity");

        // Enum ref
        AssertProp("currentPriority", t => t is TsType.TypeRef { Name: "Priority" }, "Priority");

        // Nested type refs
        AssertProp("homeAddress", t => t is TsType.TypeRef { Name: "AddressDto" }, "AddressDto");
        AssertProp("workAddress", t => t is TsType.Nullable { Inner: TsType.TypeRef { Name: "AddressDto" } }, "AddressDto | null");

        // Collections
        AssertProp("tags", t => t is TsType.Array { Element: TsType.Primitive { Name: "string" } }, "string[]");
        AssertProp("idList", t => t is TsType.Array { Element: TsType.Primitive { Name: "string", Format: "uuid" } }, "string(uuid)[]");

        // Dictionaries
        AssertProp("metadata", t => t is TsType.Dictionary { Value: TsType.Primitive { Name: "string" } }, "Record<string, string>");
        AssertProp("counts", t => t is TsType.Dictionary { Value: TsType.Primitive { Name: "number" } }, "Record<string, number>");

        // Deprecated
        var legacyProp = taskDef.Properties.First(p => p.Name == "legacyField");
        Assert.True(legacyProp.IsDeprecated, "legacyField should be deprecated");

        // ===== AddressDto nullable field =====

        var addrDef = walker.Definitions["AddressDto"];
        var line2Prop = addrDef.Properties.First(p => p.Name == "line2");
        Assert.True(line2Prop.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "string" } },
            $"AddressDto.line2 should be nullable string, got {line2Prop.Type}");

        // ===== Generics survived =====

        // PagedResult<T> should exist as a generic template
        Assert.True(walker.Definitions.ContainsKey("PagedResult"), "PagedResult generic should survive");
        var pagedDef = walker.Definitions["PagedResult"];
        Assert.True(pagedDef.TypeParameters.Count > 0, "PagedResult should have type parameters");

        // ===== Endpoint count =====

        // tasks: getTask, searchTasks, createTask, updateTask, deleteTask, patchTask
        // users: listUsers, getUser
        // files: upload
        // health: check
        Assert.Equal(10, endpoints.Count);

        // ===== GET /api/tasks/{id} =====

        var getTask = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks/{id}");
        Assert.True(getTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"getTask return type should be TaskDto, got {getTask.ReturnType}");
        Assert.Single(getTask.Params, p => p.Source == ParamSource.Route && p.Name == "id");
        Assert.Equal("Retrieve a single task", getTask.Description);

        // ===== GET /api/tasks (search with query params + generic return) =====

        var searchTasks = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks");
        Assert.True(searchTasks.ReturnType is TsType.Generic { Name: "PagedResult" },
            $"searchTasks return type should be PagedResult<TaskDto>, got {searchTasks.ReturnType}");
        var queryParams = searchTasks.Params.Where(p => p.Source == ParamSource.Query).ToList();
        Assert.True(queryParams.Count >= 2, $"searchTasks should have at least 2 query params, got {queryParams.Count}");
        // query should be non-nullable string
        var queryParam = queryParams.FirstOrDefault(p => p.Name == "query");
        Assert.NotNull(queryParam);
        Assert.True(queryParam.Type is TsType.Primitive { Name: "string" },
            $"query param should be string, got {queryParam.Type}");
        // offset should be nullable (int? in source)
        var offsetParam = queryParams.FirstOrDefault(p => p.Name == "offset");
        Assert.NotNull(offsetParam);
        Assert.True(offsetParam.Type is TsType.Nullable { Inner: TsType.Primitive { Name: "number" } },
            $"offset param should be nullable number, got {offsetParam.Type}");

        // ===== POST /api/tasks (201 + multi-response) =====

        var createTask = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.True(createTask.ReturnType is TsType.TypeRef { Name: "TaskDto" },
            $"createTask return type should be TaskDto, got {createTask.ReturnType}");
        Assert.Contains(createTask.Responses, r => r.StatusCode == 201);
        Assert.Contains(createTask.Responses, r => r.StatusCode == 422);
        var validationResp = createTask.Responses.First(r => r.StatusCode == 422);
        Assert.True(validationResp.DataType is TsType.TypeRef { Name: "ValidationError" },
            $"422 response should be ValidationError, got {validationResp.DataType}");

        // ===== DELETE /api/tasks/{id} (void + 204 + 404 error) =====

        var deleteTask = endpoints.First(e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{id}");
        Assert.Null(deleteTask.ReturnType);
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 204);
        Assert.Contains(deleteTask.Responses, r => r.StatusCode == 404);
        var notFoundResp = deleteTask.Responses.First(r => r.StatusCode == 404);
        Assert.True(notFoundResp.DataType is TsType.TypeRef { Name: "NotFoundError" },
            $"404 response should be NotFoundError, got {notFoundResp.DataType}");

        // ===== GET /api/users (second generic instantiation) =====

        var listUsers = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.True(listUsers.ReturnType is TsType.Generic { Name: "PagedResult" },
            $"listUsers return type should be PagedResult<UserDto>, got {listUsers.ReturnType}");
        if (listUsers.ReturnType is TsType.Generic g)
        {
            Assert.Single(g.TypeArguments);
            Assert.True(g.TypeArguments[0] is TsType.TypeRef { Name: "UserDto" },
                $"PagedResult type arg should be UserDto, got {g.TypeArguments[0]}");
        }

        // ===== POST /api/files (file upload) =====

        var upload = endpoints.First(e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/files");
        Assert.True(upload.ReturnType is TsType.TypeRef { Name: "UploadResult" },
            $"upload return type should be UploadResult, got {upload.ReturnType}");
        Assert.Contains(upload.Params, p => p.Source == ParamSource.File);
        Assert.Contains(upload.Responses, r => r.StatusCode == 201);

        // ===== GET /api/health (anonymous void) =====

        var health = endpoints.First(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
        Assert.Null(health.ReturnType);
        Assert.NotNull(health.Security);
        Assert.True(health.Security!.IsAnonymous, "Health endpoint should be anonymous");

        // ===== PATCH /api/tasks/{id} (input + void + 204) =====
        // Known limitation: input-only endpoints (Define.Patch<TInput>) round-trip with the
        // body type visible — OpenAPI doesn't distinguish input-only from output-only.
        // The important thing is the route param and 204 status survive.

        var patch = endpoints.First(e => e.HttpMethod == "PATCH" && e.RouteTemplate == "/api/tasks/{id}");
        Assert.Contains(patch.Responses, r => r.StatusCode == 204);
        Assert.Single(patch.Params, p => p.Source == ParamSource.Route);
    }
}
