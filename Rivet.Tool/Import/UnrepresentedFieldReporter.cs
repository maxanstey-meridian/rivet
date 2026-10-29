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
                        root,
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
        JsonObject root,
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
                // A response component keeps its own fields as raw provenance; any other
                // reference is inlined by the import, so its fields belong to this pointer.
                if (
                    value is not JsonObject reference
                    || OpenApiImporter.InlinedResponse(reference, root) is not { } response
                )
                {
                    continue;
                }
                var responsePointer = $"{operationPointer}/responses/{JsonPointer.Escape(status)}";
                Preserve(preserved, response, responsePointer, "links");
                PreserveMediaEncodings(response, responsePointer, preserved);
                PreserveIgnoredResponseHeaders(
                    response,
                    responsePointer,
                    preserved,
                    OpenApiImporter.IsSwagger2(root)
                );
            }
        }
    }

    /// <summary>
    /// A response header named <c>Content-Type</c> is one OpenAPI says to ignore, so the
    /// contract leaves it out and it comes back verbatim: under the emitted headers when the
    /// response keeps others, or as the response's whole <c>headers</c> when it keeps none.
    /// A Swagger 2 header comes back in OpenAPI 3 shape, since that's what emit writes.
    /// </summary>
    private static void PreserveIgnoredResponseHeaders(
        JsonObject response,
        string responsePointer,
        List<OpenApiPreservedFieldProvenance> preserved,
        bool swagger2
    )
    {
        if (response["headers"] is not JsonObject headers)
        {
            return;
        }
        // The contract's own rule decides which headers emit, so the ignored one lands under
        // an emitted headers object or brings the whole object back when none emits.
        var kept = new List<string>();
        var ignored = new List<string>();
        foreach (var (name, _) in headers)
        {
            switch (ContractBuilder.ResponseHeaderNameDrop(name, kept))
            {
                case null:
                    kept.Add(name);
                    break;
                case ContractBuilder.IgnoredResponseHeader:
                    ignored.Add(name);
                    break;
            }
        }
        if (ignored.Count == 0)
        {
            return;
        }
        if (kept.Count == 0)
        {
            var whole = new JsonObject();
            foreach (var (name, header) in headers)
            {
                whole[name] = Header(header, swagger2);
            }
            PreserveValue(preserved, responsePointer, "headers", whole);
            return;
        }
        foreach (var name in ignored)
        {
            PreserveValue(
                preserved,
                $"{responsePointer}/headers",
                name,
                Header(headers[name], swagger2)
            );
        }
    }

    /// <summary>
    /// A response header as OpenAPI 3 writes it. A Swagger 2 header carries its schema inline
    /// (<c>type</c>, <c>format</c>, <c>items</c>, <c>enum</c> and the rest); OpenAPI 3 moves
    /// those under <c>schema</c> and keeps <c>description</c> and its extensions beside it.
    /// <c>collectionFormat</c> has no slot on a header there: its <c>simple</c> style is the
    /// only one a header takes. A <c>$ref</c> stays a reference.
    /// </summary>
    private static JsonNode? Header(JsonNode? header, bool swagger2)
    {
        if (
            !swagger2
            || header is not JsonObject swaggerHeader
            || swaggerHeader.ContainsKey("$ref")
        )
        {
            return header?.DeepClone();
        }
        var converted = new JsonObject();
        var schema = new JsonObject();
        foreach (var (key, value) in swaggerHeader)
        {
            if (key == "description" || key.StartsWith("x-", StringComparison.Ordinal))
            {
                converted[key] = value?.DeepClone();
            }
            else if (key != "collectionFormat")
            {
                schema[key] = value?.DeepClone();
            }
        }
        converted["schema"] = schema;
        return converted;
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

    private static void PreserveValue(
        List<OpenApiPreservedFieldProvenance> preserved,
        string ownerPointer,
        string name,
        JsonNode? value
    )
    {
        if (value is not null)
        {
            preserved.Add(
                new OpenApiPreservedFieldProvenance(ownerPointer, name, value.ToJsonString())
            );
        }
    }

    private static IEnumerable<string> Keys(JsonNode? node) =>
        node is JsonObject map ? map.Select(entry => entry.Key) : [];
}
