using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Pim.Module.Mcp.DTOs;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Executes MCP tool calls with behavior identical to the retired Python server
/// (<c>scripts/mcp/pim_mcp_server.py</c>): same validation messages, same verify
/// semantics (in-process <c>McpClientService.VerifyAsync</c>), same REST passthrough
/// (in-process pipeline), same redaction/truncation/local-pagination rules.
/// </summary>
public sealed class McpToolExecutor
{
    private const long TruncationBytes = 50 * 1024;
    private const int MaxTimeSpanDays = 366;
    private const int ParamsSummaryMaxChars = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<McpToolContract[]> Contract = new(LoadContract);

    private readonly McpClientService _verifyService;
    private readonly McpInProcessClient _client;
    private readonly McpStdioTokenSource _stdioTokenSource;
    private readonly HttpContext? _httpContext;

    public McpToolExecutor(
        McpClientService verifyService,
        McpInProcessClient client,
        McpStdioTokenSource stdioTokenSource,
        HttpContext? httpContext = null)
    {
        _verifyService = verifyService;
        _client = client;
        _stdioTokenSource = stdioTokenSource;
        _httpContext = httpContext;
    }

    /// <summary>Loaded 151-tool contract (name/description/inputSchema) from the embedded JSON.</summary>
    public static IReadOnlyList<McpToolContract> ToolContract => Contract.Value;

