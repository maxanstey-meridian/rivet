using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class CoverageCheckerTests
{
    private const string TasksContract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record TaskDto(string Id, string Title);

        [RivetType]
        public sealed record CreateTaskInput(string Title);

        [RivetType]
        public sealed record UpdateTaskInput(string Id, string Title);

        [RivetType]
        public sealed record PatchTaskInput(string Title);

        [RivetContract]
        public static class TasksContract
        {
            public static readonly RouteDefinition<TaskDto> ListTasks =
                Define.Get<TaskDto>("/api/tasks");

            public static readonly RouteDefinition<CreateTaskInput, TaskDto> CreateTask =
                Define.Post<CreateTaskInput, TaskDto>("/api/tasks");

            public static readonly RouteDefinition<UpdateTaskInput, TaskDto> UpdateTask =
                Define.Put<UpdateTaskInput, TaskDto>("/api/tasks/{id}");

            public static readonly RouteDefinition<PatchTaskInput, TaskDto> PatchTask =
                Define.Patch<PatchTaskInput, TaskDto>("/api/tasks/{id}");

            public static readonly RouteDefinition RemoveTask =
                Define.Delete("/api/tasks/{id}");
        }
        """;

    private const string MembersContract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record MemberDto(string Id, string Name);

        [RivetContract]
        public static class MembersContract
        {
            public static readonly RouteDefinition<MemberDto> ListMembers =
                Define.Get<MemberDto>("/api/members");
        }
        """;

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, IReadOnlyList<CoverageWarning> Warnings)
        RunCheck(params string[] sources)
    {
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);
        var warnings = CoverageChecker.Check(compilation, endpoints);
        return (endpoints, warnings);
    }

    // --- Controller tests (correct — no warnings) ---

    [Fact]
    public void Controller_Get_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "ListTasks");
    }

    [Fact]
    public void Controller_Post_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public async Task<IActionResult> Create([FromBody] CreateTaskInput input)
                {
                    var result = await TasksContract.CreateTask.Invoke(input, async i => new TaskDto("1", i.Title));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "CreateTask");
    }

    [Fact]
    public void Controller_Put_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPut("{id}")]
                public async Task<IActionResult> Update(string id, [FromBody] UpdateTaskInput input)
                {
                    var result = await TasksContract.UpdateTask.Invoke(input, async i => new TaskDto(i.Id, i.Title));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "UpdateTask");
    }

    [Fact]
    public void Controller_Patch_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPatch("{id}")]
                public async Task<IActionResult> Patch(string id, [FromBody] PatchTaskInput input)
                {
                    var result = await TasksContract.PatchTask.Invoke(input, async i => new TaskDto("1", i.Title));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "PatchTask");
    }

    [Fact]
    public void Controller_Delete_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpDelete("{id}")]
                public async Task<IActionResult> Remove(string id)
                {
                    var result = await TasksContract.RemoveTask.Invoke(async () => { });
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "RemoveTask");
    }

    [Fact]
    public void Controller_AllEndpoints_AllCorrect_NoWarnings()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }

                [HttpPost]
                public async Task<IActionResult> Create([FromBody] CreateTaskInput input)
                {
                    var result = await TasksContract.CreateTask.Invoke(input, async i => new TaskDto("1", i.Title));
                    return null!;
                }

                [HttpPut("{id}")]
                public async Task<IActionResult> Update(string id, [FromBody] UpdateTaskInput input)
                {
                    var result = await TasksContract.UpdateTask.Invoke(input, async i => new TaskDto(i.Id, i.Title));
                    return null!;
                }

                [HttpPatch("{id}")]
                public async Task<IActionResult> Patch(string id, [FromBody] PatchTaskInput input)
                {
                    var result = await TasksContract.PatchTask.Invoke(input, async i => new TaskDto("1", i.Title));
                    return null!;
                }

                [HttpDelete("{id}")]
                public async Task<IActionResult> Remove(string id)
                {
                    var result = await TasksContract.RemoveTask.Invoke(async () => { });
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.Empty(warnings);
    }

    // --- Controller tests (mismatches — warnings) ---

    [Fact]
    public void Controller_WrongHttpMethod_Warning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpPost]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var warning = Assert.Single(warnings, w => w.Kind == CoverageWarningKind.HttpMethodMismatch);
        Assert.Equal("GET", warning.Expected);
        Assert.Equal("POST", warning.Actual);
        Assert.Equal("ListTasks", warning.FieldName);
    }

    [Fact]
    public void Controller_WrongRoute_Warning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/items")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var warning = Assert.Single(warnings, w => w.Kind == CoverageWarningKind.RouteMismatch);
        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/api/items", warning.Actual);
    }

    [Fact]
    public void Controller_MissingImplementation_Warning()
    {
        // No implementation at all — no .Invoke() calls
        var impl = """
            namespace Test;

            public sealed class Dummy { }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var missing = warnings.Where(w => w.Kind == CoverageWarningKind.MissingImplementation).ToList();
        Assert.Equal(5, missing.Count); // All 5 fields missing
    }

    [Fact]
    public void Controller_RouteWithConstraints_Normalized()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpDelete("{id:guid}")]
                public async Task<IActionResult> Remove(string id)
                {
                    var result = await TasksContract.RemoveTask.Invoke(async () => { });
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        // RemoveTask should match — constraints stripped
        Assert.DoesNotContain(warnings, w =>
            w.FieldName == "RemoveTask" && w.Kind == CoverageWarningKind.RouteMismatch);
        Assert.DoesNotContain(warnings, w =>
            w.FieldName == "RemoveTask" && w.Kind == CoverageWarningKind.HttpMethodMismatch);
    }

    // --- Minimal API tests (correct — no warnings) ---

    [Fact]
    public void MinimalApi_MapGet_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/tasks", async () =>
                    {
                        var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "ListTasks");
    }

    [Fact]
    public void MinimalApi_MapPost_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/api/tasks", async (CreateTaskInput input) =>
                    {
                        var result = await TasksContract.CreateTask.Invoke(input, async i => new TaskDto("1", i.Title));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "CreateTask");
    }

    [Fact]
    public void MinimalApi_MapPut_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPut("/api/tasks/{id}", async (string id, UpdateTaskInput input) =>
                    {
                        var result = await TasksContract.UpdateTask.Invoke(input, async i => new TaskDto(i.Id, i.Title));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "UpdateTask");
    }

    [Fact]
    public void MinimalApi_MapPatch_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPatch("/api/tasks/{id}", async (string id, PatchTaskInput input) =>
                    {
                        var result = await TasksContract.PatchTask.Invoke(input, async i => new TaskDto("1", i.Title));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "PatchTask");
    }

    [Fact]
    public void MinimalApi_MapDelete_Correct_NoWarning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapDelete("/api/tasks/{id}", async (string id) =>
                    {
                        var result = await TasksContract.RemoveTask.Invoke(async () => { });
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        Assert.DoesNotContain(warnings, w => w.FieldName == "RemoveTask");
    }

    // --- Minimal API tests (mismatches — warnings) ---

    [Fact]
    public void MinimalApi_WrongMethod_Warning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/api/tasks", async () =>
                    {
                        var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var warning = Assert.Single(warnings, w =>
            w.FieldName == "ListTasks" && w.Kind == CoverageWarningKind.HttpMethodMismatch);
        Assert.Equal("GET", warning.Expected);
        Assert.Equal("POST", warning.Actual);
    }

    [Fact]
    public void MinimalApi_WrongRoute_Warning()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class TaskEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/items", async () =>
                    {
                        var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                        return result;
                    });
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var warning = Assert.Single(warnings, w =>
            w.FieldName == "ListTasks" && w.Kind == CoverageWarningKind.RouteMismatch);
        Assert.Equal("/api/tasks", warning.Expected);
        Assert.Equal("/api/items", warning.Actual);
    }

    // --- Multi-endpoint / edge cases ---

    [Fact]
    public void MultipleContracts_PartialCoverage()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, MembersContract, impl);

        // TasksContract: ListTasks covered, rest missing (4)
        // MembersContract: ListMembers missing (1)
        var missing = warnings.Where(w => w.Kind == CoverageWarningKind.MissingImplementation).ToList();
        Assert.Equal(5, missing.Count);
        Assert.Contains(missing, w => w.ContractName == "MembersContract" && w.FieldName == "ListMembers");
    }

    [Fact]
    public void SingleContract_OnlySomeImplemented()
    {
        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List()
                {
                    var result = await TasksContract.ListTasks.Invoke(async () => new TaskDto("1", "Test"));
                    return null!;
                }

                [HttpPost]
                public async Task<IActionResult> Create([FromBody] CreateTaskInput input)
                {
                    var result = await TasksContract.CreateTask.Invoke(input, async i => new TaskDto("1", i.Title));
                    return null!;
                }

                [HttpDelete("{id}")]
                public async Task<IActionResult> Remove(string id)
                {
                    var result = await TasksContract.RemoveTask.Invoke(async () => { });
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(TasksContract, impl);
        var missing = warnings.Where(w => w.Kind == CoverageWarningKind.MissingImplementation).ToList();
        Assert.Equal(2, missing.Count); // UpdateTask and PatchTask missing
        Assert.Contains(missing, w => w.FieldName == "UpdateTask");
        Assert.Contains(missing, w => w.FieldName == "PatchTask");
    }

    [Fact]
    public void VoidEndpoint_Covered()
    {
        var contract = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ActionsContract
            {
                public static readonly RouteDefinition RunAction =
                    Define.Post("/api/actions/run");
            }
            """;

        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/actions")]
            public sealed class ActionsController : ControllerBase
            {
                [HttpPost("run")]
                public async Task<IActionResult> Run()
                {
                    var result = await ActionsContract.RunAction.Invoke(async () => { });
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(contract, impl);
        Assert.Empty(warnings);
    }

    [Fact]
    public void InputOutputEndpoint_Covered()
    {
        var contract = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MyInput(string Name);

            [RivetType]
            public sealed record MyOutput(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<MyInput, MyOutput> CreateItem =
                    Define.Post<MyInput, MyOutput>("/api/items");
            }
            """;

        var impl = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/items")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpPost]
                public async Task<IActionResult> Create([FromBody] MyInput input)
                {
                    var result = await ItemsContract.CreateItem.Invoke(input, async i => new MyOutput("1"));
                    return null!;
                }
            }
            """;

        var (_, warnings) = RunCheck(contract, impl);
        Assert.Empty(warnings);
    }

    [Fact]
    public void InvokeOnNonContractField_Ignored()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Rivet;

            namespace Test;

            public static class NotAContract
            {
                public static readonly RouteDefinition<string> SomeField =
                    Define.Get<string>("/api/whatever");
            }

            public sealed class SomeService
            {
                public async Task DoStuff()
                {
                    var result = await NotAContract.SomeField.Invoke(async () => "hi");
                }
            }
            """;

        var (_, warnings) = RunCheck(source);
        Assert.Empty(warnings);
    }

    [Fact]
    public void NoContracts_NoWarnings()
    {
        var source = """
            namespace Test;

            public sealed class Dummy
            {
                public void DoNothing() { }
            }
            """;

        var (_, warnings) = RunCheck(source);
        Assert.Empty(warnings);
    }
}
