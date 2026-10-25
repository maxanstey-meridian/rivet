using System.Text.Json.Nodes;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Accounts for every authored OpenAPI field the typed contract model has no slot for, so
/// the import never drops one silently: documentation fields (path-item summary and
/// description, operation externalDocs, links, media-type encoding, info.summary,
/// jsonSchemaDialect) are carried as preserved fields and re-emitted verbatim; callbacks,
/// webhooks and path-item components, which describe requests outside this API's
/// contracts, are reported. Runs before reference normalisation, so a <c>$ref</c> is still a
/// reference: referenced responses and request bodies are handled once, at their component,
/// component responses and parameters (kept as raw provenance) are skipped, and a
/// referenced path item, which the import inlines, is read through its reference.
/// </summary>
internal static class UnrepresentedFieldReporter
{
    public static void Report(
        JsonObject root,
        List<string> warnings,
        List<OpenApiPreservedFieldProvenance> preserved
    )
    {
        if (root["info"] is JsonObject info)
        {
            Preserve(preserved, info, "#/info", "summary");
        }
        Preserve(preserved, root, "#", "jsonSchemaDialect");

        ReportPathItemsOutsidePaths(
            root["webhooks"],
            "#/webhooks",
            "Webhook",
            "webhooks are requests the API sends, not ones it receives. "
                + "Document the outgoing call in the description, or give the receiving service its own contract.",
            warnings
        );

        if (root["paths"] is JsonObject paths)
        {
            foreach (var (path, value) in paths)
            {
                if (value is JsonObject pathItem)
                {
                    ReportPathItem(
                        EffectivePathItem(root, pathItem, []),
                        $"#/paths/{JsonPointer.Escape(path)}",
                        warnings,
                        preserved
                    );
                }
            }
        }

        if (root["components"] is not JsonObject components)
        {
            return;
        }
        ReportPathItemsOutsidePaths(
            components["pathItems"],
            "#/components/pathItems",
            "Path item component",
            "only path items under 'paths' become contract endpoints, and a path that references it is imported inline. "
                + "Reference it from 'paths' if its operations belong to this API.",
            warnings
        );
        foreach (var name in Keys(components["callbacks"]))
        {
            warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportCallbackDropped,
                    $"Callback dropped: #/components/callbacks/{JsonPointer.Escape(name)}; {CallbackReason}"
                )
            );
        }
        Preserve(preserved, components, "#/components", "links");
        if (components["requestBodies"] is JsonObject requestBodies)
        {
            foreach (var (name, requestBody) in requestBodies)
            {
                PreserveMediaEncodings(
                    requestBody,
                    $"#/components/requestBodies/{JsonPointer.Escape(name)}",
                    preserved
                );
            }
        }
    }

    private const string CallbackReason =
        "Rivet contracts describe the requests an API receives, not the ones it sends. "
        + "Document the outgoing call in the operation description, or give the receiving service its own contract.";

    /// <summary>
    /// The path item the import inlines for a <c>$ref</c>'d one: the referenced item (itself
    /// resolved) with the referencing item's own fields laid over it. A reference that does
    /// not resolve, or cycles, is left for the import to refuse.
    /// </summary>
    private static JsonObject EffectivePathItem(
        JsonObject root,
        JsonObject pathItem,
        HashSet<string> referenceChain
    )
    {
        if (
            pathItem["$ref"] is not JsonValue referenceValue
            || !referenceValue.TryGetValue<string>(out var reference)
            || !referenceChain.Add(reference)
            || !JsonPointer.TryResolve(root, reference, out var target)
            || target is not JsonObject referenced
        )
        {
            return pathItem;
        }

        var effective = (JsonObject)EffectivePathItem(root, referenced, referenceChain).DeepClone();
        foreach (var (name, value) in pathItem.Where(entry => entry.Key != "$ref"))
        {
            effective[name] = value?.DeepClone();
        }
        return effective;
    }

    private static void ReportPathItem(
        JsonObject pathItem,
        string pointer,
        List<string> warnings,
        List<OpenApiPreservedFieldProvenance> preserved
    )
    {
        Preserve(preserved, pathItem, pointer, "summary");
        Preserve(preserved, pathItem, pointer, "description");

        // TRACE is reported (and dropped whole) as RIV3003, so its fields are not repeated.
        foreach (var method in OpenApiImporter.OperationMethods.Where(m => m != "trace"))
        {
            if (pathItem[method] is not JsonObject operation)
            {
                continue;
            }
            var operationPointer = $"{pointer}/{method}";
            Preserve(preserved, operation, operationPointer, "externalDocs");
            foreach (var name in Keys(operation["callbacks"]))
            {
                warnings.Add(
                    Diagnostics.Prefix(
                        Diagnostics.ImportCallbackDropped,
                        $"Callback dropped: {operationPointer}/callbacks/{JsonPointer.Escape(name)}; {CallbackReason}"
                    )
                );
            }
            PreserveMediaEncodings(
                operation["requestBody"],
                $"{operationPointer}/requestBody",
                preserved
            );
            if (operation["responses"] is not JsonObject responses)
            {
                continue;
            }
            foreach (var (status, value) in responses)
            {
                if (value is not JsonObject response || response.ContainsKey("$ref"))
                {
                    continue;
                }
                var responsePointer = $"{operationPointer}/responses/{JsonPointer.Escape(status)}";
                Preserve(preserved, response, responsePointer, "links");
                PreserveMediaEncodings(response, responsePointer, preserved);
            }
        }
    }

    private static void PreserveMediaEncodings(
        JsonNode? owner,
        string pointer,
        List<OpenApiPreservedFieldProvenance> preserved
    )
    {
        if (owner is not JsonObject ownerObject || ownerObject.ContainsKey("$ref"))
        {
            return;
        }
        if (ownerObject["content"] is not JsonObject content)
        {
            return;
        }
        foreach (var (mediaType, media) in content)
        {
            if (media is JsonObject mediaObject)
            {
                Preserve(
                    preserved,
                    mediaObject,
                    $"{pointer}/content/{JsonPointer.Escape(mediaType)}",
                    "encoding"
                );
            }
        }
    }

    private static void ReportPathItemsOutsidePaths(
        JsonNode? items,
        string pointer,
        string label,
        string reason,
        List<string> warnings
    )
    {
        foreach (var name in Keys(items))
        {
            warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportPathItemOutsidePathsDropped,
                    $"{label} dropped: {pointer}/{JsonPointer.Escape(name)}; {reason}"
                )
            );
        }
    }

    /// <summary>
    /// Carries a documentation field the contract model has no slot for as a preserved
    /// field, re-attached verbatim at the same owner on emit.
    /// </summary>
    private static void Preserve(
        List<OpenApiPreservedFieldProvenance> preserved,
        JsonObject owner,
        string ownerPointer,
        string name
    )
    {
        if (owner[name] is { } value)
        {
            preserved.Add(
                new OpenApiPreservedFieldProvenance(ownerPointer, name, value.ToJsonString())
            );
        }
    }

    private static IEnumerable<string> Keys(JsonNode? node) =>
        node is JsonObject map ? map.Select(entry => entry.Key) : [];
}
