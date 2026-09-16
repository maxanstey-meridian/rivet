using AnnotationApi.Application;
using AnnotationApi.Application.CreateTask;
using AnnotationApi.Domain;
using Microsoft.AspNetCore.Mvc;
using Rivet;

namespace AnnotationApi.Controllers;

// Response DTOs — colocated with the controller that serves them
[RivetType]
public sealed record TaskListItemDto(
    Guid Id,
    string Title,
    WorkItemStatus Status,
    Priority Priority,
    string? AssigneeName,
    DateTime CreatedAt
);

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
    DateTime? CompletedAt
);

[RivetType]
public sealed record CommentDto(Guid Id, string Body, string AuthorName, DateTime CreatedAt);

// Request DTOs
[RivetType]
public sealed record UpdateWorkItemStatusRequest(WorkItemStatus Status);

[RivetType]
public sealed record AddCommentRequest(string Body);

[RivetType]
public sealed record AttachmentResultDto(Guid Id, string FileName, long Size);

[RivetType]
public sealed record SearchTasksRequest(string Term, int MinPriority, string? Tag);

[RivetType]
public sealed record NotFoundDto(string Message);

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
        CancellationToken ct
    )
    {
        _ = ct;
        _ = status;
        // In a real app: query the database
        var items = new List<TaskListItemDto>();
        return Ok(new PagedResult<TaskListItemDto>(items, 0, page, pageSize));
    }

    [RivetEndpoint]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        _ = ct;
        _ = id;
        return Ok(default(TaskDetailDto));
    }

    [RivetEndpoint]
    [HttpPost]
    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command,
        CancellationToken ct
    )
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
        CancellationToken ct
    )
    {
        _ = ct;
        _ = id;
        _ = request;
        return Ok(default(TaskDetailDto));
    }

    [RivetEndpoint]
    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType(typeof(CommentDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddComment(
        Guid id,
        [FromBody] AddCommentRequest request,
        CancellationToken ct
    )
    {
        _ = ct;
        _ = id;
        _ = request;
        return StatusCode(StatusCodes.Status201Created, default(CommentDto));
    }

    [RivetEndpoint]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        _ = ct;
        _ = id;
        return Ok();
    }

    [RivetEndpoint]
    [HttpPost("{id:guid}/attachments")]
    [ProducesResponseType(typeof(AttachmentResultDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Attach(Guid id, IFormFile file, CancellationToken ct)
    {
        _ = ct;
        _ = id;
        return StatusCode(
            StatusCodes.Status201Created,
            new AttachmentResultDto(Guid.NewGuid(), file.FileName, file.Length)
        );
    }

    // Default MVC inference exercised on the real host: the defaulted scalar binds
    // from the query string and the unattributed complex type binds from the body —
    // [FromQuery(Name=)] renames the wire surface to match the actual request.
    [RivetEndpoint]
    [HttpPost("search")]
    [ProducesResponseType(typeof(PagedResult<TaskListItemDto>), StatusCodes.Status200OK)]
    public IActionResult Search(
        [FromQuery(Name = "q")] string term,
        [FromQuery] int limit,
        SearchTasksRequest request
    )
    {
        _ = term;
        _ = request;
        return Ok(new PagedResult<TaskListItemDto>([], 0, 1, limit));
    }

    // Plain-string action returning the string directly: MVC's string formatter
    // writes 200 text/plain (Ok(string) would JSON-serialize it instead) — the
    // emitted contract must agree on both status AND media type. The request side
    // accepts the JSON string form: MVC registers no text/plain input formatter by
    // default, so [Consumes("text/plain")] would 415 the JSON string body.
    [RivetEndpoint]
    [HttpPost("echo")]
    public string Echo([FromBody] string message)
    {
        return message;
    }

    // Plain DTO action with no response metadata: MVC's JSON formatter returns
    // 200 application/json — the extraction default must describe exactly that.
    // The DTO return type is statically knowable, so the emitted contract's
    // application/json content is provable, not assumed.
    [RivetEndpoint]
    [HttpPost("labels")]
    public CommentDto AddLabel([FromBody] AddCommentRequest request)
    {
        return new CommentDto(Guid.NewGuid(), request.Body, "system", DateTime.UtcNow);
    }
}
