using Rivet;
using AnnotationApi.Domain;

namespace AnnotationApi.Application.CreateTask;

/// TS: { title: string; description: string | null; priority: Priority; assigneeId: string | null; labelNames: string[] }
[RivetType]
public sealed record CreateTaskCommand(
    string Title,
    string? Description,       // → string | null
    Priority Priority,          // → "Low" | "Medium" | "High" | "Critical"
    Guid? AssigneeId,           // → string | null
    List<string> LabelNames);   // → string[]

/// TS: { id: string; createdAt: string }
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
