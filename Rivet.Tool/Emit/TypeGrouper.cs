using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Groups type definitions by namespace for split-file emission.
/// Types referenced by multiple groups are promoted to common.
/// </summary>
public static class TypeGrouper
{
    public sealed record TypeFileGroup(
        string FileName,
        IReadOnlyList<TsTypeDefinition> Definitions,
        IReadOnlyList<TsType.Brand> Brands,
        IReadOnlyDictionary<string, TsType.StringUnion> Enums,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Imports);

    public sealed record TypeGroupingResult(
        IReadOnlyList<TypeFileGroup> Groups)
    {
        /// <summary>
        /// Builds a lookup from type name to the file name it lives in.
        /// </summary>
        public Dictionary<string, string> BuildTypeFileMap()
        {
            var map = new Dictionary<string, string>();
            foreach (var group in Groups)
            {
                foreach (var def in group.Definitions)
                {
                    map[def.Name] = group.FileName;
                }

                foreach (var brand in group.Brands)
                {
                    map[brand.Name] = group.FileName;
                }

                foreach (var (name, _) in group.Enums)
                {
                    map[name] = group.FileName;
                }
            }

            return map;
        }
    };

    public static TypeGroupingResult Group(
        IReadOnlyList<TsTypeDefinition> definitions,
        IReadOnlyList<TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType.StringUnion> enums,
        IReadOnlyDictionary<string, string?> typeNamespaces)
    {
        // All type names to their namespace group (null → "common")
        var allTypeNames = new HashSet<string>();
        foreach (var d in definitions) allTypeNames.Add(d.Name);
        foreach (var b in brands) allTypeNames.Add(b.Name);
        foreach (var e in enums) allTypeNames.Add(e.Key);

        // Build initial grouping by namespace
        var typeToGroup = new Dictionary<string, string>();
        foreach (var name in allTypeNames)
        {
            var ns = typeNamespaces.GetValueOrDefault(name);
            typeToGroup[name] = ns ?? "common";
        }

        // Collect all type refs per definition (property-level references)
        var typeRefs = new Dictionary<string, HashSet<string>>();
        foreach (var def in definitions)
        {
            var refs = new HashSet<string>();
            foreach (var prop in def.Properties)
            {
                ClientEmitter.CollectTypeRefs(prop.Type, refs);
            }

            typeRefs[def.Name] = refs;
        }

        // Iteratively promote cross-referenced types to common
        bool changed;
        do
        {
            changed = false;
            foreach (var (typeName, refs) in typeRefs)
            {
                var ownerGroup = typeToGroup[typeName];
                foreach (var refName in refs)
                {
                    if (!typeToGroup.TryGetValue(refName, out var refGroup))
                    {
                        continue; // primitive or type parameter
                    }

                    if (refGroup != "common" && refGroup != ownerGroup)
                    {
                        typeToGroup[refName] = "common";
                        changed = true;
                    }
                }
            }

            // Also check: common types referencing non-common types
            foreach (var def in definitions)
            {
                if (typeToGroup[def.Name] != "common")
                {
                    continue;
                }

                if (!typeRefs.TryGetValue(def.Name, out var refs))
                {
                    continue;
                }

                foreach (var refName in refs)
                {
                    if (typeToGroup.TryGetValue(refName, out var refGroup) && refGroup != "common")
                    {
                        typeToGroup[refName] = "common";
                        changed = true;
                    }
                }
            }
        } while (changed);

        // Build file name mapping with collision handling
        var groupToFileName = BuildFileNames(typeToGroup.Values.Distinct());

        // Partition types into groups
        var groupDefs = new Dictionary<string, List<TsTypeDefinition>>();
        var groupBrands = new Dictionary<string, List<TsType.Brand>>();
        var groupEnums = new Dictionary<string, List<KeyValuePair<string, TsType.StringUnion>>>();

        foreach (var group in groupToFileName.Keys)
        {
            groupDefs[group] = new();
            groupBrands[group] = new();
            groupEnums[group] = new();
        }

        foreach (var def in definitions)
        {
            groupDefs[typeToGroup[def.Name]].Add(def);
        }

        foreach (var brand in brands)
        {
            groupBrands[typeToGroup[brand.Name]].Add(brand);
        }

        foreach (var (name, union) in enums)
        {
            groupEnums[typeToGroup[name]].Add(new(name, union));
        }

        // Build imports for each group
        var groups = new List<TypeFileGroup>();
        foreach (var (group, fileName) in groupToFileName.OrderBy(x => x.Key == "common" ? 0 : 1).ThenBy(x => x.Value))
        {
            var imports = new Dictionary<string, List<string>>();

            foreach (var def in groupDefs[group])
            {
                if (!typeRefs.TryGetValue(def.Name, out var refs))
                {
                    continue;
                }

                foreach (var refName in refs)
                {
                    if (!typeToGroup.TryGetValue(refName, out var refGroup) || refGroup == group)
                    {
                        continue;
                    }

                    var refFile = groupToFileName[refGroup];
                    if (!imports.TryGetValue(refFile, out var list))
                    {
                        list = new();
                        imports[refFile] = list;
                    }

                    if (!list.Contains(refName))
                    {
                        list.Add(refName);
                    }
                }
            }

            var sortedImports = imports
                .ToDictionary(
                    x => x.Key,
                    x => (IReadOnlyList<string>)x.Value.Order().ToList());

            groups.Add(new TypeFileGroup(
                fileName,
                groupDefs[group],
                groupBrands[group],
                groupEnums[group].ToDictionary(x => x.Key, x => x.Value),
                sortedImports));
        }

        return new TypeGroupingResult(groups);
    }

    private static Dictionary<string, string> BuildFileNames(IEnumerable<string> groups)
    {
        var result = new Dictionary<string, string>();
        var usedFileNames = new HashSet<string>();

        foreach (var group in groups.OrderBy(x => x))
        {
            var baseName = ToCamelCase(group);
            var fileName = baseName;
            var suffix = 2;
            while (!usedFileNames.Add(fileName))
            {
                fileName = $"{baseName}{suffix}";
                suffix++;
            }

            result[group] = fileName;
        }

        return result;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
