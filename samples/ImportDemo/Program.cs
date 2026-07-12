using ImportDemo.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapMembersEndpoints();

app.Run();
