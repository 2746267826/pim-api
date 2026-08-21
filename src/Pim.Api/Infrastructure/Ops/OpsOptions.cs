namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsOptions
{
    public const string SectionName = "Ops";
    public string? OpsKey { get; set; }
    public string? RoConnectionString { get; set; }
}
