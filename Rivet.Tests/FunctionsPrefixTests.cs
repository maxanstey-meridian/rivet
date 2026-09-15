using Rivet.Tool.Analysis;

namespace Rivet.Tests;

public sealed class FunctionsPrefixTests
{
    private const string Source = """
        using System;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;
        namespace Microsoft.Azure.Functions.Worker
        {
            public enum AuthorizationLevel { Anonymous }
            public sealed class FunctionAttribute(string name) : Attribute { }
            public sealed class HttpTriggerAttribute(AuthorizationLevel level, params string[] methods) : Attribute
            {
                public string Route { get; set; }
            }
        }
        namespace Test
        {
            [RivetContract]
            public static class Contract
            {
                public static readonly RouteDefinition<string> Get = Define.Get<string>("EXPECTED");
            }
            public sealed class Functions
            {
                [Microsoft.Azure.Functions.Worker.Function("hello")]
                public IActionResult Run([Microsoft.Azure.Functions.Worker.HttpTrigger(
                    Microsoft.Azure.Functions.Worker.AuthorizationLevel.Anonymous, "get", Route = "hello")] object request)
                    => Contract.Get.Success("hello").ToActionResult();
            }
        }
        """;

    [Theory]
    [InlineData("api", "/api/hello")]
    [InlineData("", "/hello")]
    [InlineData("public/v1", "/public/v1/hello")]
    public void Configured_prefix_is_used_for_route_coverage(string prefix, string expected)
    {
        var compilation = CompilationHelper.CreateCompilation(Source.Replace("EXPECTED", expected));
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contracts = CompilationHelper.WalkContracts(compilation, discovered, walker);
        Assert.Empty(
            CoverageChecker.Check(compilation, new WellKnownTypes(compilation), contracts, prefix)
        );
        var mismatch = CoverageChecker.Check(
            compilation,
            new WellKnownTypes(compilation),
            contracts,
            "wrong"
        );
        Assert.Contains(mismatch, warning => warning.Kind == CoverageWarningKind.RouteMismatch);
    }

    [Theory]
    [InlineData("api", "/api/hello")]
    [InlineData("", "/hello")]
    public void Leading_slash_in_trigger_is_not_hidden_by_route_normalization(
        string prefix,
        string expected
    )
    {
        var compilation = CompilationHelper.CreateCompilation(
            Source.Replace("EXPECTED", expected).Replace("Route = \"hello\"", "Route = \"/hello\"")
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contracts = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var warnings = CoverageChecker.Check(
            compilation,
            new WellKnownTypes(compilation),
            contracts,
            prefix
        );
        var warning = Assert.Single(warnings);
        Assert.Equal(CoverageWarningKind.RouteMismatch, warning.Kind);
        Assert.Contains("leading slash", warning.Actual);
    }

    [Theory]
    [InlineData("{}", "api")]
    [InlineData("{\"extensions\":{\"http\":{\"routePrefix\":\"\"}}}", "")]
    [InlineData("{\"extensions\":{\"http\":{\"routePrefix\":\"public/v1\"}}}", "public/v1")]
    public void Host_configuration_preserves_default_empty_and_custom_prefixes(
        string json,
        string expected
    )
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "host.json"), json);
            Assert.Equal(
                expected,
                FunctionsHostConfiguration.LoadRoutePrefix(
                    Path.Combine(directory.FullName, "Api.csproj")
                )
            );
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{\"extensions\":{\"http\":{\"routePrefix\":null}}}")]
    [InlineData("{\"extensions\":{\"http\":{\"routePrefix\":3}}}")]
    public void Invalid_host_configuration_does_not_silently_assume_default(string json)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "host.json"), json);
            Assert.Throws<ContractAnalysisException>(() =>
                FunctionsHostConfiguration.LoadRoutePrefix(
                    Path.Combine(directory.FullName, "Api.csproj")
                )
            );
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
