using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class JsonSchemaEmitterTests
{
    private static JsonElement ParseDefs(string output)
    {
        // Extract the JSON from: const $defs = {...} as const;
        var start = output.IndexOf("const $defs = ", StringComparison.Ordinal) + "const $defs = ".Length;
        var end = output.IndexOf(" as const;", start, StringComparison.Ordinal);
        var json = output[start..end];
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Primitive_String_Number_Boolean()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SimpleDto(string Name, int Age, bool IsActive);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var dto = defs.GetProperty("SimpleDto");
        Assert.Equal("object", dto.GetProperty("type").GetString());

        var props = dto.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("age").GetProperty("type").GetString());
        Assert.Equal("boolean", props.GetProperty("isActive").GetProperty("type").GetString());

        var required = dto.GetProperty("required");
        Assert.Equal(3, required.GetArrayLength());
    }

    [Fact]
    public void Primitive_Guid_DateTime_Map_To_String()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TimedDto(Guid Id, DateTime CreatedAt);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var props = defs.GetProperty("TimedDto").GetProperty("properties");
        Assert.Equal("string", props.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("createdAt").GetProperty("type").GetString());
    }

    [Fact]
    public void Nullable_Uses_AnyOf_With_Null_Type()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record OptionalDto(string? Email);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var emailSchema = defs.GetProperty("OptionalDto").GetProperty("properties").GetProperty("email");
        var anyOf = emailSchema.GetProperty("anyOf");
        Assert.Equal(2, anyOf.GetArrayLength());
        Assert.Equal("string", anyOf[0].GetProperty("type").GetString());
        Assert.Equal("null", anyOf[1].GetProperty("type").GetString());
    }

    [Fact]
    public void Array_Items_Schema()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ListDto(IReadOnlyList<string> Tags);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var tagsSchema = defs.GetProperty("ListDto").GetProperty("properties").GetProperty("tags");
        Assert.Equal("array", tagsSchema.GetProperty("type").GetString());
        Assert.Equal("string", tagsSchema.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Dictionary_AdditionalProperties()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MetaDto(Dictionary<string, string> Metadata);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var metaSchema = defs.GetProperty("MetaDto").GetProperty("properties").GetProperty("metadata");
        Assert.Equal("object", metaSchema.GetProperty("type").GetString());
        Assert.Equal("string", metaSchema.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    [Fact]
    public void String_Enum_Members()
    {
        var source = """
            using Rivet;

            namespace Test;

            public enum Priority { Low, Medium, High }

            [RivetType]
            public sealed record TaskDto(Priority Status);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        // Enum in $defs
        var prioritySchema = defs.GetProperty("Priority");
        Assert.Equal("string", prioritySchema.GetProperty("type").GetString());
        var members = prioritySchema.GetProperty("enum");
        Assert.Equal(3, members.GetArrayLength());
        Assert.Equal("Low", members[0].GetString());
        Assert.Equal("Medium", members[1].GetString());
        Assert.Equal("High", members[2].GetString());
    }

    [Fact]
    public void TypeRef_Uses_Dollar_Ref_To_Defs()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record UserDto(string Name, AddressDto Address);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var addressProp = defs.GetProperty("UserDto").GetProperty("properties").GetProperty("address");
        Assert.Equal("#/$defs/AddressDto", addressProp.GetProperty("$ref").GetString());

        // AddressDto should also be in $defs
        Assert.True(defs.TryGetProperty("AddressDto", out _));
    }

    [Fact]
    public void Generic_Monomorphised()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            [RivetType]
            public sealed record ContainerDto(PagedResult<ItemDto> Page);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        // Monomorphised PagedResult_ItemDto should exist
        Assert.True(defs.TryGetProperty("PagedResult_ItemDto", out var mono));
        var itemsProp = mono.GetProperty("properties").GetProperty("items");
        Assert.Equal("array", itemsProp.GetProperty("type").GetString());
        Assert.Equal("#/$defs/ItemDto", itemsProp.GetProperty("items").GetProperty("$ref").GetString());

        // ContainerDto references it
        var pageProp = defs.GetProperty("ContainerDto").GetProperty("properties").GetProperty("page");
        Assert.Equal("#/$defs/PagedResult_ItemDto", pageProp.GetProperty("$ref").GetString());
    }

    [Fact]
    public void Brand_Emits_Inner_Type()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Email(string Value);

            [RivetType]
            public sealed record ContactDto(Email Email);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        // Brand should be in $defs as its inner type
        var emailSchema = defs.GetProperty("Email");
        Assert.Equal("string", emailSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void Optional_Property_Not_In_Required()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record OptionalDto(string Name, string? Nickname);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var dto = defs.GetProperty("OptionalDto");
        var required = dto.GetProperty("required");

        // Only "name" should be required (nickname is nullable → optional)
        var requiredNames = new List<string>();
        foreach (var item in required.EnumerateArray())
        {
            requiredNames.Add(item.GetString()!);
        }

        Assert.Contains("name", requiredNames);
    }

    [Fact]
    public void Exports_Are_Self_Contained_Documents()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SimpleDto(string Name);
            """;

        var output = CompilationHelper.EmitSchemas(source);

        // Should contain the export with $ref and $defs
        Assert.Contains("export const SimpleDtoSchema = { \"$ref\": \"#/$defs/SimpleDto\", \"$defs\": $defs } as const;", output);
    }

    [Fact]
    public void Header_Comment()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SimpleDto(string Name);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        Assert.StartsWith("// Generated by Rivet — do not edit", output);
    }

    [Fact]
    public void Unknown_Type_Emits_Empty_Schema()
    {
        // Test via model directly — unknown maps to {}
        var schema = JsonSchemaEmitter.MapTsTypeToSchema(new TsType.Primitive("unknown"));
        Assert.Empty(schema);
    }

    [Fact]
    public void Full_Emission_Mixed_Types()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public enum Priority { Low, Medium, High }

            public sealed record Email(string Value);

            [RivetType]
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record UserDto(
                Guid Id,
                string Name,
                Email Email,
                int Age,
                bool IsActive,
                Priority Priority,
                AddressDto Address,
                IReadOnlyList<string> Tags,
                Dictionary<string, string> Metadata);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        // All types present in $defs
        Assert.True(defs.TryGetProperty("AddressDto", out _));
        Assert.True(defs.TryGetProperty("UserDto", out _));
        Assert.True(defs.TryGetProperty("Priority", out _));
        Assert.True(defs.TryGetProperty("Email", out _));

        // All exports present
        Assert.Contains("export const AddressDtoSchema", output);
        Assert.Contains("export const UserDtoSchema", output);
        Assert.Contains("export const PrioritySchema", output);
        Assert.Contains("export const EmailSchema", output);
    }

    [Fact]
    public void Defs_Json_Is_Valid()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record UserDto(string Name, AddressDto Address);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        // Verify it parsed — if JSON were invalid, ParseDefs would throw
        Assert.True(defs.TryGetProperty("UserDto", out _));
        Assert.True(defs.TryGetProperty("AddressDto", out _));
    }

    [Fact]
    public void Nullable_Ref_Uses_AnyOf()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string Street);

            [RivetType]
            public sealed record UserDto(AddressDto? Address);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);

        var addressProp = defs.GetProperty("UserDto").GetProperty("properties").GetProperty("address");
        var anyOf = addressProp.GetProperty("anyOf");
        Assert.Equal(2, anyOf.GetArrayLength());
        Assert.Equal("#/$defs/AddressDto", anyOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", anyOf[1].GetProperty("type").GetString());
    }

    [Fact]
    public void InlineObject_Emits_Object_Schema()
    {
        // Test via model directly — InlineObject is used for tuples
        var inlineObj = new TsType.InlineObject(new List<(string, TsType)>
        {
            ("item1", new TsType.Primitive("string")),
            ("item2", new TsType.Primitive("number")),
        });

        var schema = JsonSchemaEmitter.MapTsTypeToSchema(inlineObj);
        Assert.Equal("object", schema["type"]);
        var props = (Dictionary<string, object>)schema["properties"];
        Assert.Equal(2, props.Count);
    }

    [Fact]
    [Trait("Category", "Local")]
    public void Zod_Integration()
    {
        var jsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "js");
        Assert.True(
            File.Exists(Path.Combine(jsDir, "node_modules", "zod", "package.json")),
            "Zod not installed — run 'npm install' in Rivet.Tests/js/");

        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record UserDto(string Name, int Age, AddressDto Address);
            """;

        var output = CompilationHelper.EmitSchemas(source);

        var tempDir = Path.Combine(Path.GetTempPath(), $"rivet-jsonschema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "schemas.ts"), output);

            var scriptPath = Path.Combine(jsDir, "test-schemas.mjs");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{scriptPath}\" \"{Path.Combine(tempDir, "schemas.ts")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0,
                $"Zod integration test failed (exit {process.ExitCode}):\nstdout: {stdout}\nstderr: {stderr}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ========== GAP-5: Metadata attributes in JSON Schema output ==========

    [Fact]
    public void Property_Description_Emitted_In_JsonSchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(
                [property: RivetDescription("The item name")] string Name,
                [property: RivetDescription("Count of items")] int Count);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);
        var nameSchema = defs.GetProperty("ItemDto").GetProperty("properties").GetProperty("name");

        Assert.True(nameSchema.TryGetProperty("description", out var desc),
            "JSON Schema should include 'description' on property");
        Assert.Equal("The item name", desc.GetString());
    }

    [Fact]
    public void Property_Default_Emitted_In_JsonSchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ConfigDto(
                [property: RivetDefault("10")] int PageSize,
                [property: RivetDefault("\"hello\"")] string Greeting);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);
        var pageSizeSchema = defs.GetProperty("ConfigDto").GetProperty("properties").GetProperty("pageSize");

        Assert.True(pageSizeSchema.TryGetProperty("default", out var def),
            "JSON Schema should include 'default' on property");
        Assert.Equal(10, def.GetInt32());
    }

    [Fact]
    public void Property_Example_Emitted_In_JsonSchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UserDto(
                [property: RivetExample("\"john@example.com\"")] string Email);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);
        var emailSchema = defs.GetProperty("UserDto").GetProperty("properties").GetProperty("email");

        Assert.True(emailSchema.TryGetProperty("example", out var ex),
            "JSON Schema should include 'example' on property");
        Assert.Equal("john@example.com", ex.GetString());
    }

    [Fact]
    public void Property_ReadOnly_WriteOnly_Emitted_In_JsonSchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(
                [property: RivetReadOnly] string Id,
                [property: RivetWriteOnly] string Secret,
                string Name);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);
        var props = defs.GetProperty("ItemDto").GetProperty("properties");

        Assert.True(props.GetProperty("id").TryGetProperty("readOnly", out var ro) && ro.GetBoolean(),
            "JSON Schema should include 'readOnly: true' on property");
        Assert.True(props.GetProperty("secret").TryGetProperty("writeOnly", out var wo) && wo.GetBoolean(),
            "JSON Schema should include 'writeOnly: true' on property");
        Assert.False(props.GetProperty("name").TryGetProperty("readOnly", out _),
            "Non-annotated property should not have readOnly");
    }

    [Fact]
    public void Type_Description_Emitted_In_JsonSchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetDescription("A simple item")]
            [RivetType]
            public sealed record ItemDto(string Name);
            """;

        var output = CompilationHelper.EmitSchemas(source);
        var defs = ParseDefs(output);
        var itemSchema = defs.GetProperty("ItemDto");

        Assert.True(itemSchema.TryGetProperty("description", out var desc),
            "JSON Schema should include 'description' on type");
        Assert.Equal("A simple item", desc.GetString());
    }

    // ========== GAP-7: Monomorphised generic metadata in JSON Schema ==========

    [Fact]
    public void Monomorphised_Generic_Preserves_Metadata_In_JsonSchema()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PagedResult<T>(
                [property: RivetDescription("Items in this page")]
                [property: RivetConstraints(MinItems = 0, MaxItems = 50)]
                List<T> Items,
                [property: RivetDefault("1")]
                int Page);

            [RivetType]
            public sealed record TaskDto(string Title);

            [RivetContract]
            public static class TaskContract
            {
                public static readonly RouteDefinition<PagedResult<TaskDto>> List =
                    Define.Get<PagedResult<TaskDto>>("/api/tasks");
            }
            """;

        // Must pass endpoints so the generic instance collector finds PagedResult<TaskDto>
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var output = JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums, endpoints);
        var defs = ParseDefs(output);

        // Find the monomorphised schema (PagedResult_TaskDto)
        var monoName = defs.EnumerateObject()
            .First(p => p.Name.Contains("PagedResult")).Name;
        var mono = defs.GetProperty(monoName);
        var items = mono.GetProperty("properties").GetProperty("items");

        Assert.Equal("Items in this page", items.GetProperty("description").GetString());
        Assert.Equal(0, items.GetProperty("minItems").GetInt32());
        Assert.Equal(50, items.GetProperty("maxItems").GetInt32());

        var page = mono.GetProperty("properties").GetProperty("page");
        Assert.Equal(1, page.GetProperty("default").GetInt32());
    }
}
