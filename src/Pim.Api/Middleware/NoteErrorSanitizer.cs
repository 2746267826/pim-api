using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Pim.Api.Middleware;

/// <summary>
/// Keeps ordinary-user Native Notes errors actionable without echoing internal identifiers,
/// hashes, endpoints, paths or exception/state-machine detail. Admins keep the original text.
/// </summary>
public static class NoteErrorSanitizer
{
    private static readonly Regex InternalPattern = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b" +
        @"|\b[0-9a-f]{32,64}\b" +
        @"|https?://" +
        @"|(?:^|[\s""'(])(?:[a-zA-Z]:\\|/(?:data|tmp|home|var|app|srv|opt)/)" +
        @"|\b(?:attempt|fence|lease|provider|endpoint|model|batch|request[ _-]?id|log[ _-]?id|sha-?256|hash|phase|step[ _-]?key|receipt|coverage|artifact|version[ _-]?id)[a-z0-9_-]*\b" +
        @"|\b(?:Exception|IOException|JsonException|InvalidDataException|NullReferenceException)\b" +
        @"|\b[A-Za-z][A-Za-z0-9_.-]{3,}\b" +
        @"|[A-Za-z0-9_.-]+[\\/][A-Za-z0-9_.\\/-]+" +
        @"|\b\d{4,}\b" +
        @"|\{\s*""" ,
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsNativeNotesPath(PathString path) =>
        path.StartsWithSegments("/api/v1/native-notes", StringComparison.OrdinalIgnoreCase);

    public static string SanitizeDomainMessage(string message, int statusCode) =>
        InternalPattern.IsMatch(message) ? FallbackMessage(statusCode) : message;

    public static string BadRequestMessage(string? rawDetail, bool includeDiagnostics) =>
        includeDiagnostics ? $"请求参数格式无效: {rawDetail}" : "请求参数格式无效";

    public static string FallbackMessage(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "请先登录",
        StatusCodes.Status403Forbidden => "当前账号不能执行此操作",
        StatusCodes.Status404NotFound => "请求的笔记资源不存在或已过期，请刷新后重试",
        StatusCodes.Status409Conflict => "笔记状态已变化，请刷新后重新核对",
        StatusCodes.Status413PayloadTooLarge => "本次笔记操作超过服务器限制",
        StatusCodes.Status429TooManyRequests => "操作过于频繁，请稍后重试",
        StatusCodes.Status503ServiceUnavailable => "笔记服务暂时不可用，请稍后重试",
        _ => "请求无法完成，请核对后重试"
    };
}
