using ImportDemo;
using Rivet;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(MembersContract.List.Route, async () =>
    (await MembersContract.List.Invoke(async () =>
    {
        var members = GetMembers();
        return members;
    })).ToResult());

app.MapGet(MembersContract.GetById.Route, async (string id) =>
    (await MembersContract.GetById.Invoke(new GetByIdInput(id), async input =>
    {
        var members = GetMembers();
        return members.First(m => m.Id == input.Id);
    })).ToResult());

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
