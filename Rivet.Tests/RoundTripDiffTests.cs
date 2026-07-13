using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class RoundTripDiffTests
{
    [Fact]
    public void Identical_Fixture_Has_No_Findings()
    {
        var fixture = LoadFixture();

        var result = RunDiff(fixture, fixture.DeepClone().AsObject());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.DocumentFindings.EnumerateObject());
        Assert.Empty(result.OperationFindings.EnumerateObject());
        Assert.Empty(result.SchemaFindings.EnumerateObject());
        Assert.Empty(result.IntegrityFindings.EnumerateObject());
    }

    [Theory]
    [InlineData("2XX", "200")]
    [InlineData("4XX", "400")]
    [InlineData("5XX", "500")]
    [InlineData("default", "500")]
    public void Response_Ranges_And_Default_Are_Never_Concrete_Statuses(
        string sourceStatus,
        string reemittedStatus
    )
    {
        var original = LoadFixture();
        var originalResponses = original["paths"]!["/wildcard"]!["get"]!["responses"]!.AsObject();
        var response = originalResponses["2XX"]!.DeepClone();
        originalResponses.Remove("2XX");
        originalResponses[sourceStatus] = response;
        var reemitted = original.DeepClone().AsObject();
        var reemittedResponses = reemitted["paths"]!["/wildcard"]!["get"]!["responses"]!.AsObject();
        reemittedResponses[reemittedStatus] = reemittedResponses[sourceStatus]!.DeepClone();
        reemittedResponses.Remove(sourceStatus);

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.OperationFindings, "response-key-missing"));
        Assert.Equal(1, FindingCount(result.OperationFindings, "response-key-invented"));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("version")]
    [InlineData("description")]
    [InlineData("termsOfService")]
    [InlineData("contact")]
    [InlineData("license")]
    public void Document_Info_Mutations_Are_Reported(string field)
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        var reemitted = original.DeepClone().AsObject();
        reemitted["info"]![field] = field is "contact" or "license"
            ? new JsonObject { ["name"] = "Changed" }
            : "Changed";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.DocumentFindings, "info"));
    }

    [Fact]
    public void Contact_Presentation_Extensions_Are_Excluded_From_Standard_Info_Comparison()
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        original["info"]!["contact"]!["x-twitter"] = "firebase";
        var reemitted = original.DeepClone().AsObject();
        reemitted["info"]!["contact"]!.AsObject().Remove("x-twitter");

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, FindingCount(result.DocumentFindings, "info"));
        Assert.Equal(0, FindingCount(result.DocumentFindings, "vendor-extension-preserve"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("url")]
    [InlineData("email")]
    public void Standard_Contact_Mutations_Remain_Drift(string field)
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        original["info"]!["contact"]!["url"] = "https://example.test/support";
        var reemitted = original.DeepClone().AsObject();
        reemitted["info"]!["contact"]![field] = "changed";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.DocumentFindings, "info"));
    }

    [Fact]
    public void Reviewed_Contact_Extensions_Remain_Compared_By_Extension_Mechanism()
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        original["info"]!["contact"]!["x-twilio"] = "authored";
        var reemitted = original.DeepClone().AsObject();
        reemitted["info"]!["contact"]!["x-twilio"] = "changed";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, FindingCount(result.DocumentFindings, "info"));
        Assert.Equal(1, FindingCount(result.DocumentFindings, "vendor-extension-preserve"));
    }

    [Theory]
    [InlineData("servers", "servers")]
    [InlineData("tags", "tags")]
    [InlineData("externalDocs", "externalDocs")]
    [InlineData("security", "security")]
    [InlineData("securitySchemes", "security-schemes")]
    public void Document_Metadata_Mutations_Are_Reported(string mutation, string category)
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        var reemitted = original.DeepClone().AsObject();
        switch (mutation)
        {
            case "servers":
                reemitted["servers"]![0]!["variables"]!["tenant"]!["default"] = "other";
                break;
            case "tags":
                reemitted["tags"]![0]!["description"] = "Changed";
                break;
            case "externalDocs":
                reemitted["externalDocs"]!["url"] = "https://example.test/changed";
                break;
            case "security":
                reemitted["security"]![0]!["oauth"]![0] = "write";
                break;
            case "securitySchemes":
                reemitted["components"]!["securitySchemes"]!["bearer"]!["bearerFormat"] = "opaque";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.DocumentFindings, category));
    }

    [Fact]
    public void Excluded_Tag_Extensions_Do_Not_Count_As_Standard_Tag_Drift()
    {
        var original = LoadFixture();
        AddDocumentMetadata(original);
        original["tags"]![0]!["x-displayName"] = "Things";
        var reemitted = original.DeepClone().AsObject();
        reemitted["tags"]![0]!.AsObject().Remove("x-displayName");

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, FindingCount(result.DocumentFindings, "tags"));
    }

    [Theory]
    [InlineData("operationId", "operation-id")]
    [InlineData("tags", "operation-tags")]
    [InlineData("summary", "operation-summary")]
    [InlineData("description", "operation-description")]
    [InlineData("deprecated", "operation-deprecated")]
    [InlineData("security", "operation-security")]
    [InlineData("servers", "operation-servers")]
    [InlineData("extension", "operation-extensions")]
    public void Operation_Metadata_Mutations_Are_Reported(string mutation, string category)
    {
        var original = LoadFixture();
        AddOperationMetadata(original);
        var reemitted = original.DeepClone().AsObject();
        var operation = reemitted["paths"]!["/things/{thing_id}"]!["get"]!;
        switch (mutation)
        {
            case "operationId":
                operation["operationId"] = "changed";
                break;
            case "tags":
                operation["tags"]![0] = "Changed";
                break;
            case "summary":
                operation["summary"] = "Changed";
                break;
            case "description":
                operation["description"] = "Changed";
                break;
            case "deprecated":
                operation["deprecated"] = false;
                break;
            case "security":
                operation["security"]![0]!["oauth"]![0] = "admin";
                break;
            case "servers":
                operation["servers"]![0]!["url"] = "https://changed.test";
                break;
            case "extension":
                operation["x-rivet-source"]!["name"] = "changed";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.OperationFindings, category));
    }

    [Theory]
    [InlineData("required", "parameter-metadata")]
    [InlineData("description", "parameter-metadata")]
    [InlineData("deprecated", "parameter-metadata")]
    [InlineData("style", "parameter-metadata")]
    [InlineData("explode", "parameter-metadata")]
    [InlineData("allowReserved", "parameter-metadata")]
    [InlineData("allowEmptyValue", "parameter-metadata")]
    [InlineData("example", "parameter-metadata")]
    [InlineData("content-type", "parameter-metadata")]
    [InlineData("content-schema", "parameter-schema-type")]
    public void Parameter_Surface_Mutations_Are_Reported(string mutation, string category)
    {
        var original = CreateSemanticSurfaceDocument();
        var reemitted = original.DeepClone().AsObject();
        var parameters = reemitted["paths"]!["/surface"]!["post"]!["parameters"]!;
        var query = parameters[0]!;
        var content = parameters[1]!["content"]!.AsObject();
        switch (mutation)
        {
            case "required":
                query["required"] = true;
                break;
            case "description":
                query["description"] = "Changed";
                break;
            case "deprecated":
                query["deprecated"] = false;
                break;
            case "style":
                query["style"] = "spaceDelimited";
                break;
            case "explode":
                query["explode"] = false;
                break;
            case "allowReserved":
                query["allowReserved"] = false;
                break;
            case "allowEmptyValue":
                query["allowEmptyValue"] = false;
                break;
            case "example":
                query["example"] = new JsonArray("changed");
                break;
            case "content-type":
                content["text/plain"] = content["application/json"]!.DeepClone();
                break;
            case "content-schema":
                content["application/json"]!["schema"]!["type"] = "integer";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(
            FindingCount(result.OperationFindings, category) > 0,
            result.Summary.ToString()
        );
    }

    [Theory]
    [InlineData("required", "request-body-metadata")]
    [InlineData("description", "request-body-metadata")]
    [InlineData("content-type", "request-content-types")]
    [InlineData("examples", "request-examples")]
    [InlineData("encoding", "request-encoding")]
    [InlineData("schema", "request-schema-type")]
    public void Request_Body_Surface_Mutations_Are_Reported(string mutation, string category)
    {
        var original = CreateSemanticSurfaceDocument();
        var reemitted = original.DeepClone().AsObject();
        var body = reemitted["paths"]!["/surface"]!["post"]!["requestBody"]!;
        var content = body["content"]!.AsObject();
        switch (mutation)
        {
            case "required":
                body["required"] = false;
                break;
            case "description":
                body["description"] = "Changed";
                break;
            case "content-type":
                content.Remove("text/plain");
                break;
            case "examples":
                content["application/json"]!["example"]!["name"] = "changed";
                break;
            case "encoding":
                content["application/json"]!["encoding"]!["name"]!["explode"] = false;
                break;
            case "schema":
                content["application/json"]!["schema"]!["properties"]!["name"]!["type"] = "integer";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(
            FindingCount(result.OperationFindings, category) > 0,
            result.Summary.ToString()
        );
    }

    [Theory]
    [InlineData("description", "response-description")]
    [InlineData("headers", "response-header-set")]
    [InlineData("header-description", "response-header-metadata")]
    [InlineData("header-required", "response-header-metadata")]
    [InlineData("header-deprecated", "response-header-metadata")]
    [InlineData("header-style", "response-header-metadata")]
    [InlineData("header-explode", "response-header-metadata")]
    [InlineData("header-example", "response-header-metadata")]
    [InlineData("header-schema", "response-header-schema-type")]
    [InlineData("content-type", "response-content-types")]
    [InlineData("examples", "response-examples")]
    [InlineData("encoding", "response-encoding")]
    [InlineData("schema", "response-schema-type")]
    [InlineData("links", "response-links")]
    public void Response_Surface_Mutations_Are_Reported(string mutation, string category)
    {
        var original = CreateSemanticSurfaceDocument();
        var reemitted = original.DeepClone().AsObject();
        var response = reemitted["paths"]!["/surface"]!["post"]!["responses"]!["200"]!;
        var header = response["headers"]!["X-Rate"]!;
        var content = response["content"]!.AsObject();
        switch (mutation)
        {
            case "description":
                response["description"] = "Changed";
                break;
            case "headers":
                response["headers"]!.AsObject().Remove("X-Rate");
                break;
            case "header-description":
                header["description"] = "Changed";
                break;
            case "header-required":
                header["required"] = false;
                break;
            case "header-deprecated":
                header["deprecated"] = false;
                break;
            case "header-style":
                header["style"] = "form";
                break;
            case "header-explode":
                header["explode"] = true;
                break;
            case "header-example":
                header["example"] = 2;
                break;
            case "header-schema":
                header["schema"]!["type"] = "string";
                break;
            case "content-type":
                content.Remove("text/plain");
                break;
            case "examples":
                content["application/json"]!["example"]!["ok"] = false;
                break;
            case "encoding":
                content["application/json"]!["encoding"]!["result"]!["explode"] = false;
                break;
            case "schema":
                content["application/json"]!["schema"]!["properties"]!["ok"]!["type"] = "string";
                break;
            case "links":
                response["links"]!["next"]!["operationId"] = "changed";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(
            FindingCount(result.OperationFindings, category) > 0,
            result.Summary.ToString()
        );
    }

    [Theory]
    [InlineData("schemas")]
    [InlineData("responses")]
    [InlineData("parameters")]
    [InlineData("examples")]
    [InlineData("requestBodies")]
    [InlineData("headers")]
    [InlineData("securitySchemes")]
    [InlineData("links")]
    [InlineData("callbacks")]
    [InlineData("pathItems")]
    public void Component_Namespace_Identity_Mutations_Are_Reported(string componentNamespace)
    {
        var original = CreateSemanticSurfaceDocument();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]![componentNamespace]!.AsObject().Clear();

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(10, result.Summary.GetProperty("originalComponents").GetInt32());
        Assert.Equal(1, result.Summary.GetProperty("unmatchedOriginalComponents").GetInt32());
        Assert.Equal(1, FindingCount(result.DocumentFindings, "component-missing"));
    }

    [Theory]
    [InlineData("parameters", "component-parameter-metadata")]
    [InlineData("requestBodies", "component-request-body-metadata")]
    [InlineData("responses", "component-response-description")]
    [InlineData("examples", "component-example")]
    [InlineData("headers", "component-header-schema-type")]
    [InlineData("links", "component-link")]
    [InlineData("callbacks", "component-callback")]
    [InlineData("pathItems", "component-path-item")]
    public void Unused_Component_Content_Mutations_Are_Reported(
        string componentNamespace,
        string category
    )
    {
        var original = CreateSemanticSurfaceDocument();
        var reemitted = original.DeepClone().AsObject();
        var component = reemitted["components"]![componentNamespace]!.AsObject().First().Value!;
        switch (componentNamespace)
        {
            case "parameters":
            case "requestBodies":
            case "responses":
                component["description"] = "Changed";
                break;
            case "examples":
                component["value"]!["name"] = "changed";
                break;
            case "headers":
                component["schema"]!["type"] = "integer";
                break;
            case "links":
                component["operationId"] = "changed";
                break;
            case "callbacks":
                component["{$request.body#/callbackUrl}"] = new JsonObject();
                break;
            case "pathItems":
                component["summary"] = "Changed";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.DocumentFindings, category));
    }

    [Theory]
    [InlineData("changed-target")]
    [InlineData("inlined")]
    public void Named_Request_Body_Reference_Identity_Is_Compared(string mutation)
    {
        var original = CreateSemanticSurfaceDocument();
        original["components"]!["requestBodies"]!["EquivalentBody"] = original["components"]![
            "requestBodies"
        ]!["NamedBody"]!.DeepClone();
        original["paths"]!["/surface"]!["post"]!["requestBody"] = new JsonObject
        {
            ["$ref"] = "#/components/requestBodies/NamedBody",
        };
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/surface"]!["post"]!["requestBody"] =
            mutation == "changed-target"
                ? new JsonObject { ["$ref"] = "#/components/requestBodies/EquivalentBody" }
                : reemitted["components"]!["requestBodies"]!["NamedBody"]!.DeepClone();

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.OperationFindings, "request-body-ref-identity"));
    }

    [Theory]
    [InlineData("parameter", "parameter-ref-identity")]
    [InlineData("response", "response-ref-identity")]
    [InlineData("header", "response-header-ref-identity")]
    public void Equivalent_Component_Reference_Target_Mutations_Are_Reported(
        string mutation,
        string category
    )
    {
        var original = CreateSemanticSurfaceDocument();
        var components = original["components"]!;
        switch (mutation)
        {
            case "parameter":
                components["parameters"]!["EquivalentParameter"] = components["parameters"]![
                    "NamedParameter"
                ]!.DeepClone();
                original["paths"]!["/surface"]!["post"]!["parameters"] = new JsonArray(
                    new JsonObject { ["$ref"] = "#/components/parameters/NamedParameter" }
                );
                break;
            case "response":
                components["responses"]!["EquivalentResponse"] = components["responses"]![
                    "NamedResponse"
                ]!.DeepClone();
                original["paths"]!["/surface"]!["post"]!["responses"]!["200"] = new JsonObject
                {
                    ["$ref"] = "#/components/responses/NamedResponse",
                };
                break;
            case "header":
                components["headers"]!["EquivalentHeader"] = components["headers"]![
                    "NamedHeader"
                ]!.DeepClone();
                original["paths"]!["/surface"]!["post"]!["responses"]!["200"]!["headers"]![
                    "X-Rate"
                ] = new JsonObject { ["$ref"] = "#/components/headers/NamedHeader" };
                break;
        }
        var reemitted = original.DeepClone().AsObject();
        switch (mutation)
        {
            case "parameter":
                reemitted["paths"]!["/surface"]!["post"]!["parameters"]![0]!["$ref"] =
                    "#/components/parameters/EquivalentParameter";
                break;
            case "response":
                reemitted["paths"]!["/surface"]!["post"]!["responses"]!["200"]!["$ref"] =
                    "#/components/responses/EquivalentResponse";
                break;
            case "header":
                reemitted["paths"]!["/surface"]!["post"]!["responses"]!["200"]!["headers"]![
                    "X-Rate"
                ]!["$ref"] = "#/components/headers/EquivalentHeader";
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.OperationFindings, category));
    }

    [Theory]
    [InlineData("array-item", "schema-type")]
    [InlineData("array-constraint", "schema-constraints")]
    [InlineData("additional-properties", "schema-type")]
    [InlineData("object-constraint", "schema-constraints")]
    [InlineData("annotation", "schema-annotations")]
    [InlineData("xml", "schema-annotations")]
    public void Recursive_Schema_Mutations_Are_Reported(string mutation, string category)
    {
        var original = LoadFixture();
        AddRecursiveSchemaSurface(original);
        var reemitted = original.DeepClone().AsObject();
        var thing = reemitted["components"]!["schemas"]!["Thing"]!;
        switch (mutation)
        {
            case "array-item":
                thing["properties"]!["matrix"]!["items"]!["items"]!["type"] = "integer";
                break;
            case "array-constraint":
                thing["properties"]!["matrix"]!["items"]!["maxItems"] = 9;
                break;
            case "additional-properties":
                thing["properties"]!["labels"]!["additionalProperties"]!["type"] = "integer";
                break;
            case "object-constraint":
                thing["properties"]!["labels"]!["minProperties"] = 2;
                break;
            case "annotation":
                thing["properties"]!["matrix"]!["items"]!["title"] = "Changed";
                break;
            case "xml":
                thing["properties"]!["labels"]!["xml"]!["wrapped"] = false;
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, category) > 0, result.Summary.ToString());
    }

    [Fact]
    public void Recursive_Ref_Cycle_Uses_Visited_Node_Pairs_And_Finds_A_Deep_Mutation()
    {
        var original = LoadFixture();
        var schemas = original["components"]!["schemas"]!.AsObject();
        for (var index = 0; index < 30; index++)
        {
            schemas[$"Deep{index}"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["next"] = new JsonObject
                    {
                        ["$ref"] = $"#/components/schemas/Deep{(index + 1) % 30}",
                    },
                    ["value"] = new JsonObject { ["type"] = "string" },
                },
            };
        }
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Deep29"]!["properties"]!["value"]!["type"] =
            "integer";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, "schema-type") > 0);
    }

    [Fact]
    public void Pure_Ref_Chains_Are_Not_Depth_Truncated()
    {
        var original = LoadFixture();
        var schemas = original["components"]!["schemas"]!.AsObject();
        for (var index = 0; index < 30; index++)
        {
            schemas[$"Link{index}"] = new JsonObject
            {
                ["$ref"] = $"#/components/schemas/Link{index + 1}",
            };
        }
        schemas["Link30"] = new JsonObject { ["type"] = "string" };
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Link30"]!["type"] = "integer";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, "schema-type") > 0);
    }

    [Fact]
    public void Changed_Public_Ref_Identity_Is_Reported_Even_When_Targets_Are_Equivalent()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["equivalent-owner"] = original["components"]![
            "schemas"
        ]!["nullable-owner"]!.DeepClone();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"]!["$ref"] =
            "#/components/schemas/equivalent-owner";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, "schema-ref-identity") > 0);
    }

    [Fact]
    public void Equivalent_Inline_And_Ref_Schemas_Are_Normalized()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"] = reemitted[
            "components"
        ]!["schemas"]!["nullable-owner"]!.DeepClone();

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Materialized_Ref_Target_Annotations_And_Required_Are_Not_Use_Site_Drift()
    {
        var original = LoadFixture();
        AddRecursiveOwnerSchema(original);
        var reemitted = original.DeepClone().AsObject();
        var ownerUseSite = reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"]!;
        ownerUseSite["description"] = "Owner schema";
        ownerUseSite["required"] = new JsonArray("id");

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Ref_And_Inline_Target_With_Annotations_Required_And_Cycle_Are_Equivalent()
    {
        var original = LoadFixture();
        AddRecursiveOwnerSchema(original);
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"] = reemitted[
            "components"
        ]!["schemas"]!["nullable-owner"]!.DeepClone();

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("annotation", "schema-annotations")]
    [InlineData("required", "schema-required")]
    [InlineData("cycle-target", "schema-type")]
    public void Ref_And_Inline_Target_Mutations_Remain_Real_Drift(string mutation, string category)
    {
        var original = LoadFixture();
        AddRecursiveOwnerSchema(original);
        var reemitted = original.DeepClone().AsObject();
        var inlineOwner = reemitted["components"]!["schemas"]!["nullable-owner"]!
            .DeepClone()
            .AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"] = inlineOwner;
        switch (mutation)
        {
            case "annotation":
                inlineOwner["description"] = "Changed owner schema";
                break;
            case "required":
                inlineOwner["required"] = new JsonArray();
                break;
            case "cycle-target":
                inlineOwner["properties"]!["manager"] = new JsonObject { ["type"] = "string" };
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, category) > 0, result.Summary.ToString());
    }

    [Fact]
    public void Inferred_Int64_Is_Not_Equivalent_To_Absent_Source_Format()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["Thing"]!["properties"]!["count"] = new JsonObject
        {
            ["type"] = "integer",
        };
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["count"]!["format"] = "int64";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.SchemaFindings, "schema-format"));
    }

    [Fact]
    public void Lost_Nullability_Remains_Real_Drift()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["name"]!
            .AsObject()
            .Remove("nullable");

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.SchemaFindings, "schema-nullable"));
    }

    [Fact]
    public void Nullable_30_And_31_Spellings_Are_Normalized()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["name"] = new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["writeOnly"] = true,
        };

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Additional_Properties_True_And_Empty_Schema_Are_Normalized()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["Thing"]!["additionalProperties"] = true;
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["additionalProperties"] = new JsonObject();

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Non_Object_Additional_Properties_Is_Ignored_Without_Hiding_Valid_Scalar_Drift()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["Thing"]!["properties"]!["sourceDefect"] =
            JsonNode.Parse("""{"type":"string","additionalProperties":{"type":"string"}}""");
        var equivalent = original.DeepClone().AsObject();
        equivalent["components"]!["schemas"]!["Thing"]!["properties"]!["sourceDefect"]![
            "additionalProperties"
        ] = true;

        var equivalentResult = RunDiff(original, equivalent);

        Assert.Equal(0, equivalentResult.ExitCode);
        Assert.Equal(0, equivalentResult.Summary.GetProperty("sourceDefects").GetInt32());

        equivalent["components"]!["schemas"]!["Thing"]!["properties"]!["sourceDefect"]!["type"] =
            "integer";
        var changedResult = RunDiff(original, equivalent);

        Assert.Equal(1, changedResult.ExitCode);
        Assert.Equal(1, FindingCount(changedResult.SchemaFindings, "schema-type"));
    }

    [Fact]
    public void Null_Only_Additional_Properties_Is_Also_A_No_Op()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["Thing"]!["properties"]!["sourceDefect"] =
            JsonNode.Parse("""{"type":["null"],"additionalProperties":{"type":"string"}}""");
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["sourceDefect"]![
            "additionalProperties"
        ] = true;

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Summary.GetProperty("sourceDefects").GetInt32());
    }

    [Fact]
    public void Schema_Like_Example_Payload_Is_Not_Classified_As_A_Source_Defect()
    {
        var original = CreateSemanticSurfaceDocument();
        original["components"]!["examples"]!["NamedExample"]!["value"] = JsonNode.Parse(
            """{"type":"string","additionalProperties":{"type":"integer"},"name":"","in":"header"}"""
        );
        var reemitted = original.DeepClone().AsObject();

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Summary.GetProperty("sourceDefects").GetInt32());
    }

    [Fact]
    public void Empty_Parameter_Name_Is_A_Source_Defect_And_Not_Parameter_Drift()
    {
        var original = CreateSemanticSurfaceDocument();
        original["paths"]!["/surface"]!["post"]!["parameters"]!
            .AsArray()
            .Add(JsonNode.Parse("""{"name":"","in":"header","schema":{"type":"string"}}"""));
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/surface"]!["post"]!["parameters"]!.AsArray().RemoveAt(2);

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Summary.GetProperty("sourceDefects").GetInt32());
        var defect = Assert.Single(result.Details.GetProperty("sourceDefects").EnumerateArray());
        Assert.Equal(
            "#/paths/~1surface/post/parameters/2/name",
            defect.GetProperty("path").GetString()
        );
        Assert.Equal(
            "parameter name is empty and therefore invalid",
            defect.GetProperty("reason").GetString()
        );
        Assert.Equal(0, FindingCount(result.OperationFindings, "parameter-missing"));
    }

    [Fact]
    public void Valid_Missing_Parameter_Remains_Drift()
    {
        var original = CreateSemanticSurfaceDocument();
        original["paths"]!["/surface"]!["post"]!["parameters"]!
            .AsArray()
            .Add(JsonNode.Parse("""{"name":"valid","in":"header","schema":{"type":"string"}}"""));
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/surface"]!["post"]!["parameters"]!.AsArray().RemoveAt(2);

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, result.Summary.GetProperty("sourceDefects").GetInt32());
        Assert.Equal(1, FindingCount(result.OperationFindings, "parameter-missing"));
    }

    [Fact]
    public void Represented_Content_Type_Header_Is_A_Source_Defect_And_Not_Parameter_Drift()
    {
        var original = CreateSemanticSurfaceDocument();
        original["paths"]!["/surface"]!["post"]!["parameters"]!
            .AsArray()
            .Add(
                JsonNode.Parse(
                    """{"name":"Content-Type","in":"header","required":true,"schema":{"type":"string","enum":["application/json"]}}"""
                )
            );
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/surface"]!["post"]!["parameters"]!.AsArray().RemoveAt(2);

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Summary.GetProperty("sourceDefects").GetInt32());
        var defect = Assert.Single(result.Details.GetProperty("sourceDefects").EnumerateArray());
        Assert.Equal(
            "#/paths/~1surface/post/parameters/2/name",
            defect.GetProperty("path").GetString()
        );
        Assert.Equal(
            "reserved Content-Type header parameter is ignored by OpenAPI; request media types are represented by requestBody.content",
            defect.GetProperty("reason").GetString()
        );
        Assert.Equal(0, FindingCount(result.OperationFindings, "parameter-missing"));
    }

    [Fact]
    public void Represented_Content_Type_Header_Is_Omitted_From_Both_Comparison_Sides()
    {
        var original = CreateSemanticSurfaceDocument();
        original["paths"]!["/surface"]!["post"]!["parameters"]!
            .AsArray()
            .Add(
                JsonNode.Parse(
                    """{"name":"Content-Type","in":"header","schema":{"type":"string","enum":["application/json"]}}"""
                )
            );

        var result = RunDiff(original, original.DeepClone().AsObject());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Summary.GetProperty("sourceDefects").GetInt32());
        Assert.Equal(0, FindingCount(result.OperationFindings, "parameter-invented"));
    }

    [Theory]
    [InlineData("Content-Type", "application/xml")]
    [InlineData("Accept", "application/json")]
    [InlineData("Authorization", "token")]
    public void Reserved_Header_Is_A_Source_Defect_Even_When_Its_Value_Is_Not_Represented(
        string headerName,
        string mediaType
    )
    {
        var original = CreateSemanticSurfaceDocument();
        original["paths"]!["/surface"]!["post"]!["parameters"]!
            .AsArray()
            .Add(
                JsonNode.Parse(
                    $"{{\"name\":\"{headerName}\",\"in\":\"header\",\"schema\":{{\"type\":\"string\",\"enum\":[\"{mediaType}\"]}}}}"
                )
            );
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/surface"]!["post"]!["parameters"]!.AsArray().RemoveAt(2);

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Summary.GetProperty("sourceDefects").GetInt32());
        Assert.Equal(0, FindingCount(result.OperationFindings, "parameter-missing"));
    }

    [Fact]
    public void Explicit_Null_Default_Is_Not_An_Absent_Default()
    {
        var original = LoadFixture();
        original["components"]!["schemas"]!["Thing"]!["properties"]!["name"]!["default"] = null;
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["name"]!
            .AsObject()
            .Remove("default");

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.SchemaFindings, "schema-annotations") > 0);
    }

    [Fact]
    public void Swagger_Projection_Is_A_Normalization_Control()
    {
        var result = RunDiff(CreateSwaggerDocument(), CreateEquivalentOpenApiDocument());

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("multi", "form", true)]
    [InlineData("csv", "form", false)]
    [InlineData("ssv", "spaceDelimited", false)]
    [InlineData("pipes", "pipeDelimited", false)]
    public void Swagger_Collection_Format_Projects_To_Equivalent_Serialization(
        string collectionFormat,
        string style,
        bool explode
    )
    {
        var original = CreateSwaggerCollectionDocument(collectionFormat);
        var reemitted = CreateOpenApiCollectionDocument(style, explode);

        Assert.Equal(0, RunDiff(original, reemitted).ExitCode);

        reemitted["paths"]!["/search"]!["get"]!["parameters"]![0]!["explode"] = !explode;
        var mutation = RunDiff(original, reemitted);

        Assert.Equal(1, mutation.ExitCode);
        Assert.Equal(1, FindingCount(mutation.OperationFindings, "parameter-metadata"));
    }

    [Fact]
    public void Unsupported_Swagger_Tsv_Collection_Format_Is_Not_Guessed_As_Equivalent()
    {
        var result = RunDiff(
            CreateSwaggerCollectionDocument("tsv"),
            CreateOpenApiCollectionDocument("form", false)
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, FindingCount(result.OperationFindings, "parameter-metadata"));
    }

    [Fact]
    public void Swagger_FormData_Descriptions_Are_Compared_To_Request_Properties()
    {
        var original = CreateSwaggerFormDocument();
        var reemitted = CreateEquivalentOpenApiFormDocument();

        Assert.Equal(0, RunDiff(original, reemitted).ExitCode);

        reemitted["paths"]!["/pets/{id}"]!["post"]!["requestBody"]!["content"]![
            "application/x-www-form-urlencoded"
        ]!["schema"]!["properties"]!["name"]!["description"] = "Changed description";
        var mutation = RunDiff(original, reemitted);

        Assert.Equal(1, mutation.ExitCode);
        Assert.Equal(1, FindingCount(mutation.OperationFindings, "request-schema-annotations"));
    }

    [Fact]
    public void Operation_Parameter_Overrides_Path_Parameter()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        var pathParameter = reemitted["paths"]!["/things/{thing_id}"]!["parameters"]![0]!;
        pathParameter["required"] = true;
        pathParameter["schema"]!["type"] = "boolean";

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Findings_Are_Structured_Json()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/wildcard"]!["get"]!["responses"]!.AsObject().Remove("2XX");

        var result = RunDiff(original, reemitted);

        var finding = Assert.Single(
            result
                .Details.GetProperty("opFindings")
                .GetProperty("response-key-missing")
                .EnumerateArray()
        );
        Assert.Equal(JsonValueKind.Object, finding.ValueKind);
        Assert.True(finding.TryGetProperty("path", out _));
        Assert.True(finding.TryGetProperty("original", out _));
        Assert.True(finding.TryGetProperty("reemitted", out _));
    }

    [Fact]
    public void Integrity_Findings_Include_Refs_Security_Path_Parameters_And_Operation_Ids()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["components"]!["schemas"]!["Thing"]!["properties"]!["owner"]!["$ref"] =
            "#/components/schemas/missing";
        reemitted["security"]![0]!["missingScheme"] = new JsonArray();
        reemitted["paths"]!["/download/{artifact_id}"]!["get"]!["parameters"]![0]!["name"] =
            "other";
        reemitted["paths"]!["/echo"]!["post"]!["operationId"] = "getThing";

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(FindingCount(result.IntegrityFindings, "unresolved-reference") > 0);
        Assert.True(FindingCount(result.IntegrityFindings, "undefined-security-scheme") > 0);
        Assert.True(FindingCount(result.IntegrityFindings, "path-parameter-mismatch") > 0);
        Assert.True(FindingCount(result.IntegrityFindings, "duplicate-operation-id") > 0);
    }

    [Fact]
    public void Generated_Source_Unsupported_Markers_Are_Integrity_Findings()
    {
        var sourceDirectory = Directory.CreateTempSubdirectory("rivet-roundtrip-source-");
        try
        {
            var sourcePath = Path.Combine(sourceDirectory.FullName, "Contract.cs");
            File.WriteAllText(sourcePath, "// [rivet:unsupported body content-type=text/plain]\n");
            var fixture = LoadFixture();

            var result = RunDiff(fixture, fixture.DeepClone().AsObject(), sourceDirectory.FullName);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(1, FindingCount(result.IntegrityFindings, "unsupported-marker"));
        }
        finally
        {
            sourceDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("missing-arguments")]
    [InlineData("invalid-json")]
    [InlineData("non-object-json")]
    public void Invalid_Arguments_And_Input_Exit_Two(string scenario)
    {
        var workDir = Directory.CreateTempSubdirectory("rivet-roundtrip-invalid-");
        try
        {
            var input = Path.Combine(workDir.FullName, "input.json");
            File.WriteAllText(input, scenario == "non-object-json" ? "[]" : "{");
            var arguments =
                scenario == "missing-arguments" ? Array.Empty<string>() : new[] { input, input };

            var process = CliRunner.Run(
                workDir.FullName,
                "python3",
                [CliRunner.RepoPath("tools", "roundtrip-diff.py"), .. arguments]
            );

            Assert.Equal(2, process.ExitCode);
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_Operation_And_Schema_Are_Reported()
    {
        var original = LoadFixture();
        var reemitted = original.DeepClone().AsObject();
        reemitted["paths"]!["/things/{thing_id}"]!.AsObject().Remove("put");
        reemitted["components"]!["schemas"]!.AsObject().Remove("nullable-owner");

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Summary.GetProperty("missingOperations").GetInt32());
        Assert.Equal(1, result.Summary.GetProperty("unmatchedOriginalSchemas").GetInt32());
        Assert.Single(result.Details.GetProperty("missingOperations").EnumerateArray());
        Assert.Single(result.Details.GetProperty("unmatchedOriginalSchemas").EnumerateArray());
    }

    private static int FindingCount(JsonElement findings, string category) =>
        findings.TryGetProperty(category, out var count) ? count.GetInt32() : 0;

    private static JsonObject LoadFixture() =>
        JsonNode
            .Parse(
                File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "roundtrip-probe.json")
                )
            )!
            .AsObject();

    private static void AddDocumentMetadata(JsonObject document)
    {
        document["info"] = JsonNode.Parse(
            """
            {"title":"Probe","version":"1.0.0","description":"Description","termsOfService":"https://example.test/terms","contact":{"name":"Support","email":"support@example.test"},"license":{"name":"MIT","url":"https://example.test/license"}}
            """
        );
        document["servers"] = JsonNode.Parse(
            """
            [{"url":"https://{tenant}.example.test","description":"Primary","variables":{"tenant":{"default":"api","enum":["api","sandbox"],"description":"Tenant"}}}]
            """
        );
        document["tags"] = JsonNode.Parse(
            """[{"name":"Things","description":"Thing operations","externalDocs":{"url":"https://example.test/things"}}]"""
        );
        document["externalDocs"] = JsonNode.Parse(
            """{"description":"Documentation","url":"https://example.test/docs"}"""
        );
    }

    private static void AddOperationMetadata(JsonObject document)
    {
        var operation = document["paths"]!["/things/{thing_id}"]!["get"]!;
        operation["tags"] = new JsonArray("Things");
        operation["deprecated"] = true;
        operation["servers"] = JsonNode.Parse("""[{"url":"https://operation.example.test"}]""");
        operation["x-rivet-source"] = new JsonObject { ["name"] = "authored" };
    }

    private static void AddRecursiveSchemaSurface(JsonObject document)
    {
        var properties = document["components"]!["schemas"]!["Thing"]!["properties"]!;
        properties["matrix"] = JsonNode.Parse(
            """{"type":"array","items":{"type":"array","title":"Row","minItems":1,"maxItems":4,"items":{"type":"string","minLength":2}}}"""
        );
        properties["labels"] = JsonNode.Parse(
            """{"type":"object","minProperties":1,"maxProperties":5,"additionalProperties":{"type":"string","maxLength":20},"xml":{"name":"labels","wrapped":true}}"""
        );
    }

    private static void AddRecursiveOwnerSchema(JsonObject document)
    {
        var owner = document["components"]!["schemas"]!["nullable-owner"]!;
        owner["description"] = "Owner schema";
        owner["required"] = new JsonArray("id");
        owner["properties"]!["manager"] = new JsonObject
        {
            ["$ref"] = "#/components/schemas/nullable-owner",
        };
    }

    private static JsonObject CreateSemanticSurfaceDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "openapi":"3.1.0",
                  "info":{"title":"Surface","version":"1"},
                  "paths":{"/surface":{"post":{
                    "operationId":"surface",
                    "parameters":[
                      {"name":"filter","in":"query","required":false,"description":"Filter","deprecated":true,"style":"form","explode":true,"allowReserved":true,"allowEmptyValue":true,"example":["one"],"schema":{"type":"array","items":{"type":"string"}}},
                      {"name":"X-Mode","in":"header","content":{"application/json":{"schema":{"type":"string"}}}}
                    ],
                    "requestBody":{"required":true,"description":"Body","content":{"application/json":{"example":{"name":"one"},"encoding":{"name":{"style":"form","explode":true}},"schema":{"type":"object","properties":{"name":{"type":"string"}}}},"text/plain":{"schema":{"type":"string"}}}},
                    "responses":{"200":{"description":"OK","headers":{"X-Rate":{"required":true,"description":"Rate","deprecated":true,"style":"simple","explode":false,"example":1,"schema":{"type":"integer","format":"int32"}}},"content":{"application/json":{"example":{"ok":true},"encoding":{"result":{"style":"form","explode":true}},"schema":{"type":"object","properties":{"ok":{"type":"boolean"}}}},"text/plain":{"schema":{"type":"string"}}},"links":{"next":{"operationId":"surface"}}}}
                  }}},
                  "components":{
                    "schemas":{"Payload":{"type":"object","properties":{"name":{"type":"string"}}}},
                    "responses":{"NamedResponse":{"description":"Named"}},
                    "parameters":{"NamedParameter":{"name":"named","in":"query","schema":{"type":"string"}}},
                    "examples":{"NamedExample":{"value":{"name":"one"}}},
                    "requestBodies":{"NamedBody":{"required":true,"description":"Body","content":{"application/json":{"example":{"name":"one"},"encoding":{"name":{"style":"form","explode":true}},"schema":{"type":"object","properties":{"name":{"type":"string"}}}},"text/plain":{"schema":{"type":"string"}}}}},
                    "headers":{"NamedHeader":{"schema":{"type":"string"}}},
                    "securitySchemes":{"bearer":{"type":"http","scheme":"bearer"}},
                    "links":{"NamedLink":{"operationId":"surface"}},
                    "callbacks":{"NamedCallback":{}},
                    "pathItems":{"NamedPath":{}}
                  }
                }
                """
            )!
            .AsObject();

    private static JsonObject CreateSwaggerCollectionDocument(string collectionFormat)
    {
        var document = JsonNode
            .Parse(
                """
                {
                  "swagger":"2.0","info":{"title":"Collection","version":"1"},
                  "paths":{"/search":{"get":{"operationId":"search","parameters":[{"name":"tags","in":"query","type":"array","items":{"type":"string"}}],"responses":{"200":{"description":"OK"}}}}}
                }
                """
            )!
            .AsObject();
        document["paths"]!["/search"]!["get"]!["parameters"]![0]!["collectionFormat"] =
            collectionFormat;
        return document;
    }

    private static JsonObject CreateOpenApiCollectionDocument(string style, bool explode)
    {
        var document = JsonNode
            .Parse(
                """
                {
                  "openapi":"3.1.0","info":{"title":"Collection","version":"1"},
                  "paths":{"/search":{"get":{"operationId":"search","parameters":[{"name":"tags","in":"query","schema":{"type":"array","items":{"type":"string"}}}],"responses":{"200":{"description":"OK"}}}}}
                }
                """
            )!
            .AsObject();
        var parameter = document["paths"]!["/search"]!["get"]!["parameters"]![0]!;
        parameter["style"] = style;
        parameter["explode"] = explode;
        return document;
    }

    private static JsonObject CreateSwaggerDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "swagger":"2.0","info":{"title":"Projection","version":"1"},
                  "consumes":["application/json"],"produces":["application/json"],
                  "paths":{"/items/{id}":{"parameters":[{"name":"id","in":"path","required":true,"type":"string","format":"uuid"}],"post":{"operationId":"createItem","parameters":[{"name":"body","in":"body","required":true,"schema":{"$ref":"#/definitions/Payload"}}],"responses":{"200":{"description":"created","schema":{"$ref":"#/definitions/Payload"}}}}}},
                  "definitions":{"Payload":{"type":"object","properties":{"name":{"type":"string"}}}}
                }
                """
            )!
            .AsObject();

    private static JsonObject CreateEquivalentOpenApiDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "openapi":"3.1.0","info":{"title":"Projection","version":"1"},
                  "paths":{"/items/{id}":{"parameters":[{"name":"id","in":"path","required":true,"schema":{"type":"string","format":"uuid"}}],"post":{"operationId":"createItem","requestBody":{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/Payload"}}}},"responses":{"200":{"description":"created","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Payload"}}}}}}}},
                  "components":{"schemas":{"Payload":{"type":"object","properties":{"name":{"type":"string"}}}}}
                }
                """
            )!
            .AsObject();

    private static JsonObject CreateSwaggerFormDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "swagger":"2.0","info":{"title":"Projection","version":"1"},
                  "consumes":["application/x-www-form-urlencoded"],
                  "paths":{"/pets/{id}":{"post":{"operationId":"updatePet","parameters":[{"name":"id","in":"path","required":true,"type":"integer","format":"int64"},{"name":"name","in":"formData","description":"Updated name of the pet","required":false,"type":"string"}],"responses":{"204":{"description":"updated"}}}}}
                }
                """
            )!
            .AsObject();

    private static JsonObject CreateEquivalentOpenApiFormDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "openapi":"3.1.0","info":{"title":"Projection","version":"1"},
                  "paths":{"/pets/{id}":{"post":{"operationId":"updatePet","parameters":[{"name":"id","in":"path","required":true,"schema":{"type":"integer","format":"int64"}}],"requestBody":{"required":false,"content":{"application/x-www-form-urlencoded":{"schema":{"type":"object","properties":{"name":{"type":"string","description":"Updated name of the pet"}}}}}},"responses":{"204":{"description":"updated"}}}}}
                }
                """
            )!
            .AsObject();

    private static DiffResult RunDiff(
        JsonObject original,
        JsonObject reemitted,
        string? generatedSource = null
    )
    {
        var workDir = Directory.CreateTempSubdirectory("rivet-roundtrip-diff-");
        try
        {
            var originalPath = Path.Combine(workDir.FullName, "original.json");
            var reemittedPath = Path.Combine(workDir.FullName, "reemitted.json");
            var summaryPath = Path.Combine(workDir.FullName, "summary.json");
            var detailsPath = Path.Combine(workDir.FullName, "details.json");
            File.WriteAllText(originalPath, original.ToJsonString());
            File.WriteAllText(reemittedPath, reemitted.ToJsonString());
            var arguments = new List<string>
            {
                CliRunner.RepoPath("tools", "roundtrip-diff.py"),
                originalPath,
                reemittedPath,
                "--summary-json",
                summaryPath,
                "--details-json",
                detailsPath,
            };
            if (generatedSource is not null)
            {
                arguments.Add("--generated-source");
                arguments.Add(generatedSource);
            }

            var process = CliRunner.Run(workDir.FullName, "python3", arguments);
            Assert.True(
                File.Exists(summaryPath),
                $"Comparator did not write summary. Exit: {process.ExitCode}; stderr: {process.StdErr}"
            );
            using var summaryDocument = JsonDocument.Parse(File.ReadAllText(summaryPath));
            using var detailsDocument = JsonDocument.Parse(File.ReadAllText(detailsPath));
            var summary = summaryDocument.RootElement.Clone();
            return new DiffResult(
                process.ExitCode,
                summary,
                detailsDocument.RootElement.Clone(),
                summary.GetProperty("documentFindings"),
                summary.GetProperty("opFindings"),
                summary.GetProperty("schemaFindings"),
                summary.GetProperty("integrityFindings")
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private sealed record DiffResult(
        int ExitCode,
        JsonElement Summary,
        JsonElement Details,
        JsonElement DocumentFindings,
        JsonElement OperationFindings,
        JsonElement SchemaFindings,
        JsonElement IntegrityFindings
    );
}
