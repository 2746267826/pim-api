using System.Text.Json;
using Pim.Module.Calendar.DTOs;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// api#465：UpdateTaskRequest presence converter 的单元级行为。
/// HTTP 真实路径回归在 TaskPartialUpdateHttpTests；这里只锁定序列化边界本身。
/// </summary>
public sealed class UpdateTaskRequestConverterTests
{
    private static UpdateTaskRequest Read(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<UpdateTaskRequest>(json, options)
           ?? throw new InvalidOperationException("converter returned null");

    [Fact]
    public void WebOptions_CamelCase_IsCaseInsensitive()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Web options 对字段名大小写不敏感：camel 与 Pascal 属性名都必须识别为 provided。
        var request = Read("""{"due":"2026-11-05T08:00:00Z","percentComplete":0,"dtStart":null,"TaskBookId":"11111111-1111-1111-1111-111111111111"}""", options);

        // 只认序列化输出里的真实字段，不依赖 internal ProvidedFields 的表示。
        var json = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "dtStart", "due", "percentComplete", "taskBookId" }, propertyNames);
        Assert.Equal(0, request.PercentComplete);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), request.TaskBookId);
    }

    [Fact]
    public void PascalCase_Payload_WithNonWebOptions_IsStillRecognized()
    {
        var request = Read("""{"Due":"2026-11-05T08:00:00Z","Title":"x"}""");

        Assert.Equal("x", request.Title);
    }

    [Fact]
    public void DueOnlyWithExplicitNullDescription_RoundTripsWithoutAddingFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = Read("""{"due":"2026-11-05T08:00:00Z","description":null}""", options);

        var json = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "description", "due" }, propertyNames);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("description").ValueKind);
    }

    [Fact]
    public void DefaultNonWebOptions_PascalCaseRoundTrip()
    {
        // 默认 options 的 PropertyNamingPolicy 为 null → 字段按 PascalCase（属性原名）输出。
        var options = new JsonSerializerOptions();
        var request = Read("""{"Due":"2026-11-05T08:00:00Z","Description":null}""", options);

        var json = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), document.RootElement.GetProperty("Due").GetDateTimeOffset());
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Description", "Due" }, propertyNames);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Description").ValueKind);
    }

    [Fact]
    public void UnknownFields_AreIgnored_AndDoNotPolluteProvided()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = Read("""{"due":"2026-11-05T08:00:00Z","hackerField":1,"__proto":"x"}""", options);

        var json = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "due" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData("""{"priority":2.5}""")]
    [InlineData("""{"dtStart":123}""")]
    [InlineData("""{"calendarId":"not-a-guid"}""")]
    [InlineData("""{"due":{}}""")]
    [InlineData("""{"title":[]}""")]
    public void MalformedFieldType_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => Read(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void MissingFields_AreNotProvided_AndStayNull()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = Read("""{"title":"only title"}""", options);

        var json = JsonSerializer.Serialize(request, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "title" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Null(request.Due);
        Assert.Null(request.Description);
        Assert.Null(request.Priority);
    }
}
