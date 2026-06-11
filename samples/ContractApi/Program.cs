using System.Text.Json;
using ContractApi.Contracts;
using ContractApi.Controllers;
using ContractApi.Models;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// [ApiController] model validation rejects invalid request DTOs before the
// action runs. The default response is a 400 ValidationProblemDetails — but
// the contract declares .Returns<ValidationErrorDto>(422), so reshape the
// automatic response to match the spec.
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(entry => entry.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    entry => JsonNamingPolicy.CamelCase.ConvertName(entry.Key),
                    entry => entry.Value!.Errors.Select(e => e.ErrorMessage).ToArray());

            return new UnprocessableEntityObjectResult(
                new ValidationErrorDto("Validation failed", errors));
        });

var app = builder.Build();

app.MapControllers();

// Minimal API example — .Route and .Method are available at runtime.
// Mapped under /minimal to avoid colliding with the controller's /api/health
// action (duplicate registration is an AmbiguousMatchException at runtime).
app.MapGet("/minimal" + MembersContract.Health.Route, async () =>
    (await MembersContract.Health.Invoke(async () => { })).ToResult());

app.Run();
