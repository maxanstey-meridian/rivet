using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Extraction-oracle tests for the binding-and-properties slice: explicit FromQuery/
/// FromRoute/FromForm declarations (including Name= wire names) succeed, unattributed
/// non-allowlisted inputs refuse with RIV1100, and the retained narrow conventions
/// (exact route match, CancellationToken) behave as documented.
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
    public void Explicit_Bindings_Produce_Their_Declared_Surface()
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
                    [FromBody] SaveProfileRequest request,
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

        // The CancellationToken is host plumbing — never emitted as user input.
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
    public void Unattributed_Interface_Parameter_Refuses_With_RIV1100()
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

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'pricing'", exception.Message);
        Assert.Contains("IPricingService", exception.Message);
    }

    [Fact]
    public void Unattributed_Host_Plumbing_Parameter_Refuses_With_RIV1100()
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

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'http'", exception.Message);
        Assert.Contains("HttpContext", exception.Message);
    }

    [Fact]
    public void Unattributed_Complex_Parameters_Refuse_With_RIV1100()
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

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        // No partial operation is emitted for the unresolved inputs.
        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'first'", exception.Message);
    }

    [Fact]
    public void Unattributed_Object_Parameter_Refuses_With_RIV1100()
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

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'payload'", exception.Message);
    }

    [Fact]
    public void Unattributed_Scalar_Parameter_Refuses_With_RIV1100()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/echo")]
            public sealed class QueryController
            {
                [RivetEndpoint]
                [HttpGet("")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Get(bool dryRun)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'dryRun'", exception.Message);
    }

    [Fact]
    public void Exact_Route_Name_Match_Is_The_Retained_Convention()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record SaveProfileRequest(string DisplayName);

            [Route("api/profiles")]
            public sealed class ProfilesController
            {
                [RivetEndpoint]
                [HttpPut("{id:guid}")]
                [ProducesResponseType(typeof(void), 204)]
                public Task<IActionResult> Save(
                    Guid id,
                    [FromQuery] bool dryRun,
                    [FromBody] SaveProfileRequest request,
                    CancellationToken ct = default)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var ep = Assert.Single(endpoints);
        // Exact parameter-name match against the declared placeholder is Route input.
        var route = Assert.Single(ep.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", route.Name);
        Assert.True(route.Type is TsType.Primitive { Name: "string" });

        // Explicit [FromQuery] and [FromBody] keep their declared sources.
        Assert.Contains(ep.Params, p => p.Source == ParamSource.Query && p.Name == "dryRun");
        var body = Assert.Single(ep.Params, p => p.Source == ParamSource.Body);
        Assert.True(body.Type is TsType.TypeRef { Name: "SaveProfileRequest" });

        Assert.DoesNotContain(ep.Params, p => p.Name == "ct");
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

    [Fact]
    public void Unattributed_Parameter_Beside_File_Refuses_With_RIV1100()
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
                public Task<IActionResult> Upload(IFormFile file, string altText)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => WalkEndpoints(source));

        Assert.Contains("RIV1100", exception.Message);
        Assert.Contains("'altText'", exception.Message);
    }
}