    public async ValueTask<CallToolResult> ExecuteAsync(
        CallToolRequestParams requestParams,
        CancellationToken ct)
    {
        var toolName = requestParams.Name;
        var spec = McpToolTable.TryGet(toolName);
        if (spec is null)
            throw new McpException($"Unknown tool: {toolName}");

        var args = ToJsonObject(requestParams.Arguments);
        JsonNode? result;
        try
        {
            result = await ExecuteSpecAsync(spec, args, ct);
        }
        catch (McpToolAuthException auth)
        {
            result = new JsonObject { ["error"] = auth.Message, ["code"] = auth.HttpStatus };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["error"] = $"request failed: {ex.Message}", ["code"] = 500 };
        }
        return ToToolResult(result);
    }

    private async Task<JsonNode?> ExecuteSpecAsync(McpToolSpec spec, JsonObject args, CancellationToken ct)
    {
        var validationError = Validate(spec, args);
        if (validationError is not null)
            return validationError;

        var accessToken = await ResolveAccessTokenAsync(spec.Name, args, ct);
        if (accessToken is null && spec.Kind is not (McpToolKind.Health or McpToolKind.Version))
        {
            return _httpContext is null
                ? Error("missing bearer token: call MCP with Authorization: Bearer <PIM JWT>. Obtain token via POST /api/v1/auth/login {\"username\",\"password\"} -> accessToken", 401)
                : Error("missing bearer token: call MCP with Authorization: Bearer <pim_mcp_* token>. Generate a token in WebUI Settings -> MCP 管理.", 401);
        }

        return await DispatchAsync(spec, args, accessToken, ct);
    }

    // ===================== validation (Python _validate_* parity) =====================

    private static JsonObject? Validate(McpToolSpec spec, JsonObject args)
    {
        if (spec.Required.Count > 0)
        {
            foreach (var required in spec.Required)
            {
                if (!HasValue(args, required))
                    return Error($"{required} is required", 400);
            }

            if (spec.Name == "batch_update_tasks")
            {
                var hasUpdate = new[] { "status", "priority", "calendarId" }.Any(p => args.TryGetPropertyValue(p, out var node) && node is not null);
                if (!hasUpdate)
                    return Error("at least one of status/priority/calendarId is required", 400);
            }
        }

        if (args.TryGetPropertyValue("page", out var pageNode) && pageNode is not null)
        {
            var page = GetInt(pageNode);
            if (page is not null && page < 1)
                return Error("page must be >=1", 400);
        }
        if (args.TryGetPropertyValue("pageSize", out var pageSizeNode) && pageSizeNode is not null)
        {
            var pageSize = GetInt(pageSizeNode);
            if (pageSize is not null && pageSize is < 1 or > 100)
                return Error("pageSize must be between 1 and 100", 400);
        }

        if (args.TryGetPropertyValue("limit", out var limitNode) && limitNode is not null)
        {
            var limit = GetInt(limitNode);
            if (limit is not null && limit is < 1 or > 100)
                return Error("limit must be 1..100", 400);
        }

        if (args.TryGetPropertyValue("blockMinutes", out var blockNode) && blockNode is not null)
        {
            var block = GetInt(blockNode);
            if (block is not null && block is not (15 or 30 or 60))
                return Error("blockMinutes must be 15, 30 or 60", 400);
        }

        foreach (var dateParam in new[] { "date", "dateFrom", "dateTo" })
        {
            if (args.TryGetPropertyValue(dateParam, out var dateNode) && dateNode is not null)
            {
                var value = GetString(dateNode);
                if (value is not null && !IsDateOnly(value))
                    return Error(dateParam == "date" ? "date must be YYYY-MM-DD" : $"date {value} must be YYYY-MM-DD", 400);
            }
        }

        var timeError = ValidateTimeParams(args);
        if (timeError is not null)
            return timeError;

        if (spec.Name == "get_ai_usage_summary"
            && args.TryGetPropertyValue("from_time", out var fromNode) && fromNode is not null
            && !args.ContainsKey("to"))
        {
            var from = GetString(fromNode);
            if (from is not null && !TryParseIso8601(from, out _, out _))
                return Error($"invalid iso8601 '{from}'", 400);
        }

        return null;
    }

    private static JsonObject? ValidateTimeParams(JsonObject args)
    {
        var start = GetString(args, "start");
        var end = GetString(args, "end");
        if (start is not null && end is not null)
            return ValidateTimeRange(start, end);

        var fromTime = GetString(args, "from_time");
        var to = GetString(args, "to");
        if (fromTime is not null && to is not null)
            return ValidateTimeRange(fromTime, to);
        return null;
    }

    private static JsonObject? ValidateTimeRange(string start, string end)
    {
        if (!TryParseIso8601(start, out var startDt, out var startError))
            return Error($"invalid time format: {startError}", 400);
        if (!TryParseIso8601(end, out var endDt, out var endError))
            return Error($"invalid time format: {endError}", 400);
        if (startDt > endDt)
            return Error("invalid time range: start must be <= end", 400);
        if ((endDt - startDt).Days > MaxTimeSpanDays)
            return Error("time range too large: max span 366 days", 400);
        return null;
    }

    private static bool TryParseIso8601(string value, out DateTimeOffset result, out string error)
    {
        result = default;
        error = string.Empty;
        try
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$"))
            {
                result = DateTimeOffset.ParseExact(value + "T00:00:00+00:00", "yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            var iso = value.Replace("Z", "+00:00");
            if (!DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out result))
            {
                error = $"invalid iso8601 '{value}'";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"invalid iso8601 '{value}': {ex.Message}";
            return false;
        }
    }

    private static bool IsDateOnly(string value)
        => System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{1,2}-\d{1,2}$")
            && System.DateOnly.TryParseExact(value, "yyyy-M-d", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    private static bool HasValue(JsonObject args, string name)
        => args.TryGetPropertyValue(name, out var node) && node is not null
            && (node.GetValueKind() != JsonValueKind.String || !string.IsNullOrEmpty(node.GetValue<string>()));

    // ===================== auth =====================

    private async Task<string?> ResolveAccessTokenAsync(string toolName, JsonObject args, CancellationToken ct)
    {
        if (_httpContext is null)
            return _stdioTokenSource.GetAccessToken();

        var rawToken = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(rawToken))
            return null;

        var summary = SummarizeParams(args);
        var outcome = await _verifyService.VerifyAsync(rawToken!, toolName, summary, ct);
        if (outcome.HttpStatus != 0)
            throw new McpToolAuthException(outcome.HttpStatus, outcome.Error ?? "unauthorized");
        return outcome.Result?.AccessToken;
    }

    private string? ExtractBearerToken()
    {
        var auth = _httpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth))
            return null;
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static string? SummarizeParams(JsonObject args)
    {
        if (args is null || args.Count == 0)
            return null;
        var text = JsonSerializer.Serialize(args, SerializerOptions);
        return text.Length <= ParamsSummaryMaxChars ? text : text[..ParamsSummaryMaxChars];
    }

    // ===================== dispatch =====================

    private async Task<JsonNode?> DispatchAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        switch (spec.Kind)
        {
            case McpToolKind.Health:
                return await CallOptionalAuthAsync("GET", "/health", accessToken, ct);
            case McpToolKind.Version:
                return await CallOptionalAuthAsync("GET", "/api/version", accessToken, ct);
            case McpToolKind.AttachmentMeta:
                return await AttachmentMetaAsync(spec, args, accessToken, ct);
            case McpToolKind.EventById:
                return await EventByIdAsync(spec, args, accessToken, ct);
            case McpToolKind.TaskById:
                return await TaskByIdAsync(spec, args, accessToken, ct);
            case McpToolKind.HabitOccurrences:
                return await HabitOccurrencesAsync(spec, args, accessToken, ct);
            case McpToolKind.TaskChecklist:
                return await TaskChecklistAsync(spec, args, accessToken, ct);
            case McpToolKind.CalendarById:
                return await CalendarByIdAsync(spec, args, accessToken, ct);
            case McpToolKind.MobileLocationLatest:
                return await MobileLocationLatestAsync(spec, args, accessToken, ct);
            case McpToolKind.FileOpenLink:
                return await CallAsync(spec, args, accessToken, redact: true, ct);
            case McpToolKind.SearchEvents:
            case McpToolKind.SearchTasks:
                return await SearchAsync(spec, args, accessToken, ct);
            case McpToolKind.SchedulePreview:
                return await CallAsync(spec, args, accessToken, redact: false, ct);
            case McpToolKind.ExportIcs:
                return await ExportIcsAsync(spec, args, accessToken, ct);
            default:
                var redact = spec.RedactUrls && !(args.TryGetPropertyValue("redactUrls", out var redactArg) && redactArg is JsonValue redactValue && redactValue.TryGetValue<bool>(out var redactBool) && !redactBool);
                return await CallAsync(spec, args, accessToken, redact, ct);
        }
    }

    private async Task<JsonNode?> CallAsync(
        McpToolSpec spec,
        JsonObject args,
        string accessToken,
        bool redact,
        CancellationToken ct)
    {
        if (spec.Multipart)
            return await CallMultipartAsync(spec, args, accessToken, ct);

        var (query, body) = BuildQueryAndBody(spec, args);
        var raw = await SendAsync(spec, args, query, body, accessToken, ct);

        if (raw.Status == 401 && _httpContext is null)
        {
            var retried = await TryRefreshAndRetryAsync(spec, args, query, body, accessToken, ct);
            if (retried is not null)
                raw = retried.Value;
        }

        var shaped = ShapeResponse(raw.Status, raw.Text, raw.ContentType, raw.Data, addAuthHint: _httpContext is null);
        if (shaped is JsonObject shapedObject && redact)
        {
            if (shapedObject.TryGetPropertyValue("data", out var dataNode) && dataNode is not null)
                shapedObject["data"] = Redact(dataNode);
            else
                shaped = Redact(shapedObject);
        }
        return PostProcess(shaped, spec, args);
    }

    /// <summary>Post-processes the shaped response: local pagination, >50KB truncation hint.</summary>
    private static JsonNode? PostProcess(JsonNode? result, McpToolSpec spec, JsonObject args)
    {
        if (result is not JsonObject resultObject)
            return result;

        if (spec.LocalPagination || spec.LocalPaginationTruncation)
        {
            if (resultObject.TryGetPropertyValue("data", out var dataNode) && dataNode is JsonArray array)
            {
                var page = GetInt(args, "page") ?? 1;
                var pageSize = GetInt(args, "pageSize") ?? 20;
                var total = array.Count;
                var startIdx = (page - 1) * pageSize;
                var paged = new JsonArray();
                for (var i = startIdx; i < Math.Min(startIdx + pageSize, total); i++)
                    paged.Add(array[i]?.DeepClone());
                var copy = resultObject.DeepClone().AsObject();
                copy["data"] = paged;
                copy["page"] = page;
                copy["pageSize"] = pageSize;
                copy["total"] = total;
                return spec.LocalPaginationTruncation ? CheckTruncation(copy, new JsonObject { ["page"] = page }) : copy;
            }
        }

        return CheckTruncation(resultObject, args);
    }

    // ---- request building ----

    private static (string Query, JsonNode? Body) BuildQueryAndBody(McpToolSpec spec, JsonObject args)
    {
        var queryParts = new List<string>();
        foreach (var entry in spec.QueryParams)
        {
            var (srcName, queryName) = SplitRename(entry);
            if (!args.TryGetPropertyValue(srcName, out var node) || node is null)
                continue;
            var value = ToQueryString(node);
            if (value is null)
                continue;
            if (spec.DateSpanConversion && (srcName is "start" or "end"))
            {
                // Python parity: heatmap/keystats/productivity-range endpoints expect YYYY-MM-DD
                // query values (converted from ISO); fall back to the raw value when unparseable.
                if (TryParseIso8601(value, out var parsed, out _))
                    value = parsed.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
            queryParts.Add($"{Uri.EscapeDataString(queryName)}={Uri.EscapeDataString(value)}");
        }

        JsonNode? body = null;
        if (spec.HasBody)
        {
            var bodyObject = new JsonObject();
            foreach (var property in args)
            {
                if (property.Value is null)
                    continue;
                if (spec.QueryParams.Any(q => SplitRename(q).Src == property.Key))
                    continue;
                if (spec.Route.Contains($"{{{property.Key}}}", StringComparison.Ordinal))
                    continue;
                bodyObject[property.Key] = property.Value.DeepClone();
            }
            if (spec.Kind == McpToolKind.SchedulePreview)
            {
                // Python: body = {"taskIds": taskIds or []} — always present.
                bodyObject["taskIds"] = bodyObject.TryGetPropertyValue("taskIds", out var ids) && ids is not null ? ids : new JsonArray();
            }
            body = bodyObject;
        }

        return (string.Join('&', queryParts), body);
    }

    private static (string Src, string QueryName) SplitRename(string entry)
    {
        var idx = entry.IndexOf('=');
        return idx < 0 ? (entry, entry) : (entry[..idx], entry[(idx + 1)..]);
    }

    private static string? ToQueryString(JsonNode node)
        => node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => node.ToJsonString(),
            _ => null,
        };

    private static string BuildPath(string route, JsonObject args)
    {
        var result = route;
        foreach (var property in args)
        {
            var placeholder = $"{{{property.Key}}}";
            if (result.Contains(placeholder, StringComparison.Ordinal) && property.Value is not null)
            {
                var raw = property.Value.GetValueKind() == JsonValueKind.String
                    ? property.Value.GetValue<string>()
                    : property.Value.ToJsonString();
                result = result.Replace(placeholder, Uri.EscapeDataString(raw), StringComparison.Ordinal);
            }
        }
        return result;
    }

    private async Task<(int Status, string Text, string? ContentType, JsonNode? Data)> SendAsync(
        McpToolSpec spec, JsonObject args, string query, JsonNode? body, string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(spec.Method), $"{BuildPath(spec.Route, args)}{(query.Length > 0 ? "?" + query : string.Empty)}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json");
        }
        var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString();
        return ((int)response.StatusCode, text, contentType, TryParseJson(text));
    }

    private async Task<JsonNode?> CallMultipartAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        if (!args.TryGetPropertyValue(spec.MultipartContentParam, out var contentNode) || contentNode is null)
            return Error($"{spec.MultipartContentParam} is required", 400);
        var contentParamValue = GetString(contentNode);
        byte[] content;
        if (spec.MultipartContentParam == "icsContent")
        {
            // import_ics passes raw ICS text (Python: icsContent.encode("utf-8")).
            content = Encoding.UTF8.GetBytes(contentParamValue ?? string.Empty);
        }
        else
        {
            try
            {
                content = Convert.FromBase64String(contentParamValue ?? string.Empty);
            }
            catch (FormatException)
            {
                return Error("fileContentBase64 must be valid base64", 400);
            }
        }

        var fileName = spec.FileName;
        if (fileName is null)
        {
            if (!args.TryGetPropertyValue("fileName", out var nameNode) || nameNode is null)
                return Error("fileName is required", 400);
            fileName = GetString(nameNode) ?? string.Empty;
        }

        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, spec.FileField, fileName);

        foreach (var property in args)
        {
            if (property.Value is null || property.Key == spec.MultipartContentParam || property.Key == "fileName")
                continue;
            if (spec.Route.Contains($"{{{property.Key}}}", StringComparison.Ordinal))
                continue;
            form.Add(new StringContent(property.Value.ToJsonString(SerializerOptions).Trim('"')), property.Key);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, BuildPath(spec.Route, args));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Content = form;
        var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var data = TryParseJson(text);
        var status = (int)response.StatusCode;

        if (status >= 400)
        {
            if (data is JsonObject obj)
            {
                if (!obj.ContainsKey("error"))
                    return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 800)}", ["details"] = obj.DeepClone(), ["code"] = status };
                if (!obj.ContainsKey("code"))
                    obj["code"] = status;
                return obj;
            }
            return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 800)}", ["details"] = data?.DeepClone() ?? JsonValue.Create(Truncate(text, 2000)), ["code"] = status };
        }

        var result = data ?? new JsonObject { ["raw"] = Truncate(text, 2000), ["status"] = status };
        return CheckTruncation(result, null);
    }

    // ---- response shaping (Python _call_api parity) ----

    private static JsonNode? ShapeResponse(int status, string text, string? contentType, JsonNode? data, bool addAuthHint)
    {
        if (status >= 400)
        {
            if (data is JsonObject obj)
            {
                if (!obj.ContainsKey("error"))
                    return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 800)}", ["details"] = obj.DeepClone(), ["code"] = status };

                var result = obj;
                if (!result.ContainsKey("code"))
                    result["code"] = status;
                if (addAuthHint && status == 401)
                {
                    var errorValue = GetString(result, "error") ?? string.Empty;
                    const string hint = " token expired or invalid; re-login via POST /api/v1/auth/login or set PIM_REFRESH_TOKEN for auto-refresh";
                    if (!errorValue.Contains("missing bearer", StringComparison.OrdinalIgnoreCase) && !errorValue.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        var copy = obj.DeepClone().AsObject();
                        copy["error"] = errorValue + hint;
                        result = copy;
                    }
                }
                return result;
            }
            return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 800)}", ["details"] = data?.DeepClone() ?? JsonValue.Create(Truncate(text, 2000)), ["code"] = status };
        }

        if (data is not null)
            return data;

        if (contentType is not null && (contentType.Contains("text/calendar", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
        {
            return new JsonObject { ["data"] = text, ["code"] = 0, ["contentType"] = contentType };
        }
        return new JsonObject { ["raw"] = Truncate(text, 2000), ["status"] = status };
    }

    /// <summary>Python _check_truncation: >50KB success responses gain truncated/nextPage hints.</summary>
    private static JsonNode? CheckTruncation(JsonNode? data, JsonObject? paramsNode)
    {
        if (data is not JsonObject obj)
            return data;
        var serialized = obj.ToJsonString(SerializerOptions);
        if (serialized.Length <= TruncationBytes)
            return obj;

        // Error responses are left untouched (Python parity).
        if (obj.TryGetPropertyValue("code", out var codeNode) && codeNode is not null
            && (!GetInt(codeNode).Equals(0)) && obj.ContainsKey("error"))
        {
            return obj;
        }

        var copy = obj.DeepClone().AsObject();
        copy["truncated"] = true;
        var pageParam = GetInt(paramsNode, "page");

        if (GetInt(copy, "page") is { } page)
            copy["nextPage"] = page + 1;
        else if (copy.TryGetPropertyValue("data", out var inner) && inner is JsonObject innerObj && GetInt(innerObj, "page") is { } innerPage)
            copy["nextPage"] = innerPage + 1;
        else if (copy.TryGetPropertyValue("data", out inner) && inner is JsonArray)
        {
            copy["nextPage"] = pageParam.HasValue ? pageParam + 1 : 2;
            copy["_note"] = "response >50KB, list truncated suggestion nextPage";
            return copy;
        }
        else
            copy["nextPage"] = pageParam.HasValue ? pageParam + 1 : 2;

        copy["_note"] = "response >50KB, consider pagination with nextPage";
        return copy;
    }

    // ---- redaction (Python _apply_redact parity) ----

    private static JsonNode? Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
            {
                var key = property.Key;
                var value = property.Value;
                var lowerKey = key.ToLowerInvariant();
                var isUrlLike = lowerKey.Contains("url", StringComparison.Ordinal)
                    || lowerKey.Contains("link", StringComparison.Ordinal)
                    || lowerKey == "href"
                    || lowerKey.EndsWith("href", StringComparison.Ordinal);
                if (isUrlLike && value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text))
                {
                    var newKey = lowerKey == "url" ? "urlHash"
                        : lowerKey == "href" ? "hrefHash"
                        : key.EndsWith("Hash", StringComparison.Ordinal) ? key : key + "Hash";
                    result[newKey] = string.IsNullOrEmpty(text) ? string.Empty : RedactValue(text);
                }
                else if (value is JsonObject or JsonArray)
                {
                    result[key] = Redact(value);
                }
                else
                {
                    result[key] = value?.DeepClone();
                }
            }
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(Redact(item));
            return result;
        }
        return node?.DeepClone();
    }

    private static string RedactValue(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ===================== special tools =====================

    private async Task<JsonNode?> CallOptionalAuthAsync(string method, string path, string? accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (accessToken is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        var data = TryParseJson(text);
        if (status >= 400)
        {
            if (data is JsonObject obj && obj.ContainsKey("error"))
            {
                if (!obj.ContainsKey("code"))
                    obj["code"] = status;
                return obj;
            }
            return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 500)}", ["code"] = status, ["details"] = data?.DeepClone() ?? JsonValue.Create(Truncate(text, 500)) };
        }
        return data ?? new JsonObject { ["raw"] = Truncate(text, 500), ["status"] = status };
    }

    private async Task<JsonNode?> AttachmentMetaAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var path = BuildPath(spec.Route, args);
        var attachmentId = GetString(args, "attachment_id") ?? string.Empty;

        var headRequest = new HttpRequestMessage(HttpMethod.Head, path);
        headRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        var headResponse = await _client.SendAsync(headRequest, ct);
        if ((int)headResponse.StatusCode < 400)
        {
            return new JsonObject
            {
                ["code"] = 0,
                ["data"] = new JsonObject
                {
                    ["attachmentId"] = attachmentId,
                    ["headers"] = HeadersToJson(headResponse),
                    ["status"] = (int)headResponse.StatusCode,
                    ["note"] = "metadata from HEAD, binary not downloaded",
                },
            };
        }

        var getRequest = new HttpRequestMessage(HttpMethod.Get, path);
        getRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        var getResponse = await _client.SendAsync(getRequest, ct);
        var text = await getResponse.Content.ReadAsStringAsync(ct);
        var status = (int)getResponse.StatusCode;
        if (status >= 400)
        {
            var data = TryParseJson(text);
            if (data is JsonObject obj && obj.ContainsKey("error"))
            {
                if (!obj.ContainsKey("code"))
                    obj["code"] = status;
                return obj;
            }
            return new JsonObject { ["error"] = $"HTTP {status}: {Truncate(text, 500)}", ["details"] = data?.DeepClone() ?? JsonValue.Create(Truncate(text, 500)), ["code"] = status };
        }

        return new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["attachmentId"] = attachmentId,
                ["headers"] = HeadersToJson(getResponse),
                ["size"] = getResponse.Content.Headers.ContentLength?.ToString(),
                ["contentType"] = getResponse.Content.Headers.ContentType?.ToString(),
                ["note"] = "binary not returned, only metadata per read-only policy",
            },
        };
    }

    private static JsonObject HeadersToJson(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        foreach (var header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    private async Task<JsonNode?> EventByIdAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var eventId = GetString(args, "event_id") ?? string.Empty;
        var direct = await CallAsync(spec, args, accessToken, redact: false, ct);
        if (direct is JsonObject directObj && GetInt(directObj, "code") is { } code && code is 404 or 405)
        {
            // Fallback 1: data-center query by exact object.
            var dc = await SendAsync(
                new McpToolSpec { Name = "query_data_center", Method = "POST", Route = "/api/v1/calendar/data-center/query", HasBody = true },
                new JsonObject { ["search"] = eventId, ["objectType"] = "event", ["page"] = 1, ["pageSize"] = 20 },
                string.Empty,
                new JsonObject { ["search"] = eventId, ["objectType"] = "event", ["page"] = 1, ["pageSize"] = 20 },
                accessToken, ct);
            var dcResult = ShapeResponse(dc.Status, dc.Text, dc.ContentType, dc.Data, addAuthHint: false);
            if (dcResult is JsonObject dcObj && dcObj.TryGetPropertyValue("data", out var dcData) && dcData is JsonObject dcInner)
            {
                var items = dcInner["items"] as JsonArray ?? dcInner["data"] as JsonArray;
                if (items is not null)
                {
                    foreach (var item in items)
                    {
                        if (item is JsonObject itemObj && string.Equals(GetString(itemObj, "objectId"), eventId, StringComparison.Ordinal))
                            return new JsonObject { ["code"] = 0, ["data"] = itemObj.DeepClone(), ["note"] = "via data-center fallback" };
                    }
                }
            }

            // Fallback 2: broad 730-day scan (page 1 only).
            var now = DateTimeOffset.UtcNow;
            var start = (now.AddDays(-365)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var end = (now.AddDays(365)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var scan = await SendAsync(
                new McpToolSpec { Name = "get_events", Method = "GET", Route = "/api/v1/calendar/events", QueryParams = new[] { "start", "end", "page", "pageSize" } },
                new JsonObject { ["start"] = start, ["end"] = end, ["page"] = 1, ["pageSize"] = 100 },
                "start=" + Uri.EscapeDataString(start) + "&end=" + Uri.EscapeDataString(end) + "&page=1&pageSize=100",
                null, accessToken, ct);
            var scanResult = ShapeResponse(scan.Status, scan.Text, scan.ContentType, scan.Data, addAuthHint: false);
            if (FindById(scanResult, eventId) is { } found)
                return new JsonObject { ["code"] = 0, ["data"] = found };

            return Error($"event {eventId} not found (fallback scanned 730-day window page 1 only, use broader get_events search if needed)", 404);
        }
        return direct;
    }

    private async Task<JsonNode?> TaskByIdAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var taskId = GetString(args, "task_id") ?? string.Empty;
        var direct = await CallAsync(spec, args, accessToken, redact: false, ct);

        var directObj = direct as JsonObject;
        var code = directObj is not null ? GetInt(directObj, "code") : null;
        var errorText = directObj is not null ? GetString(directObj, "error") ?? string.Empty : string.Empty;

        if (code is 404 or 405 || errorText.Contains("404", StringComparison.Ordinal))
        {
            // Fallback 1: data-center query.
            var dc = await SendAsync(
                new McpToolSpec { Name = "query_data_center", Method = "POST", Route = "/api/v1/calendar/data-center/query", HasBody = true },
                new JsonObject { ["search"] = taskId, ["objectType"] = "task", ["page"] = 1, ["pageSize"] = 20 },
                string.Empty,
                new JsonObject { ["search"] = taskId, ["objectType"] = "task", ["page"] = 1, ["pageSize"] = 20 },
                accessToken, ct);
            var dcResult = ShapeResponse(dc.Status, dc.Text, dc.ContentType, dc.Data, addAuthHint: false);
            if (dcResult is JsonObject dcObj && dcObj.TryGetPropertyValue("data", out var dcData) && dcData is JsonObject dcInner)
            {
                var items = dcInner["items"] as JsonArray ?? dcInner["data"] as JsonArray;
                if (items is not null)
                {
                    foreach (var item in items)
                    {
                        if (item is JsonObject itemObj && string.Equals(GetString(itemObj, "objectId"), taskId, StringComparison.Ordinal))
                            return new JsonObject { ["code"] = 0, ["data"] = itemObj.DeepClone(), ["note"] = "via data-center fallback" };
                    }
                }
            }

            // Fallback 2: list scan (page 1 only).
            var scan = await SendAsync(
                new McpToolSpec { Name = "get_tasks", Method = "GET", Route = "/api/v1/calendar/tasks", QueryParams = new[] { "page", "pageSize" } },
                new JsonObject { ["page"] = 1, ["pageSize"] = 100 },
                "page=1&pageSize=100", null, accessToken, ct);
            var scanResult = ShapeResponse(scan.Status, scan.Text, scan.ContentType, scan.Data, addAuthHint: false);
            if (FindById(scanResult, taskId) is { } found)
                return new JsonObject { ["code"] = 0, ["data"] = found };

            return code is 404 or 405
                ? Error($"task {taskId} not found (fallback page 1 only)", 404)
                : direct;
        }
        return direct;
    }

    private async Task<JsonNode?> HabitOccurrencesAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var direct = await CallAsync(spec, args, accessToken, redact: false, ct);
        var directObj = direct as JsonObject;
        var code = directObj is not null ? GetInt(directObj, "code") : null;
        if (code is not (404 or 405) && directObj is not null && !directObj.ContainsKey("error"))
            return direct;

        var habitId = GetString(args, "habit_id") ?? string.Empty;
        var dc = await SendAsync(
            new McpToolSpec { Name = "query_data_center", Method = "POST", Route = "/api/v1/calendar/data-center/query", HasBody = true },
            new JsonObject { ["search"] = habitId, ["objectType"] = "habit-occurrence", ["page"] = 1, ["pageSize"] = 50 },
            string.Empty,
            new JsonObject { ["search"] = habitId, ["objectType"] = "habit-occurrence", ["page"] = 1, ["pageSize"] = 50 },
            accessToken, ct);
        return ShapeResponse(dc.Status, dc.Text, dc.ContentType, dc.Data, addAuthHint: false);
    }

    private async Task<JsonNode?> TaskChecklistAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var direct = await CallAsync(spec, args, accessToken, redact: false, ct);
        var directObj = direct as JsonObject;
        var code = directObj is not null ? GetInt(directObj, "code") : null;
        if (code is not (404 or 405) && directObj is not null && !directObj.ContainsKey("error"))
            return direct;

        // Fallback: task detail may embed checklist items.
        var taskId = GetString(args, "task_id") ?? string.Empty;
        var taskSpec = new McpToolSpec { Name = "get_task_by_id", Method = "GET", Route = "/api/v1/calendar/tasks/{task_id}" };
        var taskRes = await CallAsync(taskSpec, new JsonObject { ["task_id"] = taskId }, accessToken, redact: false, ct);
        if (taskRes is JsonObject taskObj && taskObj.TryGetPropertyValue("data", out var dataNode))
        {
            if (dataNode is JsonObject dataObj)
            {
                var embedded = ExtractChecklist(dataObj, taskId);
                if (embedded is not null)
                    return embedded;
            }
            else if (dataNode is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject itemObj)
                    {
                        var embedded = ExtractChecklist(itemObj, taskId);
                        if (embedded is not null)
                            return embedded;
                    }
                }
            }
        }
        return new JsonObject { ["error"] = "checklist endpoint not available and not embedded in task", ["code"] = 404, ["details"] = taskRes?.DeepClone() };
    }

    private static JsonNode? ExtractChecklist(JsonObject obj, string taskId)
    {
        if (!string.Equals(GetString(obj, "id"), taskId, StringComparison.Ordinal))
            return null;
        if (obj.TryGetPropertyValue("checklist", out var checklist) && checklist is not null)
            return new JsonObject { ["code"] = 0, ["data"] = checklist.DeepClone() };
        if (obj.TryGetPropertyValue("checklistItems", out var items) && items is not null)
            return new JsonObject { ["code"] = 0, ["data"] = items.DeepClone() };
        return null;
    }

    private async Task<JsonNode?> CalendarByIdAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var calendarId = GetString(args, "calendar_id") ?? string.Empty;
        var listSpec = new McpToolSpec { Name = "get_calendars", Method = "GET", Route = "/api/v1/calendar/calendars" };
        var list = await CallAsync(listSpec, new JsonObject(), accessToken, redact: false, ct);
        if (list is JsonObject listObj && listObj.TryGetPropertyValue("data", out var dataNode) && dataNode is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject itemObj && string.Equals(GetString(itemObj, "id"), calendarId, StringComparison.Ordinal))
                    return new JsonObject { ["code"] = 0, ["data"] = itemObj.DeepClone() };
            }
            return Error($"calendar {calendarId} not found", 404);
        }
        return list;
    }

    private async Task<JsonNode?> MobileLocationLatestAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var res = await CallAsync(spec, args, accessToken, redact: false, ct);
        if (res is JsonObject obj && obj.TryGetPropertyValue("data", out var dataNode) && dataNode is JsonObject inner)
        {
            var points = inner["points"] as JsonArray ?? inner["items"] as JsonArray;
            if (points is not null && points.Count > 0)
            {
                var latest = points[^1]?.DeepClone();
                return new JsonObject { ["code"] = 0, ["data"] = latest, ["meta"] = new JsonObject { ["total"] = points.Count } };
            }
        }
        return res;
    }

    private async Task<JsonNode?> SearchAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var result = await CallAsync(spec, args, accessToken, redact: false, ct);
        if (result is JsonObject obj && GetInt(obj, "code") == 400
            && (GetString(obj, "error") ?? string.Empty).Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            var q = GetString(args, "q") ?? string.Empty;
            var pageSize = GetInt(args, "pageSize") ?? 20;
            var searchSpec = new McpToolSpec
            {
                Name = "search_pim",
                Method = "GET",
                Route = "/api/v1/search",
                QueryParams = new[] { "q", "type", "limit" },
            };
            var fallback = await CallAsync(searchSpec, new JsonObject { ["q"] = q, ["type"] = spec.Kind == McpToolKind.SearchEvents ? "event" : "task", ["limit"] = pageSize }, accessToken, redact: false, ct);
            return fallback;
        }
        return result;
    }

    private async Task<JsonNode?> ExportIcsAsync(McpToolSpec spec, JsonObject args, string accessToken, CancellationToken ct)
    {
        var ids = GetString(args, "ids");
        var calendarId = GetString(args, "calendarId");
        var effectiveIds = ids ?? calendarId;
        var queryArgs = new JsonObject
        {
            ["start"] = args["start"]?.DeepClone(),
            ["end"] = args["end"]?.DeepClone(),
        };
        if (effectiveIds is not null)
            queryArgs["ids"] = effectiveIds;
        if (calendarId is not null && ids is null)
            queryArgs["calendarId"] = calendarId;

        var icsSpec = new McpToolSpec
        {
            Name = spec.Name,
            Method = "GET",
            Route = "/api/v1/calendar/export-ics",
            QueryParams = new[] { "start", "end", "ids", "calendarId" },
        };
        return await CallAsync(icsSpec, queryArgs, accessToken, redact: false, ct);
    }

    // ---- stdio 401 refresh (Python _refresh_access_token parity) ----

    private async Task<(int Status, string Text, string? ContentType, JsonNode? Data)?> TryRefreshAndRetryAsync(
        McpToolSpec spec, JsonObject args, string query, JsonNode? body, string oldToken, CancellationToken ct)
    {
        var refreshToken = _stdioTokenSource.GetRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = new StringContent(new JsonObject { ["refreshToken"] = refreshToken }.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };
        var refreshResponse = await _client.SendAsync(refreshRequest, ct);
        if ((int)refreshResponse.StatusCode >= 400)
            return null;
        var refreshText = await refreshResponse.Content.ReadAsStringAsync(ct);
        var refreshData = TryParseJson(refreshText);
        var payload = refreshData is JsonObject refreshObj && refreshObj.TryGetPropertyValue("data", out var payloadNode) && payloadNode is JsonObject payloadObj
            ? payloadObj
            : refreshData as JsonObject;
        var newToken = payload is null ? null : GetString(payload, "accessToken") ?? GetString(payload, "access_token") ?? GetString(payload, "token");
        if (string.IsNullOrWhiteSpace(newToken) || newToken == oldToken)
            return null;

        // Persist for subsequent calls (best-effort, no token files in stdio .NET mode).
        Environment.SetEnvironmentVariable("PIM_ACCESS_TOKEN", newToken);

        return await SendAsync(spec, args, query, body, newToken, ct);
    }

    // ===================== helpers =====================

    private static JsonObject ToJsonObject(IDictionary<string, JsonElement>? arguments)
    {
        var result = new JsonObject();
        if (arguments is null)
            return result;
        foreach (var (key, value) in arguments)
        {
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
                continue;
            result[key] = JsonNode.Parse(value.GetRawText());
        }
        return result;
    }

    private static JsonNode? FindById(JsonNode? result, string id)
    {
        if (result is not JsonObject obj || !obj.TryGetPropertyValue("data", out var dataNode))
            return null;
        if (dataNode is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject itemObj && string.Equals(GetString(itemObj, "id"), id, StringComparison.Ordinal))
                    return itemObj.DeepClone();
            }
            return null;
        }
        if (dataNode is JsonObject inner && inner.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is JsonObject itemObj && string.Equals(GetString(itemObj, "id"), id, StringComparison.Ordinal))
                    return itemObj.DeepClone();
            }
        }
        return null;
    }

    private static int? GetInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<long>(out var longValue))
            return (int)longValue;
        return null;
    }

    private static int? GetInt(JsonObject? obj, string key)
        => obj is not null && obj.TryGetPropertyValue(key, out var node) ? GetInt(node) : null;

    private static string? GetString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;
        return text;
    }

    private static string? GetString(JsonObject? obj, string key)
        => obj is not null && obj.TryGetPropertyValue(key, out var node) ? GetString(node) : null;

    private static JsonObject Error(string message, int code)
        => new() { ["error"] = message, ["code"] = code };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CallToolResult ToToolResult(JsonNode? result)
    {
        var text = result?.ToJsonString(SerializerOptions) ?? "null";
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
        };
    }

    private static McpToolContract[] LoadContract()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Pim.Module.Mcp.Contract.mcp-tools.json")
            ?? throw new InvalidOperationException("Embedded MCP tool contract mcp-tools.json is missing.");
        return JsonSerializer.Deserialize<McpToolContract[]>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("Embedded MCP tool contract failed to deserialize.");
    }
}

/// <summary>Tool-level auth failure (missing token / verify 400-403) — mapped to a result dict by the executor.</summary>
public sealed class McpToolAuthException : Exception
{
    public int HttpStatus { get; }

    public McpToolAuthException(int httpStatus, string message)
        : base(message)
    {
        HttpStatus = httpStatus;
    }
}