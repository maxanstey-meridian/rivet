using Rivet.Tool.Analysis;

namespace Rivet.Tests;

public sealed class CoverageCheckerTests
{
    private const string Contract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record TaskDto(string Id, string Title);

        [RivetType]
        public sealed record TaskInput(string Title);

        [RivetContract]
        public static class TasksContract
        {
            public static readonly RouteDefinition<TaskDto> ListTasks =
                Define.Get<TaskDto>("/api/tasks").Returns(404);

            public static readonly RouteDefinition<TaskInput, TaskDto> CreateTask =
                Define.Post<TaskInput, TaskDto>("/api/tasks");

            public static readonly RouteDefinition RemoveTask =
                Define.Delete("/api/tasks/{id}");

            public static readonly RouteDefinition UpdateTask =
                Define.Put("/api/tasks/{id}");

            public static readonly RouteDefinition PatchTask =
                Define.Patch("/api/tasks/{id}");

            public static readonly FileRouteDefinition DownloadTask =
                Define.File("/api/tasks/{id}/download");

            public static readonly RouteDefinition HeadTasks =
                Define.Head("/api/tasks");

            public static readonly RouteDefinition OptionsTasks =
                Define.Options("/api/tasks");
        }
        """;

    private const string AzureAttributes = """
        using System;

        namespace Microsoft.Azure.Functions.Worker;

        public enum AuthorizationLevel
        {
            Anonymous,
            Function,
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FunctionAttribute(string name) : Attribute
        {
            public string Name { get; } = name;
        }

        [AttributeUsage(AttributeTargets.Parameter)]
        public sealed class HttpTriggerAttribute : Attribute
        {
            public HttpTriggerAttribute(AuthorizationLevel authorizationLevel, params string[] methods)
            {
                AuthorizationLevel = authorizationLevel;
                Methods = methods;
            }

            public AuthorizationLevel AuthorizationLevel { get; }
            public string[] Methods { get; }
            public string? Route { get; set; }
        }
        """;

    private static IReadOnlyList<CoverageWarning> RunCheck(params string[] sources)
    {
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return CompilationHelper.CheckCoverage(compilation, endpoints);
    }

    [Fact]
    public void Direct_success_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Terminal_result_local_initializer_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    return result.ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Terminal_result_single_assignment_in_same_block_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    RivetResult result;
                    result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    return result.ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Switch_expression_terminal_arms_are_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List(bool found)
                {
                    return found switch
                    {
                        true => TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult(),
                        false => TasksContract.ListTasks.Error(404).ToActionResult(),
                    };
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Conditional_terminal_branches_are_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List(bool found) =>
                    found
                        ? TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult()
                        : TasksContract.ListTasks.Error(404).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Direct_error_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => TasksContract.ListTasks.Error(404).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Direct_file_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet("{id}/download")]
                public IActionResult Download() =>
                    TasksContract.DownloadTask.File(new byte[] { 1, 2, 3 }).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "DownloadTask");
    }

    [Fact]
    public void Inline_bound_terminal_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(TaskInput input) =>
                    TasksContract.CreateTask.Bind(input)
                        .Success(new TaskDto("1", input.Title))
                        .ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "CreateTask");
    }

    [Fact]
    public void Bound_local_initializer_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(TaskInput input)
                {
                    var endpoint = TasksContract.CreateTask.Bind(input);
                    return endpoint.Success(new TaskDto("1", input.Title)).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "CreateTask");
    }

    [Fact]
    public void Direct_field_local_initializer_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var endpoint = TasksContract.ListTasks;
                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Reassigned_local_does_not_establish_provenance()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class OtherContract
            {
                public static readonly RouteDefinition<TaskDto> ListTasks =
                    Define.Get<TaskDto>("/api/other");
            }

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var endpoint = TasksContract.ListTasks;
                    endpoint = OtherContract.ListTasks;
                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.Contains(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.MissingImplementation
                && warning.ContractName == "TasksContract"
                && warning.FieldName == "ListTasks"
        );
    }

    [Fact]
    public void Declaration_then_single_assignment_in_same_block_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    RouteDefinition<TaskDto> endpoint;
                    endpoint = TasksContract.ListTasks;
                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Assignment_in_branch_does_not_establish_provenance()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    RouteDefinition<TaskDto> endpoint;
                    if (true)
                    {
                        endpoint = TasksContract.ListTasks;
                    }

                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Multiple_assignments_do_not_establish_provenance()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    RouteDefinition<TaskDto> endpoint;
                    endpoint = TasksContract.ListTasks;
                    endpoint = TasksContract.ListTasks;
                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Ref_use_does_not_establish_provenance()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    RouteDefinition<TaskDto> endpoint;
                    endpoint = TasksContract.ListTasks;
                    Touch(ref endpoint);
                    return endpoint.Success(new TaskDto("1", "Test")).ToActionResult();
                }

                private static void Touch(ref RouteDefinition<TaskDto> endpoint) { }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Helper_terminal_called_by_endpoint_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => BuildResult().ToActionResult();

                private static RivetResult BuildResult() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Discarded_terminal_expression_in_endpoint_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    return Ok();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Unreachable_returned_terminal_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    if (false)
                    {
                        return TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToActionResult();
                    }

                    return Ok();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Expression_bodied_void_endpoint_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public void List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Unused_terminal_local_in_endpoint_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    return Ok();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Reassigned_terminal_result_local_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    result = TasksContract.ListTasks.Error(404);
                    return result.ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Branched_terminal_result_assignment_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List(bool useError)
                {
                    RivetResult result;
                    if (useError)
                    {
                        result = TasksContract.ListTasks.Error(404);
                    }
                    else
                    {
                        result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    }

                    return result.ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Ref_terminal_result_local_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    Touch(ref result);
                    return result.ToActionResult();
                }

                private static void Touch(ref RivetResult result) { }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Out_terminal_result_local_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List()
                {
                    var result = TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                    Replace(out result);
                    return result.ToActionResult();
                }

                private static void Replace(out RivetResult result)
                {
                    result = null!;
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Mvc_raw_terminal_return_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public RivetResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Mvc_ok_wrapped_terminal_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    Ok(TasksContract.ListTasks.Success(new TaskDto("1", "Test")));
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Mvc_ok_wrapped_adapter_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    Ok(TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult());
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Minimal_object_wrapped_adapter_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TasksEndpoints
            {
                public static void MapTasks(this IEndpointRouteBuilder app) =>
                    app.MapGet(TasksContract.ListTasks.Route, () => new
                    {
                        Result = TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult()
                    });
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Mvc_terminal_using_minimal_adapter_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public object List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Lookalike_action_result_adapter_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class LookalikeAdapters
            {
                public static IActionResult ToActionResult(this RivetResult result) =>
                    new OkResult();
            }

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Returned_terminal_in_non_route_method_is_not_coverage()
    {
        var implementation = """
            using Rivet;

