using TaskBoard.Application.CreateTask;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<CreateTaskUseCase>();

var app = builder.Build();

app.MapControllers();
app.Run();
