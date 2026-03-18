using Microsoft.AspNetCore.Mvc;
using Rivet;
using TaskBoard.Application;
using TaskBoard.Application.CreateTask;
using TaskBoard.Domain;

namespace TaskBoard.Controllers;

// Response DTOs — colocated with the controller that serves them
[RivetType]
public sealed record TaskListItemDto(
    Guid Id,
    string Title,
    WorkItemStatus Status,
    Priority Priority,
    string? AssigneeName,
    DateTime CreatedAt);

[RivetType]
public sealed record TaskDetailDto(
    Guid Id,
    string Title,
    string? Description,
    WorkItemStatus Status,
    Priority Priority,
    string? AssigneeName,
    List<Label> Labels,
    List<CommentDto> Comments,
    DateTime CreatedAt,
    DateTime? CompletedAt);

[RivetType]
public sealed record CommentDto(
    Guid Id,
    string Body,
    string AuthorName,
    DateTime CreatedAt);

// Request DTOs
[RivetType]
public sealed record UpdateWorkItemStatusRequest(WorkItemStatus Status);

[RivetType]
public sealed record AddCommentRequest(string Body);

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(CreateTaskUseCase createTask) : ControllerBase
{
    [RivetEndpoint]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<TaskListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        // In a real app: query the database
        var items = new List<TaskListItemDto>();
        return Ok(new PagedResult<TaskListItemDto>(items, 0, page, pageSize));
    }

    [RivetEndpoint]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        return Ok(default(TaskDetailDto));
    }

    [RivetEndpoint]
    [HttpPost]
    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command,
        CancellationToken ct)
    {
        var result = await createTask.ExecuteAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [RivetEndpoint]
    [HttpPut("{id:guid}/status")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateWorkItemStatusRequest request,
        CancellationToken ct)
    {
        return Ok(default(TaskDetailDto));
    }

    [RivetEndpoint]
    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType(typeof(CommentDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddComment(
        Guid id,
        [FromBody] AddCommentRequest request,
        CancellationToken ct)
    {
        return StatusCode(StatusCodes.Status201Created, default(CommentDto));
    }

    [RivetEndpoint]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        return Ok();
    }
}
