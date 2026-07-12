using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// WP-1.2 gap-fill tests (FABLE_REWRITE_PLAN.md), each tied to a FABLE_REVIEW finding.
/// Written test-first: each test was red against the pre-fix production code.
/// </summary>
public sealed class Wp12GapFillTests
{
    // ---------------------------------------------------------------
    // A3 — inherited properties (BaseType chains were never walked)
    // ---------------------------------------------------------------

    [Fact]
    public void A3_WalkedDto_WithBaseClass_IncludesInheritedProperties()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public Guid Id { get; init; }
                public DateTime CreatedAt { get; init; }
            }

            [RivetType]
            public sealed record TaskDto : BaseDto
            {
                public string Name { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var taskDto = walker.Definitions["TaskDto"];
        var names = taskDto.Properties.Select(p => p.Name).ToList();

        Assert.Contains("id", names);
        Assert.Contains("createdAt", names);
        Assert.Contains("name", names);
        // Records' synthesized members (EqualityContract) must not leak in
        Assert.DoesNotContain("equalityContract", names);
        Assert.Equal(3, taskDto.Properties.Count);

        // Inherited property types map correctly
        var id = Assert.Single(taskDto.Properties, p => p.Name == "id");
        Assert.Equal("uuid", Assert.IsType<TsType.Primitive>(id.Type).Format);
    }

    [Fact]
    public void A3_MultiLevelBaseChain_FlattensAllLevels()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record Level0
            {
                public string A { get; init; } = "";
            }

            public abstract record Level1 : Level0
            {
                public string B { get; init; } = "";
            }

            [RivetType]
            public sealed record Level2 : Level1
            {
                public string C { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var names = walker.Definitions["Level2"].Properties.Select(p => p.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("a", names);
        Assert.Contains("b", names);
        Assert.Contains("c", names);
    }

    [Fact]
    public void A3_DerivedOverride_WinsOverBaseProperty()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public virtual string? Label { get; init; }
            }

            [RivetType]
            public sealed record DerivedDto : BaseDto
            {
                // Override tightens nullability — derived declaration must win
                public override string Label { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var label = Assert.Single(
            walker.Definitions["DerivedDto"].Properties,
            p => p.Name == "label"
        );
        // Derived (non-nullable) wins; base (nullable) would be TsType.Nullable
        Assert.IsType<TsType.Primitive>(label.Type);
        Assert.False(label.IsOptional);
    }

    [Fact]
    public void A3_DerivedShadowing_NewProperty_Wins()
    {
        var source = """
            using Rivet;

            namespace Test;

            public record BaseDto
            {
                public string Code { get; init; } = "";
            }

            [RivetType]
            public sealed record DerivedDto : BaseDto
            {
                public new int Code { get; init; }
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var props = walker.Definitions["DerivedDto"].Properties;
        var code = Assert.Single(props, p => p.Name == "code");
        Assert.Equal("number", Assert.IsType<TsType.Primitive>(code.Type).Name);
    }

    [Fact]
    public void A3_PositionalRecordInheritance_IncludesBasePositionalParams()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record AuditedDto(Guid Id, DateTime CreatedAt);

            [RivetType]
            public sealed record OrderDto(Guid Id, DateTime CreatedAt, string Number)
                : AuditedDto(Id, CreatedAt);
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var names = walker.Definitions["OrderDto"].Properties.Select(p => p.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("id", names);
        Assert.Contains("createdAt", names);
        Assert.Contains("number", names);
    }

    [Fact]
    public void A3_GenericBase_SubstitutesTypeArguments()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record Envelope<T>
            {
                public T Payload { get; init; } = default!;
                public int Version { get; init; }
            }

            [RivetType]
            public sealed record MessageDto : Envelope<string>
            {
                public string Topic { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var props = walker.Definitions["MessageDto"].Properties;
        Assert.Equal(3, props.Count);

        // T is substituted with the concrete type argument from the base instantiation
        var payload = Assert.Single(props, p => p.Name == "payload");
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(payload.Type).Name);
    }

    [Fact]
    public void A3_OpenApiSchema_IncludesInheritedProperties_AndRequiredArray()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public Guid Id { get; init; }
                public string? Note { get; init; }
            }

            [RivetType]
            public sealed record TaskDto : BaseDto
            {
                public string Name { get; init; } = "";
            }

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask = Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);

        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("TaskDto");
        var properties = schema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("id", out _));
        Assert.True(properties.TryGetProperty("note", out _));
        Assert.True(properties.TryGetProperty("name", out _));

        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("id", required);
        Assert.Contains("name", required);
        Assert.DoesNotContain("note", required); // nullable → optional
    }

    [Fact]
    public void A3_ContractInput_WithBaseQuery_InheritedQueryParamsSurvive()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record PagedQuery
            {
                public int Page { get; init; }
                public int PageSize { get; init; }
            }

            public sealed record ListTasksQuery : PagedQuery
            {
                public string? Filter { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define List = Define.Get<ListTasksQuery, TaskDto[]>("/api/tasks");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var list = Assert.Single(endpoints);
        var queryParams = list
            .Params.Where(p => p.Source == ParamSource.Query)
            .Select(p => p.Name)
            .ToList();

        Assert.Contains("page", queryParams);
        Assert.Contains("pageSize", queryParams);
        Assert.Contains("filter", queryParams);
    }

    [Fact]
    public void A3_ContractInput_WithBaseRouteProperty_BindsAsRouteParam()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record ByIdQuery
            {
                public Guid Id { get; init; }
            }

            public sealed record GetTaskQuery : ByIdQuery
            {
                public bool IncludeDetails { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define Get = Define.Get<GetTaskQuery, TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var get = Assert.Single(endpoints);
        var id = Assert.Single(get.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, id.Source);
        // Typed from the inherited property, not the string fallback
        Assert.Equal("uuid", Assert.IsType<TsType.Primitive>(id.Type).Format);
    }

    [Fact]
    public void A3_MixedUploadInput_WithBaseFormFields_InheritedFieldsSurvive()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            public abstract record UploadMetadata
            {
                public string Category { get; init; } = "";
            }

            public sealed record UploadRequest : UploadMetadata
            {
                public IFormFile File { get; init; } = default!;
            }

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Upload = Define.Post<UploadRequest, string>("/api/files");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var upload = Assert.Single(endpoints);
        Assert.Single(upload.Params, p => p.Name == "file" && p.Source == ParamSource.File);
        Assert.Single(
            upload.Params,
            p => p.Name == "category" && p.Source == ParamSource.FormField
        );
    }

    [Fact]
    public void A3_RoundTrip_FlattenedShape_SurvivesImport()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public Guid Id { get; init; }
            }

            [RivetType]
            public sealed record TaskDto : BaseDto
            {
                public string Name { get; init; } = "";
            }

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask = Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var importResult = CompilationHelper.Import(doc.RootElement.GetRawText());

        var taskDto = CompilationHelper.FindFile(importResult, "TaskDto.cs");
        Assert.Contains("Id", taskDto);
        Assert.Contains("Name", taskDto);

        // Importer output compiles (flattened shape is self-contained)
        CompilationHelper.CompileImportResult(importResult);
    }

    // ---------------------------------------------------------------
    // A5 — namespace collisions (last-segment-only keying silently merged
    // Foo.Models.Item and Bar.Models.Item)
    // ---------------------------------------------------------------

    [Fact]
    public void A5_SameLastSegment_DifferentNamespaces_BothEmitted_WithLoudDiagnostic()
    {
        var sources = new[]
        {
            """
                using Rivet;
                namespace Foo.Models
                {
                    [RivetType]
                    public sealed record Item(string Name);

                    [RivetType]
                    public sealed record FooWrapper(Item Item);
                }
                """,
            """
                using Rivet;
                namespace Bar.Models
                {
                    [RivetType]
                    public sealed record Item(int Count);

                    [RivetType]
                    public sealed record BarWrapper(Item Item);
                }
                """,
        };

        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var discovered = Rivet.Tool.Analysis.SymbolDiscovery.Discover(compilation);
            walker = Rivet.Tool.Analysis.TypeWalker.Create(compilation, discovered.RivetTypes);
        });

        // Loud diagnostic — never a silent merge
        Assert.Contains("collision", stderr);
        Assert.Contains("Item", stderr);

        // Both shapes emitted under deterministic distinct names; first-walked keeps the short name
        Assert.Single(walker.Definitions["Item"].Properties, p => p.Name == "name");
        Assert.Single(walker.Definitions["Item2"].Properties, p => p.Name == "count");

        // References point at the right disambiguated schema
        var fooRef = Assert.IsType<TsType.TypeRef>(
            Assert.Single(walker.Definitions["FooWrapper"].Properties).Type
        );
        Assert.Equal("Item", fooRef.Name);

        var barRef = Assert.IsType<TsType.TypeRef>(
            Assert.Single(walker.Definitions["BarWrapper"].Properties).Type
        );
        Assert.Equal("Item2", barRef.Name);
    }

    [Fact]
    public void A5_EnumCollision_BothEmitted_WithLoudDiagnostic()
    {
        var sources = new[]
        {
            """
                using Rivet;
                namespace Foo.Models
                {
                    public enum Status { Active, Closed }

                    [RivetType]
                    public sealed record FooDto(Status Status);
                }
                """,
            """
                using Rivet;
                namespace Bar.Models
                {
                    public enum Status { Draft, Published, Archived }

                    [RivetType]
                    public sealed record BarDto(Status Status);
                }
                """,
        };

        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var discovered = Rivet.Tool.Analysis.SymbolDiscovery.Discover(compilation);
            walker = Rivet.Tool.Analysis.TypeWalker.Create(compilation, discovered.RivetTypes);
        });

        Assert.Contains("collision", stderr);

        // Enums used to be keyed by simple name with TryAdd — second silently dropped
        var first = Assert.IsType<TsType.StringUnion>(walker.Enums["Status"]);
        Assert.Equal(2, first.Members.Count);
        var second = Assert.IsType<TsType.StringUnion>(walker.Enums["Status2"]);
        Assert.Equal(3, second.Members.Count);

        // Each DTO references its own enum
        var fooRef = Assert.IsType<TsType.TypeRef>(
            Assert.Single(walker.Definitions["FooDto"].Properties).Type
        );
        Assert.Equal("Status", fooRef.Name);
        var barRef = Assert.IsType<TsType.TypeRef>(
            Assert.Single(walker.Definitions["BarDto"].Properties).Type
        );
        Assert.Equal("Status2", barRef.Name);
    }

    [Fact]
    public void A5_GenericArity_DistinctTypes_BothEmitted()
    {
        // Result and Result<T> are distinct types — the old keying shared "Result"
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Result(bool Ok);

            [RivetType]
            public sealed record Result<T>(bool Ok, T Value);
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var discovered = Rivet.Tool.Analysis.SymbolDiscovery.Discover(compilation);
            walker = Rivet.Tool.Analysis.TypeWalker.Create(compilation, discovered.RivetTypes);
        });

        Assert.Contains("collision", stderr);
        Assert.Single(walker.Definitions["Result"].Properties);
        Assert.Equal(2, walker.Definitions["Result2"].Properties.Count);
        Assert.Equal(["T"], walker.Definitions["Result2"].TypeParameters);
    }

    // ---------------------------------------------------------------
    // A6 — [controller]/[action] route tokens
    // ---------------------------------------------------------------

    [Fact]
    public void A6_ControllerToken_IsSubstituted()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/[controller]")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{id}")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult GetTask(string id) => Ok(new TaskDto("x"));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        Assert.Equal("/api/Tasks/{id}", ep.RouteTemplate);
    }

    [Fact]
    public void A6_ActionToken_IsSubstituted()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/[controller]/[action]")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult Latest() => Ok(new TaskDto("x"));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        Assert.Equal("/api/Tasks/Latest", ep.RouteTemplate);
    }

    // ---------------------------------------------------------------
    // A7 — generic [ProducesResponseType<T>] (.NET 7+)
    // ---------------------------------------------------------------

    [Fact]
    public void A7_GenericProducesResponseType_IsRecognized()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);
            public sealed record ErrorDto(string Message);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{id}")]
                [ProducesResponseType<TaskDto>(200)]
                [ProducesResponseType<ErrorDto>(404)]
                public IActionResult GetTask(string id) => Ok(new TaskDto("x"));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);

        // Success return type resolved from the generic attribute
        var ret = Assert.IsType<TsType.TypeRef>(ep.ReturnType);
        Assert.Equal("TaskDto", ret.Name);

        // Both responses present with their body types
        Assert.Equal(2, ep.Responses.Count);
        var ok = Assert.Single(ep.Responses, r => r.StatusCode == 200);
        Assert.Equal("TaskDto", Assert.IsType<TsType.TypeRef>(ok.DataType).Name);
        var notFound = Assert.Single(ep.Responses, r => r.StatusCode == 404);
        Assert.Equal("ErrorDto", Assert.IsType<TsType.TypeRef>(notFound.DataType).Name);
    }

    // ---------------------------------------------------------------
    // A8 — typed-results mapping table completeness + unmapped diagnostic
    // ---------------------------------------------------------------

    [Fact]
    public void A8_ProblemValidationForbidJson_TypedResults_AreMapped()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading.Tasks;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPost]
                public Task<Results<Ok<TaskDto>, ProblemHttpResult, ValidationProblem, ForbidHttpResult>> Create()
                    => Task.FromResult<Results<Ok<TaskDto>, ProblemHttpResult, ValidationProblem, ForbidHttpResult>>(
                        TypedResults.Ok(new TaskDto("x")));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        var statuses = ep.Responses.Select(r => r.StatusCode).ToList();

        Assert.Contains(200, statuses);
        Assert.Contains(500, statuses); // ProblemHttpResult
        Assert.Contains(400, statuses); // ValidationProblem
        Assert.Contains(403, statuses); // ForbidHttpResult
    }

    [Fact]
    public void A8_JsonHttpResult_And_InternalServerError_AreMapped()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading.Tasks;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);
            public sealed record ErrorDto(string Message);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                public Task<Results<JsonHttpResult<TaskDto>, InternalServerError<ErrorDto>>> Get()
                    => Task.FromResult<Results<JsonHttpResult<TaskDto>, InternalServerError<ErrorDto>>>(
                        TypedResults.Json(new TaskDto("x")));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);

        var ok = Assert.Single(ep.Responses, r => r.StatusCode == 200);
        Assert.Equal("TaskDto", Assert.IsType<TsType.TypeRef>(ok.DataType).Name);

        var ise = Assert.Single(ep.Responses, r => r.StatusCode == 500);
        Assert.Equal("ErrorDto", Assert.IsType<TsType.TypeRef>(ise.DataType).Name);
    }

    [Fact]
    public void A8_UnmappedResultsBranch_EmitsLoudDiagnostic_NotSilentDrop()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading.Tasks;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                public Task<Results<Ok<TaskDto>, ChallengeHttpResult>> Get()
                    => Task.FromResult<Results<Ok<TaskDto>, ChallengeHttpResult>>(
                        TypedResults.Ok(new TaskDto("x")));
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkMerged(source);
        });

        var ep = Assert.Single(endpoints);

        // The mapped branch survives…
        Assert.Single(ep.Responses, r => r.StatusCode == 200);

        // …and the unmapped one is loudly diagnosed (enforceability rule), not silently dropped
        Assert.Contains("ChallengeHttpResult", stderr);
        Assert.Contains("unmapped", stderr);
    }

    // ---------------------------------------------------------------
    // A9 — [Range(typeof(decimal), "0.1", "100")] overload crashed the walker
    // ---------------------------------------------------------------

    [Fact]
    public void A9_RangeTypeStringOverload_ParsesInvariant_NoCrash()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PriceDto
            {
                [Range(typeof(decimal), "0.1", "100")]
                public decimal Amount { get; init; }
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var amount = Assert.Single(walker.Definitions["PriceDto"].Properties);
        Assert.NotNull(amount.Constraints);
        Assert.Equal(0.1, amount.Constraints!.Minimum);
        Assert.Equal(100, amount.Constraints.Maximum);
    }

    [Fact]
    public void A9_RangeUnparseableBound_SkipsConstraintWithWarning()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PriceDto
            {
                [Range(typeof(decimal), "not-a-number", "100")]
                public decimal Amount { get; init; }
            }
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var amount = Assert.Single(walker.Definitions["PriceDto"].Properties);
        Assert.True(
            amount.Constraints is null
                || (amount.Constraints.Minimum is null && amount.Constraints.Maximum is null)
        );
        Assert.Contains("Range", stderr);
    }

    // ---------------------------------------------------------------
    // A10 — [FromHeader]/[FromServices] params were mis-bucketed
    // (DI services are excluded silently; headers used to be excluded with RIV1005 —
    //  P2 wave 5 retired that drop: [FromHeader] now maps to ParamSource.Header)
    // ---------------------------------------------------------------

    [Fact]
    public void A10_FromServices_IsExcludedFromContract()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);
            public sealed class TaskService { }

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{id}")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult GetTask(string id, [FromServices] TaskService service)
                    => Ok(new TaskDto("x"));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        var param = Assert.Single(ep.Params);
        Assert.Equal("id", param.Name);
        Assert.Equal(ParamSource.Route, param.Source);
    }

    [Fact]
    public void A10_FromHeader_MapsToHeaderParam_KeepingWireCasing()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult List([FromHeader(Name = "X-Api-Key")] string apiKey)
                    => Ok(new TaskDto("x"));
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkMerged(source);
        });

        // P2 wave 5 inverts the old drop pin: [FromHeader] maps to ParamSource.Header,
        // the wire name keeps the attribute's casing, and RIV1005 is retired (no warning).
        var ep = Assert.Single(endpoints);
        var param = Assert.Single(ep.Params);
        Assert.Equal("X-Api-Key", param.Name);
        Assert.Equal(ParamSource.Header, param.Source);
        Assert.DoesNotContain("RIV1005", stderr);
    }

    [Fact]
    public void A10_FromHeaderAndServices_InMixedUpload_NotTurnedIntoFormFields()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed class AuditService { }

            [ApiController]
            [Route("api/files")]
            public class FilesController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPost]
                public IActionResult Upload(
                    IFormFile file,
                    [FromForm] string title,
                    [FromHeader(Name = "X-Trace")] string trace,
                    [FromServices] AuditService audit)
                    => Ok();
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkMerged(source);
        });

        var ep = Assert.Single(endpoints);

        // The old fallback turned headers and concrete DI services into FormFields.
        // P2 wave 5: the header is a first-class Header param now, never a form field.
        Assert.DoesNotContain(ep.Params, p => p.Name is "trace" or "audit");
        Assert.Single(ep.Params, p => p.Name == "X-Trace" && p.Source == ParamSource.Header);
        Assert.Single(ep.Params, p => p.Name == "file" && p.Source == ParamSource.File);
        Assert.Single(ep.Params, p => p.Name == "title");
    }

    // ---------------------------------------------------------------
    // A12 — T? on type parameters lowered as plain T
    // ---------------------------------------------------------------

    [Fact]
    public void A12_NullableTypeParameter_LowersAsNullableTypeParam()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(T? Value);
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var value = Assert.Single(walker.Definitions["Wrapper"].Properties);
        var nullable = Assert.IsType<TsType.Nullable>(value.Type);
        Assert.Equal("T", Assert.IsType<TsType.TypeParam>(nullable.Inner).Name);
    }

    // ---------------------------------------------------------------
    // A14 — [JsonPropertyName] on a route-bound property broke interpolation:
    // the contract param must keep the ROUTE name (binding uses the C# name),
    // with a loud diagnostic on the mismatch.
    // ---------------------------------------------------------------

    [Fact]
    public void A14_RouteBoundProperty_WithJsonPropertyName_KeepsRouteName_AndDiagnoses()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public sealed record GetTaskQuery
            {
                [JsonPropertyName("renamed")]
                public string Id { get; init; } = "";

                public bool Verbose { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define Get = Define.Get<GetTaskQuery, TaskDto>("/api/tasks/{id}");
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkContract(source);
        });

        var ep = Assert.Single(endpoints);

        // Route param keeps the route-template name, not the JSON rename
        var route = Assert.Single(ep.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", route.Name);
        Assert.DoesNotContain(ep.Params, p => p.Name == "renamed");

        // Loud diagnostic on the rename/route mismatch
        Assert.Contains("renamed", stderr);

        // Non-route props keep JSON naming behavior
        Assert.Single(ep.Params, p => p.Name == "verbose" && p.Source == ParamSource.Query);
    }

    // ---------------------------------------------------------------
    // E6 — generic TEMPLATE definitions were walked when collecting generic
    // instances, producing garbage Foo_T schemas full of object fallbacks
    // ---------------------------------------------------------------

    [Fact]
    public void E6_GenericTemplateReferencingGeneric_EmitsNoGarbageTemplateSchemas()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PagedResult<T>(List<T> Items, int Total);

            [RivetType]
            public sealed record Wrapper<T>(PagedResult<T> Page, string Label);

            public sealed record MessageDto(string Text);

            [RivetContract]
            public static class MessagesContract
            {
                public static readonly Define Get = Define.Get<Wrapper<MessageDto>>("/api/messages");
            }
            """;

        string json = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            using var emitted = CompilationHelper.EmitOpenApi(source);
            json = emitted.RootElement.GetRawText();
        });

        using var doc = JsonDocument.Parse(json);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var schemaNames = schemas.EnumerateObject().Select(p => p.Name).ToList();

        // Templates emit as templates only — never as bogus monomorphised *_T schemas
        Assert.DoesNotContain(schemaNames, n => n.EndsWith("_T", StringComparison.Ordinal));
        Assert.DoesNotContain("unresolved type parameter", stderr);

        // The concrete instantiations exist, including the nested one introduced by resolution
        Assert.Contains("Wrapper_MessageDto", schemaNames);
        Assert.Contains("PagedResult_MessageDto", schemaNames);

        // No dangling $refs anywhere in the document
        foreach (
            var match in System
                .Text.RegularExpressions.Regex.Matches(
                    json,
                    "\"\\$ref\":\\s*\"#/components/schemas/([^\"]+)\""
                )
                .Cast<System.Text.RegularExpressions.Match>()
        )
        {
            Assert.Contains(match.Groups[1].Value, schemaNames);
        }
    }

    // ---------------------------------------------------------------
    // E8 remainder — producer side never set IsOptional
    // ---------------------------------------------------------------

    [Fact]
    public void E8_DefaultValuedActionParam_SetsIsOptional_AndEmitsRequiredFalse()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(string Name);

            [ApiController]
            [Route("api/tasks")]
            public class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult List([FromQuery] int page = 1, [FromQuery] string? filter = null)
                    => Ok(new TaskDto("x"));
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);
        var ep = Assert.Single(endpoints);

        var page = Assert.Single(ep.Params, p => p.Name == "page");
        Assert.True(page.IsOptional, "C# default-valued param must set IsOptional (E8)");

        using var doc = CompilationHelper.EmitOpenApi(source);
        var parameters = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToList();

        var pageParam = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "page");
        Assert.False(pageParam.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void E8_RivetOptional_NonNullableQueryProperty_EmitsRequiredFalse()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record ListQuery
            {
                [RivetOptional]
                public int Page { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define List = Define.Get<ListQuery, TaskDto[]>("/api/tasks");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);
        var page = Assert.Single(Assert.Single(endpoints).Params, p => p.Name == "page");
        Assert.True(
            page.IsOptional,
            "[RivetOptional] non-nullable query property must set IsOptional (E8)"
        );

        using var doc = CompilationHelper.EmitOpenApi(source);
        var parameters = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToList();

        var pageParam = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "page");
        Assert.False(pageParam.GetProperty("required").GetBoolean());
    }

    // ---------------------------------------------------------------
    // E11 remainder — request bodies were always required: true, even for
    // a Nullable body type
    // ---------------------------------------------------------------

    [Fact]
    public void E11_NullableRequestBody_EmitsRequiredFalse()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record PatchRequest(string? Note);
            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define Patch = Define.Patch<PatchRequest?, TaskDto>("/api/tasks/{id}");
                public static readonly Define Update = Define.Put<PatchRequest, TaskDto>("/api/tasks/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var path = doc.RootElement.GetProperty("paths").GetProperty("/api/tasks/{id}");

        // Nullable body → required: false
        var patchBody = path.GetProperty("patch").GetProperty("requestBody");
        Assert.False(patchBody.GetProperty("required").GetBoolean());

        // Non-nullable body keeps required: true
        var putBody = path.GetProperty("put").GetProperty("requestBody");
        Assert.True(putBody.GetProperty("required").GetBoolean());
    }

    // ---------------------------------------------------------------
    // I1 — "Alias": {"$ref": "#/components/schemas/Target"} component aliases:
    // the importer registered the alias name then skipped it, leaving consumers
    // dangling (CS0246). Aliases must resolve to the TARGET's mapped name.
    // ---------------------------------------------------------------

    [Fact]
    public void I1_RefAliasSchema_ConsumerResolvesToTarget_AndCompiles()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Real": {
                "type": "object",
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
            },
            "Alias": { "$ref": "#/components/schemas/Real" },
            "Holder": {
                "type": "object",
                "properties": { "thing": { "$ref": "#/components/schemas/Alias" } }
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        // No Alias type is generated…
        Assert.DoesNotContain(result.Files, f => f.FileName.EndsWith("Alias.cs"));

        // …and the consumer references the target type, not the dangling alias name
        var holder = CompilationHelper.FindFile(result, "Holder.cs");
        Assert.Contains("Real", holder);
        Assert.DoesNotContain("Alias", holder);

        // WouldGenerateType-agreement oracle: generated C# compiles
        CompilationHelper.CompileImportResult(result);
    }

    [Fact]
    public void I1_AliasChain_ChasesToFinalTarget()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Real": {
                "type": "object",
                "properties": { "value": { "type": "string" } }
            },
            "Middle": { "$ref": "#/components/schemas/Real" },
            "Outer": { "$ref": "#/components/schemas/Middle" },
            "Holder": {
                "type": "object",
                "properties": { "thing": { "$ref": "#/components/schemas/Outer" } }
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        var holder = CompilationHelper.FindFile(result, "Holder.cs");
        Assert.Contains("Real", holder);
        Assert.DoesNotContain("Outer", holder);
        Assert.DoesNotContain("Middle", holder);

        CompilationHelper.CompileImportResult(result);
    }

    [Fact]
    public void I1_AliasToEnum_ConsumerResolvesToEnum()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Status": {
                "type": "string",
                "enum": ["active", "closed"]
            },
            "StatusAlias": { "$ref": "#/components/schemas/Status" },
            "Holder": {
                "type": "object",
                "properties": { "status": { "$ref": "#/components/schemas/StatusAlias" } }
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        var holder = CompilationHelper.FindFile(result, "Holder.cs");
        Assert.Contains("Status", holder);
        Assert.Contains("[RivetSchemaRef(\"StatusAlias\")]", holder);
        Assert.Contains(
            "[assembly: RivetGeneratedSchema(\"StatusAlias\", \"StatusAlias\"",
            CompilationHelper.FindFile(result, "RivetScalarSchemas.cs")
        );

        CompilationHelper.CompileImportResult(result);
    }

    [Fact]
    public void I1_AliasCycle_DoesNotHang_WarnsLoudly()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "A": { "$ref": "#/components/schemas/B" },
            "B": { "$ref": "#/components/schemas/A" },
            "Holder": {
                "type": "object",
                "properties": { "thing": { "$ref": "#/components/schemas/A" } }
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        // Cycle is diagnosed, not silently dropped (and the import must terminate)
        Assert.Contains(
            result.Warnings,
            w =>
                w.Contains("cycle", StringComparison.OrdinalIgnoreCase)
                || w.Contains("circular", StringComparison.OrdinalIgnoreCase)
        );

        CompilationHelper.CompileImportResult(result);
    }
}
