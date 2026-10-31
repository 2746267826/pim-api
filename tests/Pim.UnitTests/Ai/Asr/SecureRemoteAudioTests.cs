using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai.Asr;
using Xunit;

namespace Pim.UnitTests.Ai.Asr;

public sealed class SecureRemoteAudioTests
{
    private static readonly byte[] Wav =
        ["R"u8[0], "I"u8[0], "F"u8[0], "F"u8[0], 36, 0, 0, 0, "W"u8[0], "A"u8[0], "V"u8[0], "E"u8[0]];

    [Fact]
    public async Task Fetch_ValidatesAllDnsAnswersBeforeCreatingTransport()
    {
        var resolver = new FakeResolver([IPAddress.Parse("1.1.1.1"), IPAddress.Parse("10.0.0.2")]);
        var clients = new FakeClients(_ => Ok(Wav, "audio/wav"));
        var fetcher = Create(resolver, clients);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(new Uri("https://audio.example.test/signed?token=secret"), Expect(), default));

        Assert.Equal(40302, error.ErrorCode);
        Assert.Equal(0, clients.Calls);
        Assert.DoesNotContain("token=secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_RejectsPrivateIpv4MappedAddressInMixedDnsAnswers()
    {
        var resolver = new FakeResolver([
            IPAddress.Parse("1.1.1.1"),
            IPAddress.Parse("::ffff:10.1.2.3")
        ]);
        var clients = new FakeClients(_ => Ok(Wav, "audio/wav"));
        var fetcher = Create(resolver, clients);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));

