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
        Assert.Contains("int IntField", content);
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
        Assert.Contains("int? NullableInt", content);
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
    public void Enum_Values_PascalCased()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var priority = walker.Enums["Priority"];
        Assert.Contains("Low", priority.Members);
        Assert.Contains("Medium", priority.Members);
        Assert.Contains("High", priority.Members);
        Assert.Contains("Critical", priority.Members);

        var taskStatus = walker.Enums["TaskStatus"];
        Assert.Contains("MyStatus", taskStatus.Members);
        Assert.Contains("ACTIVE", taskStatus.Members);
        Assert.Contains("InProgress", taskStatus.Members);

        var singleRole = walker.Enums["SingleRole"];
        Assert.Single(singleRole.Members);
        Assert.Contains("Admin", singleRole.Members);
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

        // DELETE with 204 → bare RouteDefinition (no type param)
        Assert.Contains("public static readonly RouteDefinition Delete", content);
        Assert.Contains("Define.Delete(\"/api/users/{userId}\")", content);
        Assert.Contains(".Status(204)", content);
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
        Assert.Contains("int? Value", requestContent);
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
        Assert.Contains("int? AsInt", content);
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
        Assert.Contains("int X", syntheticContent);
        Assert.Contains("int Y", syntheticContent);
    }

    [Fact]
    public void Inline_Object_In_Endpoint_Produces_Synthetic_Record()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");

        // Inline request body → synthetic CreateRequest record
        var requestContent = CompilationHelper.FindFile(result, "CreateRequest.cs");
        Assert.Contains("string Title", requestContent);
        Assert.Contains("int? Count", requestContent);

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

        // UntypedEnumDto has enum values but no type field. IsStringEnum requires type=="string",
        // so it won't be mapped as an enum. It falls through all checks in MapSchemas and is
        // skipped (no file generated). If referenced inline, ResolveCSharpType returns "string".
        Assert.DoesNotContain(result.Warnings, w => w.Contains("UntypedEnumDto"));
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("UntypedEnumDto"));
    }

    // ========== Optional property edge cases ==========

    [Fact]
    public void No_Required_Array_Makes_All_Properties_Optional()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "AllOptionalDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("int? Value", content);
    }

    [Fact]
    public void Empty_Required_Array_Makes_All_Properties_Optional()
    {
        var result = CompilationHelper.Import(LoadFixture(), "KitchenSink");
        var content = CompilationHelper.FindFile(result, "EmptyRequiredDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("int? Value", content);
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
        Assert.Contains("int Count", content);
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
        Assert.Contains("int Age", content);
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
