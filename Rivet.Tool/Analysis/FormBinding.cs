using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The members ASP.NET binds on a complex input, and the names its form binders read them
/// under, worked out once for the tool. Whether a form input carries files (so it lowers to
/// multipart), which parts a form body lists and what each part is called all come from here
/// (<see cref="Parts"/>), and so do the members <c>--check</c> reads on a handler's complex
/// parameter (<see cref="BoundMembers"/>, through <c>ParameterBindingSources</c>). The runtime
/// twin of <see cref="FormBoundProperties"/> is <c>RequestInputLowering.FormBoundProperties</c>;
/// Roslyn symbols and <c>System.Type</c> can't share code, so
/// <c>FormFileTests.Tool_and_runtime_agree_on_which_inputs_carry_files</c> holds the two to one
/// table of shapes. What each host reads is pinned on real hosts in <c>FormPartNameTests</c>,
/// <c>MvcBindingHostTests</c> and <c>MinimalApiBindingHostTests</c>.
/// </summary>
internal static class FormBinding
{
    private static readonly SymbolDisplayFormat _fullName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    );

    /// <summary>
    /// A member a host binds on a complex input: a property, a parameter of the bound
    /// constructor, or both when the constructor parameter sets the property (a positional
    /// record's). The hosts read its attributes from different places.
    /// </summary>
    internal sealed record BoundMember(
        string Name,
        ITypeSymbol Type,
        IPropertySymbol? Property,
        IParameterSymbol? ConstructorParameter
    )
    {
        /// <summary>
        /// The constructor parameter's attributes and the property's together, as
        /// RequestDelegateFactory reads an <c>[AsParameters]</c> member.
        /// </summary>
        public IReadOnlyList<AttributeData> Attributes =>
            [.. ConstructorParameter?.GetAttributes() ?? [], .. Property?.GetAttributes() ?? []];

        /// <summary>
        /// What MVC reads: a constructor-bound member's parameter attributes alone, since
        /// its metadata comes from the bound constructor's parameter and a
        /// <c>[property: FromForm(Name = ...)]</c> on a positional record is never seen;
        /// otherwise the property's.
        /// </summary>
        public IReadOnlyList<AttributeData> MvcAttributes =>
            ConstructorParameter?.GetAttributes() ?? Property?.GetAttributes() ?? [];

        /// <summary>
        /// What the minimal-API form mapper reads: the property's attributes, for a
        /// constructor-bound member too (<c>[DataMember]</c> can only sit on the property).
        /// </summary>
        public IReadOnlyList<AttributeData> FormMapperAttributes => Property?.GetAttributes() ?? [];
    }

    /// <summary>Which hosts can serve an endpoint whose form body is being named.</summary>
    internal enum FormHosts
    {
        /// <summary>A <c>[RivetEndpoint]</c> action: MVC's model binder alone.</summary>
        Mvc,

        /// <summary>
        /// A contract, which a controller or a minimal-API handler may implement: MVC's model
        /// binder and the minimal-API form mapper both.
        /// </summary>
        Any,
    }

    /// <summary>
    /// One part of a form body, named as every host that can serve it reads it.
    /// <paramref name="Required"/> is set when some host refuses a request without it,
    /// whatever the member says about optionality: on a contract, a positional record's
    /// constructor-bound part, since the minimal-API form mapper answers 400 when any
    /// constructor parameter is missing (nullable and <c>[RivetOptional]</c> ones included)
    /// while MVC binds it as null. Sending it satisfies both.
    /// </summary>
    internal sealed record FormPart(
        IPropertySymbol Property,
        string Name,
        bool IsFile,
        bool Required = false
    );

    /// <summary>
    /// The members ASP.NET binds on <paramref name="type"/>: each parameter of its single
    /// public parameterised constructor (a record's primary constructor) with the
    /// form-bound property it sets, then every other form-bound property with a public
    /// setter or init accessor. The properties are <see cref="FormBoundProperties"/>, so a
    /// derived member hides its base's however accessible it is, and a package base counts.
    /// </summary>
    internal static IReadOnlyList<BoundMember> BoundMembers(
        Compilation compilation,
        INamedTypeSymbol type
    )
    {
        var properties = FormBoundProperties(compilation, type);
        var constructorParameters = type
            .InstanceConstructors.Where(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length > 0
            )
            .ToList()
            is [var constructor]
            ? constructor.Parameters
            : [];

        var members = new List<BoundMember>();
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in constructorParameters)
        {
            var property = properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
            );
            bound.Add(parameter.Name);
            members.Add(
                new BoundMember(
                    property?.Name ?? parameter.Name,
                    parameter.Type,
                    property,
                    parameter
                )
            );
        }

        members.AddRange(
            properties
                .Where(property =>
                    property.SetMethod is { DeclaredAccessibility: Accessibility.Public }
                    && !bound.Contains(property.Name)
                )
                .Select(property => new BoundMember(property.Name, property.Type, property, null))
        );
        return members;
    }

    /// <summary>
    /// The parts of a form body that <paramref name="type"/> lowers to, base-most first:
    /// what <see cref="FormBoundProperties"/> lists, less <paramref name="excluded"/> (members
    /// a route token carries), <c>[RivetHeader]</c> members and a <c>[JsonExtensionData]</c>
    /// bag, which no client sends as a field named for it. A non-file member the binders
    /// can't set (no public setter and no constructor parameter) sends nothing; a file is
    /// always a part, so a file that makes the body multipart is always listed. Each part is
    /// named by <see cref="PartName"/>.
    /// </summary>
    internal static IReadOnlyList<FormPart> Parts(
        Compilation compilation,
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        ITypeSymbol type,
        IReadOnlySet<string> excluded,
        FormHosts hosts,
        string where
    )
    {
        var bound = type is INamedTypeSymbol named ? BoundMembers(compilation, named) : [];
        var parts = new List<FormPart>();
        foreach (var property in FormBoundProperties(compilation, type))
        {
            if (
                excluded.Contains(property.Name)
                || typeWalker.GetHeaderName(property) is not null
                || property.HasAttribute(wkt.JsonExtensionData)
            )
            {
                continue;
            }

            var isFile = CarriesFiles(wkt, typeWalker, property.Type);
            var member = bound.FirstOrDefault(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.Property, property)
            );
            if (member is null && !isFile)
            {
                continue;
            }

            parts.Add(
                new FormPart(
                    property,
                    PartName(
                        wkt,
                        member ?? new BoundMember(property.Name, property.Type, property, null),
                        typeWalker.GetJsonMemberName(property) ?? Naming.ToCamelCase(property.Name),
                        hosts,
                        where
                    ),
                    isFile,
                    Required: hosts == FormHosts.Any && member?.ConstructorParameter is not null
                )
            );
        }

        return parts;
    }

    /// <summary>
    /// The name every host in <paramref name="hosts"/> reads <paramref name="member"/> under.
    /// MVC's model binder reads a member's model name (<see cref="MvcModelName"/>: a
    /// <c>[FromForm(Name = ...)]</c> or <c>[BindProperty(Name = ...)]</c>), else its C# name;
    /// the minimal-API form mapper reads its <c>[DataMember(Name = ...)]</c>, else its C# name.
    /// Neither reads <c>[JsonPropertyName]</c>, and both ignore case. When the hosts read
    /// different names, or MVC reads the member from somewhere other than the form, no one
    /// spec name reaches both, so it's <c>RIV1100</c> rather than a guess. The name is spelt
    /// as <paramref name="jsonName"/> when that's the same name (so the input's component can
    /// still describe the body), as the rename when there is one, and camelCased otherwise.
    /// </summary>
    internal static string PartName(
        WellKnownTypes wkt,
        BoundMember member,
        string jsonName,
        FormHosts hosts,
        string where
    )
    {
        if (
            ParameterBindingSources.MvcMemberSource(wkt, member.MvcAttributes, member.Type)
                is { } source
            && source != InputSource.Form
        )
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedBindingSource,
                $"form input member '{member.Name}' on {where} carries a binding attribute that "
                    + $"makes MVC read it from {Describe(source)}, not the form"
                    + (
                        hosts == FormHosts.Any
                            ? ", while the minimal-API form mapper reads it from the form whatever it carries"
                            : ""
                    )
                    + ", so the form body can't say where it's sent. Take the attribute off the member, "
                    + "or move the member out of the form input."
            );
        }

        var mvcName = MvcModelName(member.MvcAttributes, member.Type);
        var mapperName =
            hosts == FormHosts.Any ? DataMemberName(member.FormMapperAttributes) : null;
        var binds = mvcName ?? member.Name;
        if (hosts == FormHosts.Any && !Same(binds, mapperName ?? member.Name))
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedBindingSource,
                $"form input member '{member.Name}' on {where} is read under '{binds}' by MVC's model "
                    + $"binder ({(mvcName is null ? "its C# name" : "its [FromForm(Name)] or [BindProperty(Name)]")}) "
                    + $"but under '{mapperName ?? member.Name}' by the minimal-API form mapper "
                    + $"({(mapperName is null ? "its C# name" : "its [DataMember(Name)]")}), so no one part name "
                    + "reaches every host that can serve the contract. Rename it for both: "
                    + $"[DataMember(Name = \"{mapperName ?? mvcName}\")] for the form mapper and "
                    + $"[FromForm(Name = \"{mapperName ?? mvcName}\")] for MVC (on a positional record's "
                    + "parameter, with the [DataMember] on the property), or drop the rename."
            );
        }

        if (Same(jsonName, binds))
        {
            return jsonName;
        }

        var rename = mvcName ?? mapperName;
        return rename is not null && !Same(rename, member.Name)
            ? rename
            : Naming.ToCamelCase(member.Name);
    }

    /// <summary>
    /// The model name MVC binds a parameter or member under (BindingInfo.BinderModelName):
    /// the name of the first IModelNameProvider on it, which is the <c>Name =</c> of a
    /// [From*] or [BindProperty] and the <c>Prefix =</c> of a [Bind], then the same on its
    /// type (a class-level [Bind(Prefix = "c")]); null when none names one.
    /// </summary>
    internal static string? MvcModelName(
        IReadOnlyList<AttributeData> attributes,
        ITypeSymbol type
    ) =>
        attributes
            .Concat(type.GetAttributes())
            .Where(attribute =>
                attribute.AttributeClass is { } attributeClass
                && attributeClass.AllInterfaces.Any(candidate =>
                    FullName(candidate)
                    == "Microsoft.AspNetCore.Mvc.ModelBinding.IModelNameProvider"
                )
            )
            .Select(attribute =>
                attribute.NamedArgument(
                    FullName(attribute.AttributeClass) == "Microsoft.AspNetCore.Mvc.BindAttribute"
                        ? "Prefix"
                        : "Name"
                )
            )
            .OfType<string>()
            .FirstOrDefault(name => name.Length > 0);

    /// <summary>
    /// The <c>[DataMember(Name =)]</c> the minimal-API form mapper reads a member under in
    /// place of its C# name. The mapper honours no other rename: a member's [FromForm(Name =)]
    /// and [JsonPropertyName] change nothing.
    /// </summary>
    internal static string? DataMemberName(IReadOnlyList<AttributeData> attributes) =>
        attributes
            .Where(attribute =>
                FullName(attribute.AttributeClass)
                == "System.Runtime.Serialization.DataMemberAttribute"
            )
            .Select(attribute => attribute.NamedArgument("Name"))
            .OfType<string>()
            .FirstOrDefault(name => name.Length > 0);

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Describe(InputSource source) =>
        source switch
        {
            InputSource.Route => "the route",
            InputSource.Query => "the query string",
            InputSource.Header => "a header",
            InputSource.Body => "a JSON body",
            InputSource.NotRequest => "a service",
            InputSource.WholeForm => "the whole form at once",
            _ => "a custom model binder",
        };

    private static string? FullName(ISymbol? symbol) => symbol?.ToDisplayString(_fullName);

    /// <summary>
    /// The properties ASP.NET's form binder binds on <paramref name="type"/>: the public
    /// instance properties that aren't indexers, along the whole base chain (the walkable-
    /// assembly cutoff doesn't apply: the binder reads a package's base class too), where the
    /// most-derived property of a name, whatever its accessibility, hides the base's.
    /// <c>[JsonIgnore]</c> doesn't count, since the form binder never reads it. Base-most first,
    /// a hiding property taking its base's place, as the body's properties are ordered.
    /// </summary>
    /// <remarks>
    /// Roslyn imports a package's types with <c>MetadataImportOptions.Public</c>, so their
    /// internal and private properties aren't symbols at all, yet at runtime an
    /// <c>internal new</c> property hides its base's as any other does. Their names are read
    /// from the package's metadata instead, so the tool and the runtime agree.
    /// </remarks>
    internal static IReadOnlyList<IPropertySymbol> FormBoundProperties(
        Compilation compilation,
        ITypeSymbol type
    )
    {
        var chain = new List<INamedTypeSymbol>();
        for (
            var current = type as INamedTypeSymbol;
            current is not null;
            current = current.BaseType
        )
        {
            if (
                current.SpecialType
                is not (SpecialType.System_Object or SpecialType.System_ValueType)
            )
            {
                chain.Add(current);
            }
        }

        chain.Reverse();
        // A null slot is a name a non-public property took: hidden, and bound by nobody.
        var ordered = new List<IPropertySymbol?>();
        var slotByName = new Dictionary<string, int>(StringComparer.Ordinal);
        void Declare(string name, IPropertySymbol? property)
        {
            if (slotByName.TryGetValue(name, out var slot))
            {
                ordered[slot] = property;
            }
            else
            {
                slotByName[name] = ordered.Count;
                ordered.Add(property);
            }
        }

        foreach (var link in chain)
        {
            var declared = link.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(property => property is { IsStatic: false, IsIndexer: false })
                .ToList();
            foreach (var property in declared)
            {
                Declare(
                    property.Name,
                    property.DeclaredAccessibility == Accessibility.Public ? property : null
                );
            }

            foreach (var hidden in UnimportedInstancePropertyNames(compilation, link, declared))
            {
                Declare(hidden, null);
            }
        }

        return ordered.OfType<IPropertySymbol>().ToList();
    }

    /// <summary>
    /// A form-bound property that carries files: an <c>IFormFile</c>, or an array, <c>List&lt;T&gt;</c>
    /// or sequence interface of them (the runtime's <c>IsFormFiles</c>).
    /// </summary>
    internal static bool CarriesFiles(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        ITypeSymbol type
    ) =>
        SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile)
        || typeWalker.IsCollectionOf(type, wkt.IFormFile);

    /// <summary>
    /// The instance, non-indexer properties a metadata type declares that Roslyn didn't
    /// import as symbols (its non-public ones). Empty for a type with source, whose every
    /// member is a symbol.
    /// </summary>
    private static IEnumerable<string> UnimportedInstancePropertyNames(
        Compilation compilation,
        INamedTypeSymbol type,
        IReadOnlyList<IPropertySymbol> imported
    )
    {
        if (
            !type.Locations.All(location => location.IsInMetadata)
            || compilation.GetMetadataReference(type.ContainingAssembly)
                is not PortableExecutableReference reference
            || reference.GetMetadata() is not AssemblyMetadata metadata
        )
        {
            yield break;
        }

        var definition = type.OriginalDefinition;
        foreach (var module in metadata.GetModules())
        {
            var reader = module.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var typeDefinition = reader.GetTypeDefinition(handle);
                if (!Matches(reader, typeDefinition, definition))
                {
                    continue;
                }

                foreach (var propertyHandle in typeDefinition.GetProperties())
                {
                    var property = reader.GetPropertyDefinition(propertyHandle);
                    var signature = reader.GetBlobReader(property.Signature);
                    var isInstance = signature.ReadSignatureHeader().IsInstance;
                    var name = reader.GetString(property.Name);
                    if (
                        isInstance
                        && signature.ReadCompressedInteger() == 0
                        && !imported.Any(symbol => symbol.MetadataName == name)
                    )
                    {
                        yield return name;
                    }
                }

                yield break;
            }
        }
    }

    private static bool Matches(
        MetadataReader reader,
        TypeDefinition typeDefinition,
        INamedTypeSymbol type
    )
    {
        if (!reader.StringComparer.Equals(typeDefinition.Name, type.MetadataName))
        {
            return false;
        }

        var declaringType = typeDefinition.GetDeclaringType();
        if (type.ContainingType is { } containing)
        {
            return !declaringType.IsNil
                && Matches(reader, reader.GetTypeDefinition(declaringType), containing);
        }

        return declaringType.IsNil
            && reader.StringComparer.Equals(
                typeDefinition.Namespace,
                type.ContainingNamespace.IsGlobalNamespace
                    ? ""
                    : type.ContainingNamespace.ToDisplayString()
            );
    }
}
