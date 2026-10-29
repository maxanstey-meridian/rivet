using System.Text.Json;
using ContractApi.Auth;
using ContractApi.Endpoints;
using ContractApi.Models;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// [ApiController] model validation rejects invalid request DTOs before the
// action runs. The default response is a 400 ValidationProblemDetails — but
// the contract declares .Returns<ValidationErrorDto>(422), so reshape the
// automatic response to match the spec.
builder
    .Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context
                .ModelState.Where(entry => entry.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    entry => JsonNamingPolicy.CamelCase.ConvertName(entry.Key),
                    entry => entry.Value!.Errors.Select(e => e.ErrorMessage).ToArray()
                );

            return new UnprocessableEntityObjectResult(
                new ValidationErrorDto("Validation failed", errors)
            );
        }
    );

// The contract's .Secure("bearer") endpoints are [Authorize]d, and this is the scheme that
// lets a caller through. DemoBearerHandler accepts any token; a real app registers
// JwtBearer (or its own handler) under the same name.
builder
    .Services.AddAuthentication(DemoBearerHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoBearerHandler>(
        DemoBearerHandler.SchemeName,
        null
    );
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapMembersEndpoints();

app.Run();
