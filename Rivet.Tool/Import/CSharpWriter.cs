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
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Rivet;");
        sb.AppendLine("using Endpoint = Rivet.Endpoint;");
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
        sb.Append($"    public static readonly Endpoint {field.FieldName} =");
        sb.AppendLine();

        // Factory call
        var method = field.HttpMethod;
        var typeArgs = BuildTypeArgs(field.InputType, field.OutputType);
        sb.Append($"        Endpoint.{method}{typeArgs}(\"{field.Route}\")");

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

        return "";
    }

    private static List<string> BuildChainCalls(GeneratedEndpointField field)
    {
        var calls = new List<string>();

        if (field.Description is not null)
        {
            calls.Add($".Description(\"{EscapeString(field.Description)}\")");
        }

        if (field.SuccessStatus is not null)
        {
            calls.Add($".Status({field.SuccessStatus})");
        }

        foreach (var error in field.ErrorResponses)
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

        if (field.IsAnonymous)
        {
            calls.Add(".Anonymous()");
        }
        else if (field.SecurityScheme is not null)
        {
            calls.Add($".Secure(\"{field.SecurityScheme}\")");
        }

        return calls;
    }

    private static string EscapeString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

// --- Contract intermediate types ---

internal sealed record GeneratedContract(
    string ClassName,
    IReadOnlyList<GeneratedEndpointField> Fields);

internal sealed record GeneratedEndpointField(
    string FieldName,
    string HttpMethod,
    string Route,
    string? InputType,
    string? OutputType,
    string? Description,
    int? SuccessStatus,
    IReadOnlyList<GeneratedErrorResponse> ErrorResponses,
    bool IsAnonymous,
    string? SecurityScheme);

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string TypeName,
    string? Description);
