using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Emits syntactically correct C# source from intermediate representations.
/// </summary>
internal static class CSharpWriter
{
    public static string WriteScalarSchemas(IReadOnlyList<GeneratedScalarSchema> schemas)
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var sb = new StringBuilder("using Rivet;\n\n");
        foreach (var schema in schemas)
        {
            var schemaType = schema.SchemaType is null
                ? "null"
                : $"\"{EscapeString(schema.SchemaType)}\"";
            var format = schema.Format is null ? "null" : $"\"{EscapeString(schema.Format)}\"";
            var metadata = JsonSerializer.Serialize(schema.Metadata, options);
            var schemaRef = schema.SchemaRef is null
                ? schema.IsArray
                    ? ", null"
                    : ""
                : $", \"{EscapeString(schema.SchemaRef)}\"";
            var arrayArguments = schema.IsArray
                ? $", true, \"{EscapeString(schema.ItemSchemaRef!)}\""
                : "";
            sb.AppendLine(
                $"[assembly: RivetGeneratedSchema(\"{EscapeString(schema.Name)}\", \"{EscapeString(schema.ComponentId)}\", {schemaType}, {format}, {schema.IsNullable.ToString().ToLowerInvariant()}, \"{EscapeString(metadata)}\", {schema.IsEnum.ToString().ToLowerInvariant()}{schemaRef}{arrayArguments})]"
            );
        }

