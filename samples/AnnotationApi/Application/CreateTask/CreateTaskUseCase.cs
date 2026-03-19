using Rivet;
using TaskBoard.Domain;

namespace TaskBoard.Application.CreateTask;

[RivetType]
public sealed record CreateTaskCommand(
    string Title,
    string? Description,
    Priority Priority,
    Guid? AssigneeId,
    List<string> LabelNames);

[RivetType]
public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);

public sealed class CreateTaskUseCase
{
    public Task<CreateTaskResult> ExecuteAsync(CreateTaskCommand command, CancellationToken ct)
    {
        // In a real app: validate, persist, return
        return Task.FromResult(new CreateTaskResult(Guid.NewGuid(), DateTime.UtcNow));
    }
}
