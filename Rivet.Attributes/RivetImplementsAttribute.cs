using System;

namespace Rivet;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RivetImplementsAttribute : Attribute
{
    public string ContractName { get; }
    public RivetImplementsAttribute(string contractName) => ContractName = contractName;
}
