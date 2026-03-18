using Rivet;

namespace TaskBoard.Application;

[RivetType]
public sealed record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize);
