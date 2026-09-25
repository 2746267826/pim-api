using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Pim.UnitTests.Files;

/// <summary>
/// 一个**真实 HTTP**（<see cref="HttpListener"/>）的假 Microsoft Graph 服务，用于
/// WO-FILES-20260923 PR-2 的 V1~V4 与 REQ-14/21 验证。
///
/// 为什么必须是真实 HTTP 而不是 stub handler：
/// <list type="bullet">
///   <item><b>分片上传协议</b>是「多次 PUT + Content-Range + 顺序」的交互，
///   stub 只能断言单次调用形状，证明不了会话推进、断点续传与最终字节内容；</item>
///   <item><b>CORS 预检</b>是浏览器与服务器的真实协商，只有真实栈能观察
///   （issue #343 的教训：stub 不模拟真实语义就会假绿）；</item>
///   <item>本类记录**请求全序**（含每片字节数与 Content-Range），
///   可直接用来核对「320KiB 整数倍 / 单块 ≤60MiB / 顺序上传」；</item>
///   <item>它同时统计**到达服务器的字节数**，作为「上传有没有经过 PIM 服务器」的对照口径。</item>
/// </list>
///
/// 服务只绑定回环地址、只接受测试实际发起的请求；不访问外网、不接触任何真实账号。
/// </summary>
internal sealed class FakeGraphServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, UploadSessionState> _sessions = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    /// <summary>各父目录下已存在的子文件夹名（用于复刻真实的重名自动改名）。</summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _folderChildren = new();

    /// <summary>已创建的文件夹 id → (最终名称, 父路径)，供按 id 回读返回**服务端真实**名称。</summary>
    private readonly ConcurrentDictionary<string, (string Name, string ParentPath)> _idToName = new();

    public FakeGraphServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/v1.0";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public string BaseUrl { get; }

    /// <summary>按发生顺序记录的请求（方法 + 路径 + Content-Range + 字节数）。</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>到达本服务的**上传字节总数**（含被拒分片——它们确实到达了服务端）。</summary>
    public long UploadedBytesToServer => Interlocked.Read(ref _uploadedBytes);

    /// <summary>**被接受**的分片字节总数（用于「续传后恰好补齐、无缺口无重复」的断言）。</summary>
    public long AcceptedUploadBytes => Interlocked.Read(ref _acceptedBytes);

    private long _uploadedBytes;
    private long _acceptedBytes;

    /// <summary>让下一次请求返回 503（模拟网络/上游失败，供失败与重试用例）。</summary>
    public bool FailNextUploadChunk { get; set; }

    /// <summary>最近一次请求的请求体原文（字节原样保留：上传的是二进制，不能过字符串往返）。</summary>
    public byte[] LastRequestBody { get; private set; } = [];

    /// <summary>最近一次请求体的文本视图（仅用于 JSON 请求，如 createUploadSession）。</summary>
    public string? LastRequestBodyText => LastRequestBody.Length == 0 ? null : Encoding.UTF8.GetString(LastRequestBody);

    /// <summary>上传完成时 Graph 返回的最终条目 id/name（可改为模拟「重名自动改名」）。</summary>
    public string CompletedItemId { get; set; } = "fake-uploaded-1";

    public string CompletedItemName { get; set; } = "uploaded.bin";

    /// <summary>分享链接：createLink 返回的地址（可由用例改为 view/edit 以区分权限档）。</summary>
    public string ShareLinkFactory(string itemId, string type) => $"https://1drv.ms/{type}/{itemId}";

    public async Task StartAsync()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await HandleCoreAsync(context);
        }
        catch
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* 已关闭 */ }
        }
    }

    private async Task HandleCoreAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        var method = request.HttpMethod;

        // HttpListener 的 InputStream 只能读一次：先整体读成**字节**缓存，后续所有分支都用缓存。
        // 必须保留字节：上传分片是二进制，若先解成 UTF-8 字符串再编码回来，
        // 非法序列会被替换、字节数与内容都会变（曾导致分片校验恒失败）。
        LastRequestBody = await ReadRawBodyAsync(request);


        // CORS 预检：真实协商，记录并如实回应（V1 要看的就是这里）
        if (method == "OPTIONS")
        {
            AddCors(request, context);
            context.Response.StatusCode = 200;
            context.Response.Close();
            Record(method, path, request.Headers["Content-Range"], 0, isPreflight: true);
            return;
        }

        AddCors(request, context);

        // ---- createUploadSession ----
        if (method == "POST" && path.Contains("createUploadSession", StringComparison.Ordinal))
        {
            var sessionId = Guid.NewGuid().ToString("N");
            _sessions[sessionId] = new UploadSessionState();
            var body = JsonSerializer.Serialize(new
            {
                uploadUrl = $"{BaseUrl}/upload/{sessionId}",
                expirationDateTime = DateTimeOffset.UtcNow.AddHours(1).ToString("o"),
            });
            await WriteAsync(context, 200, body);
            Record(method, path, null, 0);
            return;
        }

        // ---- 分片上传（Graph 的预授权 uploadUrl，不带 Authorization）----
        if (method == "PUT" && path.StartsWith("/v1.0/upload/", StringComparison.Ordinal))
        {
            var sessionId = path["/v1.0/upload/".Length..];
            var range = request.Headers["Content-Range"];
            var bytes = LastRequestBody;
            Interlocked.Add(ref _uploadedBytes, bytes.Length);
            Record(method, path, range, bytes.Length);

            if (FailNextUploadChunk)
            {
                FailNextUploadChunk = false;
                await WriteAsync(context, 503, """{"error":{"code":"serviceUnavailable"}}""");
                return;
            }

            if (!_sessions.TryGetValue(sessionId, out var state))
            {
                await WriteAsync(context, 404, """{"error":{"code":"invalidSession"}}""");
                return;
            }

            // 断点续传：客户端可能是「查询进度」后从中间继续，这里按 Content-Range 拼接
            var (start, end, total) = ParseContentRange(range);
            if (start != state.Received)
            {
                // 与真实 Graph 一致：顺序不符时回 416 并给出期望区间
                await WriteAsync(context, 416, $"{{\"nextExpectedRanges\":[\"{state.Received}-\"]}}");
                return;
            }

            state.Append(bytes);
            state.Total = total;
            Interlocked.Add(ref _acceptedBytes, bytes.Length);
            if (state.Received >= total)
            {
                _files[sessionId] = state.Completed!;
                // 201 的响应体是**最终条目**：重名时 Graph 会把名字改掉，客户端必须用它
                await WriteAsync(context, 201, JsonSerializer.Serialize(new
                {
                    id = CompletedItemId,
                    name = CompletedItemName,
                    size = total,
                    parentReference = new { path = "/drive/root:/文档" },
                    file = new { mimeType = "application/octet-stream" },
                }));
                return;
            }

            await WriteAsync(context, 202, $"{{\"nextExpectedRanges\":[\"{state.Received}-\"]}}");
            return;
        }

        // ---- 查询上传会话（断点续传用）----
        if (method == "GET" && path.StartsWith("/v1.0/upload/", StringComparison.Ordinal))
        {
            var sessionId = path["/v1.0/upload/".Length..];
            var state = _sessions.TryGetValue(sessionId, out var s) ? s : new UploadSessionState();
            await WriteAsync(context, 200, $"{{\"nextExpectedRanges\":[\"{state.Received}-\"]}}");
            Record(method, path, null, 0);
            return;
        }

        // ---- 在父目录下新建文件夹（REQ-15）----
        // 真实 Graph 只接受「父目录的 children」形态：
        //   POST /drive/root/children               （根目录）
        //   POST /drive/root:/{parent}:/children    （子目录）
        // 缺 `:/children` 的形态在真实账号上实测 **400 invalidRequest**，
        // 所以这里如实拒绝它——假服务不许比真服务宽松，否则端点形态错了也照样绿。
        if (method == "POST" && path.Contains("/drive/root", StringComparison.Ordinal))
        {
            var prefix = "/v1.0/drive/root";
            var rest = path[prefix.Length..];
            string? parentPath = null;
            if (rest == "/children")
            {
                parentPath = "/";
            }
            else if (rest.StartsWith(":/", StringComparison.Ordinal)
                && rest.EndsWith(":/children", StringComparison.Ordinal))
            {
                var raw = rest[2..^":/children".Length];
                parentPath = "/" + Uri.UnescapeDataString(raw).Trim('/');
            }

            if (parentPath is null)
            {
                // 复刻真实账号的 400 invalidRequest
                await WriteAsync(context, 400, """{"error":{"code":"invalidRequest","message":"The request is malformed or incorrect."}}""");
                Record(method, path, null, LastRequestBody.Length);
                return;
            }

            var body = JsonSerializer.Deserialize<JsonElement>(LastRequestBody);
            var folderName = body.TryGetProperty("name", out var n) ? n.GetString() ?? "新建文件夹" : "新建文件夹";
            var conflict = body.TryGetProperty("@microsoft.graph.conflictBehavior", out var cb) ? cb.GetString() : null;

            var children = _folderChildren.GetOrAdd(parentPath, _ => []);
            var finalName = folderName;
            if (!children.Add(finalName))
            {
                if (conflict != "rename")
                {
                    await WriteAsync(context, 409, """{"error":{"code":"nameAlreadyExists"}}""");
                    Record(method, path, null, LastRequestBody.Length);
                    return;
                }

                // 真实账号实测的重名命名格式是「名称 1」（空格 + 序号，无括号）
                var index = 1;
                while (!children.Add($"{folderName} {index}"))
                {
                    index++;
                }

                finalName = $"{folderName} {index}";
            }

            var folderId = $"folder-{Guid.NewGuid():N}";
            _idToName[folderId] = (finalName, parentPath);
            await WriteAsync(context, 201, JsonSerializer.Serialize(new
            {
                id = folderId,
                name = finalName,
                folder = new { childCount = 0 },
                parentReference = new { path = parentPath == "/" ? "/drive/root:" : $"/drive/root:{parentPath}" },
            }));
            Record(method, path, null, LastRequestBody.Length);
            return;
        }

        // ---- createLink（分享）----
        if (method == "POST" && path.EndsWith("/createLink", StringComparison.Ordinal))
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(LastRequestBody);
            var type = payload.TryGetProperty("type", out var t) ? t.GetString() ?? "view" : "view";
            var itemId = path.Split('/')[^2];
            var link = ShareLinkFactory(itemId, type);
            await WriteAsync(context, 200, JsonSerializer.Serialize(new
            {
                id = $"perm-{itemId}",
                link = new { webUrl = link, type, scope = "anonymous" },
            }));
            Record(method, path, null, 0);
            return;
        }

        // ---- 按 id 回读条目（上传完成后登记 / 新建文件夹后登记用）----
        if (method == "GET" && path.Contains("/drive/items/", StringComparison.Ordinal))
        {
            var requestedId = path[(path.IndexOf("/drive/items/", StringComparison.Ordinal) + "/drive/items/".Length)..];
            requestedId = Uri.UnescapeDataString(requestedId.Trim('/'));

            // 建好的文件夹：返回**服务端真实**名称（重名时已自动改名）与父路径
            if (_idToName.TryGetValue(requestedId, out var folder))
            {
                await WriteAsync(context, 200, JsonSerializer.Serialize(new
                {
                    id = requestedId,
                    name = folder.Name,
                    size = 0,
                    folder = new { childCount = 0 },
                    parentReference = new { path = folder.ParentPath == "/" ? "/drive/root:" : $"/drive/root:{folder.ParentPath}" },
                }));
                Record(method, path, null, 0);
                return;
            }

            await WriteAsync(context, 200, JsonSerializer.Serialize(new
            {
                id = CompletedItemId,
                name = CompletedItemName,
                size = 1024,
                parentReference = new { path = "/drive/root:/文档" },
                file = new { mimeType = "application/octet-stream" },
            }));
            Record(method, path, null, 0);
            return;
        }

        // ---- 默认：条目元数据（带 downloadUrl 注解）----
        await WriteAsync(context, 200, JsonSerializer.Serialize(new
        {
            id = fileId,
            name = fileName,
            webUrl = $"https://onedrive.live.com/?id={fileId}",
            size = 1024,
            file = new { mimeType = "application/octet-stream" },
            thumbnails = Array.Empty<object>(),
            url = $"{BaseUrl}/thumb/{fileId}",
        }));
        Record(method, path, null, 0);
    }

    private const string fileId = "fake-item-1";
    private const string fileName = "fake.bin";

    private void Record(string method, string path, string? contentRange, int byteCount, bool isPreflight = false)
    {
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(method, path, contentRange, byteCount, isPreflight));
        }
    }

    private static void AddCors(HttpListenerRequest request, HttpListenerContext context)
    {
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin))
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        }
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, PUT, POST, DELETE, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, Content-Range";
        context.Response.Headers["Access-Control-Max-Age"] = "3600";
    }

    private static async Task<byte[]> ReadRawBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    internal static (long Start, long End, long Total) ParseContentRange(string? range)
    {
        // 形如 "bytes 0-1048575/5242880"
        if (string.IsNullOrWhiteSpace(range))
        {
            return (0, 0, 0);
        }

        var value = range.Replace("bytes", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var slash = value.Split('/');
        var span = slash[0].Split('-');
        return (long.Parse(span[0]), long.Parse(span[1]), long.Parse(slash[1]));
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>已收到的分片会话状态（用于核对顺序与断点续传）。</summary>
    private sealed class UploadSessionState
    {
        private readonly MemoryStream _buffer = new();

        public long Received { get; private set; }
        public long Total { get; set; }
        public byte[]? Completed { get; private set; }

        public void Append(byte[] bytes)
        {
            _buffer.Write(bytes, 0, bytes.Length);
            Received += bytes.Length;
            if (Total > 0 && Received >= Total)
            {
                Completed = _buffer.ToArray();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* 已停 */ }
        _listener.Close();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>假 Graph 服务上发生的一次请求。</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    string? ContentRange,
    int ByteCount,
    bool IsPreflight);
