using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteMaterialManifestValidatorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly NoteMaterialUploadOptions Options = new()
    {
        StorageRoot = Path.GetTempPath(),
        MaxVersionBytes = 32 * 1024 * 1024,
        MaxUserReservedBytes = 64 * 1024 * 1024,
    };

    [Fact]
    public void AcceptsLegacyInkDefaultsAndUnknownExtensionFields()
    {
        var request = BuildRequest(new
        {
            width = 320.0,
            height = 240.0,
            futureRoot = new { keep = true },
            strokes = new[]
            {
                new
                {
                    futureStroke = "keep",
                    points = new[]
                    {
                        new { x = 12.0, y = 18.0, futurePoint = new { keep = true } },
                    },
                },
            },
        });

        var files = NoteMaterialManifestValidator.Validate(request, Options);

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void AcceptsCanonicalInkStylesIdsAndDefaults()
    {
        var strokeId = Guid.NewGuid().ToString("D");
        var request = BuildRequest(new
        {
            width = 320,
            height = 240,
            strokeIds = new[] { strokeId },
            strokeStyles = new[]
            {
                new { color = "#263B85", width = 2.5, futureStyle = new { keep = true } },
            },
            strokes = new[]
            {
                new
                {
                    color = "#263b85",
                    width = 2.5,
                    points = new[] { new { x = 12, y = 18, pressure = 0.4, futurePoint = 1 } },
                },
            },
        });

        var files = NoteMaterialManifestValidator.Validate(request, Options);

        Assert.Equal(3, files.Count);
    }

    public static IEnumerable<object[]> InvalidInk()
    {
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        yield return new object[]
        {
            "misaligned strokeIds",
            new
            {
                width = 320,
                height = 240,
                strokeIds = new[] { first },
                strokes = new[]
                {
                    new { points = new[] { new { x = 1, y = 1 } } },
                    new { points = new[] { new { x = 2, y = 2 } } },
                },
            },
        };
        yield return new object[]
        {
            "duplicate strokeIds",
            new
            {
                width = 320,
                height = 240,
                strokeIds = new[] { first, first.ToUpperInvariant() },
                strokes = new[]
                {
                    new { points = new[] { new { x = 1, y = 1 } } },
                    new { points = new[] { new { x = 2, y = 2 } } },
                },
            },
        };
        yield return new object[]
        {
            "invalid strokeId",
            new
            {
                width = 320,
                height = 240,
                strokeIds = new[] { "not-a-uuid" },
                strokes = new[] { new { points = new[] { new { x = 1, y = 1 } } } },
            },
        };
        yield return new object[]
        {
            "misaligned strokeStyles",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#b8373c", width = 2.0 } },
                strokes = new[]
                {
                    new { points = new[] { new { x = 1, y = 1 } } },
                    new { points = new[] { new { x = 2, y = 2 } } },
                },
            },
        };
        yield return new object[]
        {
            "invalid style color",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#12345G", width = 2.0 } },
                strokes = new[] { new { points = new[] { new { x = 1, y = 1 } } } },
            },
        };
        yield return new object[]
        {
            "inline and style color conflict",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#b8373c", width = 2.0 } },
                strokes = new[] { new { color = "#245fba", width = 2.0, points = new[] { new { x = 1, y = 1 } } } },
            },
        };
        yield return new object[]
        {
            "inline and style width conflict",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#b8373c", width = 2.5 } },
                strokes = new[] { new { color = "#b8373c", width = 2.0, points = new[] { new { x = 1, y = 1 } } } },
            },
        };
        yield return new object[]
        {
            "style width out of range",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#b8373c", width = 25.0 } },
                strokes = new[] { new { points = new[] { new { x = 1, y = 1 } } } },
            },
        };
        yield return new object[]
        {
            "point outside canvas",
            new
            {
                width = 320,
                height = 240,
                strokes = new[] { new { points = new[] { new { x = 321, y = 2 } } } },
            },
        };
        yield return new object[]
        {
            "pressure outside range",
            new
            {
                width = 320,
                height = 240,
                strokes = new[] { new { points = new[] { new { x = 1, y = 1, pressure = 1.5 } } } },
            },
        };
        yield return new object[]
        {
            "missing style width",
            new
            {
                width = 320,
                height = 240,
                strokeStyles = new[] { new { color = "#b8373c" } },
                strokes = new[] { new { points = new[] { new { x = 1, y = 1 } } } },
            },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidInk))]
    public void RejectsInvalidD04Ink(string name, object ink)
    {
        var request = BuildRequest(ink);

        var error = Assert.Throws<DomainException>(() =>
            NoteMaterialManifestValidator.Validate(request, Options));

        Assert.Equal(40001, error.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    private static BeginNoteMaterialUploadRequest BuildRequest(object ink)
    {
        var versionId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var inkJson = JsonSerializer.Serialize(ink, Json);
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = noteId.ToString("D"),
            title = "validator",
            revision = 1,
            createdAtMillis = 1,
            blocks = new object[]
            {
                new { id = blockId.ToString("D"), kind = "ink", ink },
            },
        }, Json);
        var inputJson = $"{{\"schemaVersion\":1,\"id\":\"{versionId:D}\",\"createdAtMillis\":1,\"note\":{noteJson},\"recordings\":[]}}";
        var inkPath = $"assets/{blockId:D}-{NoteMaterialManifestValidator.Hash(inkJson)}.png";
        var markdown = $"![手写卡片]({inkPath})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->";
        var previewBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47 };
        var files = new object[]
        {
            new
            {
                path = "note.json",
                kind = "Document",
                sha256 = NoteMaterialManifestValidator.Hash(noteJson),
                sizeBytes = Encoding.UTF8.GetByteCount(noteJson),
            },
            new
            {
                path = "document.md",
                kind = "Markdown",
                sha256 = NoteMaterialManifestValidator.Hash(markdown),
                sizeBytes = Encoding.UTF8.GetByteCount(markdown),
            },
            new
            {
                path = inkPath,
                kind = "InkPreview",
                sha256 = new string('a', 64),
                sizeBytes = previewBytes.Length,
                blockId = blockId.ToString("D"),
            },
        };
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = NoteMaterialManifestValidator.Hash(inputJson),
            files,
        }, Json);
        return new(versionId, noteId, null, inputJson, manifestJson, true);
    }
}
