using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;
using Rivet.Tool.Import;

namespace Rivet.Tests;

public sealed class KitchenSinkImportTests
{
    private static string LoadFixture()
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "openapi-kitchen-sink.json"));
    }

    private static ImportResult Import(string json, string ns = "KitchenSink")
    {
        return OpenApiImporter.Import(json, new ImportOptions(ns));
    }

    private static string FindFile(ImportResult result, string fileName)
    {
        var file = result.Files.FirstOrDefault(f => f.FileName.EndsWith(fileName));
        Assert.NotNull(file);
        return file.Content;
    }

    private static Compilation CompileGeneratedFiles(ImportResult result)
    {
        return CompilationHelper.CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray());
    }

    // ========== Compilation ==========

    [Fact]
    public void Generated_CSharp_Compiles()
    {
        var result = Import(LoadFixture());

        var errors = CompileGeneratedFiles(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== Roslyn round-trip ==========

    [Fact]
    public void Contracts_Survive_Roslyn_RoundTrip()
    {
        var result = Import(LoadFixture());
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        // 13 endpoints: 6 Users + 1 Orgs + 1 Tenants + 1 Analytics + 1 Health + 2 Admin + 1 Default
        Assert.Equal(13, endpoints.Count);

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
    }

    [Fact]
    public void Types_Survive_Roslyn_RoundTrip()
    {
        var result = Import(LoadFixture());
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);

        // Records
        Assert.True(walker.Definitions.ContainsKey("UserDto"));
        Assert.True(walker.Definitions.ContainsKey("AddressDto"));
        Assert.True(walker.Definitions.ContainsKey("CompanyDto"));
        Assert.True(walker.Definitions.ContainsKey("ProjectTaskDto"));
        Assert.True(walker.Definitions.ContainsKey("CollectionsDto"));
        Assert.True(walker.Definitions.ContainsKey("NullableFieldsDto"));
        Assert.True(walker.Definitions.ContainsKey("NotFoundDto"));
        Assert.True(walker.Definitions.ContainsKey("ValidationErrorDto"));

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
        var result = Import(LoadFixture());
        var content = FindFile(result, "AllPrimitivesDto.cs");

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
        var result = Import(LoadFixture());
        var content = FindFile(result, "NullableFieldsDto.cs");

        Assert.Contains("string? NullableString", content);
        Assert.Contains("int? NullableInt", content);
        Assert.Contains("AddressDto? NullableRef", content);
    }

    [Fact]
    public void Collection_Types_Map_Correctly()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "CollectionsDto.cs");

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
        var result = Import(LoadFixture());
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);

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
        var result = Import(LoadFixture());

        Assert.Contains("public sealed record Email(string Value)", FindFile(result, "Email.cs"));
        Assert.Contains("public sealed record WebsiteUri(string Value)", FindFile(result, "WebsiteUri.cs"));
        Assert.Contains("public sealed record WebsiteUrl(string Value)", FindFile(result, "WebsiteUrl.cs"));
        Assert.Contains("public sealed record ResourceRef(string Value)", FindFile(result, "ResourceRef.cs"));

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
        var result = Import(LoadFixture());
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);

        Assert.True(walker.Definitions.ContainsKey("ProjectTaskDto"));
        Assert.True(walker.Definitions.ContainsKey("CompanyDto"));
        Assert.True(walker.Definitions.ContainsKey("AddressDto"));

        var projectTask = walker.Definitions["ProjectTaskDto"];
        Assert.Contains(projectTask.Properties, p => p.Name == "assignee");

        var company = walker.Definitions["CompanyDto"];
        Assert.Contains(company.Properties, p => p.Name == "address");
    }

    // ========== Routes ==========

    [Fact]
    public void Multi_Segment_Routes_Preserved()
    {
        var result = Import(LoadFixture());
        var compilation = CompileGeneratedFiles(result);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);

        Assert.Contains(endpoints, e =>
            e.RouteTemplate == "/api/orgs/{orgId}/projects/{projectId}/tasks/{taskId}");
    }

    // ========== Endpoints ==========

    [Fact]
    public void Void_Endpoints_Have_No_OutputType()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "UsersContract.cs");

        // DELETE with 204 → bare RouteDefinition (no type param)
        Assert.Contains("public static readonly RouteDefinition Delete", content);
        Assert.Contains("Define.Delete(\"/api/users/{userId}\")", content);
        Assert.Contains(".Status(204)", content);
    }

    [Fact]
    public void Error_Responses_Preserved()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "UsersContract.cs");

        // PUT /api/users/{userId} has 400, 404, 409, 422 error responses
        Assert.Contains(".Returns<ValidationErrorDto>(400, \"Bad request\")", content);
        Assert.Contains(".Returns<NotFoundDto>(404, \"User not found\")", content);
        Assert.Contains(".Returns<ConflictDto>(409, \"Conflict\")", content);
        Assert.Contains(".Returns<ValidationErrorDto>(422, \"Unprocessable entity\")", content);
    }

    [Fact]
    public void Security_Annotations_Correct()
    {
        var result = Import(LoadFixture());

        // Health endpoint has security: [] → .Anonymous()
        Assert.Contains(".Anonymous()", FindFile(result, "HealthContract.cs"));

        // Admin purge has security: [{"admin": []}] → .Secure("admin")
        Assert.Contains(".Secure(\"admin\")", FindFile(result, "AdminContract.cs"));

        // Users endpoints inherit global → .Secure("bearer")
        Assert.Contains(".Secure(\"bearer\")", FindFile(result, "UsersContract.cs"));
    }

    // ========== Tags / contracts ==========

    [Fact]
    public void Tags_Produce_Separate_Contracts()
    {
        var result = Import(LoadFixture());
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
        var result = Import(LoadFixture());
        Assert.Contains(result.Files, f => f.FileName == "Contracts/DefaultContract.cs");
    }

    // ========== Warnings ==========

    [Fact]
    public void Unsupported_Schemas_Produce_Warnings_Not_Failures()
    {
        var result = Import(LoadFixture());

        // Should have warnings for allOf, anyOf, oneOf, discriminator
        Assert.Contains(result.Warnings, w => w.Contains("UnionShape"));
        Assert.Contains(result.Warnings, w => w.Contains("ComposedDto"));
        Assert.Contains(result.Warnings, w => w.Contains("FlexibleDto"));
        Assert.Contains(result.Warnings, w => w.Contains("DiscriminatedShape"));

        // Import should not throw — we already got here
        // Generated C# should still compile
        var errors = CompileGeneratedFiles(result).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    // ========== File upload ==========

    [Fact]
    public void File_Upload_Maps_To_IFormFile()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "AvatarUploadRequest.cs");

        Assert.Contains("IFormFile File", content);
        Assert.Contains("using Microsoft.AspNetCore.Http;", content);
        Assert.Contains("string? Caption", content);
    }

    // ========== Operation naming ==========

    [Fact]
    public void OperationId_Stripped_Of_Tag_Prefix()
    {
        // "users_listAll" with tag "Users" → field name "ListAll"
        var result = Import(LoadFixture());
        Assert.Contains("ListAll", FindFile(result, "UsersContract.cs"));
    }

    [Fact]
    public void Missing_OperationId_Derives_FieldName()
    {
        // GET /api/status with no operationId, no tag → DefaultContract, derived name "GetApiStatus"
        var result = Import(LoadFixture());
        Assert.Contains("GetApiStatus", FindFile(result, "DefaultContract.cs"));
    }

    // ========== Optional property edge cases ==========

    [Fact]
    public void No_Required_Array_Makes_All_Properties_Optional()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "AllOptionalDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("int? Value", content);
    }

    [Fact]
    public void Empty_Required_Array_Makes_All_Properties_Optional()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "EmptyRequiredDto.cs");

        Assert.Contains("string? Name", content);
        Assert.Contains("int? Value", content);
    }

    [Fact]
    public void Empty_Object_Produces_Parameterless_Record()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "EmptyDto.cs");

        Assert.Contains("public sealed record EmptyDto()", content);
    }

    [Fact]
    public void Single_Property_Record()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "SinglePropDto.cs");

        Assert.Contains("string Value", content);
    }

    // ========== Wide record ==========

    [Fact]
    public void Wide_Record_Has_All_Properties()
    {
        var result = Import(LoadFixture());
        var content = FindFile(result, "UserDto.cs");

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
}
