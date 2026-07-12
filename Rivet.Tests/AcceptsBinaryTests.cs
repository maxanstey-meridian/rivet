using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Raw binary request bodies (.AcceptsBinary): builder semantics, contract walking,
/// OpenAPI emission, contract-JSON round-trip, import and emit→import→emit equivalence.
/// </summary>
public sealed class AcceptsBinaryTests
{
    public sealed record ChunkInput(string Id, int ChunkIndex);

    // ----- Builder -----

    [Fact]
    public void AcceptsBinary_Defaults_To_OctetStream()
    {
        var route = Define.Put("/api/things/{id}/chunks/{n}").AcceptsBinary();

        Assert.Equal("application/octet-stream", route.BinaryRequestContentType);
    }

    [Fact]
    public void AcceptsBinary_Sets_Custom_ContentType()
    {
        var route = Define.Put("/api/recordings/{id}/audio").AcceptsBinary("audio/mpeg");

        Assert.Equal("audio/mpeg", route.BinaryRequestContentType);
    }

    [Fact]
    public void AcceptsBinary_IsFluent()
    {
        var route = Define.Put("/api/things/{id}");
        var returned = route.AcceptsBinary();

        Assert.Same(route, returned);
    }

    [Fact]
    public void AcceptsBinary_Survives_Accepts_Conversion()
    {
        var route = Define
            .Put("/api/things/{id}/chunks/{chunkIndex}")
            .AcceptsBinary("video/mp4")
            .Accepts<ChunkInput>();

        Assert.Equal("video/mp4", route.BinaryRequestContentType);
    }

    [Fact]
    public void AcceptsBinary_After_AcceptsFile_Throws()
    {
        var route = Define.Post("/api/upload").AcceptsFile();

        var ex = Assert.Throws<InvalidOperationException>(() => route.AcceptsBinary());
        Assert.Contains(".AcceptsFile()", ex.Message);
    }

    [Fact]
    public void AcceptsFile_After_AcceptsBinary_Throws()
    {
        var route = Define.Post("/api/upload").AcceptsBinary();

        var ex = Assert.Throws<InvalidOperationException>(() => route.AcceptsFile());
        Assert.Contains(".AcceptsBinary()", ex.Message);
    }

    [Fact]
    public void AcceptsBinary_After_FormEncoded_Throws()
    {
        var route = Define.Post("/api/upload").FormEncoded();

        var ex = Assert.Throws<InvalidOperationException>(() => route.AcceptsBinary());
        Assert.Contains(".FormEncoded()", ex.Message);
    }

    [Fact]
    public void FormEncoded_After_AcceptsBinary_Throws()
    {
        var route = Define.Post("/api/upload").AcceptsBinary();

        var ex = Assert.Throws<InvalidOperationException>(() => route.FormEncoded());
        Assert.Contains(".AcceptsBinary()", ex.Message);
    }

    // ----- ContractWalker -----

    private const string ChunkUploadSource = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ChunkInput(string Id, int ChunkIndex);

        [RivetType]
        public sealed record ChunkResponse(string Etag);

