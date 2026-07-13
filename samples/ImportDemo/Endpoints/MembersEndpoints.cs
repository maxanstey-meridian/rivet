using Rivet;

namespace ImportDemo.Endpoints;

public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            MembersContract.List.Route,
            () => MembersContract.List.Success(GetMembers()).ToResult()
        );

        endpoints.MapGet(
            MembersContract.GetById.Route,
            (string id) =>
            {
                var input = new GetByIdInput(id);
                var endpoint = MembersContract.GetById.Bind(input);
                var member = GetMembers().First(m => m.Id == input.Id);
                return endpoint.Success(member).ToResult();
            }
        );

        return endpoints;
    }

    private static List<MemberDto> GetMembers() =>
        [
            new("1", "Alice", "alice@example.com", "admin"),
            new("2", "Bob", "bob@example.com", "member"),
            new("3", "Charlie", "charlie@example.com", "viewer"),
        ];
}
