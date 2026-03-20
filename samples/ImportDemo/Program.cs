using ImportDemo;
using Rivet;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(MembersContract.List.Route, async () =>
{
    var members = GetMembers();
    return (await MembersContract.List.Invoke(async () => members)).ToResult();
});

app.MapGet(MembersContract.GetById.Route, async (string id) =>
{
    var members = GetMembers();
    var member = members.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"Member {id} not found");
    return (await MembersContract.GetById.Invoke(async () => member)).ToResult();
});

app.Run();

// Imagine this is a database call
List<MemberDto> GetMembers() =>
[
    new("1", "Alice", "alice@example.com", "admin"),
    new("2", "Bob", "bob@example.com", "member"),
    new("3", "Charlie", "charlie@example.com", "viewer"),
];

static class RivetExtensions
{
    public static IResult ToResult<T>(this RivetResult<T> r) => Results.Json(r.Data, statusCode: r.StatusCode);
}