        [RivetContract]
        public static class ThingsContract
        {
            public static readonly Define UploadChunk =
                Define.Put<ChunkInput, ChunkResponse>("/api/things/{id}/chunks/{chunkIndex}")
                    .AcceptsBinary();
        }
        """;

    [Fact]
    public void Walker_AcceptsBinary_Lowers_TInput_To_Route_Params_No_Body()
    {
        var (endpoints, _) = CompilationHelper.WalkContract(ChunkUploadSource);

        var ep = Assert.Single(endpoints);
        Assert.Equal("PUT", ep.HttpMethod);
        Assert.Equal("application/octet-stream", ep.BinaryRequestContentType);

        Assert.Equal(2, ep.Params.Count);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);

        var idParam = ep.Params.First(p => p.Name == "id");
        Assert.Equal(ParamSource.Route, idParam.Source);
        Assert.True(idParam.Type is TsType.Primitive { Name: "string" });

        var chunkIndexParam = ep.Params.First(p => p.Name == "chunkIndex");
        Assert.Equal(ParamSource.Route, chunkIndexParam.Source);
        Assert.True(chunkIndexParam.Type is TsType.Primitive { Name: "number", Format: "int32" });
    }

    [Fact]
    public void Walker_AcceptsBinary_NonRoute_Props_Become_Query_Params()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ChunkInput(string Id, int ChunkIndex, string? Checksum);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define UploadChunk =
                    Define.Put("/api/things/{id}/chunks/{chunkIndex}")
                        .Accepts<ChunkInput>()
                        .AcceptsBinary("video/mp4");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        Assert.Equal("video/mp4", ep.BinaryRequestContentType);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);

        var checksumParam = ep.Params.First(p => p.Name == "checksum");
        Assert.Equal(ParamSource.Query, checksumParam.Source);
    }

    [Fact]
    public void Walker_AcceptsBinary_Unmatched_Route_Placeholder_Defaults_To_String_Route_Param()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ChunkInput(string Id);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define UploadChunk =
                    Define.Put("/api/things/{id}/chunks/{chunkIndex}")
                        .Accepts<ChunkInput>()
                        .AcceptsBinary();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        var chunkIndexParam = ep.Params.First(p => p.Name == "chunkIndex");
        Assert.Equal(ParamSource.Route, chunkIndexParam.Source);
        Assert.True(chunkIndexParam.Type is TsType.Primitive { Name: "string" });
    }

    [Fact]
    public void Walker_AcceptsBinary_Combined_With_AcceptsFile_Refuses()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define Broken =
                    Define.Post("/api/upload")
                        .AcceptsFile()
                        .AcceptsBinary();
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.WalkContract(source)
        );
        Assert.Contains(".AcceptsBinary()", ex.Message);
        Assert.Contains(".AcceptsFile()", ex.Message);
    }

    [Fact]
    public void Walker_AcceptsBinary_Combined_With_FormEncoded_Refuses()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define Broken =
                    Define.Post("/api/upload")
                        .AcceptsBinary()
                        .FormEncoded();
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.WalkContract(source)
        );
        Assert.Contains(".AcceptsBinary()", ex.Message);
        Assert.Contains(".FormEncoded()", ex.Message);
    }

    // ----- OpenApiEmitter -----

    private static JsonDocument EmitOpenApi(string source) => CompilationHelper.EmitOpenApi(source);

    [Fact]
    public void Emitter_AcceptsBinary_Emits_Binary_RequestBody_No_Json_Content()
    {
        using var doc = EmitOpenApi(ChunkUploadSource);

        var operation = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/things/{id}/chunks/{chunkIndex}")
            .GetProperty("put");

        var requestBody = operation.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());

        var content = requestBody.GetProperty("content");
        Assert.Single(content.EnumerateObject());
        Assert.False(content.TryGetProperty("application/json", out _));

        var schema = content.GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("binary", schema.GetProperty("format").GetString());

        // Route params still emitted as path parameters
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(2, parameters.Count);
        Assert.All(parameters, p => Assert.Equal("path", p.GetProperty("in").GetString()));
    }

    [Fact]
    public void Emitter_AcceptsBinary_Custom_ContentType_Is_Emitted()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AudioInput(string Id);

            [RivetContract]
            public static class RecordingsContract
            {
                public static readonly Define UploadAudio =
                    Define.Put("/api/recordings/{id}/audio")
                        .Accepts<AudioInput>()
                        .AcceptsBinary("audio/mpeg");
            }
            """;

        using var doc = EmitOpenApi(source);

        var content = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/recordings/{id}/audio")
            .GetProperty("put")
            .GetProperty("requestBody")
            .GetProperty("content");

