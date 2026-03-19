using Rivet;

namespace TaskBoard.Application;

/// TS: PagedResult<T> = { items: T[]; totalCount: number; page: number; pageSize: number }
/// Generic is preserved in TypeScript output.
[RivetType]
public sealed record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize);
