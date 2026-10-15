using System.Net;
using System.Text;
using System.Text.Json;

namespace Pim.Module.Files.Providers.Google;

/// <summary>
/// Google Drive REST 调用失败。
/// <paramref name="Reason"/> 是 Google 错误体里的 <c>reason</c>（如
/// <c>invalid_page_token</c> / <c>rateLimitExceeded</c> / <c>insufficientPermissions</c>）——
/// 单看 HTTP 状态码不足以分辨「游标失效」和「权限不足」，必须带上它。
/// </summary>
public sealed class GoogleDriveException(
    int statusCode,
    string? reason,
    int? retryAfterSeconds,
    string message,
    string? rawBody = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string? Reason { get; } = reason;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    /// <summary>原始响应体（截断）。**只用于本地排查**，绝不写进响应用户的错误消息。</summary>
    public string? RawBody { get; } = rawBody;
}

/// <summary>Google 返回的令牌。</summary>
public sealed record GoogleTokenResult(string AccessToken, string? RefreshToken, int ExpiresIn, string? Scope);

/// <summary>
/// Google 账号身份。
///
/// 取自 Drive 的 <c>about.get</c> 而**不是** OAuth 的 <c>userinfo</c>：
/// userinfo 需要 <c>openid</c> 之类的身份 scope，而我们只申请 <c>.../auth/drive</c> ——
/// 拿全盘文件权限去换身份接口在干净账号上会直接 401（复审 Important）。
/// <c>about.user.permissionId</c> 是该用户在 Drive 上的稳定身份，scope 也够用。
/// </summary>
public sealed record GoogleUserInfo(string Subject, string? DisplayName);

/// <summary>Drive 文件资源（只取 PIM 需要的字段）。</summary>
public sealed record GoogleDriveFile(
    string Id,
    string Name,
    string? MimeType,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Md5,
    IReadOnlyList<string> Parents,
    bool Trashed,
    string? WebViewLink,
    string? WebContentLink,
    string? ThumbnailLink);

/// <summary>一页 changes.list 结果。</summary>
public sealed record GoogleChangePage(
    IReadOnlyList<GoogleChangeEntry> Changes,
    string? NextPageToken,
    string? NewStartPageToken);

/// <summary><c>removed=true</c> 的条目只带 fileId。</summary>
public sealed record GoogleChangeEntry(bool Removed, string FileId, GoogleDriveFile? File);

/// <summary>一页 files.list 结果。</summary>
public sealed record GoogleFilePage(IReadOnlyList<GoogleDriveFile> Files, string? NextPageToken);

/// <summary>Google 的分享权限。</summary>
public sealed record GooglePermission(string Id, string Type, string Role, string? WebViewLink);

