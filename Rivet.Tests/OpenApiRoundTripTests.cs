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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var securityConfig = security is not null ? SecurityParser.Parse(security) : null;
        var openApiJson = OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);

        // Reverse: OpenAPI → import → compile → walk
        var importResult = OpenApiImporter.Import(
            openApiJson, new ImportOptions("RoundTrip", security));
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (reDiscovered, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);
        var reEndpoints = ContractWalker.Walk(recompilation, rewalker, reDiscovered.ContractTypes);

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

        var (endpoints, _) = RoundTrip(source);

        Assert.Equal(4, endpoints.Count);
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/tasks");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/tasks/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/tasks/{id}");
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

        var (_, walker) = RoundTrip(source);

        Assert.True(walker.Definitions.ContainsKey("TaskDto"));
        Assert.True(walker.Enums.ContainsKey("Priority"));
        Assert.Contains("Low", walker.Enums["Priority"].Members);
        Assert.Contains("Medium", walker.Enums["Priority"].Members);
        Assert.Contains("High", walker.Enums["Priority"].Members);
        // Note: Brands don't survive round-trip — the emitter collapses them to { "type": "string" }
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

        // Route param should exist with correct type
        var idParam = Assert.Single(getTask.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", idParam.Name);
        Assert.IsType<TsType.Primitive>(idParam.Type);
        Assert.Equal("string", ((TsType.Primitive)idParam.Type).Name);
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
        Assert.IsType<TsType.Primitive>(queryParam.Type);
        Assert.Equal("string", ((TsType.Primitive)queryParam.Type).Name);

        var limitParam = Assert.Single(search.Params, p => p.Name == "limit");
        Assert.Equal(ParamSource.Query, limitParam.Source);
        Assert.IsType<TsType.Primitive>(limitParam.Type);
        Assert.Equal("number", ((TsType.Primitive)limitParam.Type).Name);
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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
        var reEndpoints = ContractWalker.Walk(recompilation, reWalker, reDiscovered.ContractTypes);

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
        var firstEps = ContractWalker.Walk(firstCompilation, firstWlk, firstDisc.ContractTypes);
        var firstJson = OpenApiEmitter.Emit(
            firstEps, firstWlk.Definitions, firstWlk.Brands, firstWlk.Enums, null);
        var firstImport = OpenApiImporter.Import(firstJson, new ImportOptions("RoundTrip"));
        var secondCompilation = CompilationHelper.CreateCompilationFromMultiple(
            firstImport.Files.Select(f => f.Content).ToArray());
        var (secondDisc, secondWlk) = CompilationHelper.DiscoverAndWalk(secondCompilation);
        var secondEps = ContractWalker.Walk(secondCompilation, secondWlk, secondDisc.ContractTypes);
        var secondJson = OpenApiEmitter.Emit(
            secondEps, secondWlk.Definitions, secondWlk.Brands, secondWlk.Enums, null);
        var secondImport = OpenApiImporter.Import(secondJson, new ImportOptions("RoundTrip"));
        var thirdCompilation = CompilationHelper.CreateCompilationFromMultiple(
            secondImport.Files.Select(f => f.Content).ToArray());
        var (thirdDisc, thirdWlk) = CompilationHelper.DiscoverAndWalk(thirdCompilation);
        var thirdEps = ContractWalker.Walk(thirdCompilation, thirdWlk, thirdDisc.ContractTypes);

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

            // Return type shape preserved
            if (ep1.ReturnType is not null)
            {
                Assert.NotNull(ep3.ReturnType);
            }
            else
            {
                Assert.Null(ep3.ReturnType);
            }
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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        var schema = doc.GetProperty("components").GetProperty("schemas").GetProperty("MixedDto");
        var props = schema.GetProperty("properties");

        // Flags has x-rivet-csharp-type
        var flags = props.GetProperty("flags");
        Assert.Equal("integer", flags.GetProperty("type").GetString());
        Assert.Equal("int32", flags.GetProperty("format").GetString());
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
        var eps = ContractWalker.Walk(compilation, wlk, disc.ContractTypes);
        var json1 = OpenApiEmitter.Emit(eps, wlk.Definitions, wlk.Brands, wlk.Enums, null);
        var import1 = OpenApiImporter.Import(json1, new ImportOptions("RoundTrip"));
        var recomp1 = CompilationHelper.CreateCompilationFromMultiple(
            import1.Files.Select(f => f.Content).ToArray());
        var (disc2, wlk2) = CompilationHelper.DiscoverAndWalk(recomp1);
        var eps2 = ContractWalker.Walk(recomp1, wlk2, disc2.ContractTypes);
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
}
