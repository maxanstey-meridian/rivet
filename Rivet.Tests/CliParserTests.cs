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

    // ========== C3: dangling flag values ==========

    [Theory]
    [InlineData("--from")]
    [InlineData("--from-openapi")]
    [InlineData("--project")]
    [InlineData("-p")]
    [InlineData("--output")]
    [InlineData("-o")]
    [InlineData("--security")]
    [InlineData("--namespace")]
    public void ParseArgs_ValueTakingFlag_WithoutValue_Fails_With_Loud_Error(string flag)
    {
        var originalError = Console.Error;
        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            var options = CliParser.ParseArgs([flag]);

            Assert.Null(options);
            Assert.Contains($"flag '{flag}' requires a value", sw.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void ParseArgs_DanglingFlag_After_Valid_Args_Fails_With_Loud_Error()
    {
        var originalError = Console.Error;
        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            var options = CliParser.ParseArgs(["--project", "app.csproj", "--output"]);

            Assert.Null(options);
            Assert.Contains("flag '--output' requires a value", sw.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    // ========== C7: unknown flags ==========

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-x")]
    [InlineData("--from-openapi-typo")]
    public void ParseArgs_UnknownFlag_Fails_With_Loud_Error(string flag)
    {
        var originalError = Console.Error;
        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            var options = CliParser.ParseArgs([flag, "file.cs"]);

            Assert.Null(options);
            Assert.Contains($"unknown flag '{flag}'", sw.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void ParseArgs_Plain_File_Arguments_Still_Accepted()
    {
        var options = CliParser.ParseArgs(["Contracts.cs", "Types.cs"]);

        Assert.NotNull(options);
        Assert.Equal(new[] { "Contracts.cs", "Types.cs" }, options!.Files);
        Assert.Equal("Contracts.cs", options.ProjectPath);
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
