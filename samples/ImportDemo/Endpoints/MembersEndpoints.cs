using Rivet;

namespace ImportDemo.Endpoints;

public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            MembersContract.List.Route,
            async () => (await MembersContract.List.Invoke(async () => GetMembers())).ToResult()
        );

        endpoints.MapGet(
            MembersContract.GetById.Route,
            async (string id) =>
                (
                    await MembersContract.GetById.Invoke(
                        new GetByIdInput(id),
                        async input =>
                        {
                            var members = GetMembers();
                            return members.First(m => m.Id == input.Id);
                        }
                    )
                ).ToResult()
        );

        return endpoints;
    }

    private static List<MemberDto> GetMembers() =>
        [
            new("1", "Alice", "alice@example.com", "admin"),
            new("2", "Bob", "bob@example.com", "member"),
            new("3", "Charlie", "charlie@example.com", "viewer"),
        ];

    private static IResult ToResult<T>(this RivetResult<T> result) =>
        Results.Json(result.Data, statusCode: result.StatusCode);
}
