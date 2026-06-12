using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Emits syntactically correct C# source from intermediate representations.
/// </summary>
internal static class CSharpWriter
{
    public static string WriteRecord(GeneratedRecord record, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        if (record.Properties.Any(p => p.Constraints is { } cc && HasStandardConstraints(cc))
            || record.Properties.Any(p => p.Format is "email" or "uri"))
        {
            sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        }
        if (record.Polymorphism is not null
            || record.Properties.Any(p => p.WireName is not null))
        {
            sb.AppendLine("using System.Text.Json.Serialization;");
        }
        // I6: substring match — IFormFile can appear nested (List<IFormFile>,
        // Dictionary<string, IFormFile>), not only as the bare property type.
        if (record.Properties.Any(p => p.CSharpType.Contains("IFormFile", StringComparison.Ordinal)))
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
            sb.AppendLine($"[JsonPolymorphic(TypeDiscriminatorPropertyName = \"{EscapeString(poly.DiscriminatorPropertyName)}\")]");
            foreach (var variant in poly.Variants)
            {
                sb.AppendLine($"[JsonDerivedType(typeof({variant.TypeName}), \"{EscapeString(variant.Tag)}\")]");
            }
        }
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
        if (CarriesValidationAttribute(record))
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
                var initializer = !prop.IsRequired && !prop.CSharpType.EndsWith('?')
                    ? " = default!;"
                    : "";
                sb.AppendLine($"    public {requiredKeyword}{prop.CSharpType} {prop.Name} {{ get; init; }}{initializer}");
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
    private static bool CarriesValidationAttribute(GeneratedRecord record)
        => record.Properties.Any(p =>
            p.Format is "email" or "uri" || p.Constraints is { HasAny: true });

