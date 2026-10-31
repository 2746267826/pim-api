namespace Pim.Module.Mobile;

/// <summary>
/// Endpoint metadata marker for the usage/events upload: when the pipeline reports a
/// processing phase the endpoint answers 202 with a Retry-After header, which must be
/// described in the OpenAPI contract.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MobileUsageProcessingResponseAttribute : Attribute;
