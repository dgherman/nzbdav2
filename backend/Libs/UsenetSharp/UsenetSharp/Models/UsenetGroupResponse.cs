namespace UsenetSharp.Models;

public record UsenetGroupResponse : UsenetResponse
{
    public long Count { get; init; }
    public long First { get; init; }
    public long Last { get; init; }
    public string Group { get; init; } = string.Empty;
}
