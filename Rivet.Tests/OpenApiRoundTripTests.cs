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

    // Note: File upload does NOT survive round-trip because the emitter creates inline
    // multipart/form-data schemas (not $refs), which the importer can't resolve back
    // to named records. File upload is tested via KitchenSinkImportTests and
    // OpenApiImporterTests instead.
}