    private static void EmitPropertyAttributes(StringBuilder sb, RecordProperty prop, string target)
    {
        if (prop.WireName is not null)
        {
            sb.AppendLine($"    [{target}JsonPropertyName(\"{EscapeString(prop.WireName)}\")]");
        }
        if (prop.HeaderName is not null)
        {
            sb.AppendLine($"    [{target}RivetHeader(\"{EscapeString(prop.HeaderName)}\")]");
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
            sb.AppendLine($"    [{target}RivetFormat(\"{prop.Format}\")]");
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
            sb.AppendLine($"[JsonConverter(typeof(JsonNumberEnumConverter<{enumDef.Name}>))]");
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
        sb.AppendLine($"public sealed record {brand.Name}({brand.InnerType} Value)");
        sb.AppendLine("{");
        sb.AppendLine(brand.InnerType == "string"
            ? "    public override string ToString() => Value;"
            : "    public override string ToString() => Value.ToString();");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string WriteContract(GeneratedContract contract, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        // I6: substring match — IFormFile can appear nested (List<IFormFile>), not only bare.
        if (contract.Fields.Any(f =>
            f.InputType?.Contains("IFormFile", StringComparison.Ordinal) == true
            || f.OutputType?.Contains("IFormFile", StringComparison.Ordinal) == true))
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
            sb.Append($"        Define.{field.HttpMethod}{typeArgs}(\"{EscapeString(field.Route)}\")");
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

    private static string BuildFieldType(string? inputType, string? outputType, bool isFileEndpoint = false)
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
            && field.ResponseExamples.Any(example => example.StatusCode == field.SuccessStatus.Value);

        if (field.SuccessStatus is not null
            && (field.SuccessStatus != defaultStatus || needsExplicitSuccessStatusForExamples))
        {
            calls.Add($".Status({field.SuccessStatus})");
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
            if (error.TypeName is not null)
            {
                if (error.Description is not null)
                {
                    calls.Add($".Returns<{error.TypeName}>({error.StatusCode}, \"{EscapeString(error.Description)}\")");
                }
                else
                {
                    calls.Add($".Returns<{error.TypeName}>({error.StatusCode})");
                }
            }
            else
            {
                if (error.Description is not null)
                {
                    calls.Add($".Returns({error.StatusCode}, \"{EscapeString(error.Description)}\")");
                }
                else
                {
                    calls.Add($".Returns({error.StatusCode})");
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

        return calls;
    }

    private static string BuildResponseHeaderCall(GeneratedResponseHeader header)
    {
        var call = $".WithResponseHeader({header.StatusCode}, \"{EscapeString(header.Name)}\"";

        if (header.Description is not null)
        {
            call += $", \"{EscapeString(header.Description)}\"";
        }

        if (header.Required)
        {
            call += ", required: true";
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

    private static string? BuildResponseExampleCall(GeneratedEndpointResponseExample responseExample)
    {
        var example = responseExample.Example;

        if (example.Json is not null)
        {
            return $".ResponseExampleJson({responseExample.StatusCode}, \"{EscapeString(example.Json)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return $".ResponseExampleRef({responseExample.StatusCode}, \"{EscapeString(example.ComponentExampleId)}\", \"{EscapeString(example.ResolvedJson)}\", mediaType: \"{EscapeString(example.MediaType)}\"{BuildOptionalExampleArguments(example)})";
        }

        return null;
    }

    private static string BuildOptionalExampleArguments(Rivet.Tool.Model.TsEndpointExample example)
    {
        return example.Name is not null
            ? $", name: \"{EscapeString(example.Name)}\""
            : "";
    }

    private static bool HasStandardConstraints(TsPropertyConstraints c)
        => c.MinLength.HasValue || c.MaxLength.HasValue || c.Pattern is not null
           || c.Minimum.HasValue || c.Maximum.HasValue;

    private static void EmitConstraintAttributes(StringBuilder sb, TsPropertyConstraints c, string target)
    {
        // StringLength when both min and max length are present
        if (c.MinLength.HasValue && c.MaxLength.HasValue)
        {
            sb.AppendLine($"    [{target}StringLength({c.MaxLength}, MinimumLength = {c.MinLength})]");
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
            sb.AppendLine($"    [{target}RangeAttribute({c.Minimum.Value.ToString(CultureInfo.InvariantCulture)}, {c.Maximum.Value.ToString(CultureInfo.InvariantCulture)})]");
        }
        else if (c.Minimum.HasValue)
        {
            sb.AppendLine($"    [{target}RangeAttribute({c.Minimum.Value.ToString(CultureInfo.InvariantCulture)}, double.MaxValue)]");
        }
        else if (c.Maximum.HasValue)
        {
            sb.AppendLine($"    [{target}RangeAttribute(double.MinValue, {c.Maximum.Value.ToString(CultureInfo.InvariantCulture)})]");
        }

        // Pattern
        if (c.Pattern is not null)
        {
            sb.AppendLine($"    [{target}RegularExpression(\"{EscapeString(c.Pattern)}\")]");
        }

        // Exotic constraints → RivetConstraints
        var exoticParts = new List<string>();
        if (c.ExclusiveMinimum.HasValue)
            exoticParts.Add($"ExclusiveMinimum = {c.ExclusiveMinimum.Value.ToString(CultureInfo.InvariantCulture)}");
        if (c.ExclusiveMaximum.HasValue)
            exoticParts.Add($"ExclusiveMaximum = {c.ExclusiveMaximum.Value.ToString(CultureInfo.InvariantCulture)}");
        if (c.MultipleOf.HasValue)
            exoticParts.Add($"MultipleOf = {c.MultipleOf.Value.ToString(CultureInfo.InvariantCulture)}");
        if (c.MinItems.HasValue)
            exoticParts.Add($"MinItems = {c.MinItems}");
        if (c.MaxItems.HasValue)
            exoticParts.Add($"MaxItems = {c.MaxItems}");
        if (c.UniqueItems == true)
            exoticParts.Add("UniqueItems = true");

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
}
