namespace Pim.Infrastructure.Operations;

public sealed record DataQualityInspectionResult(
    string CheckName,
    bool IsHealthy,
    int IssueCount,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null);

public interface IDataQualityInspector
{
    string CheckName { get; }
    Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default);
}
