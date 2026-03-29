using Rivet.Tool;

namespace Rivet.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void ParseArgs_FromFlag_SetsFromContractPath()
    {
        var args = new[] { "--from", "contracts.json", "--output", "./generated" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Equal("./generated", options.OutputDir);
    }

    [Fact]
    public void ParseArgs_FromFlag_WithoutOutput_SetsStdoutMode()
    {
        var args = new[] { "--from", "contracts.json" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Null(options.OutputDir);
    }

    [Fact]
    public void ParseArgs_FromFlag_WithCompile_SetsCompileMode()
    {
        var args = new[] { "--from", "contracts.json", "--output", "./out", "--compile" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Equal("compile", options.Mode);
    }

    [Fact]
    public void ParseArgs_FromFlag_ForwardsJsonSchemaFlag()
    {
        var args = new[] { "--from", "contracts.json", "--output", "./out", "--jsonschema" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.True(options!.JsonSchema, "--jsonschema should be forwarded when using --from");
    }

    [Fact]
    public void ParseArgs_FromFlag_ForwardsQuietFlag()
    {
        var args = new[] { "--from", "contracts.json", "--quiet" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.True(options!.Quiet, "--quiet should be forwarded when using --from");
    }

    [Fact]
    public void PrintUsage_IncludesFromFlag()
    {
        var originalError = Console.Error;
        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            CliParser.PrintUsage();

            var output = sw.ToString();
            Assert.Contains("--from", output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
