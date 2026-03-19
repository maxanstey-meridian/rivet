using TaskBoard.Contracts;
using TaskBoard.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

// Minimal API example — .Route and .Method are available at runtime
app.MapGet(MembersContract.Health.Route, async () =>
    (await MembersContract.Health.Invoke(async () => { })).ToResult());

app.Run();