            namespace Test;

            public static class TaskService
            {
                public static RivetResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Bind_without_terminal_reports_orphan_and_missing_implementation()
    {
        var implementation = """
            namespace Test;

            public static class TaskEndpoints
            {
                public static void Create(TaskInput input)
                {
                    _ = TasksContract.CreateTask.Bind(input);
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "CreateTask");
        var orphan = Assert.Single(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.OrphanedBinding
                && warning.FieldName == "CreateTask"
        );
        Assert.Equal(
            implementation.IndexOf(
                "TasksContract.CreateTask.Bind(input)",
                StringComparison.Ordinal
            ),
            orphan.Location!.SourceSpan.Start
        );
        Assert.Equal(
            "TasksContract.CreateTask.Bind(input)".Length,
            orphan.Location.SourceSpan.Length
        );
    }

    [Fact]
    public void Returned_adapted_bind_is_consumed()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(TaskInput input)
                {
                    var endpoint = TasksContract.CreateTask.Bind(input);
                    return endpoint.Success(new TaskDto("1", input.Title)).ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "CreateTask");
    }

    [Fact]
    public void Orphan_bind_is_reported_when_same_field_has_valid_bind()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(TaskInput input)
                {
                    _ = TasksContract.CreateTask.Bind(input);
                    return TasksContract.CreateTask.Bind(input)
                        .Success(new TaskDto("1", input.Title))
                        .ToActionResult();
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "CreateTask");
        var orphan = Assert.Single(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.OrphanedBinding
                && warning.FieldName == "CreateTask"
        );
        Assert.Equal(
            implementation.IndexOf(
                "TasksContract.CreateTask.Bind(input)",
                StringComparison.Ordinal
            ),
            orphan.Location!.SourceSpan.Start
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData(".ToResult()")]
    public void Bind_with_wrong_or_missing_adapter_reports_orphan_and_missing_implementation(
        string adapter
    )
    {
        var implementation = $$"""
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public object Create(TaskInput input) =>
                    TasksContract.CreateTask.Bind(input)
                        .Success(new TaskDto("1", input.Title)){{adapter}};
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "CreateTask");
        Assert.Contains(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.OrphanedBinding
                && warning.FieldName == "CreateTask"
        );
    }

    [Fact]
    public void Unrelated_terminal_names_are_ignored()
    {
        var implementation = """
            namespace Test;

            public sealed class Lookalike
            {
                public object Success(object value) => value;
                public object Error(int statusCode) => statusCode;
                public object File(byte[] value) => value;
            }

            public static class TaskEndpoints
            {
                public static object List() => new Lookalike().Success(TasksContract.ListTasks);
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.Contains(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.MissingImplementation
                && warning.FieldName == "ListTasks"
        );
    }

    [Theory]
    [InlineData(
        "HttpGet",
        "ListTasks",
        "TasksContract.ListTasks.Success(new TaskDto(\"1\", \"Test\"))"
    )]
    [InlineData(
        "HttpPost",
        "CreateTask",
        "TasksContract.CreateTask.Bind(new TaskInput(\"Test\")).Success(new TaskDto(\"1\", \"Test\"))"
    )]
    [InlineData("HttpDelete(\"{id}\")", "RemoveTask", "TasksContract.RemoveTask.Success()")]
    [InlineData("HttpHead", "HeadTasks", "TasksContract.HeadTasks.Success()")]
    [InlineData("HttpOptions", "OptionsTasks", "TasksContract.OptionsTasks.Success()")]
    public void Mvc_endpoint_context_is_recognized(
        string attribute,
        string fieldName,
        string terminal
    )
    {
        var implementation = $$"""
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [{{attribute}}]
                public IActionResult Handle() => {{terminal}}.ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == fieldName);
    }

