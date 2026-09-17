using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Extraction-oracle tests for the binding-and-properties slice: MVC default inference
/// (scalar → Query, complex → Body), explicit FromQuery/FromRoute/FromForm Name= wire
/// names, infrastructure exclusion, and the RIV1100 unresolved-binding diagnostic.
/// </summary>
public sealed class BindingPropertySurfaceTests
{
    private static IReadOnlyList<TsEndpointDefinition> WalkEndpoints(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        return CompilationHelper.WalkEndpoints(compilation, discovered, walker);
    }

    [Fact]
    public void Defaulted_Scalar_Binds_Query_And_Complex_Binds_Body()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SaveProfileRequest(string DisplayName, string Bio);

            [Route("api/profiles")]
            public sealed class ProfilesController
            {
                [RivetEndpoint]
                [HttpPut("{id:guid}")]
                [ProducesResponseType(typeof(void), 204)]
                public Task<IActionResult> Save(
                    Guid id,
                    [FromQuery(Name = "view")] string view,
                    SaveProfileRequest request,
                    [FromQuery] int maxResults = 25,
                    CancellationToken ct = default)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var ep = Assert.Single(endpoints);
        // The route param keeps the placeholder name; the query param honors FromQuery(Name=).
        var route = Assert.Single(ep.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", route.Name);

        var query = Assert.Single(
            ep.Params,
            p => p.Source == ParamSource.Query && p.Name == "view"
        );
        Assert.Equal("view", query.Name);

        // The C# default value makes the scalar optional on the wire (E8) —
        // HasExplicitDefaultValue flows into IsOptional.
        var defaulted = Assert.Single(
            ep.Params,
            p => p.Source == ParamSource.Query && p.Name == "maxResults"
        );
        Assert.True(defaulted.IsOptional);

        var body = Assert.Single(ep.Params, p => p.Source == ParamSource.Body);
        Assert.True(body.Type is TsType.TypeRef { Name: "SaveProfileRequest" });

        // The CancellationToken is infrastructure — never emitted as user input.
        Assert.DoesNotContain(ep.Params, p => p.Name == "ct");
    }

    [Fact]
    public void FromRoute_Name_Matches_Route_Placeholder()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/orgs")]
            public sealed class OrgsController
            {
                [RivetEndpoint]
                [HttpGet("{orgId}/members")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> List(
                    [FromRoute(Name = "orgId")] string orgId)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var ep = Assert.Single(endpoints);
        var route = Assert.Single(ep.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("orgId", route.Name);
    }

    [Fact]
    public void Unattributed_Interface_Parameter_Is_Reported_And_Excluded()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public interface IPricingService
            {
                decimal GetRate();
            }

            [Route("api/pricing")]
            public sealed class PricingController
            {
                [RivetEndpoint]
                [HttpGet("rate")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Rate(IPricingService pricing)
                    => throw new NotImplementedException();
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => WalkEndpoints(source));

        // Actionable diagnostic naming parameter and type; the input is excluded, not dropped silently.
        Assert.Contains("RIV1100", stderr);
        Assert.Contains("'pricing'", stderr);
        Assert.Contains("IPricingService", stderr);
        Assert.Contains("EXCLUDED", stderr);
    }

    [Fact]
    public void Host_Plumbing_Parameter_Is_Reported_And_Excluded()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/echo")]
            public sealed class EchoController
            {
                [RivetEndpoint]
                [HttpGet]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Echo(HttpContext http)
                    => throw new NotImplementedException();
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", stderr);
        Assert.Contains("'http'", stderr);
        Assert.Contains("HttpContext", stderr);
    }

    [Fact]
    public void Second_Unattributed_Complex_Parameter_Is_Reported_Not_Invented()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FirstBody(string A);
            [RivetType]
            public sealed record SecondBody(string B);

            [Route("api/two")]
            public sealed class TwoController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Post(FirstBody first, SecondBody second)
                    => throw new NotImplementedException();
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => WalkEndpoints(source));

        // MVC itself rejects multiple body params — the second cannot be confidently bound.
        Assert.Contains("RIV1100", stderr);
        Assert.Contains("'second'", stderr);
        Assert.Contains("SecondBody", stderr);
    }

    [Fact]
    public void Unattributed_Object_Parameter_Binds_Body_Untyped_Not_Query()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/patch")]
            public sealed class PatchController
            {
                [RivetEndpoint]
                [HttpPatch("")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Patch(object payload)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var ep = Assert.Single(endpoints);
        var body = Assert.Single(ep.Params, p => p.Source == ParamSource.Body);
        // object keeps the established untyped-schema representation.
        Assert.True(body.Type is TsType.Primitive { Name: "unknown" });
    }

    [Fact]
    public void FromForm_Name_Is_Honored_On_Mixed_Upload()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/uploads")]
            public sealed class UploadsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(void), 201)]
                public Task<IActionResult> Upload(
                    IFormFile file,
                    [FromForm(Name = "alt_text")] string altText,
                    [FromQuery] string title)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var ep = Assert.Single(endpoints);
        Assert.Contains(ep.Params, p => p.Source == ParamSource.File && p.Name == "file");
        var field = Assert.Single(ep.Params, p => p.Source == ParamSource.FormField);
        Assert.Equal("alt_text", field.Name);
    }
}
