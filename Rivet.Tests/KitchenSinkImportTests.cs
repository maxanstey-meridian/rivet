using System.Text.Json;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class KitchenSinkImportTests
{
    private static string LoadFixture()
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "openapi-kitchen-sink.json"));
    }

    // ========== Compilation ==========

    [Fact]
    public void Generated_CSharp_Compiles()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        var errors = CompilationHelper.CompileImportResult(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== Roslyn round-trip ==========

    [Fact]
    public void Contracts_Survive_Roslyn_RoundTrip()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        // 15 endpoints: 6 Users + 1 Orgs + 1 Tenants + 1 Analytics + 1 Health + 2 Admin + 1 Default + 1 InlineTest + 1 Forms
        Assert.Equal(15, endpoints.Count);

        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/users");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/users/{userId}/avatar");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/orgs/{orgId}/projects/{projectId}/tasks/{taskId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/{tenantId}/users/{userId}");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/analytics");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/health");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/admin/purge");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/status");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/webhooks");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "/api/form-submit");

        // Validate return types on key endpoints
        var getUser = endpoints.Single(e => e.HttpMethod == "GET" && e.RouteTemplate == "/api/users/{userId}");
        Assert.NotNull(getUser.ReturnType);
        var getUserReturn = Assert.IsType<TsType.TypeRef>(getUser.ReturnType);
        Assert.Equal("UserDto", getUserReturn.Name);

        var deleteUser = endpoints.Single(e => e.HttpMethod == "DELETE" && e.RouteTemplate == "/api/users/{userId}");
        Assert.Null(deleteUser.ReturnType);

        // Validate route params exist and have correct source
        Assert.Contains(getUser.Params, p => p.Name == "userId" && p.Source == ParamSource.Route);
    }

    [Fact]
    public void Types_Survive_Roslyn_RoundTrip()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // Records
        Assert.True(walker.Definitions.ContainsKey("UserDto"));
        Assert.True(walker.Definitions.ContainsKey("AddressDto"));
        Assert.True(walker.Definitions.ContainsKey("CompanyDto"));
        Assert.True(walker.Definitions.ContainsKey("ProjectTaskDto"));
        Assert.True(walker.Definitions.ContainsKey("CollectionsDto"));
        Assert.True(walker.Definitions.ContainsKey("NullableFieldsDto"));
        Assert.True(walker.Definitions.ContainsKey("NotFoundDto"));
        Assert.True(walker.Definitions.ContainsKey("ValidationErrorDto"));

        // Composition
        Assert.True(walker.Definitions.ContainsKey("ComposedDto"));
        Assert.True(walker.Definitions.ContainsKey("ComposedMultiRefDto"));
        Assert.True(walker.Definitions.ContainsKey("UnionShape"));
        Assert.True(walker.Definitions.ContainsKey("FlexibleDto"));
        Assert.True(walker.Definitions.ContainsKey("UnionWithPrimitiveDto"));
        Assert.True(walker.Definitions.ContainsKey("DiscriminatedShape"));

        // Enums
        Assert.True(walker.Enums.ContainsKey("Priority"));
        Assert.True(walker.Enums.ContainsKey("TaskStatus"));
        Assert.True(walker.Enums.ContainsKey("SingleRole"));

        // Brands
        Assert.True(walker.Brands.ContainsKey("Email"));
        Assert.True(walker.Brands.ContainsKey("WebsiteUri"));
        Assert.True(walker.Brands.ContainsKey("WebsiteUrl"));
        Assert.True(walker.Brands.ContainsKey("ResourceRef"));
    }

    // ========== Type mapping ==========

    [Fact]
    public void All_Primitive_Types_Map_Correctly()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "AllPrimitivesDto.cs");

        Assert.Contains("string StringField", content);
        Assert.Contains("long IntField", content);
        Assert.Contains("long LongField", content);
        Assert.Contains("double DoubleField", content);
        Assert.Contains("float FloatField", content);
        Assert.Contains("bool BoolField", content);
        Assert.Contains("DateTime DateTimeField", content);
        Assert.Contains("Guid GuidField", content);
    }

    [Fact]
    public void Nullable_Types_Handled()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "NullableFieldsDto.cs");

        Assert.Contains("string? NullableString", content);
        Assert.Contains("long? NullableInt", content);
        Assert.Contains("AddressDto? NullableRef", content);
    }

    [Fact]
    public void Collection_Types_Map_Correctly()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "CollectionsDto.cs");

        Assert.Contains("List<string> StringList", content);
        Assert.Contains("List<AddressDto> RefList", content);
        Assert.Contains("List<List<string>> NestedList", content);
        Assert.Contains("Dictionary<string, string> StringDict", content);
        Assert.Contains("Dictionary<string, AddressDto> RefDict", content);
        Assert.Contains("Dictionary<string, List<string>> ArrayDict", content);
    }

    [Fact]
    public void Enum_Values_Preserve_Original_Names()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // Original values preserved via [JsonStringEnumMemberName] round-trip
        var priority = (TsType.StringUnion)walker.Enums["Priority"];
        Assert.Contains("low", priority.Members);
        Assert.Contains("medium", priority.Members);
        Assert.Contains("high", priority.Members);
        Assert.Contains("critical", priority.Members);

        var taskStatus = (TsType.StringUnion)walker.Enums["TaskStatus"];
        Assert.Contains("my_status", taskStatus.Members);
        Assert.Contains("ACTIVE", taskStatus.Members);
        Assert.Contains("in-progress", taskStatus.Members);

        var singleRole = (TsType.StringUnion)walker.Enums["SingleRole"];
        Assert.Single(singleRole.Members);
        Assert.Contains("admin", singleRole.Members);
    }

    [Fact]
    public void All_Brand_Formats_Produce_Value_Objects()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        Assert.Contains("public sealed record Email(string Value)", CompilationHelper.FindFile(result, "Email.cs"));
        Assert.Contains("public sealed record WebsiteUri(string Value)", CompilationHelper.FindFile(result, "WebsiteUri.cs"));
        Assert.Contains("public sealed record WebsiteUrl(string Value)", CompilationHelper.FindFile(result, "WebsiteUrl.cs"));
        Assert.Contains("public sealed record ResourceRef(string Value)", CompilationHelper.FindFile(result, "ResourceRef.cs"));

        // All brands should be in Domain/ folder
        Assert.Contains(result.Files, f => f.FileName == "Domain/Email.cs");
        Assert.Contains(result.Files, f => f.FileName == "Domain/WebsiteUri.cs");
        Assert.Contains(result.Files, f => f.FileName == "Domain/WebsiteUrl.cs");
        Assert.Contains(result.Files, f => f.FileName == "Domain/ResourceRef.cs");
    }

    [Fact]
    public void Nested_Refs_Resolved_Transitively()
    {
        // ProjectTaskDto → CompanyDto → AddressDto (3 deep)
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        Assert.True(walker.Definitions.ContainsKey("ProjectTaskDto"));
        Assert.True(walker.Definitions.ContainsKey("CompanyDto"));
        Assert.True(walker.Definitions.ContainsKey("AddressDto"));

        var projectTask = walker.Definitions["ProjectTaskDto"];
        var assigneeProp = Assert.Single(projectTask.Properties, p => p.Name == "assignee");
        var assigneeType = Assert.IsType<TsType.TypeRef>(assigneeProp.Type);
        Assert.Equal("CompanyDto", assigneeType.Name);

        var company = walker.Definitions["CompanyDto"];
        var addressProp = Assert.Single(company.Properties, p => p.Name == "address");
        var addressType = Assert.IsType<TsType.TypeRef>(addressProp.Type);
        Assert.Equal("AddressDto", addressType.Name);
    }

    // ========== Routes ==========

    [Fact]
    public void Multi_Segment_Routes_Preserved()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        Assert.Contains(endpoints, e =>
            e.RouteTemplate == "/api/orgs/{orgId}/projects/{projectId}/tasks/{taskId}");
    }

    // ========== Endpoints ==========

    [Fact]
    public void Void_Endpoints_Have_No_OutputType()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "UsersContract.cs");

        // DELETE with 204 → bare RouteDefinition (no type param), 204 is default for DELETE
        Assert.Contains("public static readonly RouteDefinition Delete", content);
        Assert.Contains("Define.Delete(\"/api/users/{userId}\")", content);
        Assert.DoesNotContain(".Status(", content.Split("Delete =")[1].Split(";")[0]);
    }

    [Fact]
    public void Error_Responses_Preserved()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "UsersContract.cs");

        // PUT /api/users/{userId} has 400, 404, 409, 422 error responses
        Assert.Contains(".Returns<ValidationErrorDto>(400, \"Bad request\")", content);
        Assert.Contains(".Returns<NotFoundDto>(404, \"User not found\")", content);
        Assert.Contains(".Returns<ConflictDto>(409, \"Conflict\")", content);
        Assert.Contains(".Returns<ValidationErrorDto>(422, \"Unprocessable entity\")", content);
    }

    [Fact]
    public void Security_Annotations_Correct()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        // Health endpoint has security: [] → .Anonymous()
        Assert.Contains(".Anonymous()", CompilationHelper.FindFile(result, "HealthContract.cs"));

        // Admin purge has security: [{"admin": []}] → .Secure("admin")
        Assert.Contains(".Secure(\"admin\")", CompilationHelper.FindFile(result, "AdminContract.cs"));

        // Users endpoints inherit global → .Secure("bearer")
        Assert.Contains(".Secure(\"bearer\")", CompilationHelper.FindFile(result, "UsersContract.cs"));
    }

    // ========== Tags / contracts ==========

    [Fact]
    public void Tags_Produce_Separate_Contracts()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var contractFiles = result.Files.Where(f => f.FileName.StartsWith("Contracts/")).ToList();

        Assert.Contains(contractFiles, f => f.FileName == "Contracts/UsersContract.cs");
        Assert.Contains(contractFiles, f => f.FileName == "Contracts/OrganizationsContract.cs");
        Assert.Contains(contractFiles, f => f.FileName == "Contracts/TenantsContract.cs");
        Assert.Contains(contractFiles, f => f.FileName == "Contracts/AnalyticsContract.cs");
        Assert.Contains(contractFiles, f => f.FileName == "Contracts/HealthContract.cs");
        Assert.Contains(contractFiles, f => f.FileName == "Contracts/AdminContract.cs");
        Assert.True(contractFiles.Count >= 4);
    }

    [Fact]
    public void No_Tag_Produces_DefaultContract()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.Contains(result.Files, f => f.FileName == "Contracts/DefaultContract.cs");
    }

    // ========== Form-encoded request body ==========

    [Fact]
    public void Form_Encoded_Request_Body_Produces_Input_Type()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "FormsContract.cs");

        Assert.Contains("SubmitRequest", content);
        Assert.Contains("HealthStatusDto", content);
        Assert.Contains("/api/form-submit", content);

        // Synthetic request type should exist
        var requestContent = CompilationHelper.FindFile(result, "SubmitRequest.cs");
        Assert.Contains("string Name", requestContent);
        Assert.Contains("long? Value", requestContent);
    }

    // ========== Default error response ==========

    [Fact]
    public void Default_Error_Response_Mapped_As_500()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "FormsContract.cs");

        Assert.Contains(".Returns<ValidationErrorDto>(500, \"Unexpected error\")", content);
    }

    // ========== Composition ==========

    [Fact]
    public void AllOf_Produces_Flattened_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "ComposedDto.cs");

        // Should have AddressDto props flattened in
        Assert.Contains("string Street", content);
        Assert.Contains("string City", content);
        Assert.Contains("string ZipCode", content);
        // Plus the inline extension property
        Assert.Contains("string? Extra", content);
    }

    [Fact]
    public void AllOf_MultiRef_Merges_Properties()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "ComposedMultiRefDto.cs");

        // AddressDto props
        Assert.Contains("string Street", content);
        Assert.Contains("string City", content);
        Assert.Contains("string ZipCode", content);
        // SinglePropDto props
        Assert.Contains("string Value", content);
    }

    [Fact]
    public void AllOf_With_Sibling_Properties_Merges()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "AllOfWithSiblingPropsDto.cs");

        // AddressDto props from allOf
        Assert.Contains("string Street", content);
        Assert.Contains("string City", content);
        Assert.Contains("string ZipCode", content);
        // Sibling property
        Assert.Contains("string Extra", content);
    }

    [Fact]
    public void OneOf_Produces_Union_Wrapper()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "UnionShape.cs");

        Assert.Contains("AddressDto? AsAddressDto", content);
        Assert.Contains("CompanyDto? AsCompanyDto", content);
    }

    [Fact]
    public void AnyOf_Produces_Union_Wrapper()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "FlexibleDto.cs");

        Assert.Contains("AddressDto? AsAddressDto", content);
        Assert.Contains("CompanyDto? AsCompanyDto", content);
    }

    [Fact]
    public void OneOf_With_Primitives_Produces_Union()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "UnionWithPrimitiveDto.cs");

        Assert.Contains("string? AsString", content);
        Assert.Contains("long? AsLong", content);
    }

    [Fact]
    public void Discriminator_Object_Becomes_Regular_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "DiscriminatedShape.cs");

        Assert.Contains("string Kind", content);
    }

    // ========== File upload ==========

    [Fact]
    public void File_Upload_Maps_To_IFormFile()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "AvatarUploadRequest.cs");

        Assert.Contains("IFormFile File", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.Contains("string? Caption", content);
    }

    // ========== Operation naming ==========

    [Fact]
    public void OperationId_Stripped_Of_Tag_Prefix()
    {
        // "users_listAll" with tag "Users" → field name "ListAll"
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.Contains("ListAll", CompilationHelper.FindFile(result, "UsersContract.cs"));
    }

    [Fact]
    public void Missing_OperationId_Derives_FieldName()
    {
        // GET /api/status with no operationId, no tag → DefaultContract, derived name "GetApiStatus"
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.Contains("GetApiStatus", CompilationHelper.FindFile(result, "DefaultContract.cs"));
    }

    // ========== additionalProperties: false ==========

    [Fact]
    public void AdditionalProperties_False_Does_Not_Crash()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "StrictDto.cs");

        Assert.Contains("string Name", content);
        Assert.Contains("bool Locked", content);
        // Should NOT produce a Dictionary — it's a regular record
        Assert.DoesNotContain("Dictionary", content);
    }

    [Fact]
    public void AdditionalProperties_True_Skips_Record_Generation()
    {
        // A top-level schema with additionalProperties: true and no properties
        // should NOT generate a record — it resolves to Dictionary inline.
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("OpenMapDto"));
    }

    // ========== Bare object ==========

    [Fact]
    public void Bare_Object_Skips_Record_Generation()
    {
        // { "type": "object" } with no properties — resolved inline as Dictionary, no record generated
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("BareObjectDto"));
    }

    [Fact]
    public void Ref_To_PropertyLess_Object_Resolves_To_Dictionary()
    {
        // A $ref pointing to a property-less object schema (OpenMapDto, BareObjectDto)
        // should resolve to Dictionary on the consuming property, not to a dead type name.
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "MapRefConsumerDto.cs");

        Assert.Contains("Dictionary<string, System.Text.Json.JsonElement> OpenMap", content);
        Assert.Contains("Dictionary<string, System.Text.Json.JsonElement> BareObj", content);
        Assert.Contains("string Label", content);
    }

    // ========== Inline anonymous objects ==========

    [Fact]
    public void Inline_Object_In_Schema_Produces_Synthetic_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "InlineParentDto.cs");

        // The nested property should reference the synthetic type, not JsonElement
        Assert.Contains("InlineParentDtoNested Nested", content);
        Assert.DoesNotContain("JsonElement", content);

        // The synthetic record should be emitted
        var syntheticContent = CompilationHelper.FindFile(result, "InlineParentDtoNested.cs");
        Assert.Contains("long X", syntheticContent);
        Assert.Contains("long Y", syntheticContent);
    }

    [Fact]
    public void Inline_Object_In_Endpoint_Produces_Synthetic_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        // Inline request body → synthetic CreateRequest record
        var requestContent = CompilationHelper.FindFile(result, "CreateRequest.cs");
        Assert.Contains("string Title", requestContent);
        Assert.Contains("long? Count", requestContent);

        // Inline response body → synthetic CreateResponse record
        var responseContent = CompilationHelper.FindFile(result, "CreateResponse.cs");
        Assert.Contains("Guid Id", responseContent);
        Assert.Contains("bool Created", responseContent);
    }

    // ========== Inline enum properties ==========

    [Fact]
    public void Inline_Enum_Property_Produces_Synthetic_Enum()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "InlineParentDto.cs");

        // Multi-value inline enums should reference synthesised enum types
        Assert.Contains("InlineParentDtoStatus Status", content);
        Assert.Contains("InlineParentDtoCategory? Category", content);

        // Single-value inline enum stays as string (not worth an enum)
        Assert.Contains("string? SingleTag", content);
    }

    [Fact]
    public void Inline_Enum_Emits_Correct_Members()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        var statusContent = CompilationHelper.FindFile(result, "InlineParentDtoStatus.cs");
        Assert.Contains("enum InlineParentDtoStatus", statusContent);
        Assert.Contains("Active", statusContent);
        Assert.Contains("Paused", statusContent);
        Assert.Contains("Canceled", statusContent);

        var categoryContent = CompilationHelper.FindFile(result, "InlineParentDtoCategory.cs");
        Assert.Contains("Bug", categoryContent);
        Assert.Contains("Feature", categoryContent);
        Assert.Contains("Chore", categoryContent);
    }

    // ========== Enum without type ==========

    [Fact]
    public void Enum_Without_Type_Treated_As_String()
    {
        // UntypedEnumDto has enum but no type field — should not crash or warn
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        // UntypedEnumDto has enum values but no explicit type field.
        // IsStringEnum infers type from values — all strings → generates an enum.
        Assert.Contains(result.Files, f => f.FileName.Contains("UntypedEnumDto"));
    }

    // ========== Optional property edge cases ==========

    [Fact]
    public void No_Required_Array_Makes_All_Properties_Optional()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "AllOptionalDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("long? Value", content);
    }

    [Fact]
    public void Empty_Required_Array_Makes_All_Properties_Optional()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "EmptyRequiredDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("long? Value", content);
    }

    [Fact]
    public void Empty_Object_Skips_Record_Generation()
    {
        // { "type": "object", "properties": {} } — no actual properties, no record generated
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("EmptyDto"));
    }

    [Fact]
    public void Single_Property_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "SinglePropDto.cs");

        Assert.Contains("string Value", content);
    }

    // ========== const without type ==========

    [Fact]
    public void Const_Int_Infers_Int_Type()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "ConstIntDto.cs");

        Assert.Contains("int Version", content);
        Assert.Contains("string Label", content);
    }

    [Fact]
    public void Const_String_Infers_String_Type()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "ConstStringDto.cs");

        Assert.Contains("string Kind", content);
        Assert.Contains("bool Enabled", content);
    }

    // ========== bare nullable ==========

    [Fact]
    public void Bare_Nullable_Maps_To_Nullable_JsonElement()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "BareNullableDto.cs");

        Assert.Contains("System.Text.Json.JsonElement? Data", content);
        Assert.Contains("string Name", content);
    }

    // ========== implied object ==========

    [Fact]
    public void Properties_Without_Type_Implies_Object()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "ImpliedObjectDto.cs");

        Assert.Contains("string Title", content);
        Assert.Contains("long Count", content);
    }

    // ========== contextless inline composition ==========

    [Fact]
    public void Inline_AllOf_In_Array_Items_Gets_Named()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "InlineComposedArrayDto.cs");

        // The array items allOf gets a context-derived name, not JsonElement
        Assert.DoesNotContain("JsonElement", content);
        Assert.Contains("List<InlineComposedArrayDtoItems>", content);

        // The synthetic record should have the flattened properties
        var syntheticContent = CompilationHelper.FindFile(result, "InlineComposedArrayDtoItems.cs");
        Assert.Contains("string Street", syntheticContent);
        Assert.Contains("string City", syntheticContent);
    }

    // ========== bare/doc-only schemas suppress warnings ==========

    [Fact]
    public void Bare_Schema_Does_Not_Warn()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "BareSchemaDto.cs");

        // Should map to JsonElement without a warning
        Assert.Contains("System.Text.Json.JsonElement Opaque", content);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("BareSchemaDto"));
    }

    // ========== Wide record ==========

    [Fact]
    public void Wide_Record_Has_All_Properties()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "UserDto.cs");

        Assert.Contains("Guid Id", content);
        Assert.Contains("string Name", content);
        Assert.Contains("Email Email", content);
        Assert.Contains("string? Bio", content);
        Assert.Contains("long Age", content);
        Assert.Contains("long TotalPoints", content);
        Assert.Contains("double Score", content);
        Assert.Contains("float Rating", content);
        Assert.Contains("bool IsActive", content);
        Assert.Contains("DateTime CreatedAt", content);
        Assert.Contains("Priority Priority", content);
        Assert.Contains("List<string> Tags", content);
        Assert.Contains("Dictionary<string, string> Metadata", content);
    }

    // ========== Full round-trip: emit → re-import ==========

    [Fact]
    public void Full_RoundTrip_Emit_And_ReImport()
    {
        // Pass 1: Import → compile → walk
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        // Emit → OpenAPI JSON
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Verify emitted JSON is well-formed and all $refs resolve
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var schemas = doc.GetProperty("components").GetProperty("schemas");
        var schemaNames = new HashSet<string>();
        foreach (var s in schemas.EnumerateObject())
        {
            schemaNames.Add(s.Name);
        }

        var allRefs = new List<string>();
        CollectRefs(doc, allRefs);
        var brokenRefs = allRefs
            .Where(r => r.StartsWith("#/components/schemas/"))
            .Where(r => !schemaNames.Contains(r["#/components/schemas/".Length..]))
            .ToList();
        Assert.True(brokenRefs.Count == 0,
            $"Broken $refs: {string.Join(", ", brokenRefs)}");

        // Pass 2: Re-import → compile → walk
        var result2 = CompilationHelper.Import(json, "KitchenSink");
        var compilation2 = CompilationHelper.CompileImportResult(result2);
        var (discovered2, walker2) = CompilationHelper.DiscoverAndWalk(compilation2);
        var endpoints2 = CompilationHelper.WalkContracts(compilation2, discovered2, walker2);

        // Structural stability: endpoints preserved
        Assert.True(endpoints2.Count >= endpoints.Count,
            $"Lost endpoints: {endpoints.Count} → {endpoints2.Count}");

        // Route templates preserved
        var routes1 = endpoints.Select(e => $"{e.HttpMethod} {e.RouteTemplate}").OrderBy(x => x).ToList();
        var routes2 = endpoints2.Select(e => $"{e.HttpMethod} {e.RouteTemplate}").OrderBy(x => x).ToList();
        foreach (var route in routes1)
        {
            Assert.Contains(route, routes2);
        }

        // Types referenced by endpoints survive (orphan schemas are legitimately excluded)
        var referencedNames = new HashSet<string>();
        foreach (var ep in endpoints)
        {
            foreach (var p in ep.Params)
            {
                TsType.CollectTypeRefs(p.Type, referencedNames);
            }
            foreach (var r in ep.Responses)
            {
                if (r.DataType is not null)
                {
                    TsType.CollectTypeRefs(r.DataType, referencedNames);
                }
            }
        }

        var lostReferenced = referencedNames
            .Where(n => walker.Definitions.ContainsKey(n) && !walker2.Definitions.ContainsKey(n))
            .ToList();
        Assert.True(lostReferenced.Count == 0,
            $"Lost endpoint-referenced types: {string.Join(", ", lostReferenced)}");

        // Enums and brands that were used survive
        var lostEnums = walker.Enums.Keys
            .Where(k => referencedNames.Contains(k))
            .Except(walker2.Enums.Keys).ToList();
        Assert.True(lostEnums.Count == 0,
            $"Lost enums: {string.Join(", ", lostEnums)}");

        var lostBrands = walker.Brands.Keys
            .Where(k => referencedNames.Contains(k))
            .Except(walker2.Brands.Keys).ToList();
        Assert.True(lostBrands.Count == 0,
            $"Lost brands: {string.Join(", ", lostBrands)}");
    }

    // ========== Property-level fidelity: emitted OpenAPI matches input ==========

    [Fact]
    public void Emitted_OpenApi_Preserves_Every_Schema_Property()
    {
        // Import → compile → walk → emit OpenAPI
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var emittedJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var emitted = JsonSerializer.Deserialize<JsonElement>(emittedJson);

        var emittedSchemas = emitted.GetProperty("components").GetProperty("schemas");

        // --- Every object schema's properties must survive with correct type ---

        // UserDto: wide record with every common type
        AssertSchemaProperty(emittedSchemas, "UserDto", "id", "string", "uuid", required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "name", "string", null, required: true);
        // bio is required+nullable: positional constructor param that accepts null
        AssertSchemaProperty(emittedSchemas, "UserDto", "bio", "string", null, required: true, nullable: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "age", "integer", null, required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "totalPoints", "integer", "int64", required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "score", "number", null, required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "rating", "number", "float", required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "isActive", "boolean", null, required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "createdAt", "string", "date-time", required: true);
        AssertSchemaRef(emittedSchemas, "UserDto", "email", "Email", required: true);
        // company is not in original required[] — [RivetOptional] preserves this through round-trip
        AssertSchemaRef(emittedSchemas, "UserDto", "company", "CompanyDto", required: false);
        AssertSchemaRef(emittedSchemas, "UserDto", "priority", "Priority", required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "tags", "array", null, required: true);
        AssertSchemaProperty(emittedSchemas, "UserDto", "metadata", "object", null, required: true);

        // AddressDto: simple flat record
        AssertSchemaProperty(emittedSchemas, "AddressDto", "street", "string", null, required: true);
        AssertSchemaProperty(emittedSchemas, "AddressDto", "city", "string", null, required: true);
        AssertSchemaProperty(emittedSchemas, "AddressDto", "zipCode", "string", null, required: true);

        // CompanyDto: nested $ref
        AssertSchemaProperty(emittedSchemas, "CompanyDto", "name", "string", null, required: true);
        AssertSchemaRef(emittedSchemas, "CompanyDto", "address", "AddressDto", required: true);

        // NOTE: AllPrimitivesDto, NullableFieldsDto are not referenced by any endpoint
        // but are still emitted (all defined schemas are included). Primitive type mapping
        // is tested via UserDto properties above (uuid, int64, float, date-time, etc.)

        // CollectionsDto: arrays and dicts (referenced by GET /api/analytics)
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "stringList", "array", null, required: true);
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "refList", "array", null, required: true);
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "nestedList", "array", null, required: true);
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "stringDict", "object", null, required: true);
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "refDict", "object", null, required: true);
        AssertSchemaProperty(emittedSchemas, "CollectionsDto", "arrayDict", "object", null, required: true);

        // Enums (transitively referenced via UserDto → Priority, ProjectTaskDto → TaskStatus)
        // Original values preserved via [JsonStringEnumMemberName] round-trip
        AssertEnumSchema(emittedSchemas, "Priority", ["low", "medium", "high", "critical"]);
        AssertEnumSchema(emittedSchemas, "TaskStatus", ["my_status", "ACTIVE", "in-progress"]);
        AssertEnumSchema(emittedSchemas, "SingleRole", ["admin"]);

        // Brands (transitively referenced via UserDto)
        AssertBrandSchema(emittedSchemas, "Email", "string");
        AssertBrandSchema(emittedSchemas, "WebsiteUri", "string");
        AssertBrandSchema(emittedSchemas, "WebsiteUrl", "string");
        AssertBrandSchema(emittedSchemas, "ResourceRef", "string");

        // NOTE: ComposedDto, UnionShape, FlexibleDto, InlineParentDto, etc. are not referenced
        // by any endpoint but are still emitted (all defined schemas are included). Composition
        // and union behaviour is tested by the import-side tests above (AllOf_Produces_Flattened_Record, etc.).
    }

    [Fact]
    public void Emitted_OpenApi_Preserves_Every_Endpoint()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var emittedJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var emitted = JsonSerializer.Deserialize<JsonElement>(emittedJson);
        var paths = emitted.GetProperty("paths");

        // Every endpoint from the original spec must appear with correct method + route
        AssertEndpointExists(paths, "get", "/api/users");
        AssertEndpointExists(paths, "post", "/api/users");
        AssertEndpointExists(paths, "get", "/api/users/{userId}");
        AssertEndpointExists(paths, "put", "/api/users/{userId}");
        AssertEndpointExists(paths, "delete", "/api/users/{userId}");
        AssertEndpointExists(paths, "post", "/api/users/{userId}/avatar");
        AssertEndpointExists(paths, "get", "/api/orgs/{orgId}/projects/{projectId}/tasks/{taskId}");
        AssertEndpointExists(paths, "get", "/api/{tenantId}/users/{userId}");
        AssertEndpointExists(paths, "get", "/api/analytics");
        AssertEndpointExists(paths, "get", "/api/health");
        AssertEndpointExists(paths, "delete", "/api/admin/purge");
        AssertEndpointExists(paths, "get", "/api/status");
        AssertEndpointExists(paths, "post", "/api/form-submit");
        AssertEndpointExists(paths, "post", "/api/inline-test");

        // Response types correct
        AssertEndpointResponse(paths, "get", "/api/users", 200, "#/components/schemas/UserDto", isArray: true);
        AssertEndpointResponse(paths, "post", "/api/users", 201, "#/components/schemas/UserDto");
        AssertEndpointResponse(paths, "get", "/api/users/{userId}", 200, "#/components/schemas/UserDto");
        AssertEndpointResponse(paths, "put", "/api/users/{userId}", 200, "#/components/schemas/UserDto");
        AssertEndpointResponse(paths, "delete", "/api/users/{userId}", 204, null);
        AssertEndpointResponse(paths, "get", "/api/health", 200, "#/components/schemas/HealthStatusDto");

        // Error responses preserved
        var putUser = paths.GetProperty("/api/users/{userId}").GetProperty("put");
        var responses = putUser.GetProperty("responses");
        Assert.True(responses.TryGetProperty("400", out _), "PUT /users/{id} missing 400 response");
        Assert.True(responses.TryGetProperty("404", out _), "PUT /users/{id} missing 404 response");
        Assert.True(responses.TryGetProperty("409", out _), "PUT /users/{id} missing 409 response");
        Assert.True(responses.TryGetProperty("422", out _), "PUT /users/{id} missing 422 response");

        // Security: health is anonymous, admin has explicit scheme
        var healthOp = paths.GetProperty("/api/health").GetProperty("get");
        Assert.True(healthOp.TryGetProperty("security", out var healthSec), "Health endpoint missing security");
        Assert.Equal(0, healthSec.GetArrayLength()); // empty array = anonymous

        var adminOp = paths.GetProperty("/api/admin/purge").GetProperty("delete");
        Assert.True(adminOp.TryGetProperty("security", out var adminSec), "Admin endpoint missing security");
        Assert.True(adminSec.GetArrayLength() > 0, "Admin endpoint should have security scheme");
    }

    // --- Schema assertion helpers ---

    private static void AssertSchemaProperty(
        JsonElement schemas, string schemaName, string propName,
        string expectedType, string? expectedFormat,
        bool required, bool nullable = false)
    {
        Assert.True(schemas.TryGetProperty(schemaName, out var schema),
            $"Schema '{schemaName}' not found in emitted OpenAPI");

        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty(propName, out var prop),
            $"Property '{propName}' not found in schema '{schemaName}'");

        // Check type (may be nested under allOf for nullable $ref)
        var actualType = GetType(prop);
        Assert.True(actualType == expectedType,
            $"{schemaName}.{propName}: expected type '{expectedType}', got '{actualType}'");

        // Check format
        if (expectedFormat is not null)
        {
            Assert.True(prop.TryGetProperty("format", out var fmt)
                || (TryUnwrapAllOf(prop, out var inner) && inner.TryGetProperty("format", out fmt)),
                $"{schemaName}.{propName}: expected format '{expectedFormat}', got none");
            Assert.Equal(expectedFormat, fmt.GetString());
        }

        // Check required
        var isRequired = IsInRequired(schema, propName);
        Assert.True(isRequired == required,
            $"{schemaName}.{propName}: expected required={required}, got {isRequired}");

        // Check nullable
        if (nullable)
        {
            var isNullable = prop.TryGetProperty("nullable", out var n) && n.GetBoolean()
                || (TryUnwrapAllOf(prop, out _) && prop.TryGetProperty("nullable", out n) && n.GetBoolean());
            Assert.True(isNullable,
                $"{schemaName}.{propName}: expected nullable=true");
        }
    }

    private static void AssertSchemaRef(
        JsonElement schemas, string schemaName, string propName,
        string expectedRefName, bool required, bool nullable = false)
    {
        Assert.True(schemas.TryGetProperty(schemaName, out var schema),
            $"Schema '{schemaName}' not found");

        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty(propName, out var prop),
            $"Property '{propName}' not found in schema '{schemaName}'");

        // $ref may be direct or wrapped in allOf (for nullable)
        string? actualRef = null;
        if (prop.TryGetProperty("$ref", out var refVal))
        {
            actualRef = refVal.GetString();
        }
        else if (TryUnwrapAllOf(prop, out var inner) && inner.TryGetProperty("$ref", out refVal))
        {
            actualRef = refVal.GetString();
        }

        Assert.True(actualRef is not null,
            $"{schemaName}.{propName}: expected $ref to '{expectedRefName}', got no $ref");
        Assert.Equal($"#/components/schemas/{expectedRefName}", actualRef);

        var isRequired = IsInRequired(schema, propName);
        Assert.True(isRequired == required,
            $"{schemaName}.{propName}: expected required={required}, got {isRequired}");
    }

    private static void AssertEnumSchema(JsonElement schemas, string name, string[] expectedMembers)
    {
        Assert.True(schemas.TryGetProperty(name, out var schema),
            $"Enum schema '{name}' not found");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        var members = schema.GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()!).OrderBy(s => s).ToList();
        var expected = expectedMembers.OrderBy(s => s).ToList();
        Assert.Equal(expected, members);
    }

    private static void AssertBrandSchema(JsonElement schemas, string name, string expectedType)
    {
        Assert.True(schemas.TryGetProperty(name, out var schema),
            $"Brand schema '{name}' not found");
        Assert.True(schema.TryGetProperty("x-rivet-brand", out _),
            $"Brand schema '{name}' missing x-rivet-brand extension");
        Assert.Equal(expectedType, schema.GetProperty("type").GetString());
    }

    private static void AssertEndpointExists(JsonElement paths, string method, string route)
    {
        Assert.True(paths.TryGetProperty(route, out var pathItem),
            $"Path '{route}' not found in emitted OpenAPI");
        Assert.True(pathItem.TryGetProperty(method, out _),
            $"Method '{method}' not found for path '{route}'");
    }

    private static void AssertEndpointResponse(
        JsonElement paths, string method, string route,
        int statusCode, string? expectedSchemaRef, bool isArray = false)
    {
        var op = paths.GetProperty(route).GetProperty(method);
        var responses = op.GetProperty("responses");
        Assert.True(responses.TryGetProperty(statusCode.ToString(), out var resp),
            $"{method.ToUpperInvariant()} {route}: missing {statusCode} response");

        if (expectedSchemaRef is null)
        {
            // Void response — no content block
            Assert.False(resp.TryGetProperty("content", out _),
                $"{method.ToUpperInvariant()} {route}: expected no content for {statusCode}");
            return;
        }

        var content = resp.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (isArray)
        {
            Assert.Equal("array", content.GetProperty("type").GetString());
            Assert.Equal(expectedSchemaRef, content.GetProperty("items").GetProperty("$ref").GetString());
        }
        else
        {
            Assert.Equal(expectedSchemaRef, content.GetProperty("$ref").GetString());
        }
    }

    private static string? GetType(JsonElement prop)
    {
        if (prop.TryGetProperty("type", out var t))
            return t.GetString();
        if (TryUnwrapAllOf(prop, out var inner) && inner.TryGetProperty("type", out t))
            return t.GetString();
        return null;
    }

    private static bool TryUnwrapAllOf(JsonElement prop, out JsonElement inner)
    {
        if (prop.TryGetProperty("allOf", out var allOf) && allOf.GetArrayLength() > 0)
        {
            inner = allOf[0];
            return true;
        }
        inner = default;
        return false;
    }

    private static bool IsInRequired(JsonElement schema, string propName)
    {
        if (!schema.TryGetProperty("required", out var req))
            return false;
        return req.EnumerateArray().Any(v => v.GetString() == propName);
    }

    private static void CollectRefs(JsonElement element, List<string> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        refs.Add(prop.Value.GetString()!);
                    }
                    else
                    {
                        CollectRefs(prop.Value, refs);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectRefs(item, refs);
                }
                break;
        }
    }
}