    [Theory]
    [InlineData(
        "MapGet",
        "ListTasks",
        "TasksContract.ListTasks.Success(new TaskDto(\"1\", \"Test\"))"
    )]
    [InlineData(
        "MapPost",
        "CreateTask",
        "TasksContract.CreateTask.Bind(new TaskInput(\"Test\")).Success(new TaskDto(\"1\", \"Test\"))"
    )]
    [InlineData("MapPut", "UpdateTask", "TasksContract.UpdateTask.Success()")]
    [InlineData("MapPatch", "PatchTask", "TasksContract.PatchTask.Success()")]
    [InlineData("MapDelete", "RemoveTask", "TasksContract.RemoveTask.Success()")]
    public void Minimal_endpoint_context_is_recognized(
        string mapMethod,
        string fieldName,
        string terminal
    )
    {
        var route = fieldName is "RemoveTask" or "UpdateTask" or "PatchTask"
            ? "/api/tasks/{id}"
            : "/api/tasks";
        var implementation = $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.{{mapMethod}}("{{route}}", () => {{terminal}}.ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, fieldName);
    }

    [Theory]
    [InlineData("HEAD", "HeadTasks", "TasksContract.HeadTasks.Success()")]
    [InlineData("OPTIONS", "OptionsTasks", "TasksContract.OptionsTasks.Success()")]
    public void Minimal_MapMethods_head_and_options_are_recognized(
        string method,
        string fieldName,
        string terminal
    )
    {
        var implementation = $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapMethods("/api/tasks", new[] { "{{method}}" }, () => {{terminal}}.ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == fieldName);
    }

    [Fact]
    public void Minimal_block_lambda_return_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", () =>
                    {
                        return TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToResult();
                    });
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Minimal_discarded_terminal_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", () =>
                    {
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
                        return Results.Ok();
                    });
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Minimal_raw_terminal_return_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")));
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Minimal_terminal_using_mvc_adapter_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", () =>
                        TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToActionResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Minimal_method_mismatch_is_reported()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/api/tasks", () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);
        var warning = Assert.Single(
            warnings,
            warning =>
                warning.FieldName == "ListTasks"
                && warning.Kind == CoverageWarningKind.HttpMethodMismatch
        );

        Assert.Equal("GET", warning.Expected);
        Assert.Equal("POST", warning.Actual);
    }

    [Fact]
    public void Minimal_route_mismatch_is_reported()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/items", () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);
        var warning = Assert.Single(
            warnings,
            warning =>
                warning.FieldName == "ListTasks"
                && warning.Kind == CoverageWarningKind.RouteMismatch
        );

        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/api/items", warning.Actual);
    }

    [Fact]
    public void Isolated_function_route_and_method_are_recognized()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class TaskFunctions
            {
                [Function("list-tasks")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tasks")] object request
                ) => TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, AzureAttributes, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListTasks");
    }

    [Fact]
    public void Isolated_function_uses_function_name_and_default_api_prefix()
    {
        var contract = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class HealthContract
            {
                public static readonly RouteDefinition Health = Define.Get("/api/health");
            }
            """;
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class HealthFunction
            {
                [Function("health")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get")] object request
                ) => HealthContract.Health.Success().ToActionResult();
            }
            """;

        var warnings = RunCheck(contract, AzureAttributes, implementation);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Http_trigger_without_function_attribute_is_not_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class TaskFunctions
            {
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tasks")] object request
                ) => TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, AzureAttributes, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Isolated_function_raw_terminal_return_is_not_coverage()
    {
        var implementation = """
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class TaskFunctions
            {
                [Function("list-tasks")]
                public RivetResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tasks")] object request
                ) => TasksContract.ListTasks.Success(new TaskDto("1", "Test"));
            }
            """;

        var warnings = RunCheck(Contract, AzureAttributes, implementation);

        AssertMissingWarning(warnings, "ListTasks");
    }

    [Fact]
    public void Missing_terminal_reports_expected_endpoint()
    {
        var warnings = RunCheck(Contract);
        var warning = Assert.Single(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.MissingImplementation
                && warning.FieldName == "ListTasks"
        );

        Assert.Equal("GET /api/tasks", warning.Expected);
        Assert.Equal("(none)", warning.Actual);
        Assert.NotNull(warning.Location);
    }

    private static void AssertNoMissingWarning(
        IReadOnlyList<CoverageWarning> warnings,
        string fieldName
    ) =>
        Assert.DoesNotContain(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.MissingImplementation
                && warning.FieldName == fieldName
        );

    private static void AssertMissingWarning(
        IReadOnlyList<CoverageWarning> warnings,
        string fieldName
    ) =>
        Assert.Contains(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.MissingImplementation
                && warning.FieldName == fieldName
        );
}