        var schema = content.GetProperty("audio/mpeg").GetProperty("schema");
        Assert.Equal("binary", schema.GetProperty("format").GetString());
    }

    // ----- Contract JSON round-trip (rivet-ts pipeline) -----

    [Fact]
    public void BinaryRequestContentType_Survives_ContractJson_RoundTrip()
    {
        var (endpoints, walker) = CompilationHelper.WalkContract(ChunkUploadSource);
        var json = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value),
            walker.Enums.ToDictionary(kv => kv.Key, kv => kv.Value),
            endpoints
        );

        var (_, _, readEndpoints, _) = JsonContractReader.Read(json);

        var ep = Assert.Single(readEndpoints);
        Assert.Equal("application/octet-stream", ep.BinaryRequestContentType);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);
    }

    // ----- Import -----

    [Fact]
    public void Import_Binary_RequestBody_Scaffolds_AcceptsBinary()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/api/things/{id}/chunks/{chunkIndex}": {
                "put": {
                    "operationId": "things_uploadChunk",
                    "tags": ["Things"],
                    "parameters": [
                        { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                        { "name": "chunkIndex", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
                    ],
                    "requestBody": {
                        "required": true,
                        "content": {
                            "application/octet-stream": {
                                "schema": { "type": "string", "format": "binary" }
                            }
                        }
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "ThingsContract.cs");

        Assert.Contains(".AcceptsBinary()", contract);
        Assert.DoesNotContain("[rivet:unsupported body", contract);

        // The generated contract must compile and walk back to the same shape
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var ep = Assert.Single(endpoints);
        Assert.Equal("application/octet-stream", ep.BinaryRequestContentType);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);
        Assert.Equal(2, ep.Params.Count(p => p.Source == ParamSource.Route));
    }

    [Fact]
    public void Import_Binary_RequestBody_NonOctetStream_Keeps_ContentType()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/api/recordings/{id}/audio": {
                "put": {
                    "operationId": "recordings_uploadAudio",
                    "tags": ["Recordings"],
                    "parameters": [
                        { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "requestBody": {
                        "required": true,
                        "content": {
                            "audio/mpeg": {
                                "schema": { "type": "string", "format": "binary" }
                            }
                        }
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "RecordingsContract.cs");

        Assert.Contains(".AcceptsBinary(\"audio/mpeg\")", contract);
    }

    [Fact]
    public void Import_Multipart_Binary_Schema_Is_Not_AcceptsBinary()
    {
        // multipart/form-data stays on the multipart path even when its schema
        // (pathologically) claims to be a bare binary string.
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/api/upload": {
                "post": {
                    "operationId": "files_upload",
                    "tags": ["Files"],
                    "requestBody": {
                        "content": {
                            "multipart/form-data": {
                                "schema": { "type": "string", "format": "binary" }
                            }
                        }
                    },
                    "responses": { "201": { "description": "Created" } }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "FilesContract.cs");

        Assert.DoesNotContain(".AcceptsBinary(", contract);
    }

    // ----- emit → import → emit equivalence -----

    [Fact]
    public void AcceptsBinary_Survives_OpenApi_RoundTrip()
    {
        // Forward: C# → OpenAPI
        var compilation = CompilationHelper.CreateCompilation(ChunkUploadSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var firstSpec = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        // Reverse: OpenAPI → import → compile → walk
        var importResult = OpenApiImporter.Import(firstSpec, new ImportOptions("RoundTrip", null));
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray()
        );
        var (reDiscovered, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);
        var reEndpoints = CompilationHelper.WalkContracts(recompilation, reDiscovered, rewalker);

        var ep = Assert.Single(reEndpoints);
        Assert.Equal("PUT", ep.HttpMethod);
        Assert.Equal("/api/things/{id}/chunks/{chunkIndex}", ep.RouteTemplate);
        Assert.Equal("application/octet-stream", ep.BinaryRequestContentType);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);

        var idParam = ep.Params.First(p => p.Name == "id");
        Assert.Equal(ParamSource.Route, idParam.Source);
        Assert.True(idParam.Type is TsType.Primitive { Name: "string" });

        var chunkIndexParam = ep.Params.First(p => p.Name == "chunkIndex");
        Assert.Equal(ParamSource.Route, chunkIndexParam.Source);
        Assert.True(chunkIndexParam.Type is TsType.Primitive { Name: "number", Format: "int32" });

        // Forward again: the re-emitted operation must be equivalent
        var secondSpec = OpenApiEmitter.Emit(
            reEndpoints,
            rewalker.Definitions,
            rewalker.Brands,
            rewalker.Enums,
            null
        );

        using var first = JsonDocument.Parse(firstSpec);
        using var second = JsonDocument.Parse(secondSpec);

        var firstOp = first
            .RootElement.GetProperty("paths")
            .GetProperty("/api/things/{id}/chunks/{chunkIndex}")
            .GetProperty("put");
        var secondOp = second
            .RootElement.GetProperty("paths")
            .GetProperty("/api/things/{id}/chunks/{chunkIndex}")
            .GetProperty("put");

        Assert.Equal(
            firstOp.GetProperty("requestBody").GetRawText(),
            secondOp.GetProperty("requestBody").GetRawText()
        );
        Assert.Equal(
            firstOp.GetProperty("parameters").GetRawText(),
            secondOp.GetProperty("parameters").GetRawText()
        );
    }
}
