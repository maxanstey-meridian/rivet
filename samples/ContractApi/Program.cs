using ContractApi.Contracts;
using ContractApi.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

// Minimal API example — .Route and .Method are available at runtime.
// Mapped under /minimal to avoid colliding with the controller's /api/health
// action (duplicate registration is an AmbiguousMatchException at runtime).
app.MapGet("/minimal" + MembersContract.Health.Route, async () =>
    (await MembersContract.Health.Invoke(async () => { })).ToResult());

app.Run();