        Assert.Equal(40302, error.ErrorCode);
        Assert.Equal(0, clients.Calls);
    }

    [Fact]
    public async Task Fetch_RejectsOversizeAndCrLfUrlBeforeDns()
    {
        var resolver = new FakeResolver([IPAddress.Parse("1.1.1.1")]);
        var clients = new FakeClients(_ => Ok(Wav, "audio/wav"));
        var fetcher = Create(resolver, clients);
        var tooLong = new Uri("https://audio.example.test/" +
                              new string('a', SecureRemoteAudioFetcher.MaxUrlChars));

        var lengthError = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(tooLong, Expect(), default));
        Assert.Equal(40001, lengthError.ErrorCode);

        var encoded = new Uri("https://audio.example.test/a.wav?next=%0d%0aInjected");
        var controlError = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(encoded, Expect(), default));
        Assert.Equal(40001, controlError.ErrorCode);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, clients.Calls);
    }

    [Theory]
    [InlineData("http://audio.example.test/a.wav")]
    [InlineData("https://user:pass@audio.example.test/a.wav")]
    [InlineData("https://audio.example.test/a.wav#fragment")]
    [InlineData("https://localhost/a.wav")]
    [InlineData("https://printer.local/a.wav")]
    [InlineData("https://service.internal/a.wav")]
    public async Task Fetch_RejectsUnsafeUrlWithoutDnsOrHttp(string value)
    {
        var resolver = new FakeResolver([IPAddress.Parse("1.1.1.1")]);
        var clients = new FakeClients(_ => Ok(Wav, "audio/wav"));
        var fetcher = Create(resolver, clients);
        await Assert.ThrowsAsync<DomainException>(() => fetcher.FetchAsync(new Uri(value), Expect(), default));
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, clients.Calls);
    }

    [Fact]
    public async Task Fetch_UsesApprovedAddressesAndNeverReResolves()
    {
        var approved = new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };
        var resolver = new FakeResolver(approved);
        var clients = new FakeClients(_ => Ok(Wav, "audio/wav"));
        var fetcher = Create(resolver, clients);

        var result = await fetcher.FetchAsync(new Uri("https://audio.example.test/private/path?signature=secret"),
            Expect(), default);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(approved, clients.Approved);
        Assert.Equal("https://audio.example.test", result.Provenance.Origin);
        Assert.DoesNotContain("private/path", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("signature=secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_DoesNotSendCredentialsCookiesProxyOrFollowRedirects()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.ProxyAuthorization);
            if (request.Method == HttpMethod.Head) return Ok(Wav, "audio/wav");
            Assert.Equal(HttpMethod.Get, request.Method);
            return Ok(Wav, "audio/wav");
        });
        var fetcher = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(handler));

        await fetcher.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default);

        var redirectFetcher = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect))));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            redirectFetcher.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(50204, error.ErrorCode);
    }

    [Fact]
    public async Task Fetch_AcceptsHeadMethodNotAllowedAndUsesGetMetadata()
    {
        var handler = new ScriptedHandler((request, _) => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : Ok(Wav, "audio/wav"));
        var result = await Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(handler)).FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default);
        Assert.Equal("audio/wav", result.Provenance.ContentType);
        Assert.Equal(Wav, result.Bytes.ToArray());
    }

    [Fact]
    public async Task Fetch_EnforcesStreamingLimitEvenWhenContentLengthLies()
    {
        var oversized = new byte[33];
        Wav.CopyTo(oversized, 0);
        var handler = new ScriptedHandler((request, _) =>
        {
            var response = Ok(oversized, "audio/wav");
            response.Content.Headers.ContentLength = 1;
            return response;
        });
        var fetcher = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(handler), new RemoteAudioFetchLimits { MaxBytes = 32 });

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(41301, error.ErrorCode);
    }

    [Fact]
    public async Task Fetch_RejectsDeclaredOversizeAndWrongContentType()
    {
        var oversized = new ScriptedHandler((_, _) =>
        {
            var response = Ok(Wav, "audio/wav");
            response.Content.Headers.ContentLength = 100;
            return response;
        });
        var fetcher = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(oversized), new RemoteAudioFetchLimits { MaxBytes = 32 });
        var tooLarge = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(41301, tooLarge.ErrorCode);

        var wrongType = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(_ => Ok(Wav, "text/html")));
        var wrong = await Assert.ThrowsAsync<DomainException>(() =>
            wrongType.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(40001, wrong.ErrorCode);
    }

    [Fact]
    public async Task Fetch_RequiresDeclaredFormatSignatureAndOptionalDigest()
    {
        var mismatch = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(_ => Ok(Encoding.UTF8.GetBytes("not audio"), "audio/wav")));
        var formatError = await Assert.ThrowsAsync<DomainException>(() =>
            mismatch.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(40001, formatError.ErrorCode);

        var digest = Convert.ToHexString(SHA256.HashData(Wav)).ToLowerInvariant();
        var digestError = await Assert.ThrowsAsync<DomainException>(() =>
            mismatch.FetchAsync(new Uri("https://audio.example.test/a.wav"),
                new RemoteAudioExpectation("wav", digest), default));
        Assert.Equal(40001, digestError.ErrorCode);

        var good = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(_ => Ok(Wav, "audio/wav")));
        var result = await good.FetchAsync(new Uri("https://audio.example.test/a.wav"),
            new RemoteAudioExpectation("wav", digest), default);
        Assert.Equal(digest, result.Provenance.Sha256);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("read")]
    [InlineData("dispose")]
    public async Task Fetch_SanitizesBodyStreamAndDisposeTransportFailures(string failure)
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Ok(Wav, "audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = failure switch
                {
                    "stream" => new StreamFailureContent(),
                    "read" => new ReadFailureContent(),
                    _ => new DisposeFailureContent(Wav)
                }
            };
        });
        var fetcher = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(handler));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fetcher.FetchAsync(new Uri("https://audio.example.test/private?signature=secret"),
                Expect(), default));

        Assert.Equal(50206, error.ErrorCode);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("signature=secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_EnforcesDnsHeadAndGetTimeouts()
    {
        var dns = Create(new DelayedResolver(), new FakeClients(_ => Ok(Wav, "audio/wav")),
            new RemoteAudioFetchLimits { DnsTimeout = TimeSpan.FromMilliseconds(20) });
        var dnsError = await Assert.ThrowsAsync<DomainException>(() =>
            dns.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(50401, dnsError.ErrorCode);

        var head = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(new DelayedHandler()),
            new RemoteAudioFetchLimits { HeadTimeout = TimeSpan.FromMilliseconds(20) });
        var headError = await Assert.ThrowsAsync<DomainException>(() =>
            head.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(50401, headError.ErrorCode);

        var get = Create(new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(new ScriptedHandler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Head) return Ok(Wav, "audio/wav");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Ok(Wav, "audio/wav");
            })), new RemoteAudioFetchLimits { GetTimeout = TimeSpan.FromMilliseconds(20) });
        var getError = await Assert.ThrowsAsync<DomainException>(() =>
            get.FetchAsync(new Uri("https://audio.example.test/a.wav"), Expect(), default));
        Assert.Equal(50401, getError.ErrorCode);
    }

    [Fact]
    public async Task ConnectPinned_TriesOnlyApprovedAddresses()
    {
        var approved = new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };
        var attempted = new List<IPEndPoint>();
        await using var expected = new MemoryStream(Wav);
        var stream = await SecureRemoteAudioFetcher.ConnectPinnedAsync(
            new DnsEndPoint("audio.example.test", 443), approved, (endpoint, _) =>
            {
                attempted.Add(endpoint);
                return endpoint.Address.Equals(approved[0])
                    ? ValueTask.FromException<Stream>(new SocketException())
                    : ValueTask.FromResult<Stream>(expected);
            }, default);
        Assert.Same(expected, stream);
        Assert.Equal(approved, attempted.Select(endpoint => endpoint.Address));
        Assert.All(attempted, endpoint => Assert.Equal(443, endpoint.Port));
    }

    [Fact]
    public void PinnedFactory_DisablesProxyCookiesCredentialsAndRedirects()
    {
        using var handler = PinnedRemoteAudioHttpClientFactory.CreateHandler([IPAddress.Parse("1.1.1.1")]);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2002:7f00:0001::", false)]
    [InlineData("2001:0000:4136:e378:8000:63bf:3fff:fdd2", false)]
    public void PublicAddressPolicy_RejectsSpecialUseRanges(string value, bool expected) =>
        Assert.Equal(expected, SecureRemoteAudioFetcher.IsPublicAddress(IPAddress.Parse(value)));

    [Fact]
    public void Limits_RejectMoreThan100MiB()
    {
        var error = Assert.Throws<DomainException>(() => Create(
            new FakeResolver([IPAddress.Parse("1.1.1.1")]),
            new FakeClients(_ => Ok(Wav, "audio/wav")),
            new RemoteAudioFetchLimits { MaxBytes = RemoteAudioFetchLimits.HardMaxBytes + 1 }));
        Assert.Equal(40001, error.ErrorCode);
    }

    [Fact]
    public void Limits_RejectUnboundedTimeouts()
    {
        AssertRejected(new RemoteAudioFetchLimits
        {
            DnsTimeout = RemoteAudioFetchLimits.HardMaxDnsTimeout + TimeSpan.FromSeconds(1)
        });
        AssertRejected(new RemoteAudioFetchLimits
        {
            HeadTimeout = RemoteAudioFetchLimits.HardMaxHeadTimeout + TimeSpan.FromSeconds(1)
        });
        AssertRejected(new RemoteAudioFetchLimits
        {
            GetTimeout = RemoteAudioFetchLimits.HardMaxGetTimeout + TimeSpan.FromSeconds(1)
        });
        return;

        void AssertRejected(RemoteAudioFetchLimits limits)
        {
            var error = Assert.Throws<DomainException>(() => Create(
                new FakeResolver([IPAddress.Parse("1.1.1.1")]),
                new FakeClients(_ => Ok(Wav, "audio/wav")), limits));
            Assert.Equal(40001, error.ErrorCode);
        }
    }

    private static SecureRemoteAudioFetcher Create(IRemoteAudioDnsResolver resolver,
        IRemoteAudioHttpClientFactory clients, RemoteAudioFetchLimits? limits = null) =>
        new(resolver, clients, limits ?? RemoteAudioFetchLimits.Default,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-10-31T10:00:00Z")));

    private static RemoteAudioExpectation Expect() => new("wav");

    private static HttpResponseMessage Ok(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private sealed class FakeResolver(IPAddress[] addresses) : IRemoteAudioDnsResolver
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
        }
    }

    private sealed class DelayedResolver : IRemoteAudioDnsResolver
    {
        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return [IPAddress.Parse("1.1.1.1")];
        }
    }

    private sealed class FakeClients : IRemoteAudioHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeClients(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this(new ScriptedHandler((request, _) => response(request)))
        {
        }
        public FakeClients(HttpMessageHandler handler) => _handler = handler;
        public int Calls { get; private set; }
        public IPAddress[] Approved { get; private set; } = [];
        public HttpClient Create(IReadOnlyList<IPAddress> approved, DnsEndPoint endpoint)
        {
            Calls++;
            Approved = approved.ToArray();
            return new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> asyncResponse)
            : this((request, ct) => asyncResponse(request, ct).GetAwaiter().GetResult())
        {
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Ok(Wav, "audio/wav");
        }
    }

    private sealed class StreamFailureContent : HttpContent
    {
        public StreamFailureContent()
        {
            Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/wav");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(new IOException("synthetic stream open failure"));
    }

    private sealed class ReadFailureContent : HttpContent
    {
        public ReadFailureContent()
        {
            Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/wav");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ReadFailureStream());
    }

    private sealed class DisposeFailureContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("synthetic response dispose failure");
        }
    }

    private sealed class ReadFailureStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic body read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(new IOException("synthetic body read failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
