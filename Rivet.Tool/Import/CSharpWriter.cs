using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

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
        if (record.Properties.Any(p => p.CSharpType is "IFormFile" or "IFormFile?"))
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
        sb.AppendLine("[RivetType]");
        var typeParamSuffix = record.TypeParameters is { Count: > 0 }
            ? $"<{string.Join(", ", record.TypeParameters)}>"
            : "";
        sb.Append($"public sealed record {record.Name}{typeParamSuffix}(");

        if (record.Properties.Count == 0)
        {
            sb.AppendLine(");");
            return sb.ToString();
        }

        sb.AppendLine();

        for (var i = 0; i < record.Properties.Count; i++)
        {
            var prop = record.Properties[i];
            var separator = i < record.Properties.Count - 1 ? "," : ");";
            if (!prop.IsRequired)
            {
                sb.AppendLine("    [property: RivetOptional]");
            }
            if (prop.Format is not null)
            {
                sb.AppendLine($"    [property: RivetFormat(\"{prop.Format}\")]");
            }
            if (prop.IsDeprecated)
            {
                sb.AppendLine("    [property: Obsolete]");
            }
            if (prop.Description is not null)
            {
                sb.AppendLine($"    [property: RivetDescription(\"{EscapeString(prop.Description)}\")]");
            }
            if (prop.DefaultValue is not null)
            {
                sb.AppendLine($"    [property: RivetDefault(\"{EscapeString(prop.DefaultValue)}\")]");
            }
            if (prop.Example is not null)
            {
                sb.AppendLine($"    [property: RivetExample(\"{EscapeString(prop.Example)}\")]");
            }
            if (prop.IsReadOnly)
            {
                sb.AppendLine("    [property: RivetReadOnly]");
            }
            if (prop.IsWriteOnly)
            {
                sb.AppendLine("    [property: RivetWriteOnly]");
            }
            if (prop.Constraints is { HasAny: true } c)
            {
                var parts = new List<string>();
                if (c.MinLength.HasValue) parts.Add($"MinLength = {c.MinLength}");
                if (c.MaxLength.HasValue) parts.Add($"MaxLength = {c.MaxLength}");
                if (c.Pattern is not null) parts.Add($"Pattern = \"{EscapeString(c.Pattern)}\"");
                if (c.Minimum.HasValue) parts.Add($"Minimum = {c.Minimum.Value.ToString(CultureInfo.InvariantCulture)}");
                if (c.Maximum.HasValue) parts.Add($"Maximum = {c.Maximum.Value.ToString(CultureInfo.InvariantCulture)}");
                if (c.ExclusiveMinimum.HasValue) parts.Add($"ExclusiveMinimum = {c.ExclusiveMinimum.Value.ToString(CultureInfo.InvariantCulture)}");
                if (c.ExclusiveMaximum.HasValue) parts.Add($"ExclusiveMaximum = {c.ExclusiveMaximum.Value.ToString(CultureInfo.InvariantCulture)}");
                if (c.MultipleOf.HasValue) parts.Add($"MultipleOf = {c.MultipleOf.Value.ToString(CultureInfo.InvariantCulture)}");
                if (c.MinItems.HasValue) parts.Add($"MinItems = {c.MinItems}");
                if (c.MaxItems.HasValue) parts.Add($"MaxItems = {c.MaxItems}");
                if (c.UniqueItems == true) parts.Add("UniqueItems = true");
                sb.AppendLine($"    [property: RivetConstraints({string.Join(", ", parts)})]");
            }
            sb.AppendLine($"    {prop.CSharpType} {prop.Name}{separator}");
        }

        return sb.ToString();
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
        if (contract.Fields.Any(f =>
            f.InputType is "IFormFile" or "IFormFile?"
            || f.OutputType is "IFormFile" or "IFormFile?"))
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

        // Field type: RouteDefinition<TIn, TOut>, RouteDefinition<TOut>, or RouteDefinition
        var fieldType = BuildFieldType(field.InputType, field.OutputType);
        sb.Append($"    public static readonly {fieldType} {field.FieldName} =");
        sb.AppendLine();

        // Factory call
        var typeArgs = BuildTypeArgs(field.InputType, field.OutputType);
        sb.Append($"        Define.{field.HttpMethod}{typeArgs}(\"{EscapeString(field.Route)}\")");

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

    private static string BuildFieldType(string? inputType, string? outputType)
    {
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

        // Emit .Status() when the code differs from the HTTP method default
        var defaultStatus = field.HttpMethod switch { "Post" => 201, "Delete" => 204, _ => 200 };
        if (field.SuccessStatus is not null && field.SuccessStatus != defaultStatus)
        {
            calls.Add($".Status({field.SuccessStatus})");
        }

        // Input-only endpoint: type arg goes on .Accepts<T>()
        if (field.InputType is not null && field.OutputType is null)
        {
            calls.Add($".Accepts<{field.InputType}>()");
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

        if (field.IsFormEncoded)
        {
            calls.Add(".FormEncoded()");
        }

        if (field.FileContentType is not null)
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

    private static string EscapeString(string value)
    {
        var literal = SymbolDisplay.FormatLiteral(value, quote: true);
        // Strip the surrounding quotes — callers already provide their own delimiters
        return literal[1..^1];
    }
}
