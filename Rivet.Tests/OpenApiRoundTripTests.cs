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
        // The input type should be UploadInput (not UploadRequest or anonymous)
        var bodyParam = Assert.Single(upload.Params, p => p.Source == ParamSource.Body);
        Assert.True(bodyParam.Type is TsType.TypeRef { Name: "UploadInput" },
            $"Expected TypeRef(UploadInput) but got {bodyParam.Type}");
        // Note: File/FormField decomposition requires AspNetCore reference in the compilation,
        // which test compilations don't have. The key assertion is that the name is preserved.
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

        // Same routes and methods
        foreach (var ep1 in firstEndpoints)
        {
            var ep3 = thirdEps.FirstOrDefault(e =>
                e.HttpMethod == ep1.HttpMethod && e.RouteTemplate == ep1.RouteTemplate);
            Assert.NotNull(ep3);
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
}
