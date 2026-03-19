namespace TaskBoard.Domain;

/// <summary>Email address value object.</summary>
public sealed record Email(string Value)
{
    public override string ToString() => Value;
}
