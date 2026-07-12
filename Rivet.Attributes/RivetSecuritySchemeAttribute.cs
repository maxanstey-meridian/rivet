namespace Rivet;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetSecuritySchemeAttribute(string name, string definitionJson) : Attribute
{
    public string Name { get; } = name;

    public string DefinitionJson { get; } = definitionJson;
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RivetGlobalSecurityAttribute(string requirementsJson) : Attribute
{
    public string RequirementsJson { get; } = requirementsJson;
}