/// <summary>
/// Google Drive REST 客户端（工单 05 / F4）。
///
/// 只做「HTTP + JSON」这一层，不做任何业务决策；provider 语义（游标、增量、路径）
/// 在 <see cref="GoogleFileProvider"/> 一族里。错误统一抛
/// <see cref="GoogleDriveException"/>，由调用方翻译成通用抽象异常。
/// </summary>
public sealed class GoogleDriveClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "google-drive";

    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// 文件资源的字段选择。**必须显式列出**：Drive v3 默认只返回 id/name/mimeType/kind，
    /// 少写一个字段就会静默拿到 null（本仓在 Graph 上吃过同样的亏）。
    /// </summary>
    public const string FileFields =
        "id,name,mimeType,size,modifiedTime,md5Checksum,parents,trashed,webViewLink,webContentLink,thumbnailLink";

    /// <summary>Google 原生文档（Docs/Sheets/Slides…）：没有 size，也不能按 alt=media 下载。</summary>
    public static bool IsGoogleNativeDocument(string? mimeType)
        => mimeType is not null
           && mimeType.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 原生文档导出成什么类型。返回 null 表示**该类型不支持文本导出**，调用方应给出明确错误，
    /// 而不是拿一个必然 400 的请求去打 Drive。
    ///
    /// 依据 Drive 的官方支持矩阵：
    /// <list type="bullet">
    /// <item>Docs → <c>text/plain</c></item>
    /// <item>Sheets → <c>text/csv</c></item>
    /// <item>Slides → <c>application/pdf</c>（文本抽取需要 Tika；未配置时给明确错误）</item>
    /// <item>Forms / Shortcuts / Sites / 目录等 → 不支持导出；Drawings 只支持 png/pdf/svg 等
    /// 非文本目标，本文本抽取路径同样按「不支持」处理（要支持图片得另做视觉链路）</item>
    /// </list>
    /// </summary>
    public static string? ExportMimeTypeFor(string? mimeType)
        => mimeType switch
        {
            "application/vnd.google-apps.document" => "text/plain",
            "application/vnd.google-apps.spreadsheet" => "text/csv",
            "application/vnd.google-apps.presentation" => "application/pdf",
            _ => null,
        };

    /// <summary>目录的 MIME。</summary>
    public const string FolderMimeType = "application/vnd.google-apps.folder";

    // ---- OAuth ----

    /// <summary>用授权码换令牌（浏览器跳转授权的第一步回调）。</summary>
    public Task<GoogleTokenResult> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken ct = default)
        => PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        }, ct);

    /// <summary>用 refresh token 换 access token。</summary>
    public Task<GoogleTokenResult> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken ct = default)
        => PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

    /// <summary>
    /// 取账号身份。用 Drive 的 <c>about.get</c>（只需要 drive scope）。
    /// </summary>
    public async Task<GoogleUserInfo> GetAboutUserAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetJsonAsync(
            $"{ApiBase}/about?fields=user(permissionId,emailAddress,displayName)",
            accessToken,
            ct);
        if (!json.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            throw new GoogleDriveException(200, null, null, "Google about.get 未返回 user 信息");
        }

        return new GoogleUserInfo(
            ReadString(user, "permissionId")
                ?? throw new GoogleDriveException(200, null, null, "Google about.get 未返回 user.permissionId"),
            ReadString(user, "displayName") ?? ReadString(user, "emailAddress"));
    }

    private async Task<GoogleTokenResult> PostTokenAsync(
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw GoogleErrorMapper.FromTokenFailure(response.StatusCode, body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = ReadString(root, "access_token")
            ?? throw new GoogleDriveException((int)response.StatusCode, null, null, "Google 未返回 access_token");
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        return new GoogleTokenResult(accessToken, ReadString(root, "refresh_token"), expiresIn, ReadString(root, "scope"));
    }

    // ---- Drive ----

    public async Task<GoogleFilePage> ListFilesPageAsync(
        string accessToken,
        string? pageToken,
        string query,
        CancellationToken ct = default)
    {
        var url = new StringBuilder($"{ApiBase}/files?q={Uri.EscapeDataString(query)}")
            .Append("&pageSize=1000&fields=")
            .Append(Uri.EscapeDataString($"nextPageToken,files({FileFields})"));
        if (!string.IsNullOrEmpty(pageToken))
        {
            url.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        var json = await GetJsonAsync(url.ToString(), accessToken, ct);
        var files = new List<GoogleDriveFile>();
        if (json.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                files.Add(ReadFile(element));
            }
        }

        return new GoogleFilePage(files, ReadString(json, "nextPageToken"));
    }

    public async Task<GoogleDriveFile?> GetFileAsync(
        string accessToken,
        string fileId,
        CancellationToken ct = default)
    {
        try
        {
            var json = await GetJsonAsync(
                $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(FileFields)}",
                accessToken,
                ct);
            return ReadFile(json);
        }
        catch (GoogleDriveException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> GetStartPageTokenAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{ApiBase}/changes/startPageToken", accessToken, ct);
        return ReadString(json, "startPageToken")
            ?? throw new GoogleDriveException(200, null, null, "Google 未返回 startPageToken");
    }

    public async Task<GoogleChangePage> ListChangesPageAsync(
        string accessToken,
        string pageToken,
        CancellationToken ct = default)
    {
        var url = new StringBuilder($"{ApiBase}/changes")
            .Append("?pageToken=").Append(Uri.EscapeDataString(pageToken))
            .Append("&pageSize=1000&includeRemoved=true&fields=")
            .Append(Uri.EscapeDataString(
                $"nextPageToken,newStartPageToken,changes(removed,fileId,file({FileFields}))"));

        var json = await GetJsonAsync(url.ToString(), accessToken, ct);
        var changes = new List<GoogleChangeEntry>();
        if (json.TryGetProperty("changes", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var removed = element.TryGetProperty("removed", out var removedElement)
                    && removedElement.ValueKind == JsonValueKind.True;
                var fileId = ReadString(element, "fileId") ?? string.Empty;
                GoogleDriveFile? file = null;
                if (element.TryGetProperty("file", out var fileElement) && fileElement.ValueKind == JsonValueKind.Object)
                {
                    file = ReadFile(fileElement);
                }

                changes.Add(new GoogleChangeEntry(removed, fileId, file));
            }
        }

        return new GoogleChangePage(
            changes,
            ReadString(json, "nextPageToken"),
            ReadString(json, "newStartPageToken"));
    }

    /// <summary>小文件上传（multipart，≤4MB 场景）。</summary>
    public async Task<GoogleDriveFile> UploadNewFileAsync(
        string accessToken,
        string? parentId,
        string name,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = name,
        };
        if (!string.IsNullOrEmpty(parentId))
        {
            metadata["parents"] = new[] { parentId };
        }

        var json = await MultipartUploadAsync(
            $"{UploadBase}/files?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}",
            accessToken,
            JsonSerializer.Serialize(metadata),
            bytes,
            contentType,
            ct);
        return ReadFile(json);
    }

    /// <summary>
    /// 覆盖已有文件内容。
    ///
    /// 必须是 <c>PATCH .../files/{id}?uploadType=media</c> + **原始字节体**：
    /// 新建文件用的 multipart/related 上传原语在这里不成立（复审 Important），
    /// 复用它会得到一个 400，表现为「Google 上存不了文本」。
    /// </summary>
    public async Task UpdateFileContentAsync(
        string accessToken,
        string fileId,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{UploadBase}/files/{Uri.EscapeDataString(fileId)}?uploadType=media&fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await SendJsonAsync(request, accessToken, ct);
    }

    /// <summary>改名 / 换父目录。</summary>
    public async Task<GoogleDriveFile> PatchFileAsync(
        string accessToken,
        string fileId,
        string? newName,
        string? addParentId,
        string? removeParentId,
        CancellationToken ct = default)
    {
        var url = new StringBuilder($"{ApiBase}/files/{Uri.EscapeDataString(fileId)}?fields=")
            .Append(Uri.EscapeDataString(FileFields));
        if (!string.IsNullOrEmpty(addParentId))
        {
            url.Append("&addParents=").Append(Uri.EscapeDataString(addParentId));
        }

        if (!string.IsNullOrEmpty(removeParentId))
        {
            url.Append("&removeParents=").Append(Uri.EscapeDataString(removeParentId));
        }

        var payload = new Dictionary<string, object?>();
        if (newName is not null)
        {
            payload["name"] = newName;
        }

        using var request = new HttpRequestMessage(HttpMethod.Patch, url.ToString())
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        var json = await SendJsonAsync(request, accessToken, ct);
        return ReadFile(json);
    }

    /// <summary>
    /// 新建目录，返回 Drive 在响应体里给出的条目（**必须**用它拿 id）。
    ///
    /// 不要「POST 之后立刻 files.list 按名字回查」：Drive 的搜索索引是最终一致的
    /// （新条目通常要几百毫秒到数秒才可见），回查会随机查不到 → 误报「未能读回」，
    /// 而网盘上目录其实已经建好了，用户重试就会堆出一串同名孤儿目录。
    /// </summary>
    public async Task<GoogleDriveFile> CreateFolderAsync(
        string accessToken,
        string? parentId,
        string name,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["mimeType"] = FolderMimeType,
        };
        if (!string.IsNullOrEmpty(parentId))
        {
            payload["parents"] = new[] { parentId };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/files?fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        return ReadFile(await SendJsonAsync(request, accessToken, ct));
    }

    /// <summary>删除（进 Drive 回收站）。</summary>
    public async Task DeleteFileAsync(string accessToken, string fileId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}?fields=id")
        {
            Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json"),
        };

        try
        {
            await SendJsonAsync(request, accessToken, ct);
        }
        catch (GoogleDriveException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // 远端已不存在：与 OneDrive 实现同样的「视为已删除」语义
        }
    }

    /// <summary>创建可恢复上传会话（浏览器直接向返回的 session URI 分片 PUT）。</summary>
    /// <param name="origin">
    /// 发起上传的页面来源（如 <c>https://home.hsww.party:15858</c>）。**必须带上**：
    /// Drive 只有在初始化会话时看到 <c>Origin</c>，才会给后续浏览器分片 PUT 返回
    /// CORS 响应头；不带的话浏览器直传会被跨域拦截（复审 Important）。
    /// </param>
    public async Task<string> CreateResumableUploadSessionAsync(
        string accessToken,
        string? parentId,
        string name,
        string contentType,
        string? origin,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = name,
        };
        if (!string.IsNullOrEmpty(parentId))
        {
            metadata["parents"] = new[] { parentId };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{UploadBase}/files?uploadType=resumable&fields=id")
        {
            Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", contentType);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using var response = await SendAsync(request, accessToken, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleErrorMapper.FromResponseAsync(response, ct);
        }

        // session URI 在 Location 头里（Google 不返回 JSON 体）
        return response.Headers.Location?.ToString()
               ?? throw new GoogleDriveException(
                   (int)response.StatusCode, null, null, "Google 未返回 resumable session URI");
    }

    /// <summary>
    /// 下载内容。Google 原生文档走 export（导出的目标 MIME 由调用方决定，见
    /// <see cref="ExportMimeTypeFor"/>），其余走 <c>alt=media</c>。
    /// </summary>
    /// <param name="exportMimeType">
    /// 原生文档的导出目标类型；非原生文档传 null。
    /// **不能一律用 text/plain**：Sheets 只支持 csv/pdf/xlsx 等，Slides 只支持 pdf/pptx，
    /// 对它们请求 text/plain 会被 Drive 以 400 拒绝（复审 Critical）。
    /// </param>
    public async Task<byte[]> DownloadAsync(
        string accessToken,
        string fileId,
        string? mimeType,
        string? exportMimeType,
        long maxBytes,
        CancellationToken ct = default)
    {
        var url = IsGoogleNativeDocument(mimeType) && exportMimeType is not null
            ? $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString(exportMimeType)}"
            : $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // ResponseHeadersRead：先在响应头就返回，再按上限流式读取。
        // 默认的 ResponseContentRead 会把整个响应体先缓冲完，字节上限再严也没用（复审 Important）。
        using var response = await SendAsync(request, accessToken, ct, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleErrorMapper.FromResponseAsync(response, ct);
        }

        // 不信任 Content-Length：边读边计数，超限立即中断
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new Abstractions.ProviderContentTooLargeException(buffer.Length + read);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // ---- 分享 ----

    public async Task<GooglePermission> CreateAnyonePermissionAsync(
        string accessToken,
        string fileId,
        string role,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "anyone",
            ["role"] = role,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}/permissions?fields=id,type,role")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var json = await SendJsonAsync(request, accessToken, ct);
        return new GooglePermission(
            ReadString(json, "id") ?? string.Empty,
            ReadString(json, "type") ?? "anyone",
            ReadString(json, "role") ?? role,
            null);
    }

    public async Task<IReadOnlyList<GooglePermission>> ListPermissionsAsync(
        string accessToken,
        string fileId,
        CancellationToken ct = default)
    {
        var json = await GetJsonAsync(
            $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}/permissions?fields=permissions(id,type,role)",
            accessToken,
            ct);
        var result = new List<GooglePermission>();
        if (json.TryGetProperty("permissions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                result.Add(new GooglePermission(
                    ReadString(element, "id") ?? string.Empty,
                    ReadString(element, "type") ?? string.Empty,
                    ReadString(element, "role") ?? string.Empty,
                    null));
            }
        }

        return result;
    }

    public async Task RevokePermissionAsync(
        string accessToken,
        string fileId,
        string permissionId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{ApiBase}/files/{Uri.EscapeDataString(fileId)}/permissions/{Uri.EscapeDataString(permissionId)}");
        try
        {
            await SendJsonAsync(request, accessToken, ct);
        }
        catch (GoogleDriveException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // 已撤销或不存在：视为成功
        }
    }

    // ---- 传输原语 ----

    private async Task<JsonElement> MultipartUploadAsync(
        string url,
        string accessToken,
        string? metadataJson,
        byte[] bytes,
        string contentType,
        CancellationToken ct)
    {
        const string boundary = "pim-drive-boundary-8f2a";
        using var content = new MultipartContent("related", boundary);
        if (metadataJson is not null)
        {
            content.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"));
        }

        var media = new ByteArrayContent(bytes);
        media.Headers.TryAddWithoutValidation("Content-Type", contentType);
        content.Add(media);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        return await SendJsonAsync(request, accessToken, ct);
    }

    private async Task<JsonElement> GetJsonAsync(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendJsonAsync(request, accessToken, ct);
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpRequestMessage request,
        string accessToken,
        CancellationToken ct)
    {
        using var response = await SendAsync(request, accessToken, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleErrorMapper.FromResponseAsync(response, ct);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string accessToken,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // token 只进 Authorization 头，绝不进 URL / 日志 / 异常消息
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        var client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, completionOption, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw GoogleErrorMapper.FromBody((int)response.StatusCode, body);
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static GoogleDriveFile ReadFile(JsonElement element)
    {
        var parents = new List<string>();
        if (element.TryGetProperty("parents", out var parentsElement) && parentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var parent in parentsElement.EnumerateArray())
            {
                if (parent.ValueKind == JsonValueKind.String)
                {
                    parents.Add(parent.GetString()!);
                }
            }
        }

        return new GoogleDriveFile(
            ReadString(element, "id") ?? string.Empty,
            ReadString(element, "name") ?? string.Empty,
            ReadString(element, "mimeType"),
            ReadLong(element, "size"),
            ReadDateTimeOffset(element, "modifiedTime"),
            ReadString(element, "md5Checksum"),
            parents,
            element.TryGetProperty("trashed", out var trashed) && trashed.ValueKind == JsonValueKind.True,
            ReadString(element, "webViewLink"),
            ReadString(element, "webContentLink"),
            ReadString(element, "thumbnailLink"));
    }

    internal static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        // Drive 把 size 作为字符串返回（大文件超 int64 精度的历史原因）
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    internal static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
        => DateTimeOffset.TryParse(ReadString(element, name), out var parsed) ? parsed : null;
}
