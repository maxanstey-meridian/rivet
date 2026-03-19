using System.Text;

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
        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[RivetType]");
        sb.Append($"public sealed record {record.Name}(");

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
            sb.AppendLine($"    {prop.CSharpType} {prop.Name}{separator}");
        }

        return sb.ToString();
    }

    public static string WriteEnum(GeneratedEnum enumDef, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public enum {enumDef.Name}");
        sb.AppendLine("{");

        for (var i = 0; i < enumDef.Members.Count; i++)
        {
            var separator = i < enumDef.Members.Count - 1 ? "," : "";
            sb.AppendLine($"    {enumDef.Members[i]}{separator}");
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
        sb.AppendLine("    public override string ToString() => Value;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string WriteContract(GeneratedContract contract, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Mvc;");
        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[RivetContract]");
        sb.AppendLine($"[Route(\"{contract.RoutePrefix}\")]");
        sb.AppendLine($"public abstract class {contract.ClassName} : ControllerBase");
        sb.AppendLine("{");

        for (var i = 0; i < contract.Fields.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            WriteAbstractMethod(sb, contract.Fields[i]);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void WriteAbstractMethod(StringBuilder sb, GeneratedEndpointField field)
    {
        // HTTP method attribute with optional route suffix
        var httpAttr = field.MethodRoute is not null
            ? $"[Http{field.HttpMethod}(\"{field.MethodRoute}\")]"
            : $"[Http{field.HttpMethod}]";
        sb.AppendLine($"    {httpAttr}");

        // ProducesResponseType for success
        if (field.OutputType is not null)
        {
            var statusCode = field.SuccessStatus ?? 200;
            sb.AppendLine($"    [ProducesResponseType(typeof({field.OutputType}), {statusCode})]");
        }
        else if (field.SuccessStatus is not null)
        {
            sb.AppendLine($"    [ProducesResponseType({field.SuccessStatus})]");
        }

        // ProducesResponseType for errors
        foreach (var error in field.ErrorResponses)
        {
            if (error.TypeName is not null)
            {
                sb.AppendLine($"    [ProducesResponseType(typeof({error.TypeName}), {error.StatusCode})]");
            }
            else
            {
                sb.AppendLine($"    [ProducesResponseType({error.StatusCode})]");
            }
        }

        // Method signature
        var methodParams = BuildMethodParams(field);
        sb.AppendLine($"    public abstract Task<IActionResult> {field.FieldName}({methodParams});");
    }

    private static string BuildMethodParams(GeneratedEndpointField field)
    {
        var parts = new List<string>();

        // Route parameters
        foreach (var param in field.MethodParams)
        {
            if (param.IsBody)
            {
                parts.Add($"[FromBody] {param.CSharpType} {param.Name}");
            }
            else
            {
                parts.Add($"{param.CSharpType} {param.Name}");
            }
        }

        parts.Add("CancellationToken ct");

        return string.Join(", ", parts);
    }

    internal static string EscapeString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

// --- Contract intermediate types ---

internal sealed record GeneratedContract(
    string ClassName,
    string RoutePrefix,
    IReadOnlyList<GeneratedEndpointField> Fields);

internal sealed record GeneratedEndpointField(
    string FieldName,
    string HttpMethod,
    string Route,
    string? MethodRoute,
    string? InputType,
    string? OutputType,
    string? Description,
    int? SuccessStatus,
    IReadOnlyList<GeneratedErrorResponse> ErrorResponses,
    IReadOnlyList<GeneratedMethodParam> MethodParams);

internal sealed record GeneratedMethodParam(
    string Name,
    string CSharpType,
    bool IsBody);

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string? TypeName,
    string? Description);