        return sb.ToString();
    }

    public static string WriteSecurityMetadata(ContractSecurityMetadata security)
    {
        var sb = new StringBuilder("using Rivet;\n\n");
        foreach (
            var (name, definition) in security.Schemes.OrderBy(
                pair => pair.Key,
                StringComparer.Ordinal
            )
        )
        {
            WriteSecurityScheme(sb, name, definition);
        }

        if (security.GlobalRequirements is { } globalRequirements)
        {
            if (globalRequirements.Alternatives.Count == 0)
            {
                sb.AppendLine("[assembly: RivetEmptyGlobalSecurity]");
            }
            for (var order = 0; order < globalRequirements.Alternatives.Count; order++)
            {
                sb.AppendLine($"[assembly: RivetGlobalSecurity({order})]");
                foreach (var scheme in globalRequirements.Alternatives[order].Schemes)
                {
                    sb.AppendLine(
                        $"[assembly: RivetGlobalSecurityScheme({order}, {StringLiteral(scheme.Name)}, {StringArrayLiteral(scheme.Scopes)})]"
                    );
                }
            }
        }

        return sb.ToString();
    }

    private static void WriteSecurityScheme(
        StringBuilder sb,
        string name,
        SecuritySchemeDefinition definition
    )
    {
        string[] arguments = definition switch
        {
            ApiKeySecurityScheme apiKey =>
            [
                StringLiteral(name),
                "\"apiKey\"",
                NullableStringLiteral(apiKey.Description),
                StringLiteral(apiKey.Name),
                StringLiteral(apiKey.Location.ToString().ToLowerInvariant()),
                "null",
                "null",
                "null",
            ],
            HttpSecurityScheme http =>
            [
                StringLiteral(name),
                "\"http\"",
                NullableStringLiteral(http.Description),
                "null",
                "null",
                StringLiteral(http.Scheme),
                NullableStringLiteral(http.BearerFormat),
                "null",
            ],
            OAuth2SecurityScheme oauth =>
            [
                StringLiteral(name),
                "\"oauth2\"",
                NullableStringLiteral(oauth.Description),
            ],
            OpenIdConnectSecurityScheme openId =>
            [
                StringLiteral(name),
                "\"openIdConnect\"",
                NullableStringLiteral(openId.Description),
                "null",
                "null",
                "null",
                "null",
                StringLiteral(openId.OpenIdConnectUrl),
            ],
            MutualTlsSecurityScheme mutualTls =>
            [
                StringLiteral(name),
                "\"mutualTLS\"",
                NullableStringLiteral(mutualTls.Description),
            ],
            _ => throw new InvalidOperationException(
                $"Unsupported security scheme model '{definition.GetType().Name}'."
            ),
        };
        sb.AppendLine($"[assembly: RivetSecurityScheme({string.Join(", ", arguments)})]");

        if (definition is not OAuth2SecurityScheme oauth2)
        {
            return;
        }

        foreach (var flow in oauth2.Flows)
        {
            sb.AppendLine(
                $"[assembly: RivetOAuthFlow({StringLiteral(name)}, {StringLiteral(OAuthFlowName(flow.Type))}, {NullableStringLiteral(flow.AuthorizationUrl)}, {NullableStringLiteral(flow.TokenUrl)}, {NullableStringLiteral(flow.RefreshUrl)}, {StringArrayLiteral(flow.Scopes.Keys.ToList())}, {StringArrayLiteral(flow.Scopes.Values.ToList())})]"
            );
        }
    }

    private static string OAuthFlowName(OAuth2FlowType type) =>
        type switch
        {
            OAuth2FlowType.Implicit => "implicit",
            OAuth2FlowType.Password => "password",
            OAuth2FlowType.ClientCredentials => "clientCredentials",
            OAuth2FlowType.AuthorizationCode => "authorizationCode",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    public static string WriteDocumentProvenance(
        OpenApiDocumentProvenance document,
        string generatedNamespace
    )
    {
        var requestBodies = document.ComponentRequestBodies ?? [];
        var sb = new StringBuilder(
            "using System.Collections.Generic;\nusing Microsoft.AspNetCore.Http;\nusing Rivet;\n"
        );
        if (
            requestBodies.Any(requestBody =>
                requestBody.Contents.Any(content => content.CSharpTypeName is not null)
            )
        )
        {
            sb.AppendLine($"using {generatedNamespace};");
        }
        sb.AppendLine();
        var info = document.Info;
        sb.AppendLine(
            $"[assembly: RivetDocumentInfo({StringLiteral(info.Title)}, {StringLiteral(info.Version)}, {NullableStringLiteral(info.Description)}, {NullableStringLiteral(info.TermsOfService)}, {NullableStringLiteral(info.Contact?.Name)}, {NullableStringLiteral(info.Contact?.Url)}, {NullableStringLiteral(info.Contact?.Email)}, {(info.Contact is not null).ToString().ToLowerInvariant()}, {NullableStringLiteral(info.License?.Name)}, {NullableStringLiteral(info.License?.Url)}, {NullableStringLiteral(info.License?.Identifier)})]"
        );
        for (var tagIndex = 0; tagIndex < document.Tags.Count; tagIndex++)
        {
            var tag = document.Tags[tagIndex];
            sb.AppendLine(
                $"[assembly: RivetDocumentTag({tagIndex}, {StringLiteral(tag.Name)}, {NullableStringLiteral(tag.Description)}, {NullableStringLiteral(tag.ExternalDocs?.Url)}, {NullableStringLiteral(tag.ExternalDocs?.Description)})]"
            );
        }
        if (document.ExternalDocs is { } externalDocs)
        {
            sb.AppendLine(
                $"[assembly: RivetDocumentExternalDocs({StringLiteral(externalDocs.Url)}, {NullableStringLiteral(externalDocs.Description)})]"
            );
        }
        WriteDocumentServers(sb, document.Servers);
        for (var exampleIndex = 0; exampleIndex < document.ComponentExamples.Count; exampleIndex++)
        {
            var example = document.ComponentExamples[exampleIndex];
            sb.AppendLine(
                $"[assembly: RivetDocumentExample({exampleIndex}, {StringLiteral(example.Name)}, {NullableStringLiteral(example.Summary)}, {NullableStringLiteral(example.Description)}, {NullableStringLiteral(example.JsonValue)}, {NullableStringLiteral(example.ExternalValue)})]"
            );
        }
        for (var requestBodyIndex = 0; requestBodyIndex < requestBodies.Count; requestBodyIndex++)
        {
            var requestBody = requestBodies[requestBodyIndex];
            sb.AppendLine(
                $"[assembly: RivetDocumentRequestBody({requestBodyIndex}, {StringLiteral(requestBody.Name)}, {NullableStringLiteral(requestBody.Description)}, {requestBody.Required.ToString().ToLowerInvariant()})]"
            );
            for (var contentIndex = 0; contentIndex < requestBody.Contents.Count; contentIndex++)
            {
                var content = requestBody.Contents[contentIndex];
                var schemaType = content.CSharpTypeName is null
                    ? "null"
                    : $"typeof({content.CSharpTypeName})";
                sb.AppendLine(
                    $"[assembly: RivetDocumentRequestBodyContent({requestBodyIndex}, {contentIndex}, {StringLiteral(content.MediaType)}, {schemaType}, {content.IsBinary.ToString().ToLowerInvariant()}, {NullableStringLiteral(content.SchemaRef)}, {NullableStringLiteral(content.SchemaType)}, {NullableStringLiteral(content.Format)}, {content.IsFormatSpecified.ToString().ToLowerInvariant()}, {NullableStringLiteral(content.SchemaJson)})]"
                );
            }
            var requestBodyExamples = requestBody.Examples ?? [];
            for (var exampleIndex = 0; exampleIndex < requestBodyExamples.Count; exampleIndex++)
            {
                var example = requestBodyExamples[exampleIndex];
                var referencedComponentsJson = example.ReferencedComponents is null
                    ? null
                    : JsonSerializer.Serialize(example.ReferencedComponents);
                sb.AppendLine(
                    $"[assembly: RivetDocumentRequestBodyExample({requestBodyIndex}, {exampleIndex}, {StringLiteral(example.MediaType)}, {NullableStringLiteral(example.Name)}, {NullableStringLiteral(example.Json)}, {NullableStringLiteral(example.ComponentExampleId)}, {NullableStringLiteral(example.ResolvedJson)}, {NullableStringLiteral(referencedComponentsJson)})]"
                );
            }
        }
        for (var index = 0; index < (document.ComponentParameters?.Count ?? 0); index++)
        {
            var parameter = document.ComponentParameters![index];
            sb.AppendLine(
                $"[assembly: RivetDocumentParameter({index}, {StringLiteral(parameter.Name)}, {StringLiteral(parameter.Json)})]"
            );
        }
        for (var index = 0; index < (document.ComponentResponses?.Count ?? 0); index++)
        {
            var response = document.ComponentResponses![index];
            sb.AppendLine(
                $"[assembly: RivetDocumentResponse({index}, {StringLiteral(response.Name)}, {StringLiteral(response.Json)})]"
            );
        }
        for (var index = 0; index < (document.ComponentSchemas?.Count ?? 0); index++)
        {
            var schema = document.ComponentSchemas![index];
            sb.AppendLine(
                $"[assembly: RivetDocumentSchema({index}, {StringLiteral(schema.Name)}, {StringLiteral(schema.Json)})]"
            );
        }
        foreach (var sourceFile in document.ImportedSourceFiles ?? [])
        {
            sb.AppendLine(
                $"[assembly: RivetImportedSourceFile({StringLiteral(sourceFile.Path)}, {StringLiteral(sourceFile.Fingerprint)})]"
            );
        }
        foreach (var extension in document.VendorExtensions ?? [])
        {
            sb.AppendLine(
                $"[assembly: RivetVendorExtension({StringLiteral(extension.OwnerPointer)}, {StringLiteral(extension.Name)}, {StringLiteral(extension.JsonValue)})]"
            );
        }
        return sb.ToString();
    }

    private static void WriteDocumentServers(
        StringBuilder sb,
        IReadOnlyList<OpenApiServerProvenance> servers
    )
    {
        for (var serverIndex = 0; serverIndex < servers.Count; serverIndex++)
        {
            var server = servers[serverIndex];
            sb.AppendLine(
                $"[assembly: RivetDocumentServer({serverIndex}, {StringLiteral(server.Url)}, {NullableStringLiteral(server.Description)})]"
            );
            for (var variableIndex = 0; variableIndex < server.Variables.Count; variableIndex++)
            {
                var variable = server.Variables[variableIndex];
                sb.AppendLine(
                    $"[assembly: RivetDocumentServerVariable({serverIndex}, {variableIndex}, {StringLiteral(variable.Name)}, {StringLiteral(variable.DefaultValue)}, {StringArrayLiteral(variable.AllowedValues)}, {NullableStringLiteral(variable.Description)})]"
                );
            }
        }
    }

    public static string WriteRecord(GeneratedRecord record, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        if (
            record.Properties.Any(p => p.Constraints is { } cc && HasStandardConstraints(cc))
            || record.Properties.Any(p => p.Format is "email" or "uri")
        )
        {
            sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        }
        if (record.Polymorphism is not null || record.Properties.Any(p => p.WireName is not null))
        {
            sb.AppendLine("using System.Text.Json.Serialization;");
        }
        // I6: substring match — IFormFile can appear nested (List<IFormFile>,
        // Dictionary<string, IFormFile>), not only as the bare property type.
        if (
            record.Properties.Any(p => p.CSharpType.Contains("IFormFile", StringComparison.Ordinal))
        )
        {
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
        }
        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        if (record.Description is not null)
        {
            sb.AppendLine($"[RivetDescription(\"{EscapeString(record.Description)}\")]");
        }
        if (record.Polymorphism is { } poly)
        {
            sb.AppendLine(
                $"[JsonPolymorphic(TypeDiscriminatorPropertyName = \"{EscapeString(poly.DiscriminatorPropertyName)}\")]"
            );
            foreach (var variant in poly.Variants)
            {
                sb.AppendLine(
                    $"[JsonDerivedType(typeof({variant.TypeName}), \"{EscapeString(variant.Tag)}\")]"
                );
            }
        }
        if (record.IsUnion)
        {
            // Wire value is the bare variant: the walker re-emits oneOf, the
            // attribute's converter unwraps/rewraps at runtime.
            sb.AppendLine("[RivetUnion]");
        }
        foreach (var metadata in record.SchemaMetadata ?? [])
        {
            EmitGeneratedSchemaMetadata(sb, metadata, "", "");
        }
        sb.AppendLine(GeneratedTypeAttribute(record.ComponentId, record.IsSynthetic));
        // Derived polymorphic records are not [RivetType] entry points — the walker
        // reaches them through the base's [JsonDerivedType] registrations; attributing
        // them would emit a second, untagged component alongside the union variant.
        if (record.BaseTypeName is null)
        {
            sb.AppendLine("[RivetType]");
        }
        var typeParamSuffix = record.TypeParameters is { Count: > 0 }
            ? $"<{string.Join(", ", record.TypeParameters)}>"
            : "";
        var modifier = record.Polymorphism is not null ? "abstract" : "sealed";

        // The positional-record gotcha (docs/guides/runtime-validation.md): under an
        // [ApiController] host, MVC model validation throws InvalidOperationException at
        // request time when a positional record parameter's *property* carries a
        // ValidationAttribute — which is exactly where [property:]-targeted attributes
        // land. Records carrying any ValidationAttribute are therefore emitted in the
        // non-positional required/init form, where the single property-level placement
        // is visible to MVC, Validator.TryValidateObject, and the Rivet walker alike.
        if (
            CarriesValidationAttribute(record)
            || HasRequiredNullableProperty(record)
            || HasOptionalNonNullableProperty(record)
        )
        {
            var baseSuffix = record.BaseTypeName is null ? "" : $" : {record.BaseTypeName}";
            sb.AppendLine($"public {modifier} record {record.Name}{typeParamSuffix}{baseSuffix}");
            sb.AppendLine("{");
            for (var i = 0; i < record.Properties.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                }
                var prop = record.Properties[i];
                EmitPropertyAttributes(sb, prop, target: "");
                var requiredKeyword = prop.IsRequired ? "required " : "";
                // Optional non-nullable properties need a suppressed default — there is
                // no constructor parameter to satisfy the nullable analysis anymore.
                var initializer =
                    !prop.IsRequired && !prop.CSharpType.EndsWith('?') ? " = default!;" : "";
                sb.AppendLine(
                    $"    public {requiredKeyword}{prop.CSharpType} {prop.Name} {{ get; init; }}{initializer}"
                );
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        var closeSuffix = record.BaseTypeName is null ? ");" : $") : {record.BaseTypeName};";
        sb.Append($"public {modifier} record {record.Name}{typeParamSuffix}(");

        if (record.Properties.Count == 0)
        {
            sb.AppendLine(closeSuffix);
            return sb.ToString();
        }

        sb.AppendLine();

        for (var i = 0; i < record.Properties.Count; i++)
        {
            var prop = record.Properties[i];
            var separator = i < record.Properties.Count - 1 ? "," : closeSuffix;
            EmitPropertyAttributes(sb, prop, target: "property: ");
            sb.AppendLine($"    {prop.CSharpType} {prop.Name}{separator}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when any property would carry a ValidationAttribute: the DataAnnotations
    /// constraint set (StringLength/MinLength/MaxLength/Range/RegularExpression),
    /// EmailAddress/Url from formats, or [RivetConstraints] (a ValidationAttribute
    /// since the inbound-enforcement work).
    /// </summary>
    private static bool CarriesValidationAttribute(GeneratedRecord record) =>
        record.Properties.Any(p =>
            p.Format is "email" or "uri" || p.Constraints is { HasAny: true }
        );

    /// <summary>
    /// Required-and-nullable is only expressible with the `required` keyword
    /// (FABLE_ROUNDTRIP #6/#11b): a positional `T? X` parameter reads as
    /// optional to the walker, so records with such properties take the
    /// non-positional required/init form where the keyword carries the axis.
    /// </summary>
    private static bool HasRequiredNullableProperty(GeneratedRecord record) =>
        record.Properties.Any(p => p.IsRequired && p.CSharpType.EndsWith('?'));

    private static bool HasOptionalNonNullableProperty(GeneratedRecord record) =>
        record.Properties.Any(p => !p.IsRequired && !p.CSharpType.EndsWith('?'));

    private static void EmitPropertyAttributes(StringBuilder sb, RecordProperty prop, string target)
    {
        foreach (var metadata in prop.SchemaMetadata ?? [])
        {
            EmitGeneratedSchemaMetadata(sb, metadata, "    ", target);
        }
        if (prop.WireName is not null)
        {
            sb.AppendLine($"    [{target}JsonPropertyName(\"{EscapeString(prop.WireName)}\")]");
        }
        if (prop.HeaderName is not null)
        {
            sb.AppendLine($"    [{target}RivetHeader(\"{EscapeString(prop.HeaderName)}\")]");
        }
        if (prop.SchemaRef is not null)
        {
            sb.AppendLine($"    [{target}RivetSchemaRef(\"{EscapeString(prop.SchemaRef)}\")]");
        }
        if (!prop.IsRequired)
        {
            sb.AppendLine($"    [{target}RivetOptional]");
        }
        if (prop.Format is "email")
        {
            sb.AppendLine($"    [{target}EmailAddress]");
        }
        else if (prop.Format is "uri")
        {
            sb.AppendLine($"    [{target}Url]");
        }
        else if (prop.Format is not null)
        {
            sb.AppendLine($"    [{target}RivetFormat(\"{EscapeString(prop.Format)}\")]");
        }
        else if (prop.IsFormatSpecified)
        {
            sb.AppendLine($"    [{target}RivetFormat]");
        }
        if (prop.SchemaType is not null)
        {
            sb.AppendLine($"    [{target}RivetSchemaType(\"{EscapeString(prop.SchemaType)}\")]");
        }
        if (prop.IsDeprecated)
        {
            sb.AppendLine($"    [{target}Obsolete]");
        }
        if (prop.Description is not null)
        {
            sb.AppendLine($"    [{target}RivetDescription(\"{EscapeString(prop.Description)}\")]");
        }
        if (prop.DefaultValue is not null)
        {
            sb.AppendLine($"    [{target}RivetDefault(\"{EscapeString(prop.DefaultValue)}\")]");
        }
        if (prop.Example is not null)
        {
            sb.AppendLine($"    [{target}RivetExample(\"{EscapeString(prop.Example)}\")]");
        }
        if (prop.IsReadOnly)
        {
            sb.AppendLine($"    [{target}RivetReadOnly]");
        }
        if (prop.IsWriteOnly)
        {
            sb.AppendLine($"    [{target}RivetWriteOnly]");
        }
        if (prop.Constraints is { HasAny: true } c)
        {
            EmitConstraintAttributes(sb, c, target);
        }
    }

    private static void EmitGeneratedSchemaMetadata(
        StringBuilder sb,
        GeneratedSchemaMetadata generated,
        string indent,
        string target
    )
    {
        var metadata = generated.Metadata;
        var constraints = metadata.Constraints;
        var xml = metadata.Xml;
        sb.AppendLine(
            $"{indent}[{target}RivetGeneratedSchemaMetadata({StringLiteral(generated.Pointer)}, {NullableStringLiteral(metadata.Title)}, {NullableStringLiteral(metadata.Description)}, {NullableStringLiteral(metadata.DefaultValue)}, {NullableStringLiteral(metadata.Example)}, {NullableStringLiteral(metadata.Examples)}, {NullableIntLiteral(constraints?.MinLength)}, {NullableIntLiteral(constraints?.MaxLength)}, {NullableStringLiteral(constraints?.Pattern)}, {NullableDoubleLiteral(constraints?.Minimum)}, {NullableDoubleLiteral(constraints?.Maximum)}, {NullableDoubleLiteral(constraints?.ExclusiveMinimum)}, {NullableDoubleLiteral(constraints?.ExclusiveMaximum)}, {NullableDoubleLiteral(constraints?.MultipleOf)}, {NullableIntLiteral(constraints?.MinItems)}, {NullableIntLiteral(constraints?.MaxItems)}, {(constraints?.UniqueItems == true).ToString().ToLowerInvariant()}, {NullableStringLiteral(xml?.Name)}, {NullableStringLiteral(xml?.Namespace)}, {NullableStringLiteral(xml?.Prefix)}, {(xml?.IsAttribute == true).ToString().ToLowerInvariant()}, {(xml?.IsWrapped == true).ToString().ToLowerInvariant()}, {NullableStringLiteral(metadata.Format)}, {metadata.IsFormatSpecified.ToString().ToLowerInvariant()}, {metadata.IsNullable.ToString().ToLowerInvariant()}, {metadata.IsDeprecated.ToString().ToLowerInvariant()}, {metadata.IsReadOnly.ToString().ToLowerInvariant()}, {metadata.IsWriteOnly.ToString().ToLowerInvariant()}, {NullableStringLiteral(metadata.Required is null ? null : JsonSerializer.Serialize(metadata.Required))})]"
        );
    }

    private static string NullableIntLiteral(int? value) => value?.ToString() ?? "-1";

    private static string NullableDoubleLiteral(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "double.NaN";

    public static string WriteEnum(GeneratedEnum enumDef, string ns)
    {
        var isIntBacked = enumDef.Members.Any(m => m.IntValue.HasValue);
        var needsJsonImport = isIntBacked || enumDef.Members.Any(m => m.OriginalName is not null);

        var sb = new StringBuilder();
        if (needsJsonImport)
        {
            sb.AppendLine("using System.Text.Json.Serialization;");
            sb.AppendLine();
        }
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        if (isIntBacked)
        {
            sb.AppendLine($"[JsonConverter(typeof(JsonNumberEnumConverter<{enumDef.Name}>))]");
        }
        if (enumDef.Description is not null)
        {
            sb.AppendLine($"[Rivet.RivetDescription(\"{EscapeString(enumDef.Description)}\")]");
        }
        if (enumDef.Format is not null)
        {
            sb.AppendLine($"[Rivet.RivetFormat(\"{EscapeString(enumDef.Format)}\")]");
        }

        sb.AppendLine("[Rivet.RivetType]");
        sb.AppendLine(GeneratedTypeAttribute(enumDef.ComponentId, enumDef.IsSynthetic, "Rivet."));

        sb.AppendLine($"public enum {enumDef.Name}");
        sb.AppendLine("{");

        for (var i = 0; i < enumDef.Members.Count; i++)
        {
            var member = enumDef.Members[i];
            var separator = i < enumDef.Members.Count - 1 ? "," : "";
            if (!isIntBacked && member.OriginalName is not null)
            {
                sb.AppendLine($"    [JsonStringEnumMemberName(\"{member.OriginalName}\")]");
            }
            var valueAssignment = member.IntValue.HasValue ? $" = {member.IntValue.Value}" : "";
            sb.AppendLine($"    {member.CSharpName}{valueAssignment}{separator}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string WriteBrand(GeneratedBrand brand, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        if (brand.Description is not null)
        {
            sb.AppendLine($"[Rivet.RivetDescription(\"{EscapeString(brand.Description)}\")]");
        }
        if (brand.Format is not null)
        {
            sb.AppendLine($"[Rivet.RivetFormat(\"{EscapeString(brand.Format)}\")]");
        }
        sb.AppendLine("[Rivet.RivetType]");
        sb.AppendLine(
            GeneratedTypeAttribute(
                brand.ComponentId,
                brand.IsSynthetic,
                "Rivet.",
                valueObject: true
            )
        );
        sb.AppendLine($"public sealed record {brand.Name}({brand.InnerType} Value)");
        sb.AppendLine("{");
        sb.AppendLine(
            brand.InnerType == "string"
                ? "    public override string ToString() => Value;"
                : "    public override string ToString() => Value.ToString();"
        );
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GeneratedTypeAttribute(
        string? componentId,
        bool synthetic,
        string prefix = "",
        bool valueObject = false
    )
    {
        var id = componentId is null ? "null" : $"\"{EscapeString(componentId)}\"";
        var provenance = synthetic ? "Synthetic" : "Component";
        var valueObjectArgument = valueObject ? ", true" : "";
        return $"[{prefix}RivetGeneratedType({id}, {prefix}RivetGeneratedTypeProvenance.{provenance}{valueObjectArgument})]";
    }

    public static string WriteContract(GeneratedContract contract, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        // I6: substring match — IFormFile can appear nested (List<IFormFile>), not only bare.
        if (
            contract.Fields.Any(f =>
                f.InputType?.Contains("IFormFile", StringComparison.Ordinal) == true
                || f.OutputType?.Contains("IFormFile", StringComparison.Ordinal) == true
                || f.ErrorResponses.Any(response =>
                    response.TypeName?.Contains("IFormFile", StringComparison.Ordinal) == true
                )
                || f.RequestContents.Any(content =>
                    content.TypeName?.Contains("IFormFile", StringComparison.Ordinal) == true
                )
                || f.ResponseContents.Any(content =>
                    content.TypeName?.Contains("IFormFile", StringComparison.Ordinal) == true
                )
            )
        )
        {
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
        }

        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[RivetContract]");
        sb.AppendLine($"public static class {contract.ClassName}");
        sb.AppendLine("{");

        for (var i = 0; i < contract.Fields.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            WriteEndpointField(sb, contract.Fields[i]);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void WriteEndpointField(StringBuilder sb, GeneratedEndpointField field)
    {
        // Unsupported markers as structured comments
        foreach (var marker in field.UnsupportedMarkers)
        {
            sb.AppendLine($"    // [rivet:unsupported {marker}]");
        }

        if (field.Provenance is { } provenance)
        {
            var schemasJson = provenance.Schemas is null
                ? null
                : JsonSerializer.Serialize(provenance.Schemas);
            sb.AppendLine(
                $"    [RivetOperationProvenance({provenance.OperationIdPresent.ToString().ToLowerInvariant()}, {NullableStringLiteral(provenance.OperationId)}, {provenance.Deprecated.ToString().ToLowerInvariant()}, {StringArrayLiteral(provenance.Tags)}, {NullableStringLiteral(provenance.RequestBodyDescription)}, {(provenance.ServerOverride is not null).ToString().ToLowerInvariant()}, {NullableStringLiteral(provenance.RivetIdentity?.Contract)}, {NullableStringLiteral(provenance.RivetIdentity?.Endpoint)}, {NullableStringLiteral(provenance.RequestBodyComponentId)}, {NullableStringLiteral(schemasJson)})]"
            );
            if (provenance.ServerOverride is { } servers)
            {
                for (var serverIndex = 0; serverIndex < servers.Count; serverIndex++)
                {
                    var server = servers[serverIndex];
                    sb.AppendLine(
                        $"    [RivetOperationServer({serverIndex}, {StringLiteral(server.Url)}, {NullableStringLiteral(server.Description)})]"
                    );
                    for (
                        var variableIndex = 0;
                        variableIndex < server.Variables.Count;
                        variableIndex++
                    )
                    {
                        var variable = server.Variables[variableIndex];
                        sb.AppendLine(
                            $"    [RivetOperationServerVariable({serverIndex}, {variableIndex}, {StringLiteral(variable.Name)}, {StringLiteral(variable.DefaultValue)}, {StringArrayLiteral(variable.AllowedValues)}, {NullableStringLiteral(variable.Description)})]"
                        );
                    }
                }
            }
            for (
                var index = 0;
                index < (provenance.ParameterComponentReferences?.Count ?? 0);
                index++
            )
            {
                var reference = provenance.ParameterComponentReferences![index];
                sb.AppendLine(
                    $"    [RivetOperationParameterComponent({index}, {StringLiteral(reference.Name)}, {StringLiteral(reference.Location)}, {StringLiteral(reference.ComponentId)})]"
                );
            }
            for (
                var index = 0;
                index < (provenance.ResponseComponentReferences?.Count ?? 0);
                index++
            )
            {
                var reference = provenance.ResponseComponentReferences![index];
                sb.AppendLine(
                    $"    [RivetOperationResponseComponent({index}, {StringLiteral(reference.StatusKey)}, {StringLiteral(reference.ComponentId)})]"
                );
            }
        }

        if (field.RequestBodyType is not null)
        {
            var requiredArgument = field.RequestBodyRequired is false ? ", false" : "";
            sb.AppendLine(
                $"    [RivetRequestBody(typeof({field.RequestBodyType}){requiredArgument})]"
            );
        }

        // Field type: RouteDefinition<TIn, TOut>, RouteDefinition<TOut>, FileRouteDefinition, etc.
        var fieldType = BuildFieldType(field.InputType, field.OutputType, field.IsFileEndpoint);
        sb.Append($"    public static readonly {fieldType} {field.FieldName} =");
        sb.AppendLine();

        // Factory call
        if (field.IsFileEndpoint)
        {
            var fileTypeArgs = field.InputType is not null ? $"<{field.InputType}>" : "";
            sb.Append($"        Define.File{fileTypeArgs}(\"{EscapeString(field.Route)}\")");
        }
        else
        {
            var typeArgs = BuildTypeArgs(field.InputType, field.OutputType);
            sb.Append(
                $"        Define.{field.HttpMethod}{typeArgs}(\"{EscapeString(field.Route)}\")"
            );
        }

        // Builder chain
        var chainCalls = BuildChainCalls(field);

        if (chainCalls.Count == 0)
        {
            sb.AppendLine(";");
            return;
        }

        sb.AppendLine();

        for (var i = 0; i < chainCalls.Count; i++)
        {
            var terminator = i == chainCalls.Count - 1 ? ";" : "";
            sb.AppendLine($"            {chainCalls[i]}{terminator}");
        }
    }

    private static string BuildFieldType(
        string? inputType,
        string? outputType,
        bool isFileEndpoint = false
    )
    {
        if (isFileEndpoint)
        {
            return inputType is not null
                ? $"FileRouteDefinition<{inputType}>"
                : "FileRouteDefinition";
        }

        if (inputType is not null && outputType is not null)
        {
            return $"RouteDefinition<{inputType}, {outputType}>";
        }

        if (inputType is not null)
        {
            return $"InputRouteDefinition<{inputType}>";
        }

        if (outputType is not null)
        {
            return $"RouteDefinition<{outputType}>";
        }

        return "RouteDefinition";
    }

    private static string BuildTypeArgs(string? inputType, string? outputType)
    {
        if (inputType is not null && outputType is not null)
        {
            return $"<{inputType}, {outputType}>";
        }

        if (outputType is not null)
        {
            return $"<{outputType}>";
        }

        // InputRouteDefinition: type arg goes on .Accepts<T>(), not on Define.Method()
        return "";
    }

    private static List<string> BuildChainCalls(GeneratedEndpointField field)
    {
        var calls = new List<string>();

        if (field.Summary is not null)
        {
            calls.Add($".Summary(\"{EscapeString(field.Summary)}\")");
        }

        if (field.Description is not null)
        {
            calls.Add($".Description(\"{EscapeString(field.Description)}\")");
        }

        // Emit .Status() when the code differs from the HTTP method default.
        // Must agree with Rivet.Define runtime defaults: POST → 201;
        // DELETE without output → 204; DELETE with output → 200; otherwise 200.
        var defaultStatus = field.HttpMethod switch
        {
            "Post" => 201,
            "Delete" when field.OutputType is null => 204,
            _ => 200,
        };
        var needsExplicitSuccessStatusForExamples =
            field.SuccessStatus is not null
            && field.OutputType is null
            && field.FileContentType is null
            && field.ResponseExamples.Any(example =>
                example.StatusCode == field.SuccessStatus.Value
            );

        if (
            field.SuccessStatus is not null
            && (field.SuccessStatus != defaultStatus || needsExplicitSuccessStatusForExamples)
        )
        {
            calls.Add($".Status({field.SuccessStatus})");
        }

        if (field.SuccessStatusKey is not null)
        {
            var description = field.SuccessResponseDescription is null
                ? ""
                : $", \"{EscapeString(field.SuccessResponseDescription)}\"";
            calls.Add($".StatusKey(\"{EscapeString(field.SuccessStatusKey)}\"{description})");
        }

        if (field.SuppressImplicitResponse)
        {
            calls.Add(".SuppressImplicitResponse()");
        }

        // Input-only endpoint: type arg goes on .Accepts<T>()
        // (File endpoints have the input type on Define.File<T>() already)
        if (field.InputType is not null && field.OutputType is null && !field.IsFileEndpoint)
        {
            calls.Add($".Accepts<{field.InputType}>()");
        }

        foreach (var requestExample in field.RequestExamples)
        {
            var requestCall = BuildRequestExampleCall(requestExample);
            if (requestCall is not null)
            {
                calls.Add(requestCall);
            }
        }

        foreach (var error in field.ErrorResponses)
        {
            var statusArgument =
                error.StatusCode == 0
                    ? $"\"{EscapeString(error.StatusKey)}\""
                    : error.StatusCode.ToString();
            if (error.TypeName is not null)
            {
                if (error.Description is not null)
                {
                    calls.Add(
                        $".Returns<{error.TypeName}>({statusArgument}, \"{EscapeString(error.Description)}\")"
                    );
                }
                else
                {
                    calls.Add($".Returns<{error.TypeName}>({statusArgument})");
                }
            }
            else
            {
                if (error.Description is not null)
                {
                    calls.Add($".Returns({statusArgument}, \"{EscapeString(error.Description)}\")");
                }
                else
                {
                    calls.Add($".Returns({statusArgument})");
                }
            }
        }

        foreach (var responseHeader in field.ResponseHeaders)
        {
            calls.Add(BuildResponseHeaderCall(responseHeader));
        }

        foreach (var responseExample in field.ResponseExamples)
        {
            var responseCall = BuildResponseExampleCall(responseExample);
            if (responseCall is not null)
            {
                calls.Add(responseCall);
            }
        }

        if (field.IsFormEncoded)
        {
            calls.Add(".FormEncoded()");
        }

        if (field.BinaryRequestContentType is not null)
        {
            if (field.BinaryRequestContentType == "application/octet-stream")
            {
                calls.Add(".AcceptsBinary()");
            }
            else
            {
                calls.Add($".AcceptsBinary(\"{EscapeString(field.BinaryRequestContentType)}\")");
            }
        }

        // FABLE_ROUNDTRIP #10: text/* media types survive as content-type
        // overrides — the schema is unchanged, only the declared media type.
        if (field.RequestContentType is not null)
        {
            calls.Add($".AcceptsContentType(\"{EscapeString(field.RequestContentType)}\")");
        }

        if (field.ResponseContentType is not null)
        {
            calls.Add($".ProducesContentType(\"{EscapeString(field.ResponseContentType)}\")");
        }

        if (field.FileContentType is not null)
        {
            if (field.IsFileEndpoint)
            {
                // File endpoints use .ContentType() instead of .ProducesFile()
                if (field.FileContentType != "application/octet-stream")
                {
                    calls.Add($".ContentType(\"{EscapeString(field.FileContentType)}\")");
                }
            }
            else
            {
                if (field.FileContentType == "application/octet-stream")
                {
                    calls.Add(".ProducesFile()");
                }
                else
                {
                    calls.Add($".ProducesFile(\"{EscapeString(field.FileContentType)}\")");
                }
            }
        }

        if (field.QueryAuthParameterName is not null)
        {
            if (field.QueryAuthParameterName == "token")
            {
                calls.Add(".QueryAuth()");
            }
            else
            {
                calls.Add($".QueryAuth(\"{EscapeString(field.QueryAuthParameterName)}\")");
            }
        }

        if (field.IsAnonymous)
        {
            calls.Add(".Anonymous()");
        }
        else if (field.SecurityScheme is not null)
        {
            calls.Add($".Secure(\"{EscapeString(field.SecurityScheme)}\")");
        }

        if (field.SecurityRequirements is { } securityRequirements)
        {
            if (securityRequirements.Alternatives.Count == 0)
            {
                calls.Add(".SecurityRequirements()");
            }
            for (var order = 0; order < securityRequirements.Alternatives.Count; order++)
            {
                calls.Add($".SecurityRequirement({order})");
                foreach (var scheme in securityRequirements.Alternatives[order].Schemes)
                {
                    if (scheme.Scopes.Count == 0)
                    {
                        calls.Add($".SecurityRequirement({order}, {StringLiteral(scheme.Name)})");
                    }
                    else
                    {
                        foreach (var scope in scheme.Scopes)
                        {
                            calls.Add(
                                $".SecurityRequirement({order}, {StringLiteral(scheme.Name)}, {StringLiteral(scope)})"
                            );
                        }
                    }
                }
            }
        }

        foreach (var content in field.RequestContents)
        {
            var schemaRef = content.SchemaRef is null
                ? ""
                : $", schemaRef: \"{EscapeString(content.SchemaRef)}\"";
            var leaf = content.SchemaType is null
                ? ""
                : $", schemaType: \"{EscapeString(content.SchemaType)}\", format: \"{EscapeString(content.Format ?? "")}\"";
            calls.Add(
                content.IsBinary ? $".RequestBinaryContent(\"{EscapeString(content.MediaType)}\")"
                : content.TypeName is null
                    ? $".RequestContent(\"{EscapeString(content.MediaType)}\")"
                : $".RequestContent<{content.TypeName}>(\"{EscapeString(content.MediaType)}\"{schemaRef}{leaf})"
            );
        }

        if (field.RequestBodyPresent && field.RequestContents.Count == 0)
        {
            calls.Add(".RequestBody()");
        }

        if (field.RequestBodyRequired is { } requestBodyRequired)
        {
            calls.Add($".RequestBodyRequired({requestBodyRequired.ToString().ToLowerInvariant()})");
        }

        foreach (var parameter in field.Parameters)
        {
            var format = parameter.IsFormatSpecified
                ? $", \"{EscapeString(parameter.Format ?? "")}\""
                : "";
            var schemaType = parameter.SchemaType is null
                ? format.Length == 0
                    ? ""
                    : ", null"
                : $", \"{EscapeString(parameter.SchemaType)}\"";
            var metadata = parameter.MetadataJson is null
                ? ""
                : $", metadataJson: \"{EscapeString(parameter.MetadataJson)}\"";
            var schemaRef = parameter.SchemaRef is null
                ? ""
                : $", schemaRef: \"{EscapeString(parameter.SchemaRef)}\"";
            calls.Add(
                $".Parameter<{parameter.TypeName}>(\"{EscapeString(parameter.Name)}\", \"{parameter.Location}\", {parameter.Required.ToString().ToLowerInvariant()}{schemaType}{format}{metadata}{schemaRef})"
            );
        }

        foreach (var content in field.ResponseContents)
        {
            var statusArgument =
                content.StatusCode == 0
                    ? $"\"{EscapeString(content.StatusKey)}\""
                    : content.StatusCode.ToString();
            var schemaRef = content.SchemaRef is null
                ? ""
                : $", schemaRef: \"{EscapeString(content.SchemaRef)}\"";
            var leaf = content.SchemaType is null
                ? ""
                : $", schemaType: \"{EscapeString(content.SchemaType)}\", format: \"{EscapeString(content.Format ?? "")}\"";
            var schemaDescription = content.SchemaDescription is null
                ? ""
                : $", schemaDescription: \"{EscapeString(content.SchemaDescription)}\"";
            calls.Add(
                content.IsBinary
                    ? $".ResponseBinaryContent({statusArgument}, \"{EscapeString(content.MediaType)}\")"
                : content.TypeName is null
                    ? $".ResponseContent({statusArgument}, \"{EscapeString(content.MediaType)}\")"
                : $".ResponseContent<{content.TypeName}>({statusArgument}, \"{EscapeString(content.MediaType)}\"{schemaRef}{leaf}{schemaDescription})"
            );
        }

        return calls;
    }

    private static string BuildResponseHeaderCall(GeneratedResponseHeader header)
    {
        var statusArgument =
            header.StatusCode == 0
                ? $"\"{EscapeString(header.StatusKey)}\""
                : header.StatusCode.ToString();
        var method = header.StatusCode == 0 ? "WithResponseHeaderKey" : "WithResponseHeader";
        var call =
            $".{method}<{header.TypeName}>({statusArgument}, \"{EscapeString(header.Name)}\"";

        if (header.Description is not null)
        {
            call += $", \"{EscapeString(header.Description)}\"";
        }

        if (header.Required)
        {
            call += ", required: true";
        }

        if (header.SchemaType is not null)
        {
            call += $", schemaType: \"{EscapeString(header.SchemaType)}\"";
        }

        if (header.IsFormatSpecified)
        {
            call += $", format: \"{EscapeString(header.Format ?? "")}\"";
        }

        if (header.SchemaExamplesJson is not null)
        {
            call += $", schemaExamplesJson: \"{EscapeString(header.SchemaExamplesJson)}\"";
        }
        if (header.ExampleJson is not null)
        {
            call += $", exampleJson: \"{EscapeString(header.ExampleJson)}\"";
        }
        if (header.ExamplesJson is not null)
        {
            call += $", examplesJson: \"{EscapeString(header.ExamplesJson)}\"";
        }
        if (header.Deprecated)
        {
            call += ", deprecated: true";
        }
        if (header.Style is not null)
        {
            call += $", style: \"{EscapeString(header.Style)}\"";
        }
        if (header.Explode is { } explode)
        {
            call += $", explode: {explode.ToString().ToLowerInvariant()}";
        }
        if (header.AllowReserved)
        {
            call += ", allowReserved: true";
        }
        if (header.AllowEmptyValue)
        {
            call += ", allowEmptyValue: true";
        }
        if (header.ContentType is not null)
        {
            call += $", contentType: \"{EscapeString(header.ContentType)}\"";
        }

        return call + ")";
    }

    private static string? BuildRequestExampleCall(Rivet.Tool.Model.TsEndpointExample example)
    {
        if (example.Json is not null)
        {
            return $".RequestExampleJson(\"{EscapeString(example.Json)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return $".RequestExampleRef(\"{EscapeString(example.ComponentExampleId)}\", \"{EscapeString(example.ResolvedJson)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        return null;
    }

    private static string? BuildResponseExampleCall(
        GeneratedEndpointResponseExample responseExample
    )
    {
        var example = responseExample.Example;
        var statusArgument =
            responseExample.StatusCode == 0
                ? $"\"{EscapeString(responseExample.StatusKey)}\""
                : responseExample.StatusCode.ToString();

        if (example.Json is not null)
        {
            return $".ResponseExampleJson({statusArgument}, \"{EscapeString(example.Json)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return $".ResponseExampleRef({statusArgument}, \"{EscapeString(example.ComponentExampleId)}\", \"{EscapeString(example.ResolvedJson)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        return null;
    }

    private static string BuildOptionalExampleArguments(Rivet.Tool.Model.TsEndpointExample example)
    {
        var arguments = example.Name is not null ? $", name: \"{EscapeString(example.Name)}\"" : "";
        if (example.ReferencedComponents is not null)
        {
            arguments +=
                $", referencedComponentsJson: \"{EscapeString(JsonSerializer.Serialize(example.ReferencedComponents))}\"";
        }
        return arguments;
    }

    private static bool HasStandardConstraints(TsPropertyConstraints c) =>
        c.MinLength.HasValue
        || c.MaxLength.HasValue
        || c.Pattern is not null
        || c.Minimum.HasValue
        || c.Maximum.HasValue;

    private static void EmitConstraintAttributes(
        StringBuilder sb,
        TsPropertyConstraints c,
        string target
    )
    {
        // StringLength when both min and max length are present
        if (c.MinLength.HasValue && c.MaxLength.HasValue)
        {
            sb.AppendLine(
                $"    [{target}StringLength({c.MaxLength}, MinimumLength = {c.MinLength})]"
            );
        }
        else if (c.MinLength.HasValue)
        {
            sb.AppendLine($"    [{target}MinLength({c.MinLength})]");
        }
        else if (c.MaxLength.HasValue)
        {
            sb.AppendLine($"    [{target}MaxLength({c.MaxLength})]");
        }

        // Range when minimum or maximum are present
        // Use RangeAttribute (not Range) to disambiguate from System.Range
        if (c.Minimum.HasValue && c.Maximum.HasValue)
        {
            sb.AppendLine(
                $"    [{target}RangeAttribute({c.Minimum.Value.ToString(CultureInfo.InvariantCulture)}, {c.Maximum.Value.ToString(CultureInfo.InvariantCulture)})]"
            );
        }
        else if (c.Minimum.HasValue)
        {
            sb.AppendLine(
                $"    [{target}RangeAttribute({c.Minimum.Value.ToString(CultureInfo.InvariantCulture)}, double.MaxValue)]"
            );
        }
        else if (c.Maximum.HasValue)
        {
            sb.AppendLine(
                $"    [{target}RangeAttribute(double.MinValue, {c.Maximum.Value.ToString(CultureInfo.InvariantCulture)})]"
            );
        }

        // Pattern
        if (c.Pattern is not null)
        {
            sb.AppendLine($"    [{target}RegularExpression(\"{EscapeString(c.Pattern)}\")]");
        }

        // Exotic constraints → RivetConstraints
        var exoticParts = new List<string>();
        if (c.ExclusiveMinimum.HasValue)
        {
            exoticParts.Add(
                $"ExclusiveMinimum = {c.ExclusiveMinimum.Value.ToString(CultureInfo.InvariantCulture)}"
            );
        }

        if (c.ExclusiveMaximum.HasValue)
        {
            exoticParts.Add(
                $"ExclusiveMaximum = {c.ExclusiveMaximum.Value.ToString(CultureInfo.InvariantCulture)}"
            );
        }

        if (c.MultipleOf.HasValue)
        {
            exoticParts.Add(
                $"MultipleOf = {c.MultipleOf.Value.ToString(CultureInfo.InvariantCulture)}"
            );
        }

        if (c.MinItems.HasValue)
        {
            exoticParts.Add($"MinItems = {c.MinItems}");
        }

        if (c.MaxItems.HasValue)
        {
            exoticParts.Add($"MaxItems = {c.MaxItems}");
        }

        if (c.UniqueItems == true)
        {
            exoticParts.Add("UniqueItems = true");
        }

        if (exoticParts.Count > 0)
        {
            sb.AppendLine($"    [{target}RivetConstraints({string.Join(", ", exoticParts)})]");
        }
    }

    private static string EscapeString(string value)
    {
        var literal = SymbolDisplay.FormatLiteral(value, quote: true);
        // Strip the surrounding quotes — callers already provide their own delimiters
        return literal[1..^1];
    }

    private static string StringLiteral(string value) => $"\"{EscapeString(value)}\"";

    private static string NullableStringLiteral(string? value) =>
        value is null ? "null" : StringLiteral(value);

    private static string StringArrayLiteral(IReadOnlyList<string> values) =>
        $"new string[] {{ {string.Join(", ", values.Select(StringLiteral))} }}";
}
