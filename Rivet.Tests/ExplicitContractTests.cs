using System.Reflection;
using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ExplicitContractTests
{
    [Theory]
    [InlineData("[FromServices, FromQuery] string value")]
    [InlineData("[FromBody, FromForm] Dto value")]
    [InlineData("[FromForm, FromBody] Dto value")]
    [InlineData("[FromServices, FromBody] Dto value")]
    [InlineData("[FromServices, FromForm] Dto value")]
    [InlineData("[FromServices, FromHeader] string value")]
    [InlineData("[FromServices, FromRoute] string value")]
    [InlineData("[FromQuery] System.Threading.CancellationToken token")]
    public void Contradictory_Or_Plumbing_Bindings_Refuse(string parameters)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(Endpoint(parameters))
        );
        Assert.Contains("RIV1100", error.Message);
    }

    [Theory]
    [InlineData("[FromForm] Dto dto, [FromForm] string extra")]
    [InlineData("[FromForm] string extra, [FromForm] Dto dto")]
    [InlineData("[FromBody] Dto dto, [FromForm] string extra")]
    [InlineData("[FromForm] string extra, [FromBody] Dto dto")]
    [InlineData("[FromBody] Dto dto, IFormFile file")]
    [InlineData("IFormFile file, [FromForm] Dto dto")]
    public void Mixed_Body_And_Form_Inputs_Refuse(string parameters)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(Endpoint(parameters))
        );
        Assert.Contains("RIV1104", error.Message);
    }

    [Theory]
    [InlineData("[FromForm] string title", "application/x-www-form-urlencoded")]
    [InlineData("[FromForm] Dto dto", "application/x-www-form-urlencoded")]
    [InlineData("IFormFile file, [FromForm] string title", "multipart/form-data")]
    public void Supported_Form_Inputs_Remain_Visible(string parameters, string mediaType)
    {
        using var json = CompilationHelper.EmitOpenApi(Endpoint(parameters));
        var content = json
            .RootElement.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content");
        Assert.True(content.TryGetProperty(mediaType, out _));
    }

    [Theory]
    [InlineData("Results<Ok<Dto>, Ok<string>>")]
    [InlineData("Results<Ok<Dto>, Ok>")]
    public void Conflicting_Typed_Result_Bodies_Report_Contract_Error(string result)
    {
        var source = $$"""
            using Rivet;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Http.HttpResults;
            public sealed record Dto(string Name);
            public sealed class ItemsController : ControllerBase
            {
                [RivetEndpoint, HttpGet("/items")]
                public {{result}} Get() => throw new System.NotImplementedException();
            }
            """;
        var error = Assert.Throws<RivetUserException>(() => CompilationHelper.WalkMerged(source));
        Assert.Contains("RIV1107", error.Message);
    }

    [Theory]
    [InlineData("camelCase", "darkGreen")]
    [InlineData("snakeCase", "dark_green")]
    [InlineData("kebabCase", "dark-green")]
    [InlineData("lowerCase", "darkgreen")]
    [InlineData("unknown", "quote\"slash\\end")]
    public void Imported_Enum_Compiles_Serializes_And_Reemits(string policy, string value)
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: $$"""
            "Colour": {"type":"string","enum":[{{JsonSerializer.Serialize(
                value
            )}},"special-value"],"x-rivet-enum-naming-policy":"{{policy}}"}
            """
        );
        var imported = CompilationHelper.Import(spec, "Consumer");
        var source = CompilationHelper.FindFile(imported, "Colour.cs");
        var compilation = CompilationHelper.CreateCompilation(source);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var type = Assembly.Load(stream.ToArray()).GetType("Consumer.Colour")!;
        var wireValues = type.GetEnumValues()
            .Cast<object>()
            .Select(v => JsonSerializer.Serialize(v, type))
            .ToArray();
        Assert.Equal(new[] { JsonSerializer.Serialize(value), "\"special-value\"" }, wireValues);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        Assert.Equal(
            new[] { value, "special-value" },
            Assert.IsType<TsType.StringUnion>(walker.Enums["Colour"]).Members
        );
    }

    [Fact]
    public void Contract_Json_Preserves_Enum_Naming_Policy()
    {
        var enums = new Dictionary<string, TsType>
        {
            ["Colour"] = new TsType.StringUnion(["darkGreen"], NamingPolicy: "camelCase"),
        };
        var json = ContractEmitter.Emit(new(), enums, []);
        Assert.Contains("\"namingPolicy\": \"camelCase\"", json);
        var restored = JsonContractReader.Read(json);
        Assert.Equal(
            "camelCase",
            Assert.IsType<TsType.StringUnion>(restored.Enums["Colour"]).NamingPolicy
        );
    }

    [Theory]
    [InlineData("[-9223372036854775808,9223372036854775807]")]
    [InlineData("[0,18446744073709551615]")]
    [InlineData("[-2147483648,2147483647,4294967295]")]
    public void Numeric_Enum_Bounds_Survive_Import_Compilation_And_Contract_Json(string values)
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: $"\"Code\": {{\"type\":\"integer\",\"enum\":{values}}}"
        );
        var imported = CompilationHelper.Import(spec, "Consumer");
        var compilation = CompilationHelper.CreateCompilation(
            CompilationHelper.FindFile(imported, "Code.cs")
        );
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var json = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            []
        );
        var restored = JsonContractReader.Read(json);
        using var expected = JsonDocument.Parse(values);
        Assert.Equal(
            expected.RootElement.EnumerateArray().Select(v => v.GetRawText()),
            Assert.IsType<TsType.IntUnion>(restored.Enums["Code"]).Members
        );
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e-30")]
    [InlineData("1e3")]
    [InlineData("18446744073709551616")]
    [InlineData("\"1\"")]
    public void Invalid_Integer_Carriers_Are_Refused(string value)
    {
        Assert.Throws<JsonException>(() =>
            JsonContractReader.Read(
                $$"""{"types":[],"enums":[{"name":"Code","intValues":[{{value}}]}]}"""
            )
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TsType>($$"""{"kind":"intUnion","values":[{{value}}]}""")
        );
    }

    [Theory]
    [InlineData("public sealed record Bad(string Other);")]
    [InlineData("public sealed record Bad(string Value, int Extra);")]
    [InlineData(
        "public sealed record Bad([property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)] int Value);"
    )]
    [InlineData("public sealed class Bad { public string Value { get; } = \"x\"; }")]
    public void Invalid_Scalar_Shapes_Fail_Analysis(string declaration)
    {
        var source =
            "using Rivet; [RivetScalar] "
            + declaration
            + " [RivetType] public sealed record Holder(Bad Value);";
        Assert.Contains(
            "RIV1103",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    [Fact]
    [Trait("Category", "Local")]
    public void Cli_Reports_Eager_Scalar_Validation_Without_An_Unhandled_Exception()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.FullName, "Types.cs");
        File.WriteAllText(
            path,
            "using Rivet; [RivetScalar] public sealed record Bad(string Other); [RivetType] public sealed record Holder(Bad Value);"
        );
        var result = CliRunner.RunCli(directory.FullName, [path, "--openapi"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("RIV1103", result.StdErr);
        Assert.DoesNotContain("Unhandled exception", result.StdErr);
    }

    [Theory]
    [InlineData("[System.Flags]", "First = 1, Second = 2")]
    [InlineData("", "First = 1, Second = 1")]
    public void String_Flags_And_Aliases_Refuse(string attribute, string members)
    {
        var source = $$"""
            using Rivet;
            using System.Text.Json.Serialization;
            {{attribute}} [JsonConverter(typeof(RivetCamelCaseEnumConverter<Code>))]
            public enum Code { {{members}} }
            [RivetType] public sealed record Holder(Code Value);
            """;
        Assert.Contains(
            "RIV1108",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    [Fact]
    public void Colliding_Generated_Schema_Names_Refuse_As_User_Error()
    {
        const string source = """
            using Rivet;
            [assembly: RivetGeneratedSchema("Code", "Code", "string", null, false, "{}")]
            [assembly: RivetGeneratedSchema("Code", "Code", "string", null, false, "{}")]
            """;
        Assert.Contains(
            "collides",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    [Theory]
    [InlineData("[FromForm] string title", "multipart/form-data", true)]
    [InlineData("[FromForm] string title", "Multipart/Form-Data", true)]
    [InlineData(
        "[FromForm] string title",
        "application/x-www-form-urlencoded; charset=utf-8",
        true
    )]
    [InlineData("[FromForm] string title", "text/plain", false)]
    [InlineData("IFormFile file", "application/x-www-form-urlencoded", false)]
    [InlineData("IFormFile file", "multipart/form-data; charset=utf-8", true)]
    public void Form_Media_Type_Is_Honored_Or_Refused(
        string parameters,
        string mediaType,
        bool supported
    )
    {
        var source = Endpoint(parameters)
            .Replace("[RivetEndpoint,", $"[Consumes(\"{mediaType}\"), RivetEndpoint,");
        if (!supported)
        {
            Assert.Contains(
                "RIV1100",
                Assert
                    .Throws<RivetUserException>(() => CompilationHelper.EmitOpenApi(source))
                    .Message
            );
            return;
        }
        using var document = CompilationHelper.EmitOpenApi(source);
        Assert.True(
            document
                .RootElement.GetProperty("paths")
                .GetProperty("/items")
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .TryGetProperty(mediaType, out _)
        );
    }

    [Fact]
    public void Explicit_Route_Name_Must_Match_The_Actual_Placeholder()
    {
        var source = Endpoint("[FromRoute(Name = \"item_id\")] string value")
            .Replace("/items", "/items/{item-id}");
        Assert.Contains(
            "RIV1100",
            Assert.Throws<RivetUserException>(() => CompilationHelper.WalkMerged(source)).Message
        );
    }

    [Theory]
    [InlineData("Other.RivetCamelCaseEnumConverter<Code>")]
    [InlineData("JsonStringEnumConverter<OtherCode>")]
    public void Unknown_Or_Mistargeted_Enum_Converters_Refuse(string converter)
    {
        var source = $$"""
            using Rivet;
            using System.Text.Json.Serialization;
            [JsonConverter(typeof({{converter}}))] public enum Code { First }
            public enum OtherCode { Other }
            [RivetType] public sealed record Holder(Code Value);
            namespace Other
            {
                public sealed class RivetCamelCaseEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, System.Enum { }
            }
            """;
        Assert.Contains(
            "RIV1108",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    // A converter or number handling Rivet doesn't model decides the wire on its own, so
    // the spec would describe the CLR shape instead. Each one is refused, wherever it sits:
    // on the type, on a property or field, on a polymorphic variant, or inherited from the
    // walked type's own [JsonNumberHandling].
    private const string SerializerFixtures = """
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Rivet;

        public sealed class Upper : JsonConverter<string>
        {
            public override string Read(ref Utf8JsonReader r, System.Type t, JsonSerializerOptions o) => r.GetString()!;
            public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) => w.WriteStringValue(v.ToUpperInvariant());
        }
        public sealed class MoneyConverter : JsonConverter<Money>
        {
            public override Money Read(ref Utf8JsonReader r, System.Type t, JsonSerializerOptions o) => new(r.GetDecimal());
            public override void Write(Utf8JsonWriter w, Money v, JsonSerializerOptions o) => w.WriteNumberValue(v.Amount);
        }
        public sealed record Money(decimal Amount);
        public enum Code { First }
        """;

    [Theory]
    [InlineData(
        "[JsonConverter(typeof(MoneyConverter))] public sealed record Priced(decimal Amount); [RivetType] public sealed record Holder(Priced Value);",
        "RIV1111",
        "type 'Priced'"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonConverter(typeof(Upper))] string Value);",
        "RIV1111",
        "'Holder.Value'"
    )]
    [InlineData(
        "[RivetType] public sealed class Holder { [JsonInclude, JsonConverter(typeof(Upper))] public string Value = \"\"; }",
        "RIV1111",
        "'Holder.Value'"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonConverter(typeof(JsonStringEnumConverter<Code>))] Code Value);",
        "RIV1111",
        "JsonStringEnumConverter<Code>"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonConverter(typeof(MoneyConverter)), RivetSchemaType(\"number\")] Money Value);",
        "RIV1111",
        "'Holder.Value'"
    )]
    [InlineData(
        "[RivetType, JsonPolymorphic, JsonDerivedType(typeof(Circle), \"circle\")] public abstract record Shape; public sealed record Circle([property: JsonConverter(typeof(Upper))] string Name) : Shape;",
        "RIV1111",
        "'Shape.circle.Name'"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Value);",
        "RIV1112",
        "WriteAsString"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] IReadOnlyList<double> Value);",
        "RIV1112",
        "AllowNamedFloatingPointLiterals"
    )]
    [InlineData(
        "[RivetType, JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] public sealed record Holder(string Name, long? Value);",
        "RIV1112",
        "type 'Holder'"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] List<int> Value);",
        "RIV1112",
        "'Holder.Value'"
    )]
    public void Unmodelled_Serializer_Settings_Refuse(string declarations, string id, string named)
    {
        var compilation = CompilationHelper.CreateCompilation(SerializerFixtures + declarations);
        var message = Assert
            .Throws<RivetUserException>(() => CompilationHelper.DiscoverAndWalk(compilation))
            .Message;
        Assert.StartsWith($"error {id}:", message);
        Assert.Contains(named, message);
    }

    // The settings Rivet does model, or that leave every advertised value on the wire.
    [Theory]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int Value);"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonConverter(typeof(Upper)), RivetSchemaType(\"string\")] string Value);"
    )]
    [InlineData(
        "[RivetType] public sealed record Holder([property: JsonNumberHandling(JsonNumberHandling.WriteAsString), RivetSchemaType(\"string\")] int Value);"
    )]
    [InlineData(
        "[RivetType, JsonNumberHandling(JsonNumberHandling.WriteAsString)] public sealed record Holder(string Name, [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Value);"
    )]
    [InlineData(
        "[RivetScalar, JsonNumberHandling(JsonNumberHandling.WriteAsString)] public sealed record Cents(int Value); [RivetType] public sealed record Holder(Cents Value);"
    )]
    [InlineData(
        "[JsonConverter(typeof(JsonStringEnumConverter<Shade>))] public enum Shade { Dark } [RivetType] public sealed record Holder(Shade Value);"
    )]
    [InlineData(
        "[RivetUnion] public sealed record Either(string? Text, int? Number); [RivetType] public sealed record Holder(Either Value);"
    )]
    public void Modelled_Serializer_Settings_Are_Accepted(string declarations)
    {
        var compilation = CompilationHelper.CreateCompilation(SerializerFixtures + declarations);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        Assert.True(walker.Definitions.ContainsKey("Holder"));
    }

    [Theory]
    [InlineData(
        "public sealed class Bad { public string Value { get; } public Bad(ref string value) => Value = value; }"
    )]
    [InlineData(
        "public sealed class Bad { public string Value { get; } public Bad(object value) => Value = (string)value; }"
    )]
    [InlineData("[CustomConverter] public sealed record Bad(string Value);")]
    [InlineData("public sealed record Bad([property: CustomConverter] string Value);")]
    public void Scalar_Analysis_Rejects_Runtime_Incompatible_Declarations(string declaration)
    {
        var source =
            """
                using Rivet;
                using System.Text.Json.Serialization;
                public sealed class CustomConverterAttribute : JsonConverterAttribute { }
                """
            + "[RivetScalar] "
            + declaration
            + " [RivetType] public sealed record Holder(Bad Value);";
        Assert.Contains(
            "RIV1103",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    [Fact]
    public void Custom_Enum_Converter_Attribute_Does_Not_Become_A_Numeric_Contract()
    {
        var source = """
            using Rivet;
            using System.Text.Json.Serialization;
            public sealed class CustomConverterAttribute : JsonConverterAttribute
            {
                public override JsonConverter CreateConverter(System.Type type) => new JsonStringEnumConverter<Code>();
            }
            [CustomConverter] public enum Code { First }
            [RivetType] public sealed record Holder(Code Value);
            """;
        Assert.Contains(
            "RIV1108",
            Assert
                .Throws<RivetUserException>(() =>
                    CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
                )
                .Message
        );
    }

    [Theory]
    [InlineData("Ok<Dto>", "typeof(string), 200")]
    [InlineData("Ok<Dto>", "200")]
    [InlineData("Ok", "typeof(Dto), 200")]
    public void Single_Typed_Result_Cannot_Contradict_Response_Metadata(
        string result,
        string metadata
    )
    {
        var source = Endpoint("")
            .Replace("using Rivet;", "using Rivet; using Microsoft.AspNetCore.Http.HttpResults;")
            .Replace("[RivetEndpoint,", $"[ProducesResponseType({metadata}), RivetEndpoint,")
            .Replace(
                "public string Post() => \"ok\";",
                $"public {result} Post() => throw new System.NotImplementedException();"
            );
        Assert.Contains(
            "RIV1107",
            Assert.Throws<RivetUserException>(() => CompilationHelper.WalkMerged(source)).Message
        );
    }

    [Theory]
    [InlineData("typeof(Dto), 200", 1)]
    [InlineData("404", 2)]
    public void Single_Typed_Result_Merges_With_Response_Metadata(string metadata, int count)
    {
        var source = Endpoint("")
            .Replace("using Rivet;", "using Rivet; using Microsoft.AspNetCore.Http.HttpResults;")
            .Replace("[RivetEndpoint,", $"[ProducesResponseType({metadata}), RivetEndpoint,")
            .Replace(
                "public string Post() => \"ok\";",
                "public Ok<Dto> Post() => throw new System.NotImplementedException();"
            );
        using var document = CompilationHelper.EmitOpenApi(source);
        var responses = document
            .RootElement.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("post")
            .GetProperty("responses");
        Assert.Equal(count, responses.EnumerateObject().Count());
        Assert.Equal(
            "#/components/schemas/Dto",
            responses
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    private static string Endpoint(string parameters) =>
        $$"""
            using Rivet;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Http;
            public sealed record Dto(string Name);
            public sealed class ItemsController : ControllerBase
            {
                [RivetEndpoint, HttpPost("/items")]
                public string Post({{parameters}}) => "ok";
            }
            """;
}
