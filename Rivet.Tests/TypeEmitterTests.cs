using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class TypeEmitterTests
{
    private static string Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var grouping = TypeGrouper.Group(definitions, brands, walker.Enums, walker.TypeNamespaces);
        return string.Concat(grouping.Groups.Select(TypeEmitter.EmitGroupFile));
    }

    [Fact]
    public void Primitives_MapCorrectly()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SimpleDto(
                string Name,
                int Count,
                long BigCount,
                double Rate,
                decimal Price,
                bool IsActive,
                Guid Id,
                DateTime CreatedAt,
                DateTimeOffset UpdatedAt,
                DateOnly BirthDate);
            """;

        var result = Generate(source);

        Assert.Contains("export type SimpleDto = {", result);
        Assert.Contains("name: string;", result);
        Assert.Contains("count: number;", result);
        Assert.Contains("bigCount: number;", result);
        Assert.Contains("rate: number;", result);
        Assert.Contains("price: number;", result);
        Assert.Contains("isActive: boolean;", result);
        Assert.Contains("id: string;", result);
        Assert.Contains("createdAt: string;", result);
        Assert.Contains("updatedAt: string;", result);
        Assert.Contains("birthDate: string;", result);
    }

    [Fact]
    public void NullableValueType_MapsToUnion()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithNullables(int? MaybeCount, bool? MaybeActive);
            """;

        var result = Generate(source);

        Assert.Contains("maybeCount: number | null;", result);
        Assert.Contains("maybeActive: boolean | null;", result);
    }

    [Fact]
    public void NullableReferenceType_MapsToUnion()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithNullableRef(string? MaybeName);
            """;

        var result = Generate(source);

        Assert.Contains("maybeName: string | null;", result);
    }

    [Fact]
    public void Collections_MapToArray()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithCollections(
                List<string> Names,
                int[] Counts,
                IEnumerable<Guid> Ids,
                IReadOnlyList<bool> Flags);
            """;

        var result = Generate(source);

        Assert.Contains("names: string[];", result);
        Assert.Contains("counts: number[];", result);
        Assert.Contains("ids: string[];", result);
        Assert.Contains("flags: boolean[];", result);
    }

    [Fact]
    public void Dictionary_MapsToRecord()
    {
        var source = """
            using Rivet;
            using System.Collections.Generic;

            namespace Test;

            [RivetType]
            public sealed record WithDict(Dictionary<string, int> Scores);
            """;

        var result = Generate(source);

        Assert.Contains("scores: Record<string, number>;", result);
    }

    [Fact]
    public void Enum_MapsToStringUnion()
    {
        var source = """
            using Rivet;

            namespace Test;

            public enum Status { Draft, Active, Closed }

            [RivetType]
            public sealed record WithEnum(Status CurrentStatus);
            """;

        var result = Generate(source);

        // Enum emitted as named type alias
        Assert.Contains("""export type Status = "Draft" | "Active" | "Closed";""", result);
        // Property references the named type
        Assert.Contains("currentStatus: Status;", result);
    }

    [Fact]
    public void NestedRecord_DiscoveredTransitively()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Address(string Street, string City);

            [RivetType]
            public sealed record PersonDto(string Name, Address HomeAddress);
            """;

        var result = Generate(source);

        // Both types should be emitted
        Assert.Contains("export type PersonDto = {", result);
        Assert.Contains("export type Address = {", result);
        // PersonDto references Address by name
        Assert.Contains("homeAddress: Address;", result);
        // Address has its own properties
        Assert.Contains("street: string;", result);
        Assert.Contains("city: string;", result);
    }

    [Fact]
    public void OptionalProperty_NullableNonRequired()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithOptional(string Required, string? Optional);
            """;

        var result = Generate(source);

        Assert.Contains("required: string;", result);
        Assert.Contains("optional: string | null;", result);
    }

    [Fact]
    public void NullableArray_WrapsInParens()
    {
        var source = """
            using Rivet;
            using System.Collections.Generic;

            namespace Test;

            [RivetType]
            public sealed record WithNullableArray(List<string?> MaybeNames);
            """;

        var result = Generate(source);

        Assert.Contains("maybeNames: (string | null)[];", result);
    }

    [Fact]
    public void MultipleRivetTypes_AllEmitted()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CommandA(string Name);

            [RivetType]
            public sealed record CommandB(int Count);
            """;

        var result = Generate(source);

        Assert.Contains("export type CommandA = {", result);
        Assert.Contains("export type CommandB = {", result);
    }

    [Fact]
    public void NoRivetTypes_EmitsNothing()
    {
        var source = """
            namespace Test;

            public sealed record NotAttributed(string Name);
            """;

        var result = Generate(source);

        Assert.Empty(result);
    }

    [Fact]
    public void ComplexScenario_MatchesExpectedOutput()
    {
        var source = """
            using Rivet;
            using System;
            using System.Collections.Generic;

            namespace Test;

            public enum MessageVisibility { Internal, Public }

            [RivetType]
            public sealed record CreateMessageCommand(
                Guid SubmissionId,
                string Body,
                MessageVisibility Visibility);

            [RivetType]
            public sealed record MessageDto(
                Guid Id,
                string Body,
                string AuthorName,
                DateTime CreatedAt);
            """;

        var result = Generate(source);

        Assert.Contains("export type MessageVisibility = \"Internal\" | \"Public\";", result);
        Assert.Contains("export type CreateMessageCommand = {", result);
        Assert.Contains("submissionId: string;", result);
        Assert.Contains("body: string;", result);
        Assert.Contains("visibility: MessageVisibility;", result);
        Assert.Contains("export type MessageDto = {", result);
        Assert.Contains("id: string;", result);
        Assert.Contains("authorName: string;", result);
        Assert.Contains("createdAt: string;", result);
    }

    // ========== Complex type expressions ==========

    [Fact]
    public void DeeplyNestedGeneric_ThreeLevels()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            public sealed record TaskDto(Guid Id, string Title);

            [RivetType]
            public sealed record DashboardDto(
                Dictionary<string, List<PagedResult<TaskDto>>> TasksByCategory);
            """;

        var result = Generate(source);

        Assert.Contains("export type DashboardDto = {", result);
        Assert.Contains("tasksByCategory: Record<string, PagedResult<TaskDto>[]>;", result);
        Assert.Contains("export type PagedResult<T> = {", result);
        Assert.Contains("items: T[];", result);
        Assert.Contains("export type TaskDto = {", result);
    }

    [Fact]
    public void RecursiveType_SelfReferential()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TreeNode(
                string Label,
                List<TreeNode> Children);
            """;

        var result = Generate(source);

        Assert.Contains("export type TreeNode = {", result);
        Assert.Contains("label: string;", result);
        Assert.Contains("children: TreeNode[];", result);
    }

    [Fact]
    public void RecursiveType_MutuallyReferential()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Folder(
                string Name,
                List<Folder> SubFolders,
                List<Document> Documents);

            public sealed record Document(
                string Title,
                Folder Parent);
            """;

        var result = Generate(source);

        Assert.Contains("export type Folder = {", result);
        Assert.Contains("subFolders: Folder[];", result);
        Assert.Contains("documents: Document[];", result);
        Assert.Contains("export type Document = {", result);
        Assert.Contains("parent: Folder;", result);
    }

    [Fact]
    public void NestedDictionaries()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PermissionMatrix(
                Dictionary<string, Dictionary<string, bool>> RolePermissions);
            """;

        var result = Generate(source);

        Assert.Contains("rolePermissions: Record<string, Record<string, boolean>>;", result);
    }

    [Fact]
    public void TripleNestedArray()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Grid3D(List<List<List<int>>> Cells);
            """;

        var result = Generate(source);

        Assert.Contains("cells: number[][][];", result);
    }

    [Fact]
    public void NullableInsideGeneric()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public sealed record TaskDto(Guid Id, string Title);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            [RivetType]
            public sealed record SearchResult(
                PagedResult<TaskDto?> Results,
                List<string?> Suggestions);
            """;

        var result = Generate(source);

        Assert.Contains("results: PagedResult<TaskDto | null>;", result);
        Assert.Contains("suggestions: (string | null)[];", result);
    }

    [Fact]
    public void GenericWithMultipleTypeParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Either<TLeft, TRight>(TLeft? Left, TRight? Right);

            public sealed record ErrorDto(string Message);
            public sealed record UserDto(string Name);

            [RivetType]
            public sealed record ApiResponse(Either<ErrorDto, UserDto> Result);
            """;

        var result = Generate(source);

        Assert.Contains("export type Either<TLeft, TRight> = {", result);
        Assert.Contains("export type ApiResponse = {", result);
        Assert.Contains("result: Either<ErrorDto, UserDto>;", result);
    }

    [Fact]
    public void DictionaryOfArrayOfRecords()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public sealed record AuditEntry(DateTime Timestamp, string Action);

            [RivetType]
            public sealed record AuditLog(
                Dictionary<string, List<AuditEntry>> EntriesByUser);
            """;

        var result = Generate(source);

        Assert.Contains("entriesByUser: Record<string, AuditEntry[]>;", result);
        Assert.Contains("export type AuditEntry = {", result);
        Assert.Contains("timestamp: string;", result);
        Assert.Contains("action: string;", result);
    }

    [Fact]
    public void ArrayOfDictionaries()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ConfigHistory(
                List<Dictionary<string, string>> Snapshots);
            """;

        var result = Generate(source);

        Assert.Contains("snapshots: Record<string, string>[];", result);
    }

    [Fact]
    public void NullableRecordInsideDictionary()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public sealed record ProfileDto(string DisplayName);

            [RivetType]
            public sealed record UserCache(
                Dictionary<string, ProfileDto?> Profiles);
            """;

        var result = Generate(source);

        Assert.Contains("profiles: Record<string, ProfileDto | null>;", result);
    }

    [Fact]
    public void ValueTuple_EmitsInlineObject()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithTuple(
                string Name,
                (string Key, int Value) Pair);
            """;

        var result = Generate(source);

        Assert.Contains("export type WithTuple = {", result);
        Assert.Contains("name: string;", result);
        Assert.Contains("pair: { key: string; value: number; };", result);
    }

    [Fact]
    public void ValueTuple_NestedInCollection()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithTupleList(
                List<(string Label, double Score)> Entries);
            """;

        var result = Generate(source);

        Assert.Contains("entries: { label: string; score: number; }[];", result);
    }

    [Fact]
    public void ValueTuple_WithRecordField()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public sealed record TagDto(Guid Id, string Name);

            [RivetType]
            public sealed record WithRecordTuple(
                (TagDto Tag, int Count) Ranked);
            """;

        var result = Generate(source);

        Assert.Contains("ranked: { tag: TagDto; count: number; };", result);
        Assert.Contains("export type TagDto = {", result);
    }

    [Fact]
    public void ValueTuple_Nullable()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithNullableTuple(
                (string Key, int Value)? MaybePair);
            """;

        var result = Generate(source);

        Assert.Contains("maybePair: { key: string; value: number; } | null;", result);
    }

    // ========== Complex generics ==========

    [Fact]
    public void GenericWrappingGeneric()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(T Value, string Label);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            public sealed record PersonDto(Guid Id, string Name);

            [RivetType]
            public sealed record Nested(
                Wrapper<PagedResult<PersonDto>> WrappedPage,
                Wrapper<Wrapper<string>> DoubleWrapped);
            """;

        var result = Generate(source);

        Assert.Contains("wrappedPage: Wrapper<PagedResult<PersonDto>>;", result);
        Assert.Contains("doubleWrapped: Wrapper<Wrapper<string>>;", result);
    }

    [Fact]
    public void MultipleGenericParams_CrossReferenced()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Either<TLeft, TRight>(TLeft? Left, TRight? Right);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            public sealed record PersonDto(Guid Id, string Name);
            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record CrossGeneric(
                Either<PagedResult<PersonDto>, PagedResult<AddressDto>> EitherOfPaged,
                PagedResult<Either<string, PersonDto>> PagedOfEither);
            """;

        var result = Generate(source);

        Assert.Contains("eitherOfPaged: Either<PagedResult<PersonDto>, PagedResult<AddressDto>>;", result);
        Assert.Contains("pagedOfEither: PagedResult<Either<string, PersonDto>>;", result);
    }

    [Fact]
    public void SelfReferentialGeneric()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Chain<T>(T Value, Chain<T>? Next) where T : notnull;

            public sealed record PersonDto(Guid Id, string Name);

            [RivetType]
            public sealed record ChainConsumer(
                Chain<string> StringChain,
                Chain<int> IntChain,
                List<Chain<PersonDto>> PersonChains);
            """;

        var result = Generate(source);

        Assert.Contains("export type Chain<T> = {", result);
        Assert.Contains("value: T;", result);
        Assert.Contains("next: Chain<T> | null;", result);
        Assert.Contains("stringChain: Chain<string>;", result);
        Assert.Contains("intChain: Chain<number>;", result);
        Assert.Contains("personChains: Chain<PersonDto>[];", result);
    }

    [Fact]
    public void ThreeGenericParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Converter<TIn, TOut>(TIn Input, TOut Output, bool Success);

            [RivetType]
            public sealed record Either<TLeft, TRight>(TLeft? Left, TRight? Right);

            [RivetType]
            public sealed record Pipeline(
                Converter<string, int> Parser,
                Converter<Either<string, int>, string> ComplexPipe,
                Converter<string, string>[] TransformChain);
            """;

        var result = Generate(source);

        Assert.Contains("parser: Converter<string, number>;", result);
        Assert.Contains("complexPipe: Converter<Either<string, number>, string>;", result);
        Assert.Contains("transformChain: Converter<string, string>[];", result);
    }

    [Fact]
    public void GenericWithTupleTypeArg()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaggedResult<T>(T Data, string Source);

            public sealed record AddressDto(string Street, string City);

            [RivetType]
            public sealed record TupleGenericMix(
                TaggedResult<(string Key, int Value)> TaggedPair,
                List<TaggedResult<(string Name, int Count)>> TaggedPairs,
                Dictionary<string, TaggedResult<AddressDto>> TaggedAddresses);
            """;

        var result = Generate(source);

        Assert.Contains("taggedPair: TaggedResult<{ key: string; value: number; }>;", result);
        Assert.Contains("taggedPairs: TaggedResult<{ name: string; count: number; }>[];", result);
        Assert.Contains("taggedAddresses: Record<string, TaggedResult<AddressDto>>;", result);
    }

    [Fact]
    public void RivetType_OnEnum_EmitsStringUnion()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public enum Priority { Low, Medium, High, Critical }
            """;

        var result = Generate(source);

        Assert.Contains("export type Priority =", result);
        Assert.Contains("\"Low\"", result);
        Assert.Contains("\"Medium\"", result);
        Assert.Contains("\"High\"", result);
        Assert.Contains("\"Critical\"", result);
    }

    [Fact]
    public void DeeplyNestedGeneric_FourLevels()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(T Value);

            [RivetType]
            public sealed record FourDeep(
                Dictionary<string, List<Wrapper<List<string>>>> Deep);
            """;

        var result = Generate(source);

        Assert.Contains("deep: Record<string, Wrapper<string[]>[]>;", result);
    }

    [Fact]
    public void JsonPropertyName_OverridesCamelCase()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CustomNameDto(
                [property: JsonPropertyName("display_name")] string DisplayName,
                string NormalProp);
            """;

        var result = Generate(source);

        // display_name should be used instead of displayName
        Assert.Contains("display_name: string;", result);
        Assert.Contains("normalProp: string;", result);
        Assert.DoesNotContain("displayName:", result);
    }

    [Fact]
    public void TypeNameCollision_DifferentNamespaces_SetsHasErrors()
    {
        // Two types with the same simple name but different namespaces should flag an error
        var sources = new[]
        {
            """
            using Rivet;
            namespace Alpha
            {
                [RivetType]
                public sealed record TaskDto(string Id);
            }
            """,
            """
            using Rivet;
            namespace Beta
            {
                [RivetType]
                public sealed record TaskDto(string Name);
            }
            """,
        };

        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var discovered = Rivet.Tool.Analysis.SymbolDiscovery.Discover(compilation);
        var walker = Rivet.Tool.Analysis.TypeWalker.Create(compilation, discovered.RivetTypes);

        Assert.True(walker.HasErrors, "TypeWalker should report error on name collision from different namespaces");
    }

    [Fact]
    public void CollectTypeRefs_StringUnion_DoesNotThrow()
    {
        // StringUnion should be handled explicitly (not fall through to default)
        var names = new HashSet<string>();
        var type = new TsType.StringUnion(["Active", "Archived"]);
        TsType.CollectTypeRefs(type, names);

        // StringUnion has no type refs to collect
        Assert.Empty(names);
    }

    [Fact]
    public void CollectTypeRefs_AllVariants_Exhaustive()
    {
        var names = new HashSet<string>();

        // Ensure all TsType variants are handled without throwing
        TsType.CollectTypeRefs(new TsType.Primitive("string"), names);
        TsType.CollectTypeRefs(new TsType.TypeParam("T"), names);
        TsType.CollectTypeRefs(new TsType.StringUnion(["A"]), names);
        TsType.CollectTypeRefs(new TsType.Nullable(new TsType.TypeRef("Foo")), names);
        TsType.CollectTypeRefs(new TsType.Array(new TsType.TypeRef("Bar")), names);
        TsType.CollectTypeRefs(new TsType.Dictionary(new TsType.TypeRef("Baz")), names);
        TsType.CollectTypeRefs(new TsType.Brand("Id", new TsType.Primitive("string")), names);
        TsType.CollectTypeRefs(new TsType.InlineObject([("k", new TsType.TypeRef("Qux"))]), names);
        TsType.CollectTypeRefs(new TsType.Generic("G", [new TsType.TypeRef("Arg")]), names);

        Assert.Contains("Foo", names);
        Assert.Contains("Bar", names);
        Assert.Contains("Baz", names);
        Assert.Contains("Id", names);
        Assert.Contains("Qux", names);
        Assert.Contains("G", names);
        Assert.Contains("Arg", names);
    }

    [Fact]
    public void JsonIgnore_SkipsProperty()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithIgnoredDto(
                string Visible,
                [property: JsonIgnore] string Hidden);
            """;

        var result = Generate(source);

        Assert.Contains("visible: string;", result);
        Assert.DoesNotContain("hidden", result);
        Assert.DoesNotContain("Hidden", result);
    }

    // ========== TsType shared methods ==========

    [Theory]
    [InlineData("TypeRef")]
    [InlineData("Brand")]
    [InlineData("Dictionary")]
    [InlineData("TypeParam")]
    public void GetNameSuffix_AllBranches(string variant)
    {
        var type = variant switch
        {
            "TypeRef" => (TsType)new TsType.TypeRef("Foo"),
            "Brand" => new TsType.Brand("Email", new TsType.Primitive("string")),
            "Dictionary" => new TsType.Dictionary(new TsType.Primitive("string")),
            "TypeParam" => new TsType.TypeParam("T"),
            _ => throw new ArgumentException(variant),
        };

        var suffix = TsType.GetNameSuffix(type);

        var expected = variant switch
        {
            "TypeRef" => "Foo",
            "Brand" => "Email",
            "Dictionary" => "RecordString",
            "TypeParam" => "T",
            _ => throw new ArgumentException(variant),
        };

        Assert.Equal(expected, suffix);
    }

    [Fact]
    public void ResolveTypeParams_SubstitutesTypeParam()
    {
        var map = new Dictionary<string, TsType>
        {
            ["T"] = new TsType.TypeRef("TaskDto"),
        };

        // Simple TypeParam
        var resolved = TsType.ResolveTypeParams(new TsType.TypeParam("T"), map);
        Assert.Equal(new TsType.TypeRef("TaskDto"), resolved);

        // Unmatched TypeParam passes through
        var unmatched = TsType.ResolveTypeParams(new TsType.TypeParam("U"), map);
        Assert.Equal(new TsType.TypeParam("U"), unmatched);
    }

    [Fact]
    public void ResolveTypeParams_RecursesIntoContainers()
    {
        var map = new Dictionary<string, TsType>
        {
            ["T"] = new TsType.TypeRef("TaskDto"),
        };

        // Array<T> → Array<TaskDto>
        var arrayType = new TsType.Array(new TsType.TypeParam("T"));
        var resolved = TsType.ResolveTypeParams(arrayType, map);
        Assert.Equal(new TsType.Array(new TsType.TypeRef("TaskDto")), resolved);

        // Nullable<T> → Nullable<TaskDto>
        var nullableType = new TsType.Nullable(new TsType.TypeParam("T"));
        resolved = TsType.ResolveTypeParams(nullableType, map);
        Assert.Equal(new TsType.Nullable(new TsType.TypeRef("TaskDto")), resolved);

        // Dictionary<T> → Dictionary<TaskDto>
        var dictType = new TsType.Dictionary(new TsType.TypeParam("T"));
        resolved = TsType.ResolveTypeParams(dictType, map);
        Assert.Equal(new TsType.Dictionary(new TsType.TypeRef("TaskDto")), resolved);

        // Generic<T> → Generic<TaskDto>
        var genericType = new TsType.Generic("PagedResult", [new TsType.TypeParam("T")]);
        resolved = TsType.ResolveTypeParams(genericType, map);
        Assert.IsType<TsType.Generic>(resolved);
        var resolvedGeneric = (TsType.Generic)resolved;
        Assert.Equal("PagedResult", resolvedGeneric.Name);
        Assert.Single(resolvedGeneric.TypeArguments);
        Assert.Equal(new TsType.TypeRef("TaskDto"), resolvedGeneric.TypeArguments[0]);

        // InlineObject with T field → InlineObject with TaskDto field
        var inlineType = new TsType.InlineObject([("data", new TsType.TypeParam("T"))]);
        resolved = TsType.ResolveTypeParams(inlineType, map);
        Assert.IsType<TsType.InlineObject>(resolved);
        var resolvedInline = (TsType.InlineObject)resolved;
        Assert.Single(resolvedInline.Fields);
        Assert.Equal("data", resolvedInline.Fields[0].Name);
        Assert.Equal(new TsType.TypeRef("TaskDto"), resolvedInline.Fields[0].Type);
    }
}
