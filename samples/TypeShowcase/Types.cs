using Rivet;

namespace TypeShowcase;

// ========== Primitives & basics ==========

[RivetType]
public sealed record AllPrimitives(
    string Name,
    int Count,
    long BigCount,
    double Rate,
    decimal Price,
    float Rating,
    bool IsActive,
    Guid Id,
    DateTime CreatedAt,
    DateTimeOffset UpdatedAt,
    DateOnly BirthDate);

// ========== Nullable variants ==========

[RivetType]
public sealed record NullableShowcase(
    string? MaybeName,
    int? MaybeCount,
    bool? MaybeActive,
    DateTime? MaybeDate,
    AddressDto? MaybeAddress);

// ========== Collections ==========

[RivetType]
public sealed record CollectionShowcase(
    List<string> Tags,
    int[] Scores,
    IReadOnlyList<Guid> Ids,
    IEnumerable<bool> Flags,
    List<List<string>> NestedList,
    List<List<List<int>>> Grid3D);

// ========== Dictionaries ==========

[RivetType]
public sealed record DictionaryShowcase(
    Dictionary<string, string> SimpleMap,
    Dictionary<string, int> Counts,
    Dictionary<string, AddressDto> AddressBook,
    Dictionary<string, List<string>> TagGroups,
    Dictionary<string, Dictionary<string, bool>> PermissionMatrix);

// ========== Enums ==========

public enum Priority { Low, Medium, High, Critical }
public enum TaskStatus { Draft, Active, InProgress, Done, Cancelled }

[RivetType]
public sealed record WithEnums(
    Priority Priority,
    TaskStatus Status,
    List<Priority> AllPriorities);

// ========== Value objects (brands) ==========

public sealed record Email(string Value);
public sealed record UserId(Guid Value);
public sealed record Quantity(int Value);

[RivetType]
public sealed record BrandedShowcase(
    Email Email,
    UserId AuthorId,
    Quantity Amount,
    List<Email> CcList);

// ========== Generics ==========

[RivetType]
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);

[RivetType]
public sealed record Either<TLeft, TRight>(
    TLeft? Left,
    TRight? Right,
    bool IsLeft);

// ========== Nested records ==========

public sealed record AddressDto(string Street, string City, string ZipCode, string Country);
public sealed record CompanyDto(string Name, AddressDto HeadOffice, List<AddressDto> Branches);

[RivetType]
public sealed record PersonDto(
    Guid Id,
    string FullName,
    Email Email,
    AddressDto HomeAddress,
    CompanyDto? Employer);

// ========== Deep generic nesting ==========

[RivetType]
public sealed record DashboardDto(
    PagedResult<PersonDto> People,
    Dictionary<string, List<PagedResult<PersonDto>>> PeopleByRegion,
    Either<string, PersonDto> SearchResult,
    List<Either<string, int>> MixedResults);

// ========== Recursive / self-referential ==========

[RivetType]
public sealed record TreeNode(
    string Label,
    List<TreeNode> Children);

[RivetType]
public sealed record Category(
    Guid Id,
    string Name,
    Category? Parent,
    List<Category> SubCategories);

// ========== Mutually referential ==========

[RivetType]
public sealed record Folder(
    string Name,
    List<Folder> SubFolders,
    List<Document> Documents);

public sealed record Document(
    string Title,
    DateTime CreatedAt,
    Folder Parent);

// ========== Tuples ==========

[RivetType]
public sealed record TupleShowcase(
    (string Key, int Value) SimplePair,
    (string Label, double Score, bool Active) Triple,
    List<(string Name, int Count)> Rankings,
    Dictionary<string, (int Min, int Max)> Ranges,
    (AddressDto Address, Priority Priority) Ranked,
    (string Key, int Value)? MaybePair);

// ========== Nullable inside generics ==========

[RivetType]
public sealed record NullableGenerics(
    List<string?> MaybeStrings,
    PagedResult<PersonDto?> MaybePeople,
    Dictionary<string, AddressDto?> MaybeAddresses,
    Either<string?, int?> NullableEither);

// ========== Collections of dictionaries and vice versa ==========

[RivetType]
public sealed record CollectionDictionaryMix(
    List<Dictionary<string, string>> ListOfMaps,
    Dictionary<string, List<Dictionary<string, int>>> DeepMix);

// ========== Generic-on-generic ==========

[RivetType]
public sealed record Wrapper<T>(T Value, string Label);

[RivetType]
public sealed record GenericNesting(
    Wrapper<PagedResult<PersonDto>> WrappedPage,
    Wrapper<Either<string, int>> WrappedEither,
    Wrapper<Wrapper<string>> DoubleWrapped,
    Either<PagedResult<PersonDto>, PagedResult<AddressDto>> EitherOfPaged,
    PagedResult<Either<string, PersonDto>> PagedOfEither);

// ========== Self-referential generic (CRTP) ==========

[RivetType]
public sealed record Comparable<T>(T Value, Comparable<T>? Next) where T : notnull;

[RivetType]
public sealed record CrtpShowcase(
    Comparable<string> StringChain,
    Comparable<int> IntChain,
    List<Comparable<PersonDto>> PersonChains);

// ========== Deeply generic consumer ==========

[RivetType]
public sealed record Converter<TIn, TOut>(TIn Input, TOut Output, bool Success);

[RivetType]
public sealed record PipelineDto(
    Converter<string, int> Parser,
    Converter<Either<string, int>, PagedResult<PersonDto>> ComplexPipe,
    List<Converter<string, string>> TransformChain);

// ========== Generic with tuple fields ==========

[RivetType]
public sealed record TaggedResult<T>(
    T Data,
    (string Source, DateTime FetchedAt) Meta);

[RivetType]
public sealed record GenericTupleMix(
    TaggedResult<PersonDto> TaggedPerson,
    List<TaggedResult<(string Key, int Value)>> TaggedPairs,
    Dictionary<string, TaggedResult<AddressDto>> TaggedAddresses);
