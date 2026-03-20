using ImportDemo;
using Rivet;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory data — imagine this is a database
var members = new List<MemberDto>
{
    new("1", "Alice", "alice@example.com", "admin"),
    new("2", "Bob", "bob@example.com", "member"),
    new("3", "Charlie", "charlie@example.com", "viewer"),
};

// Wire the contracts to handlers — that's it
app.MapGet(MembersContract.List.Route, async () =>
    (await MembersContract.List.Invoke(async () => members)).ToResult());

app.MapGet(MembersContract.GetById.Route, async (string id) =>
    (await MembersContract.GetById.Invoke(async () =>
        members.FirstOrDefault(m => m.Id == id)!)).ToResult());

app.Run();

// One-line bridge — you write this once
static class RivetExtensions
{
    public static IResult ToResult<T>(this RivetResult<T> r) => Results.Json(r.Data, statusCode: r.StatusCode);
}
