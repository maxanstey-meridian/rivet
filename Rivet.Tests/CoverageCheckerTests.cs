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
        return CompilationHelper.CheckCoverage(
            CompilationHelper.CreateCompilationFromMultiple(sources)
        );
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
    public void Bound_error_is_coverage()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public IActionResult Create([FromBody] TaskInput input) =>
                    TasksContract.CreateTask.Bind(input).Error(400).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        AssertNoMissingWarning(warnings, "CreateTask");
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
    public void Error_branch_through_a_helper_keeps_the_handler_covered()
    {
        // docs/guides/contract-coverage.md promises that error terminals may live in
        // helpers: the handler's own returned .Success(...) is all --check needs.
        var lookupContract = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskLookup(string Id);

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskLookup, TaskDto> GetTask =
                    Define.Get<TaskLookup, TaskDto>("/api/tasks/{id}").Returns(404);
            }
            """;

        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks/{id}", (string id) =>
                    {
                        var endpoint = TasksContract.GetTask.Bind(new TaskLookup(id));
                        if (id == "missing")
                        {
                            return NotFound(endpoint).ToResult();
                        }

                        return endpoint.Success(new TaskDto(id)).ToResult();
                    });
                }

                private static RivetResult NotFound(BoundRouteDefinitionBase endpoint) =>
                    endpoint.Error(404);
            }
            """;

        var warnings = RunCheck(lookupContract, implementation);

        Assert.Empty(warnings);
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
                public IActionResult Create([FromBody] TaskInput input)
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
        "TasksContract.ListTasks.Success(new TaskDto(\"1\", \"Test\"))",
        ""
    )]
    [InlineData(
        "HttpPost",
        "CreateTask",
        "TasksContract.CreateTask.Bind(input).Success(new TaskDto(\"1\", \"Test\"))",
        "[FromBody] TaskInput input"
    )]
    [InlineData(
        "HttpDelete(\"{id}\")",
        "RemoveTask",
        "TasksContract.RemoveTask.Success()",
        "string id"
    )]
    [InlineData("HttpHead", "HeadTasks", "TasksContract.HeadTasks.Success()", "")]
    [InlineData("HttpOptions", "OptionsTasks", "TasksContract.OptionsTasks.Success()", "")]
    public void Mvc_endpoint_context_is_recognized(
        string attribute,
        string fieldName,
        string terminal,
        string parameters
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
                public IActionResult Handle({{parameters}}) => {{terminal}}.ToActionResult();
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

    // ---------------------------------------------------------------
    // MapGroup receiver chains, unresolved routes/methods, and MVC
    // [controller]/[action] token agreement with extraction
    // (outcome:route-coverage).
    // ---------------------------------------------------------------

    private const string GroupedTasksContract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record GroupedTaskDto(string Id);

        [RivetContract]
        public static class GroupedTasksContract
        {
            public static readonly RouteDefinition<GroupedTaskDto> ListItems =
                Define.Get<GroupedTaskDto>("/v2/items");
        }
        """;

    private const string NestedItemsContract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record NestedItemDto(string Id);

        [RivetContract]
        public static class NestedItemsContract
        {
            public static readonly RouteDefinition<NestedItemDto> ListItems =
                Define.Get<NestedItemDto>("/v2/inner/items");
        }
        """;

    [Fact]
    public void Constant_MapGroup_receiver_prefix_is_prepended()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class GroupedEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/v2");
                    group.MapGet("/items", () =>
                        GroupedTasksContract.ListItems
                            .Success(new GroupedTaskDto("1"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(GroupedTasksContract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListItems");
    }

    [Fact]
    public void Grouped_Handler_Fails_Against_A_Contract_For_The_Ungrouped_Route()
    {
        // A grouped /v2/tasks handler must not verify a contract declaring /api/tasks —
        // the resolved grouped route has to disagree with the ungrouped contract.
        var ungroupedContract = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> ListTasks =
                    Define.Get<TaskDto>("/api/tasks");
            }
            """;

        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class GroupedEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/v2");
                    group.MapGet("/tasks", () =>
                        TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(ungroupedContract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/v2/tasks", warning.Actual);
    }

    [Fact]
    public void Nested_MapGroup_Chains_Accumulate_Prefixes()
    {
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class NestedEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/v2").MapGroup("/inner");
                    group.MapGet("/items", () =>
                        NestedItemsContract.ListItems
                            .Success(new NestedItemDto("1"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(NestedItemsContract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListItems");
    }

    [Fact]
    public void Nested_Group_Prefix_Applies_To_The_Resolved_Route()
    {
        // Same nested host against the un-nested contract proves the accumulated
        // prefix is actually applied, not silently dropped.
        var flatContract = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record NestedItemDto(string Id);

            [RivetContract]
            public static class NestedItemsContract
            {
                public static readonly RouteDefinition<NestedItemDto> ListItems =
                    Define.Get<NestedItemDto>("/items");
            }
            """;

        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class NestedEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/v2").MapGroup("/inner");
                    group.MapGet("/items", () =>
                        NestedItemsContract.ListItems
                            .Success(new NestedItemDto("1"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(flatContract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListItems" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Equal("/items", warning.Expected);
        Assert.Equal("/v2/inner/items", warning.Actual);
    }

    [Fact]
    public void Nonconstant_Route_Argument_Is_Reported_Unresolved()
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
                    var route = "/api/tasks";
                    app.MapGet(route, () =>
                        TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        // Unresolved, known-mismatched and verified states remain distinguishable:
        // the Actual text states the unresolved cause instead of a resolved route.
        Assert.Contains("unresolved route", warning.Actual);
    }

    // ---------------------------------------------------------------
    // A contract field's .Route as the Map* route: resolved to the field's
    // template, then prefixed, normalised and compared like a constant.
    // ---------------------------------------------------------------

    [Fact]
    public void Contract_Route_Reference_Is_Coverage()
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
                    app.MapGet(TasksContract.ListTasks.Route, () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListTasks");
    }

    [Fact]
    public void Contract_Route_Reference_Through_A_Local_Is_Coverage()
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
                    var definition = TasksContract.ListTasks;
                    app.MapGet(definition.Route, () =>
                        definition.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListTasks");
    }

    [Fact]
    public void Contract_Route_Reference_Under_A_Group_Gets_The_Prefix()
    {
        // The contract owns the full path, so a group prefix it doesn't declare is a
        // mismatch even when the rest of the route came from the contract itself.
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGroup("/v2").MapGet(TasksContract.ListTasks.Route, () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/v2/api/tasks", warning.Actual);
    }

    [Fact]
    public void Concatenated_Contract_Route_Is_Unresolved()
    {
        // Only a bare .Route on a contract field resolves; building on it does not.
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/v2" + TasksContract.ListTasks.Route, () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Contains("unresolved route", warning.Actual);
        Assert.Contains("contract field's .Route", warning.Actual);
    }

    [Fact]
    public void Another_Fields_Route_Is_Compared_By_Value()
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
                    app.MapGet(TasksContract.DownloadTask.Route, () =>
                        TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/api/tasks/{id}/download", warning.Actual);
    }

    [Fact]
    public void Contract_Route_Reference_With_A_Constraint_Is_Coverage()
    {
        // The field's RouteTemplate has the constraint stripped and the runtime .Route
        // keeps it. Both normalise to the same route, so the reference still verifies.
        var constrainedContract = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var implementation = """
            using System;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet(TasksContract.GetTask.Route, (Guid id) =>
                        TasksContract.GetTask.Success(new TaskDto(id.ToString())).ToResult());
                }
            }
            """;

        var warnings = RunCheck(constrainedContract, implementation);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Nonconstant_MapMethods_Methods_Are_Reported_Unresolved()
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
                    var methods = new[] { "GET" };
                    app.MapMethods("/api/tasks", methods, () =>
                        TasksContract.ListTasks
                            .Success(new TaskDto("1", "Test"))
                            .ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Contains("unresolved HTTP method", warning.Actual);
    }

    [Fact]
    public void Controller_Route_Tokens_Match_Extraction()
    {
        // [controller]/[action] tokens must resolve identically in extraction and
        // coverage: a [Route("api/[controller]")] controller named TasksController
        // implements the contract route /api/tasks and verifies without a mismatch.
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListTasks");
    }

    [Fact]
    public void Unsubstituted_Controller_Tokens_Are_Not_Silently_Verified()
    {
        // Guards the token substitution: a WidgetsController under
        // [Route("api/[controller]")] resolves to /api/widgets and must NOT verify
        // the /api/tasks contract.
        var mismatchingController = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class WidgetsController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, mismatchingController);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        Assert.Equal("/api/tasks", warning.Expected);
        // SubstituteRouteTokens preserves the class name's casing ("WidgetsController"
        // → "Widgets"), identically in extraction and coverage.
        Assert.Equal("/api/Widgets", warning.Actual);
    }

    [Fact]
    public void Unroutable_Mvc_Action_Is_Reported_Unresolved()
    {
        // [HttpGet] with no template on a controller without [Route]: the route
        // template cannot be statically resolved, so the implementation is
        // unresolved — it must not disappear into an All OK result.
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() =>
                    TasksContract.ListTasks.Success(new TaskDto("1", "Test")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        var warning = Assert.Single(
            warnings,
            item => item.FieldName == "ListTasks" && item.Kind == CoverageWarningKind.RouteMismatch
        );
        // Unresolved, known-mismatched and verified states remain distinguishable:
        // the Actual text states the unresolved cause instead of a resolved route.
        Assert.Contains("unresolved route", warning.Actual);
        Assert.Contains("no route template", warning.Actual);
    }

    [Fact]
    public void Streaming_file_writer_is_coverage()
    {
        // The writer overload is still File(...), and a lambda converts to none of the
        // other overloads' parameter types, so this compiles without ambiguity.
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet(
                        "/api/tasks/{id}/download",
                        (string id) => TasksContract.DownloadTask
                            .File(async output => await output.WriteAsync(new byte[] { 1, 2, 3 }))
                            .ToResult()
                    );
                }
            }
            """;

        var warnings = RunCheck(Contract, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "DownloadTask");
    }

    // ---------------------------------------------------------------
    // Lambda arms (outcome.Match(...)) and an adapter around a switch,
    // a conditional or a local holding one. Each shape runs on an MVC
    // action and a minimal-API handler.
    // ---------------------------------------------------------------

    private const string OutcomeTypes = """
        using System;
        using System.Threading.Tasks;

        namespace Test;

        public sealed class Outcome
        {
            public bool Found { get; }

            public T Match<T>(Func<TaskDto, T> found, Func<string, T> missing) =>
                throw new NotImplementedException();

            public Task<T> MatchAsync<T>(
                Func<TaskDto, Task<T>> found,
                Func<string, Task<T>> missing
            ) => throw new NotImplementedException();
        }

        public sealed class TaskService
        {
            public Task<Outcome> Find() => throw new NotImplementedException();
        }

        // Stand-ins for LINQ's Select and First, which the test compilation doesn't reference.
        public static class TaskSequence
        {
            public static System.Collections.Generic.IEnumerable<T> Select<T>(
                this TaskDto[] items,
                Func<TaskDto, T> selector
            ) => throw new NotImplementedException();

            public static T First<T>(this System.Collections.Generic.IEnumerable<T> items) =>
                throw new NotImplementedException();
        }
        """;

    /// <summary>
    /// A /api/tasks handler for <paramref name="verb"/> with <paramref name="body"/> as its
    /// block body, where <c>tasks</c> is a <c>TaskService</c>. The body writes <c>ADAPT</c>
    /// for the host's adapter: <c>ToActionResult</c> on MVC, <c>ToResult</c> on a minimal API.
    /// </summary>
    private static string TasksHandler(string host, string body, string verb = "Get") =>
        host == "mvc"
            ? $$"""
                using System.Threading.Tasks;
                using Microsoft.AspNetCore.Mvc;
                using Rivet;

                namespace Test;

                [Route("api/tasks")]
                public sealed class TasksController(TaskService tasks) : ControllerBase
                {
                    [Http{{verb}}]
                    public async Task<IActionResult> List({{(
                    verb == "Post" ? "[FromBody] TaskInput request" : ""
                )}})
                    {
                        {{body.Replace("ADAPT", "ToActionResult")}}
                    }
                }
                """
            : $$"""
                using System.Threading.Tasks;
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Routing;
                using Rivet;

                namespace Test;

                public static class TaskEndpoints
                {
                    public static void Map(IEndpointRouteBuilder app)
                    {
                        app.Map{{verb}}("/api/tasks", async (TaskService tasks) =>
                        {
                            {{body.Replace("ADAPT", "ToResult")}}
                        });
                    }
                }
                """;

    private static void AssertCovered(IReadOnlyList<CoverageWarning> warnings) =>
        Assert.DoesNotContain(warnings, warning => warning.FieldName == "ListTasks");

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Match_lambda_arms_are_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => TasksContract.ListTasks.Success(task).ADAPT(),
                missing: _ => TasksContract.ListTasks.Error(404).ADAPT()
            );
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData(
        "mvc",
        "var found = (TaskDto task) => TasksContract.ListTasks.Success(task).ADAPT();",
        "found"
    )]
    [InlineData(
        "minimal",
        "var found = (TaskDto task) => TasksContract.ListTasks.Success(task).ADAPT();",
        "found"
    )]
    [InlineData(
        "mvc",
        "System.Func<TaskDto, IActionResult> found = task => TasksContract.ListTasks.Success(task).ADAPT();",
        "found"
    )]
    [InlineData(
        "minimal",
        "System.Func<TaskDto, Microsoft.AspNetCore.Http.IResult> found = task => TasksContract.ListTasks.Success(task).ADAPT();",
        "found"
    )]
    [InlineData(
        "mvc",
        "IActionResult Found(TaskDto task) => TasksContract.ListTasks.Success(task).ADAPT();",
        "Found"
    )]
    [InlineData(
        "minimal",
        "Microsoft.AspNetCore.Http.IResult Found(TaskDto task) => TasksContract.ListTasks.Success(task).ADAPT();",
        "Found"
    )]
    public void Match_arm_held_in_a_local_or_local_function_is_coverage(
        string host,
        string declaration,
        string arm
    )
    {
        // The arm is still the handler's own code, passed straight to the call it returns;
        // it's only been given a name first.
        var body = $$"""
            {{declaration}}
            var outcome = await tasks.Find();
            return outcome.Match({{arm}}, _ => TasksContract.HeadTasks.Success().ADAPT());
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Async_match_block_lambda_arms_are_coverage(string host)
    {
        var body = """
            var endpoint = TasksContract.ListTasks;
            return await (await tasks.Find()).MatchAsync(
                async task =>
                {
                    await Task.Yield();
                    return endpoint.Success(task).ADAPT();
                },
                async _ => TasksContract.HeadTasks.Success().ADAPT()
            );
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Configure_await_on_a_match_is_coverage(string host)
    {
        var body = """
            return await (await tasks.Find())
                .MatchAsync(
                    async task => TasksContract.ListTasks.Success(task).ADAPT(),
                    async _ => TasksContract.HeadTasks.Success().ADAPT()
                )
                .ConfigureAwait(false);
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Fact]
    public void Cast_around_a_match_is_coverage()
    {
        var body = """
            var outcome = await tasks.Find();
            return (IActionResult)outcome.Match(
                found: task => TasksContract.ListTasks.Success(task).ADAPT(),
                missing: _ => TasksContract.HeadTasks.Success().ADAPT());
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler("mvc", body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Terminal_in_a_lambda_nested_two_calls_deep_is_coverage(string host)
    {
        // A Match arm that returns a Match: each lambda is passed straight to the call
        // its parent returns. The outer missing arm is on another field, so only the
        // two-deep terminals can cover ListTasks.
        var body = """
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => outcome.Match(
                    found: _ => TasksContract.ListTasks.Success(task).ADAPT(),
                    missing: _ => TasksContract.ListTasks.Error(404).ADAPT()
                ),
                missing: _ => TasksContract.HeadTasks.Success().ADAPT()
            );
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Adapter_around_a_switch_is_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            return (
                outcome.Found switch
                {
                    true => TasksContract.ListTasks.Success(new TaskDto("1", "Test")),
                    false => TasksContract.ListTasks.Error(404),
                }
            ).ADAPT();
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Adapter_around_a_conditional_is_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            return (
                outcome.Found
                    ? TasksContract.ListTasks.Success(new TaskDto("1", "Test"))
                    : TasksContract.ListTasks.Error(404)
            ).ADAPT();
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Local_holding_a_switch_then_adapted_is_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            var response = outcome.Found switch
            {
                true => TasksContract.ListTasks.Success(new TaskDto("1", "Test")),
                false => TasksContract.ListTasks.Error(404),
            };
            return response.ADAPT();
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Adapter_around_a_match_is_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            return outcome
                .Match(
                    found: task => TasksContract.ListTasks.Success(task),
                    missing: _ => TasksContract.ListTasks.Error(404)
                )
                .ADAPT();
            """;

        AssertCovered(RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)));
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Lambda_arm_that_is_never_returned_is_not_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            outcome.Match(
                found: task => TasksContract.ListTasks.Success(task).ADAPT(),
                missing: _ => TasksContract.ListTasks.Error(404).ADAPT()
            );
            System.Func<TaskDto, object> unused = task => TasksContract.ListTasks.Success(task).ADAPT();
            return TasksContract.HeadTasks.Success().ADAPT();
            """;

        AssertMissingWarning(
            RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)),
            "ListTasks"
        );
    }

    [Theory]
    [InlineData("mvc", "OK(items.Select(task => TasksContract.ListTasks.Success(task).ADAPT()))")]
    [InlineData(
        "minimal",
        "OK(items.Select(task => TasksContract.ListTasks.Success(task).ADAPT()))"
    )]
    [InlineData(
        "mvc",
        "items.Select(task => TasksContract.ListTasks.Success(task).ADAPT()).First()"
    )]
    [InlineData(
        "minimal",
        "items.Select(task => TasksContract.ListTasks.Success(task).ADAPT()).First()"
    )]
    public void Lambda_buried_inside_a_returned_value_is_not_coverage(string host, string returned)
    {
        // The lambda goes to Select, an argument of Ok or the receiver of First, not to
        // the call the handler returns, so the response never goes through the terminal.
        var ok = host == "mvc" ? "Ok" : "Microsoft.AspNetCore.Http.Results.Ok";
        var body = $$"""
            var items = new[] { new TaskDto("1", "Test") };
            return {{returned.Replace("OK", ok)}};
            """;

        AssertMissingWarning(
            RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)),
            "ListTasks"
        );
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Bind_used_only_inside_match_arms_is_consumed(string host)
    {
        var body = """
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => endpoint.Success(task).ADAPT(),
                missing: _ => endpoint.Error(400).ADAPT()
            );
            """;

        var warnings = RunCheck(Contract, OutcomeTypes, TasksHandler(host, body, verb: "Post"));

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "CreateTask");
    }

    // ---------------------------------------------------------------
    // A binding handed to a helper on the returned path is used, so it
    // isn't orphaned, but it covers nothing: coverage still needs a
    // terminal the handler returns itself.
    // ---------------------------------------------------------------

    private const string BindingHelpers = """
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        // The consumer's own helpers over a bound endpoint, one of each per host.
        public static class Respond
        {
            public static IActionResult Refuse(this BoundRouteDefinitionBase endpoint, int status) =>
                endpoint.Error(status).ToActionResult();

            public static IResult RefuseResult(this BoundRouteDefinitionBase endpoint, int status) =>
                endpoint.Error(status).ToResult();

            public static Task<IActionResult> Done(IActionResult result) => Task.FromResult(result);

            public static Task<IResult> DoneResult(IResult result) => Task.FromResult(result);

            public static IActionResult Answer(RivetResult result) => result.ToActionResult();

            public static IResult AnswerResult(RivetResult result) => result.ToResult();

            public static RivetResult NotFound(BoundRouteDefinitionBase endpoint) =>
                endpoint.Error(404);

            public static T Later<T>(System.Func<T> make) => make();

            extension(BoundRouteDefinitionBase endpoint)
            {
                public IActionResult Deny(int status) => endpoint.Error(status).ToActionResult();

                public IResult DenyResult(int status) => endpoint.Error(status).ToResult();
            }
        }
        """;

    /// <summary>
    /// A POST /api/tasks handler (see <see cref="TasksHandler"/>) whose body writes
    /// <c>REFUSE</c>, <c>DONE</c> and <c>DENY</c> for the host's <c>Respond</c> helpers.
    /// </summary>
    private static IReadOnlyList<CoverageWarning> RunWithHelpers(string host, string body)
    {
        var (refuse, done, deny, answer) =
            host == "mvc"
                ? ("Refuse", "Done", "Deny", "Answer")
                : ("RefuseResult", "DoneResult", "DenyResult", "AnswerResult");
        var handler = TasksHandler(
            host,
            body.Replace("REFUSE", refuse)
                .Replace("DONE", done)
                .Replace("DENY", deny)
                .Replace("ANSWER", answer),
            verb: "Post"
        );
        return RunCheck(Contract, OutcomeTypes, BindingHelpers, handler);
    }

    private static bool IsCreateTaskOrphan(CoverageWarning warning) =>
        warning is { Kind: CoverageWarningKind.OrphanedBinding, FieldName: "CreateTask" };

    [Theory]
    [InlineData("mvc", "return endpoint.REFUSE(404);")]
    [InlineData("minimal", "return endpoint.REFUSE(404);")]
    [InlineData("mvc", "return await Respond.DONE(endpoint.REFUSE(404));")]
    [InlineData("minimal", "return await Respond.DONE(endpoint.REFUSE(404));")]
    [InlineData("mvc", "return Respond.NotFound(endpoint).ADAPT();")]
    [InlineData("minimal", "return Respond.NotFound(endpoint).ADAPT();")]
    [InlineData("mvc", "return endpoint.DENY(404);")]
    [InlineData("minimal", "return endpoint.DENY(404);")]
    [InlineData("mvc", "return (await Task.FromResult(Respond.NotFound(endpoint))).ADAPT();")]
    [InlineData("minimal", "return (await Task.FromResult(Respond.NotFound(endpoint))).ADAPT();")]
    [InlineData(
        "mvc",
        "return await Respond.DONE(input.Title == \"\" ? endpoint.REFUSE(404) : endpoint.REFUSE(410));"
    )]
    [InlineData(
        "minimal",
        "return await Respond.DONE(input.Title == \"\" ? endpoint.REFUSE(404) : endpoint.REFUSE(410));"
    )]
    [InlineData(
        "mvc",
        "return await Respond.DONE(input.Title switch { \"\" => endpoint.REFUSE(404), _ => endpoint.REFUSE(410) });"
    )]
    [InlineData(
        "minimal",
        "return await Respond.DONE(input.Title switch { \"\" => endpoint.REFUSE(404), _ => endpoint.REFUSE(410) });"
    )]
    [InlineData("mvc", "var answer = Respond.NotFound(endpoint); return answer.ADAPT();")]
    [InlineData("minimal", "var answer = Respond.NotFound(endpoint); return answer.ADAPT();")]
    [InlineData("mvc", "return Respond.REFUSE(input.Title == \"\" ? endpoint : endpoint, 404);")]
    [InlineData(
        "minimal",
        "return Respond.REFUSE(input.Title == \"\" ? endpoint : endpoint, 404);"
    )]
    [InlineData("mvc", "return Respond.NotFound((BoundRouteDefinitionBase)endpoint).ADAPT();")]
    [InlineData("minimal", "return Respond.NotFound((BoundRouteDefinitionBase)endpoint).ADAPT();")]
    [InlineData("mvc", "return Respond.ANSWER(endpoint.Success(new TaskDto(\"1\", input.Title)));")]
    [InlineData(
        "minimal",
        "return Respond.ANSWER(endpoint.Success(new TaskDto(\"1\", input.Title)));"
    )]
    [InlineData(
        "mvc",
        "return await Respond.DONE(endpoint.Success(new TaskDto(\"1\", input.Title)).ADAPT());"
    )]
    [InlineData(
        "minimal",
        "return await Respond.DONE(endpoint.Success(new TaskDto(\"1\", input.Title)).ADAPT());"
    )]
    public void Bind_passed_to_a_returned_helper_is_not_orphaned_but_is_not_coverage(
        string host,
        string returned
    )
    {
        var body = $$"""
            var input = new TaskInput("Test");
            var endpoint = TasksContract.CreateTask.Bind(input);
            {{returned}}
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.DoesNotContain(warnings, IsCreateTaskOrphan);
        AssertMissingWarning(warnings, "CreateTask");
    }

    [Theory]
    [InlineData("mvc", "IActionResult")]
    [InlineData("minimal", "Microsoft.AspNetCore.Http.IResult")]
    public void Helper_answer_in_a_local_assigned_later_is_found_past_an_unresolved_use(
        string host,
        string resultType
    )
    {
        // The first return sits outside the local's declaring block, so its value can't be
        // known there; the second can, and must still be followed.
        var body = $$"""
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            {{resultType}} answer;
            answer = endpoint.REFUSE(404);
            if (System.DateTime.Now.Second == 0)
            {
                return answer;
            }

            return answer;
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.DoesNotContain(warnings, IsCreateTaskOrphan);
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Bind_passed_to_a_helper_in_a_buried_lambda_is_orphaned(string host)
    {
        // The returned call's arguments are searched, but a lambda inside them is never
        // entered, the same as for terminals.
        var body = """
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            return await Respond.DONE(Respond.Later(() => endpoint.REFUSE(404)));
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.Single(warnings, IsCreateTaskOrphan);
    }

    [Fact]
    public void Bind_passed_to_a_returned_helper_in_a_function_is_not_orphaned()
    {
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class TaskFunctions
            {
                [Function("create-task")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tasks")] object request
                )
                {
                    var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
                    return endpoint.Refuse(404);
                }
            }
            """;

        var warnings = RunCheck(Contract, AzureAttributes, BindingHelpers, implementation);

        Assert.DoesNotContain(warnings, IsCreateTaskOrphan);
        AssertMissingWarning(warnings, "CreateTask");
    }

    [Fact]
    public void Bind_passed_to_a_helper_outside_a_route_handler_is_orphaned()
    {
        // Only a handler's returned path uses a binding; a plain method returning a helper
        // call over one isn't answering any request.
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Refusals
            {
                public static IActionResult Wrap(TaskInput input) =>
                    Respond.Refuse(TasksContract.CreateTask.Bind(input), 404);
            }
            """;

        var warnings = RunCheck(Contract, BindingHelpers, implementation);

        Assert.Single(warnings, IsCreateTaskOrphan);
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Bind_passed_to_a_helper_in_a_match_arm_is_not_orphaned(string host)
    {
        var body = """
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            var outcome = await tasks.Find();
            return outcome.Match(
                found: _ => endpoint.REFUSE(409),
                missing: _ => endpoint.REFUSE(404)
            );
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.DoesNotContain(warnings, IsCreateTaskOrphan);
        AssertMissingWarning(warnings, "CreateTask");
    }

    [Theory]
    [InlineData(
        "mvc",
        "reason => TasksContract.CreateTask.Bind(new TaskInput(reason)).REFUSE(422)"
    )]
    [InlineData(
        "minimal",
        "reason => TasksContract.CreateTask.Bind(new TaskInput(reason)).REFUSE(422)"
    )]
    [InlineData(
        "mvc",
        "reason => { var refused = TasksContract.CreateTask.Bind(new TaskInput(reason)); return refused.REFUSE(422); }"
    )]
    [InlineData(
        "minimal",
        "reason => { var refused = TasksContract.CreateTask.Bind(new TaskInput(reason)); return refused.REFUSE(422); }"
    )]
    public void Second_bind_in_a_refusal_arm_passed_to_a_helper_is_not_orphaned(
        string host,
        string missingArm
    )
    {
        // CaseBridge's shape: the refusal can't bind the input the success arm does, so it
        // binds its own and hands it straight to the Refuse helper.
        var body = $$"""
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => endpoint.Success(task).ADAPT(),
                missing: {{missingArm}}
            );
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "CreateTask");
    }

    [Theory]
    [InlineData("mvc", "endpoint.REFUSE(404);")]
    [InlineData("minimal", "endpoint.REFUSE(404);")]
    [InlineData("mvc", "System.Func<string, object> refuse = _ => endpoint.REFUSE(404);")]
    [InlineData("minimal", "System.Func<string, object> refuse = _ => endpoint.REFUSE(404);")]
    public void Bind_passed_to_a_helper_off_the_returned_path_is_orphaned(
        string host,
        string statement
    )
    {
        // A helper call whose result is thrown away, or one inside a lambda held in a
        // local the handler never returns, doesn't use the binding.
        var body = $$"""
            var endpoint = TasksContract.CreateTask.Bind(new TaskInput("Test"));
            {{statement}}
            return TasksContract.HeadTasks.Success().ADAPT();
            """;

        var warnings = RunWithHelpers(host, body);

        Assert.Single(warnings, IsCreateTaskOrphan);
        AssertMissingWarning(warnings, "CreateTask");
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Unreachable_return_in_a_lambda_arm_is_not_coverage(string host)
    {
        var body = """
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task =>
                {
                    if (false)
                    {
                        return TasksContract.ListTasks.Success(task).ADAPT();
                    }

                    return TasksContract.HeadTasks.Success().ADAPT();
                },
                missing: _ => TasksContract.HeadTasks.Success().ADAPT()
            );
            """;

        AssertMissingWarning(
            RunCheck(Contract, OutcomeTypes, TasksHandler(host, body)),
            "ListTasks"
        );
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Helper_call_inside_a_lambda_arm_is_not_coverage(string host)
    {
        var helper = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Respond
            {
                public static IActionResult Done(RivetResult result) => result.ToActionResult();

                public static IResult DoneResult(RivetResult result) => result.ToResult();
            }
            """;
        var done = host == "mvc" ? "Done" : "DoneResult";
        var body = $$"""
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => Respond.{{done}}(TasksContract.ListTasks.Success(task)),
                missing: _ => Respond.{{done}}(TasksContract.ListTasks.Error(404))
            );
            """;

        AssertMissingWarning(
            RunCheck(Contract, OutcomeTypes, helper, TasksHandler(host, body)),
            "ListTasks"
        );
    }

    [Theory]
    [InlineData("mvc")]
    [InlineData("minimal")]
    public void Match_arm_on_another_contract_field_is_not_coverage_for_this_one(string host)
    {
        var otherContract = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class OtherContract
            {
                public static readonly RouteDefinition<TaskDto> ListOthers =
                    Define.Get<TaskDto>("/api/others");
            }
            """;
        var body = """
            var outcome = await tasks.Find();
            return outcome.Match(
                found: task => OtherContract.ListOthers.Success(task).ADAPT(),
                missing: _ => OtherContract.ListOthers.Error(404).ADAPT()
            );
            """;

        var warnings = RunCheck(Contract, otherContract, OutcomeTypes, TasksHandler(host, body));

        AssertMissingWarning(warnings, "ListTasks");
        Assert.Contains(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.RouteMismatch
                && warning.FieldName == "ListOthers"
                && warning.Actual == "/api/tasks"
        );
    }

    [Fact]
    public void Enum_binding_reads_the_map_lambda_not_the_match_arm()
    {
        // The terminal sits in a Match arm, but the parameters ASP.NET binds are the
        // Map* lambda's, so that is the handler RIV4005 reads.
        var state = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [JsonConverter(typeof(RivetCamelCaseEnumConverter<TaskState>))]
            public enum TaskState
            {
                Open,
                InProgress,
            }
            """;
        var implementation = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", async (TaskState state, TaskService tasks) =>
                        (await tasks.Find()).Match(
                            found: task => TasksContract.ListTasks.Success(task).ToResult(),
                            missing: _ => TasksContract.ListTasks.Error(404).ToResult()
                        ));
                }
            }
            """;

        var warnings = RunCheck(Contract, state, OutcomeTypes, implementation);

        // The contract declares no 'state', so RIV4006 reports it too; this test is about RIV4005.
        var warning = Assert.Single(
            warnings,
            warning => warning.Kind == CoverageWarningKind.UnbindableEnumParameter
        );
        Assert.Equal("ListTasks", warning.FieldName);
        Assert.Contains("'state'", warning.Message);
    }

    // ---------------------------------------------------------------
    // RIV4006: the contract's inputs against what the handler binds.
    // ---------------------------------------------------------------

    private const string InputContract = """
        using System.Text.Json.Serialization;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record NoteDto(string Id);

        [RivetType]
        public sealed record NoteSearch(
            string? Search,
            [property: JsonPropertyName("q")] string? Text,
            [property: RivetHeader("X-Tenant")] string? Tenant
        );

        [RivetType]
        public sealed record NoteInput(string Title);

        [RivetContract]
        public static class NotesContract
        {
            public static readonly RouteDefinition<NoteSearch, NoteDto> Search =
                Define.Get<NoteSearch, NoteDto>("/api/notes");

            public static readonly RouteDefinition<NoteDto> Get =
                Define.Get<NoteDto>("/api/notes/{id}");

            public static readonly RouteDefinition<NoteInput, NoteDto> Create =
                Define.Post<NoteInput, NoteDto>("/api/notes");

            public static readonly RouteDefinition<NoteDto> Upload =
                Define.Post<NoteDto>("/api/notes/upload").AcceptsFile();
        }
        """;

    private const string SearchBinding =
        "[FromQuery] string? search, [FromQuery(Name = \"q\")] string? text, "
        + "[FromHeader(Name = \"X-Tenant\")] string? tenant";

    private static readonly string _searchTerminal =
        "NotesContract.Search.Bind(new NoteSearch(search, text, tenant)).Success(new NoteDto(\"1\"))";

    private static string MinimalNotes(
        string verb,
        string route,
        string parameters,
        string terminal,
        string body = ""
    ) =>
        $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class NoteEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.Map{{verb}}("{{route}}", ({{parameters}}) =>
                    {
                        {{body}}
                        return {{terminal}}.ToResult();
                    });
                }
            }
            """;

    private static string MvcNotes(
        string verb,
        string route,
        string parameters,
        string terminal,
        bool apiController = true,
        string body = ""
    ) =>
        $$"""
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            {{(apiController ? "[ApiController]" : "")}}
            [Route("api/notes")]
            public sealed class NotesController : ControllerBase
            {
                [Http{{verb}}("{{route}}")]
                public IActionResult Handle({{parameters}})
                {
                    {{body}}
                    return {{terminal}}.ToActionResult();
                }
            }
            """;

    private static List<CoverageWarning> InputMismatches(
        IReadOnlyList<CoverageWarning> warnings,
        string field
    ) =>
        warnings
            .Where(warning =>
                warning.Kind == CoverageWarningKind.InputBindingMismatch
                && warning.FieldName == field
            )
            .ToList();

    [Theory]
    [InlineData("minimal", SearchBinding)]
    [InlineData("mvc", SearchBinding)]
    [InlineData(
        "minimal",
        "string? search, [FromQuery(Name = \"Q\")] string? text, [FromHeader(Name = \"x-tenant\")] string? tenant"
    )]
    public void Handler_binding_every_declared_input_where_declared_is_clean(
        string host,
        string parameters
    )
    {
        var handler =
            host == "mvc"
                ? MvcNotes("Get", "", parameters, _searchTerminal)
                : MinimalNotes("Get", "/api/notes", parameters, _searchTerminal);

        var warnings = RunCheck(InputContract, handler);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "Search");
    }

    [Fact]
    public void AsParameters_on_the_contract_input_misses_a_json_renamed_query_name()
    {
        // The spec sends ?q=, but ASP.NET binds the member by its C# name, ?Text=.
        var handler = MinimalNotes(
            "Get",
            "/api/notes",
            "[AsParameters] NoteSearch input",
            "NotesContract.Search.Bind(input).Success(new NoteDto(\"1\"))"
        );

        var mismatches = InputMismatches(RunCheck(InputContract, handler), "Search");

        Assert.Equal(4, mismatches.Count);
        Assert.Contains(
            mismatches,
            warning =>
                warning is { Expected: "(not declared)", Actual: "query parameter 'Text'" }
                && warning.Message!.Contains("Declare it on the contract's input type")
        );
        Assert.Contains(
            mismatches,
            warning =>
                warning is { Expected: "query parameter 'q'", Actual: "(not bound)" }
                && warning.Message!.Contains("[FromQuery(Name = \"q\")]")
        );
        // The [RivetHeader] member is a header in the spec, but [AsParameters] binds it from
        // the query under its C# name.
        Assert.Contains(
            mismatches,
            warning => warning is { Expected: "header 'X-Tenant'", Actual: "(not bound)" }
        );
        Assert.Contains(
            mismatches,
            warning => warning is { Expected: "(not declared)", Actual: "query parameter 'Tenant'" }
        );
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("mvc")]
    public void Declared_input_bound_from_another_source_is_a_mismatch(string host)
    {
        var parameters =
            "[FromHeader] string? search, [FromQuery(Name = \"q\")] string? text, "
            + "[FromHeader(Name = \"X-Tenant\")] string? tenant";
        var handler =
            host == "mvc"
                ? MvcNotes("Get", "", parameters, _searchTerminal)
                : MinimalNotes("Get", "/api/notes", parameters, _searchTerminal);

        var mismatch = Assert.Single(InputMismatches(RunCheck(InputContract, handler), "Search"));

        Assert.Equal("query parameter 'search'", mismatch.Expected);
        Assert.Equal("header 'search'", mismatch.Actual);
        Assert.Contains("[FromQuery(Name = \"search\")]", mismatch.Message);
    }

    [Theory]
    [InlineData("minimal", "", true)]
    [InlineData("minimal", "string id", false)]
    [InlineData("minimal", "[FromRoute(Name = \"id\")] string noteId", false)]
    [InlineData("minimal", "HttpRequest request", false)]
    [InlineData("mvc", "", true)]
    [InlineData("mvc", "string id", false)]
    public void Declared_route_value_the_handler_never_binds_is_reported(
        string host,
        string parameters,
        bool reported
    )
    {
        const string terminal = "NotesContract.Get.Success(new NoteDto(\"1\"))";
        var handler =
            host == "mvc"
                ? MvcNotes("Get", "{id}", parameters, terminal)
                : MinimalNotes("Get", "/api/notes/{id}", parameters, terminal);

        var mismatches = InputMismatches(RunCheck(InputContract, handler), "Get");

        if (reported)
        {
            var mismatch = Assert.Single(mismatches);
            Assert.Equal("route value 'id'", mismatch.Expected);
            Assert.Equal("(not bound)", mismatch.Actual);
        }
        else
        {
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void Mvc_action_reading_the_request_itself_is_not_told_an_input_is_unbound()
    {
        var handler = MvcNotes(
            "Get",
            "{id}",
            "",
            "NotesContract.Get.Success(new NoteDto(\"1\"))",
            body: "var id = Request.RouteValues[\"id\"];"
        );

        Assert.Empty(InputMismatches(RunCheck(InputContract, handler), "Get"));
    }

    [Theory]
    [InlineData("minimal", "NoteInput input", true, false)]
    [InlineData("minimal", "[FromBody] NoteInput input", true, false)]
    [InlineData("mvc", "NoteInput input", true, false)]
    [InlineData("mvc", "[FromBody] NoteInput input", false, false)]
    [InlineData("mvc", "NoteInput input", false, true)]
    public void Declared_body_must_be_bound_as_the_body(
        string host,
        string parameters,
        bool apiController,
        bool reported
    )
    {
        const string terminal = "NotesContract.Create.Bind(input).Success(new NoteDto(\"1\"))";
        var handler =
            host == "mvc"
                ? MvcNotes("Post", "", parameters, terminal, apiController)
                : MinimalNotes("Post", "/api/notes", parameters, terminal);

        var mismatches = InputMismatches(RunCheck(InputContract, handler), "Create");

        if (reported)
        {
            // Without [ApiController], MVC binds the record from route, query and form.
            Assert.Contains(
                mismatches,
                warning => warning is { Expected: "request body", Actual: "(not bound)" }
            );
            Assert.Contains(
                mismatches,
                warning => warning.Actual == "route, query or form value 'Title'"
            );
        }
        else
        {
            Assert.Empty(mismatches);
        }
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("mvc")]
    public void Body_the_contract_does_not_declare_is_reported(string host)
    {
        const string terminal = "NotesContract.Get.Success(new NoteDto(\"1\"))";
        const string parameters = "string id, [FromBody] NoteInput extra";
        var handler =
            host == "mvc"
                ? MvcNotes("Get", "{id}", parameters, terminal)
                : MinimalNotes("Get", "/api/notes/{id}", parameters, terminal);

        var mismatch = Assert.Single(InputMismatches(RunCheck(InputContract, handler), "Get"));

        Assert.Equal("(no request body)", mismatch.Expected);
        Assert.Contains("'extra' reads the request body", mismatch.Message);
    }

    [Fact]
    public void ApiController_query_record_without_FromQuery_is_read_as_the_body()
    {
        // [ApiController] infers [FromBody] for the record, so nothing reads the query.
        var handler = MvcNotes(
            "Get",
            "",
            "NoteSearch input",
            "NotesContract.Search.Bind(input).Success(new NoteDto(\"1\"))"
        );

        var mismatches = InputMismatches(RunCheck(InputContract, handler), "Search");

        Assert.Equal(3, mismatches.Count);
        Assert.All(mismatches, warning => Assert.Equal("(not bound)", warning.Actual));
    }

    [Fact]
    public void Authorization_header_belongs_to_security_not_the_contract_inputs()
    {
        var handler = MinimalNotes(
            "Get",
            "/api/notes/{id}",
            "string id, [FromHeader(Name = \"Authorization\")] string? authorization",
            "NotesContract.Get.Success(new NoteDto(\"1\"))"
        );

        Assert.Empty(InputMismatches(RunCheck(InputContract, handler), "Get"));
    }

    [Theory]
    [InlineData("minimal", "IFormFile file", false)]
    [InlineData("minimal", "IFormFileCollection files", false)]
    [InlineData("mvc", "IFormFile file", false)]
    [InlineData("minimal", "[FromBody] NoteInput input", true)]
    public void Multipart_file_is_bound_from_the_form(string host, string parameters, bool reported)
    {
        const string terminal = "NotesContract.Upload.Success(new NoteDto(\"1\"))";
        var handler =
            host == "mvc"
                ? MvcNotes("Post", "upload", parameters, terminal)
                : MinimalNotes("Post", "/api/notes/upload", parameters, terminal);

        var mismatches = InputMismatches(RunCheck(InputContract, handler), "Upload");

        if (reported)
        {
            Assert.Contains(mismatches, warning => warning.Actual == "JSON request body");
            Assert.Contains(mismatches, warning => warning.Expected == "form field 'file'");
        }
        else
        {
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void Functions_handler_inputs_are_not_compared()
    {
        // Functions read the query, headers and body from the request by hand.
        var implementation = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class NoteFunctions
            {
                [Function("search-notes")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notes")] object request
                ) => NotesContract.Search.Bind(new NoteSearch(null, null, null))
                    .Success(new NoteDto("1"))
                    .ToActionResult();
            }
            """;

        var warnings = RunCheck(InputContract, AzureAttributes, implementation);

        Assert.DoesNotContain(warnings, warning => warning.FieldName == "Search");
    }
}
