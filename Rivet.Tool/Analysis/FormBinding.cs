using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

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
        /// <summary>An instance <c>[RivetEndpoint]</c> method, an MVC action: MVC's model binder alone.</summary>
        Mvc,

        /// <summary>
        /// A static <c>[RivetEndpoint]</c> method, a minimal-API handler (MVC never treats one as
        /// an action): the minimal-API form mapper alone.
        /// </summary>
        MinimalApi,

        /// <summary>
        /// A contract, which a controller or a minimal-API handler may implement: MVC's model
        /// binder and the minimal-API form mapper both.
        /// </summary>
        Any,
    }

    /// <summary>
    /// The binders that bind a complex type member by member. They agree on which properties
    /// they can see (<see cref="FormBoundProperties"/>) but not on which constructor they call
    /// (<see cref="BoundConstructor"/>) or which attributes take a member out.
    /// </summary>
    internal enum Binder
    {
        /// <summary>MVC's ComplexObjectModelBinder.</summary>
        Mvc,

        /// <summary>The minimal-API form mapper behind a <c>[FromForm]</c> complex parameter.</summary>
        FormMapper,

        /// <summary>RequestDelegateFactory's <c>[AsParameters]</c> expansion.</summary>
        AsParameters,
    }

    /// <summary>
    /// One part of a form body, named as every host that can serve it reads it.
    /// <paramref name="Required"/> is set when some host refuses a request without it,
    /// whatever the member says about optionality: wherever the minimal-API form mapper can
    /// serve the endpoint (a contract, a static <c>[RivetEndpoint]</c>), a part the
    /// constructor binds, since the mapper answers 400 when any constructor parameter is
    /// missing (nullable and <c>[RivetOptional]</c> ones included) while MVC binds it as null.
    /// Sending it satisfies both.
    /// </summary>
    internal sealed record FormPart(
        IPropertySymbol Property,
        string Name,
        bool IsFile,
        bool Required = false
    );

    /// <summary>
    /// The members <paramref name="binder"/> binds on <paramref name="type"/>: each parameter
    /// of the constructor it calls (<see cref="BoundConstructor"/>) with the property it sets,
    /// then every other property it can see with a public setter or init accessor, less those
    /// an attribute takes out for it (<see cref="Skips"/>); none when it can't create the type
    /// (<see cref="CantCreate"/>). The properties are <see cref="FormBoundProperties"/>, so a
    /// derived member hides its base's however accessible it is, and a package base counts.
    /// </summary>
    internal static IReadOnlyList<BoundMember> BoundMembers(
        Compilation compilation,
        WellKnownTypes wkt,
        INamedTypeSymbol type,
        Binder binder
    )
    {
        var properties = FormBoundProperties(compilation, type);
        var members = new List<BoundMember>();
        if (CantCreate(type, properties, binder) is not null)
        {
            return members;
        }

        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in BoundConstructor(type, properties, binder)?.Parameters ?? [])
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
                    && Skips(wkt, binder, property) is null
                )
                .Select(property => new BoundMember(property.Name, property.Type, property, null))
        );
        return members;
    }

    /// <summary>
    /// The constructor <paramref name="binder"/> creates <paramref name="type"/> with, when its
    /// parameters bind members; null when the binder sets properties alone (a parameterless
    /// constructor) or can't create the type (<see cref="CantCreate"/>).
    /// <list type="bullet">
    /// <item>MVC (DefaultBindingMetadataProvider.GetRecordTypeConstructor) calls a record
    /// class's one public constructor when every parameter matches a visible property by its
    /// exact name and type; otherwise it needs a public parameterless constructor.</item>
    /// <item>The form mapper (FormDataMetadataFactory) and <c>[AsParameters]</c>
    /// (ParameterBindingMethodCache.FindConstructor) call a type's one public constructor,
    /// matching parameters to properties ignoring case. With several, the form mapper binds
    /// nothing and <c>[AsParameters]</c> calls the parameterless one.</item>
    /// </list>
    /// Pinned on real hosts by <c>FormPartNameTests.The_hosts_construct_these_types_differently</c>.
    /// </summary>
    internal static IMethodSymbol? BoundConstructor(
        INamedTypeSymbol type,
        IReadOnlyList<IPropertySymbol> properties,
        Binder binder
    )
    {
        var constructors = PublicConstructors(type);
        if (constructors is not [var constructor] || constructor.Parameters.Length == 0)
        {
            return null;
        }

        return
            binder != Binder.Mvc
            || type is { IsRecord: true, IsValueType: false }
                && constructor.Parameters.All(parameter =>
                    properties.Any(property =>
                        property.Name == parameter.Name
                        && SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type)
                    )
                )
            ? constructor
            : null;
    }

    /// <summary>
    /// Why <paramref name="binder"/> binds no member of <paramref name="type"/> at all, or null
    /// when it can create it. MVC needs its record constructor (<see cref="BoundConstructor"/>),
    /// a public parameterless constructor, or a struct, and otherwise fails the request; the
    /// form mapper answers a type with more than one public constructor with nothing bound.
    /// </summary>
    internal static string? CantCreate(
        INamedTypeSymbol type,
        IReadOnlyList<IPropertySymbol> properties,
        Binder binder
    ) =>
        binder switch
        {
            Binder.Mvc
                when !type.IsValueType
                    && BoundConstructor(type, properties, Binder.Mvc) is null
                    && !PublicConstructors(type)
                        .Any(constructor => constructor.Parameters.Length == 0) =>
                "MVC's model binder can't create it: it needs a public parameterless constructor, or a "
                    + "record's one public constructor whose every parameter matches a property by its "
                    + "exact name and type",
            Binder.FormMapper when PublicConstructors(type).Count > 1 =>
                "the minimal-API form mapper binds nothing on a type with more than one public constructor",
            _ => null,
        };

    // What reflection's GetConstructors() lists: a struct's implicit parameterless
    // constructor isn't one.
    private static IReadOnlyList<IMethodSymbol> PublicConstructors(INamedTypeSymbol type) =>
        [
            .. type.InstanceConstructors.Where(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && !(
                    type.IsValueType
                    && constructor is { IsImplicitlyDeclared: true, Parameters.Length: 0 }
                )
            ),
        ];

    /// <summary>
    /// The attribute that takes <paramref name="property"/> out for <paramref name="binder"/>,
    /// or null when it binds: MVC skips a <c>[BindNever]</c> member and the form mapper an
    /// <c>[IgnoreDataMember]</c> one. Each ignores the other's attribute.
    /// </summary>
    internal static string? Skips(WellKnownTypes wkt, Binder binder, IPropertySymbol property) =>
        binder switch
        {
            Binder.Mvc when property.HasAttribute(wkt.BindNever) => "[BindNever]",
            Binder.FormMapper when property.HasAttribute(wkt.IgnoreDataMember) =>
                "[IgnoreDataMember]",
            _ => null,
        };

    /// <summary>
    /// The parts of a form body that <paramref name="type"/> lowers to, base-most first:
    /// what <see cref="FormBoundProperties"/> lists, less <paramref name="excluded"/> (members
    /// a route token carries), <c>[RivetHeader]</c> members and a <c>[JsonExtensionData]</c>
    /// bag, which no client sends as a field named for it. A member is a part when the
    /// binders of <paramref name="hosts"/> bind it (<see cref="BoundMembers"/>); a non-file
    /// member none of them can set sends nothing. Where the spec would promise a part a host
    /// drops it's <c>RIV1100</c> instead: a file member no binder can set (the body would be
    /// multipart for a part nobody reads), a member one host binds and the other skips, and,
    /// on a contract, a type MVC can't create while the form mapper calls its constructor.
    /// Each part is named by <see cref="PartName"/>.
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
        var named = type as INamedTypeSymbol;
        var properties = FormBoundProperties(compilation, type);
        var binders = hosts switch
        {
            FormHosts.Mvc => new[] { Binder.Mvc },
            FormHosts.MinimalApi => [Binder.FormMapper],
            _ => [Binder.Mvc, Binder.FormMapper],
        };
        foreach (var binder in binders)
        {
            if (named is not null && CantCreate(named, properties, binder) is { } why)
            {
                throw Diagnostics.Error(
                    Diagnostics.UnresolvedBindingSource,
                    $"form input '{named.Name}' on {where} binds no part: {why}. Give it one public "
                        + "constructor: a parameterless one with settable properties, or a positional "
                        + "record's."
                );
            }
        }

        var boundBy = binders.ToDictionary(
            binder => binder,
            binder => named is null ? [] : BoundMembers(compilation, wkt, named, binder)
        );
        var parts = new List<FormPart>();
        foreach (var property in properties)
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
            var members = binders.ToDictionary(
                binder => binder,
                binder =>
                    boundBy[binder]
                        .FirstOrDefault(candidate =>
                            SymbolEqualityComparer.Default.Equals(candidate.Property, property)
                        )
            );
            if (members.Values.All(member => member is null))
            {
                if (isFile && binders.All(binder => Skips(wkt, binder, property) is null))
                {
                    throw Diagnostics.Error(
                        Diagnostics.UnresolvedBindingSource,
                        $"form input member '{property.Name}' on {where} carries files, but nothing "
                            + "can set it: it has no public setter or init accessor and no constructor "
                            + "parameter, so the binders drop the part, and the body would be multipart "
                            + "for a file nobody reads. Give it a public setter or init accessor, or "
                            + "take it out of the form input."
                    );
                }

                continue;
            }

            // All null was handled above, so this is one host binding what the other skips.
            if (binders.Where(binder => members[binder] is null).ToList() is [var dropped])
            {
                var skippedBy = Skips(wkt, dropped, property);
                throw Diagnostics.Error(
                    Diagnostics.UnresolvedBindingSource,
                    $"form input member '{property.Name}' on {where} is "
                        + (
                            dropped == Binder.Mvc
                                ? $"skipped by MVC's model binder ({skippedBy}) but bound by the minimal-API form mapper"
                                : $"skipped by the minimal-API form mapper ({skippedBy}) but bound by MVC's model binder"
                        )
                        + ", so no one form body describes what every host that can serve the contract "
                        + "reads. Skip it for both ([BindNever] and [IgnoreDataMember]) or neither."
                );
            }

            var member = members.Values.First()!;
            parts.Add(
                new FormPart(
                    property,
                    PartName(
                        wkt,
                        member,
                        typeWalker.GetJsonMemberName(property) ?? Naming.ToCamelCase(property.Name),
                        hosts,
                        where
                    ),
                    isFile,
                    Required: hosts != FormHosts.Mvc
                        && members.GetValueOrDefault(Binder.FormMapper)?.ConstructorParameter
                            is not null
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
        var readByMvc = hosts != FormHosts.MinimalApi;
        var readByMapper = hosts != FormHosts.Mvc;
        if (
            readByMvc
            && ParameterBindingSources.MvcMemberSource(wkt, member.MvcAttributes, member.Type)
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

        var mvcName = readByMvc ? MvcModelName(wkt, member.MvcAttributes, member.Type) : null;
        var mapperName = readByMapper ? DataMemberName(wkt, member.FormMapperAttributes) : null;
        var binds = (readByMvc ? mvcName : mapperName) ?? member.Name;
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
        WellKnownTypes wkt,
        IReadOnlyList<AttributeData> attributes,
        ITypeSymbol type
    ) =>
        attributes
            .Concat(type.GetAttributes())
            .Where(attribute =>
                attribute.AttributeClass is { } attributeClass
                && wkt.IModelNameProvider is { } provider
                && attributeClass.AllInterfaces.Contains(provider, SymbolEqualityComparer.Default)
            )
            .Select(attribute =>
                attribute.NamedArgument(attribute.Is(wkt.Bind) ? "Prefix" : "Name")
            )
            .OfType<string>()
            .FirstOrDefault(name => name.Length > 0);

    /// <summary>
    /// The <c>[DataMember(Name =)]</c> the minimal-API form mapper reads a member under in
    /// place of its C# name. The mapper honours no other rename: a member's [FromForm(Name =)]
    /// and [JsonPropertyName] change nothing.
    /// </summary>
    internal static string? DataMemberName(
        WellKnownTypes wkt,
        IReadOnlyList<AttributeData> attributes
    ) =>
        attributes
            .Where(attribute => attribute.Is(wkt.DataMember))
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

    /// <summary>
    /// The properties ASP.NET's form binders can see on <paramref name="type"/>: the instance
    /// properties with a public getter (PropertyHelper.GetVisibleProperties, which both hosts
    /// list members through) that aren't indexers, along the whole base chain (the walkable-
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
                    property.GetMethod is { DeclaredAccessibility: Accessibility.Public }
                        ? property
                        : null
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
    /// What a part of <paramref name="type"/> (one that <see cref="CarriesFiles"/>) sends: one
    /// file for an <c>IFormFile</c>, an array of them (a multipart array-of-binary part) for a
    /// collection.
    /// </summary>
    internal static TsType FilePartType(WellKnownTypes wkt, ITypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile)
            ? new TsType.Primitive("File")
            : new TsType.Array(new TsType.Primitive("File"));

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
