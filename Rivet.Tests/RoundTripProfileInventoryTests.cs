using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rivet.Tool.Import;

namespace Rivet.Tests;

[Trait("Category", "Local")]
public sealed class RoundTripProfileInventoryTests
{
    [Fact]
    public void Verified_Profile_Matches_The_Artifacts()
    {
        var result = RunInventory();

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StdOut);
        Assert.True(output.RootElement.GetProperty("passed").GetBoolean());
        var facts = output.RootElement.GetProperty("facts");
        Assert.Equal(
            [
                "okta",
                "petstore-v2",
                "petstore-v3",
                "twilio",
                "square",
                "docusign",
                "notion",
                "circleci",
                "firebase",
                "docker",
                "sendgrid",
                "spotify",
                "asana",
                "box",
                "kubernetes",
                "zoom",
                "stripe",
                "slack",
                "vercel",
                "github",
                "discord",
                "bitbucket",
            ],
            facts
                .GetProperty("corpora")
                .EnumerateArray()
                .Select(corpus => corpus.GetProperty("id").GetString()!)
                .ToArray()
        );
        Assert.Empty(facts.GetProperty("unknownKeywords").EnumerateArray());
        Assert.Equal(
            5843,
            facts.GetProperty("normalizedComponentTotals").GetProperty("schemas").GetInt32()
        );
        Assert.Equal(
            78,
            facts.GetProperty("normalizedComponentTotals").GetProperty("requestBodies").GetInt32()
        );
        Assert.Equal(
            27,
            facts
                .GetProperty("sourceComponentTotals")
                .GetProperty("components.securitySchemes")
                .GetInt32()
        );
        Assert.Equal(
            29,
            facts.GetProperty("normalizedComponentTotals").GetProperty("securitySchemes").GetInt32()
        );
        Assert.Equal(
            345,
            facts.GetProperty("normalizedComponentTotals").GetProperty("parameters").GetInt32()
        );
        Assert.Equal(
            140,
            facts.GetProperty("normalizedComponentTotals").GetProperty("responses").GetInt32()
        );
        var groups = facts.GetProperty("carrierSensitiveGroups");
        Assert.NotEmpty(groups.EnumerateArray());
        var allowedCarriers = new HashSet<string>(
            [
                "record",
                "extension-data record",
                "dictionary",
                "JsonElement",
                "scalar",
                "union",
                "provenance-only",
            ],
            StringComparer.Ordinal
        );
        foreach (var group in groups.EnumerateArray())
        {
            Assert.Contains(
                group.GetProperty("corpusId").GetString(),
                facts
                    .GetProperty("corpora")
                    .EnumerateArray()
                    .Select(corpus => corpus.GetProperty("id").GetString())
            );
            Assert.All(
                group.GetProperty("ownerPointers").EnumerateArray(),
                pointer => Assert.StartsWith("#/", pointer.GetString())
            );
            Assert.Contains(group.GetProperty("carrier").GetString()!, allowedCarriers);
            var behaviorTest = group.GetProperty("behaviorTest").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(behaviorTest));
            AssertBehaviorTestExists(behaviorTest);
        }
        Assert.Equal(
            groups
                .EnumerateArray()
                .Sum(group => group.GetProperty("ownerPointers").GetArrayLength()),
            facts
                .GetProperty("carrierSensitiveCounts")
                .EnumerateObject()
                .Sum(count => count.Value.GetInt32())
        );
    }

    [Fact]
    public void Carrier_Sensitive_Inventory_Reports_Context_Owner_Carrier_And_Proof()
    {
        using var mutation = MutateOkta(document =>
        {
            document["components"] = JsonNode.Parse(
                """
                {
                  "schemas": {
                    "Closed": { "type": "object", "properties": { "id": { "type": "string" } }, "additionalProperties": false },
                    "ExplicitOpen": { "type": "object", "properties": { "id": { "type": "string" } }, "additionalProperties": true },
                    "SchemaOpen": { "type": "object", "properties": { "id": { "type": "string" } }, "additionalProperties": { "type": "string" } },
                    "ImplicitOpen": { "type": "object", "properties": { "id": { "type": "string" } } },
                    "EmptyProperties": { "type": "object", "properties": {} },
                    "EmptyClosed": { "type": "object", "additionalProperties": false },
                    "PureDictionary": { "type": "object", "additionalProperties": { "type": "string" } },
                    "Nullable": { "anyOf": [{ "type": "string" }, { "type": "string", "nullable": true }] },
                    "HierarchyRoot": { "type": "object", "discriminator": { "propertyName": "type" }, "properties": { "type": { "type": "string" } } },
                    "HierarchyChild": { "allOf": [{ "$ref": "#/components/schemas/HierarchyRoot" }, { "type": "object", "properties": { "size": { "type": "integer" } } }] },
                    "LoneDiscriminator": { "type": "object", "discriminator": { "propertyName": "type" }, "properties": { "type": { "type": "string" } } },
                    "OwnAllOfRoot": { "allOf": [{ "$ref": "#/components/schemas/Closed" }], "discriminator": { "propertyName": "type" }, "properties": { "type": { "type": "string" } } },
                    "OwnAllOfChild": { "allOf": [{ "$ref": "#/components/schemas/OwnAllOfRoot" }, { "type": "object", "properties": { "size": { "type": "integer" } } }] },
                    "SeveralRefRoot": { "type": "object", "discriminator": { "propertyName": "type" }, "properties": { "type": { "type": "string" } } },
                    "SeveralRefChild": { "allOf": [{ "$ref": "#/components/schemas/SeveralRefRoot" }, { "$ref": "#/components/schemas/Closed" }] },
                    "Envelope": {
                      "type": "object",
                      "properties": {
                        "inlineOmitted": { "type": "object" },
                        "inlineOpen": { "type": "object", "additionalProperties": true },
                        "inlineClosed": { "type": "object", "additionalProperties": false },
                        "item": {
                          "discriminator": { "propertyName": "type" },
                          "oneOf": [
                            { "$ref": "#/components/schemas/Closed" },
                            { "$ref": "#/components/schemas/ImplicitOpen" }
                          ]
                        }
                      }
                    }
                  },
                  "headers": { "Trace": { "schema": { "type": "string" } } },
                  "examples": { "Remote": { "externalValue": "https://example.test/value.json" } }
                }
                """
            );
            document["paths"] = JsonNode.Parse(
                """
                {
                  "/source": {
                    "parameters": [{
                      "name": "filter",
                      "in": "query",
                      "content": { "application/json": { "schema": {} } }
                    }],
                    "post": {
                      "operationId": "source_post",
                      "requestBody": {
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/Envelope" },
                            "encoding": { "item": { "style": "form", "explode": true } }
                          }
                        }
                      },
                      "responses": {
                        "200": {
                          "description": "OK",
                          "content": {
                            "application/json": {
                              "examples": {
                                "remote": { "externalValue": "https://example.test/response.json" }
                              }
                            }
                          }
                        }
                      }
                    }
                  },
                  "/target": { "$ref": "#/paths/~1source" }
                }
                """
            );
        });

        var result = RunInventory("--document", $"okta={mutation.Path}", "--observed");

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StdOut);
        var groups = output.RootElement.GetProperty("carrierSensitiveGroups");
        AssertOccurrence(
            groups,
            "object-properties-additional-properties-false",
            "#/components/schemas/Closed",
            "record"
        );
        AssertOccurrence(
            groups,
            "object-properties-additional-properties-true",
            "#/components/schemas/ExplicitOpen",
            "extension-data record"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-omitted",
            "#/components/schemas/EmptyProperties",
            "extension-data record"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-false",
            "#/components/schemas/EmptyClosed",
            "record"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-schema",
            "#/components/schemas/PureDictionary",
            "dictionary"
        );
        AssertOccurrence(
            groups,
            "object-properties-additional-properties-schema",
            "#/components/schemas/SchemaOpen",
            "extension-data record"
        );
        AssertOccurrence(
            groups,
            "object-properties-additional-properties-omitted",
            "#/components/schemas/ImplicitOpen",
            "extension-data record"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-omitted",
            "#/components/schemas/Envelope/properties/inlineOmitted",
            "dictionary"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-true",
            "#/components/schemas/Envelope/properties/inlineOpen",
            "dictionary"
        );
        AssertOccurrence(
            groups,
            "object-no-properties-additional-properties-false",
            "#/components/schemas/Envelope/properties/inlineClosed",
            "dictionary"
        );
        AssertOccurrence(
            groups,
            "nullable-composition-branch",
            "#/components/schemas/Nullable/anyOf/1",
            "union"
        );
        AssertOccurrence(
            groups,
            "nested-discriminator",
            "#/components/schemas/Envelope/properties/item",
            "union"
        );
        AssertOccurrence(
            groups,
            "explicit-discriminator",
            "#/components/schemas/HierarchyRoot",
            "record"
        );
        // The importer drops each of these, so no carrier proof is claimed: nothing extends
        // the first; the second sits beside an allOf of its own, so it can't root a
        // hierarchy; the third is extended only by an allOf that $refs another schema too,
        // which C# single inheritance can't hold.
        foreach (var dropped in new[] { "LoneDiscriminator", "OwnAllOfRoot", "SeveralRefRoot" })
        {
            Assert.DoesNotContain(
                groups.EnumerateArray(),
                group =>
                    group.GetProperty("shape").GetString()!.EndsWith("-discriminator")
                    && group
                        .GetProperty("ownerPointers")
                        .EnumerateArray()
                        .Any(pointer => pointer.GetString() == $"#/components/schemas/{dropped}")
            );
        }
        AssertOccurrence(
            groups,
            "empty-schema",
            "#/paths/~1source/parameters/0/content/application~1json/schema",
            "JsonElement"
        );
        AssertOccurrence(
            groups,
            "parameter-content",
            "#/paths/~1source/parameters/0",
            "JsonElement"
        );
        AssertOccurrence(
            groups,
            "encoding-object",
            "#/paths/~1source/post/requestBody/content/application~1json/encoding/item",
            "provenance-only"
        );
        AssertOccurrence(
            groups,
            "external-value-example",
            "#/paths/~1source/post/responses/200/content/application~1json/examples/remote",
            "provenance-only"
        );
        AssertOccurrence(
            groups,
            "external-value-example",
            "#/components/examples/Remote",
            "provenance-only"
        );
        AssertOccurrence(
            groups,
            "component-header",
            "#/components/headers/Trace",
            "provenance-only"
        );
        AssertOccurrence(
            groups,
            "component-example",
            "#/components/examples/Remote",
            "provenance-only"
        );
        AssertOccurrence(
            groups,
            "cross-path-reference",
            "#/paths/~1target/$ref",
            "provenance-only"
        );
    }

    [Fact]
    public void Unknown_Extension_Fails_The_Inventory()
    {
        using var mutation = MutateOkta(document => document["x-unreviewed"] = true);

        var result = RunInventory("--document", $"okta={mutation.Path}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown extension: x-unreviewed", Errors(result.StdOut));
    }

    [Fact]
    public void Unknown_Standard_Keyword_Fails_The_Inventory()
    {
        using var mutation = MutateOkta(document => document["unreviewedKeyword"] = true);

        var result = RunInventory("--document", $"okta={mutation.Path}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown keyword: /unreviewedKeyword", Errors(result.StdOut));
    }

    [Fact]
    public void Value_Outside_An_Example_Object_Fails_The_Inventory()
    {
        // 'value' is a keyword only inside an Example Object; on a schema it is unreviewed.
        using var mutation = MutateOkta(document =>
            document["components"] = JsonNode.Parse(
                """{ "schemas": { "Stray": { "type": "string", "value": 1 } } }"""
            )
        );

        var result = RunInventory("--document", $"okta={mutation.Path}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown keyword: /components/schemas/Stray/value", Errors(result.StdOut));
    }

    [Fact]
    public void Changed_Reviewed_Disposition_Fails_The_Inventory()
    {
        var profile = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        profile["vendorExtensionDispositions"]!["x-logo"]!["disposition"] = "preserve";
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reviewed vendor-extension disposition changed", Errors(result.StdOut));
    }

    [Fact]
    public void Extension_Facts_Include_Owner_Pointer_Evidence()
    {
        var result = RunInventory();

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StdOut);
        foreach (
            var extension in output
                .RootElement.GetProperty("facts")
                .GetProperty("extensions")
                .EnumerateObject()
        )
        {
            var fact = extension.Value;
            var ownerPointers = fact.GetProperty("ownerPointers");
            Assert.Equal(fact.GetProperty("count").GetInt32(), ownerPointers.GetArrayLength());
            Assert.All(
                ownerPointers.EnumerateArray(),
                pointer => Assert.StartsWith("/", pointer.GetString())
            );
        }
    }

    [Fact]
    public void Profile_Update_Cannot_Approve_A_Disposition_Change()
    {
        var profile = ReadProfile();
        profile["vendorExtensionDispositions"]!["x-logo"]!["disposition"] = "preserve";
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path, "--update-profile");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("reviewed vendor-extension disposition changed", result.StdErr);
    }

    [Fact]
    public void Profile_Update_Requires_Explicit_Disposition_Approval()
    {
        var profile = ReadProfile();
        profile["vendorExtensionDispositions"]!["x-logo"]!["disposition"] = "preserve";
        using var mutation = TemporaryJson.Write(profile);

        var update = RunInventory(
            "--profile",
            mutation.Path,
            "--update-profile",
            "--approve-disposition-change"
        );
        var check = RunInventory("--profile", mutation.Path);

        Assert.Equal(0, update.ExitCode);
        Assert.Equal(0, check.ExitCode);
    }

    [Fact]
    public void Changed_Profile_Fact_Fails_The_Inventory()
    {
        var profile = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        profile["facts"]!["corpora"]![0]!["operationCount"] = 18;
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("profile facts changed", Errors(result.StdOut));
    }

    [Fact]
    public void Changed_Carrier_Behavior_Proof_Fails_The_Inventory()
    {
        var profile = ReadProfile();
        profile["facts"]!["carrierSensitiveGroups"]![0]!["behaviorTest"] =
            "GeneratedCarrierFidelityTests.Not_A_Real_Proof";
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("profile facts changed", Errors(result.StdOut));
    }

    [Fact]
    public void Roster_Mismatch_Fails_The_Inventory()
    {
        var profile = ReadProfile();
        profile["verifiedCorpusIds"]!.AsArray().Add("asana");
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("verified roster does not match profile facts", Errors(result.StdOut));
    }

    [Theory]
    [InlineData("notion")]
    [InlineData("circleci")]
    [InlineData("docker")]
    [InlineData("sendgrid")]
    public void Source_Defect_Policy_Broadening_Fails_The_Inventory(string corpusId)
    {
        var profile = ReadProfile();
        var sourceDefects = profile["sourceDefects"]!.AsArray();
        var defect = sourceDefects.First(item => item!["corpusId"]!.GetValue<string>() == corpusId);
        sourceDefects.Add(defect!.DeepClone());
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reviewed source-defect policy changed", Errors(result.StdOut));
    }

    [Fact]
    public void Profile_Update_Cannot_Approve_A_Source_Defect_Policy_Change()
    {
        var profile = ReadProfile();
        var sourceDefects = profile["sourceDefects"]!.AsArray();
        sourceDefects.Add(sourceDefects[0]!.DeepClone());
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path, "--update-profile");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("reviewed source-defect policy changed", result.StdErr);
    }

    [Fact]
    public void Every_Cited_AllOf_Hierarchy_Root_Imports_As_A_Hierarchy()
    {
        // The inventory cites an allOf target as a candidate hierarchy root without running
        // the importer, which can still refuse the hierarchy (a mapping or tag conflict, a
        // required member redeclared). This holds the Python copy to the importer's answer.
        const string proof =
            $"{nameof(GeneratedCarrierFidelityTests)}.{nameof(GeneratedCarrierFidelityTests.Bitbucket_allOf_hierarchy_discriminator_dispatches_every_descendant_at_runtime)}";
        var cited = ReadProfile()["facts"]!["carrierSensitiveGroups"]!
            .AsArray()
            .Where(group => group!["behaviorTest"]!.GetValue<string>() == proof)
            .GroupBy(group => group!["corpusId"]!.GetValue<string>())
            .ToList();
        Assert.NotEmpty(cited);
        var manifest = JsonNode.Parse(
            File.ReadAllText(CliRunner.RepoPath("corpus", "openapi-manifest.json"))
        )!;

        foreach (var corpus in cited)
        {
            var file = manifest["corpora"]!
                .AsArray()
                .Single(entry => entry!["id"]!.GetValue<string>() == corpus.Key)![
                "file"
            ]!.GetValue<string>();
            var result = OpenApiImporter.Import(
                File.ReadAllText(CliRunner.RepoPath("openapi", file)),
                new ImportOptions("Test")
            );

            foreach (
                var pointer in corpus.SelectMany(group =>
                    group!["ownerPointers"]!.AsArray().Select(item => item!.GetValue<string>())
                )
            )
            {
                const string prefix = "#/components/schemas/";
                Assert.StartsWith(prefix, pointer);
                var componentId = pointer[prefix.Length..];
                var record = Assert.Single(
                    result.Files,
                    generated =>
                        generated.Content.Contains(
                            $"[RivetGeneratedType(\"{componentId}\",",
                            StringComparison.Ordinal
                        )
                );
                var typeName = Path.GetFileNameWithoutExtension(record.FileName);
                Assert.DoesNotContain(
                    result.Warnings,
                    warning =>
                        warning.StartsWith(
                            $"RIV3005: Discriminator dropped on '{typeName}'",
                            StringComparison.Ordinal
                        )
                );
                Assert.Contains("[JsonPolymorphic(", record.Content);
            }
        }
    }

    [Fact]
    public void Inventory_Proof_Names_Are_Existing_Tests()
    {
        var script = File.ReadAllText(CliRunner.RepoPath("tools", "roundtrip_inventory.py"));
        var proofs = Regex.Matches(
            script,
            @"^\w+_PROOF = \(?\s*""(?<type>\w+)\.(?<method>\w+)""",
            RegexOptions.Multiline
        );

        Assert.Equal(19, proofs.Count);
        foreach (Match proof in proofs)
        {
            var method = typeof(RoundTripProfileInventoryTests)
                .Assembly.GetType($"Rivet.Tests.{proof.Groups["type"].Value}")
                ?.GetMethod(proof.Groups["method"].Value);
            Assert.True(
                method?.GetCustomAttributes<FactAttribute>().Any() == true,
                $"roundtrip_inventory.py names a proof test that does not exist: {proof.Value}"
            );
        }
    }

    private static JsonObject ReadProfile() =>
        JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();

    private static TemporaryJson MutateOkta(Action<JsonObject> mutate)
    {
        var document = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("openapi", "okta.json")))!
            .AsObject();
        mutate(document);
        return TemporaryJson.Write(document);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunInventory(
        params string[] arguments
    ) =>
        CliRunner.Run(
            CliRunner.RepoPath(),
            "python3",
            [CliRunner.RepoPath("tools", "roundtrip_inventory.py"), .. arguments]
        );

    private static string[] Errors(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document
            .RootElement.GetProperty("errors")
            .EnumerateArray()
            .Select(error => error.GetString()!)
            .ToArray();
    }

    private static void AssertOccurrence(
        JsonElement groups,
        string shape,
        string ownerPointer,
        string carrier
    )
    {
        var group = Assert.Single(
            groups.EnumerateArray(),
            item =>
                item.GetProperty("corpusId").GetString() == "okta"
                && item.GetProperty("shape").GetString() == shape
                && item.GetProperty("ownerPointers")
                    .EnumerateArray()
                    .Any(pointer => pointer.GetString() == ownerPointer)
        );
        Assert.Equal(carrier, group.GetProperty("carrier").GetString());
        var behaviorTest = group.GetProperty("behaviorTest").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(behaviorTest));
        AssertBehaviorTestExists(behaviorTest);
    }

    private static void AssertBehaviorTestExists(string behaviorTest)
    {
        var separator = behaviorTest.LastIndexOf('.');
        Assert.True(separator > 0, behaviorTest);
        var type = typeof(RoundTripProfileInventoryTests).Assembly.GetType(
            $"Rivet.Tests.{behaviorTest[..separator]}"
        );
        Assert.NotNull(type);
        var method = type.GetMethod(behaviorTest[(separator + 1)..]);
        Assert.NotNull(method);
        var fact = Assert.IsAssignableFrom<FactAttribute>(
            Assert.Single(
                method.GetCustomAttributes(inherit: true),
                attribute => attribute is FactAttribute
            )
        );
        Assert.Null(fact.Skip);
    }
}
