namespace TaskBoard.Application.Ports;

public interface ITaskRepository
{
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
}
