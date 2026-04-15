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
        IReadOnlyDictionary<string, TsType> Enums,
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
        IReadOnlyDictionary<string, TsType> enums,
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
            if (def.Type is not null)
            {
                TsType.CollectTypeRefs(def.Type, refs);
            }
            else
            {
                foreach (var prop in def.Properties)
                {
                    TsType.CollectTypeRefs(prop.Type, refs);
                }
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

        // Merge namespace groups that map to the same camelCase file name
        // e.g. "CaseBridge.Common" and "CaseBridge.Shared.Common" both → "common"
        var mergedGroups = MergeGroupsByFileName(typeToGroup);

        // Build file name mapping (no collisions after merge)
        var groupToFileName = mergedGroups.Values.Distinct()
            .ToDictionary(g => g, g => Naming.ToCamelCase(g));

        // Partition types into groups
        var groupDefs = new Dictionary<string, List<TsTypeDefinition>>();
        var groupBrands = new Dictionary<string, List<TsType.Brand>>();
        var groupEnums = new Dictionary<string, List<KeyValuePair<string, TsType>>>();

        foreach (var group in groupToFileName.Keys)
        {
            groupDefs[group] = new();
            groupBrands[group] = new();
            groupEnums[group] = new();
        }

        foreach (var def in definitions)
        {
            groupDefs[mergedGroups[typeToGroup[def.Name]]].Add(def);
        }

        foreach (var brand in brands)
        {
            groupBrands[mergedGroups[typeToGroup[brand.Name]]].Add(brand);
        }

        foreach (var (name, union) in enums)
        {
            groupEnums[mergedGroups[typeToGroup[name]]].Add(new(name, union));
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
                    if (!typeToGroup.TryGetValue(refName, out var rawRefGroup))
                    {
                        continue;
                    }

                    var refMergedGroup = mergedGroups[rawRefGroup];
                    if (refMergedGroup == group)
                    {
                        continue;
                    }

                    var refFile = groupToFileName[refMergedGroup];
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

    /// <summary>
    /// Merges namespace groups that map to the same camelCase file name.
    /// Returns a mapping from original group name to the canonical (first) group name.
    /// e.g. if "CaseBridge.Common" and "CaseBridge.Shared.Common" both → "common",
    /// all types from both end up in one file.
    /// </summary>
    private static Dictionary<string, string> MergeGroupsByFileName(Dictionary<string, string> typeToGroup)
    {
        // Map each file name to the first group that claims it (canonical group)
        var fileNameToCanonical = new Dictionary<string, string>();
        foreach (var group in typeToGroup.Values.Distinct().OrderBy(x => x))
        {
            var fileName = Naming.ToCamelCase(group);
            fileNameToCanonical.TryAdd(fileName, group);
        }

        // Map every group to its canonical group
        var groupToCanonical = new Dictionary<string, string>();
        foreach (var group in typeToGroup.Values.Distinct())
        {
            var fileName = Naming.ToCamelCase(group);
            groupToCanonical[group] = fileNameToCanonical[fileName];
        }

        return groupToCanonical;
    }

}
