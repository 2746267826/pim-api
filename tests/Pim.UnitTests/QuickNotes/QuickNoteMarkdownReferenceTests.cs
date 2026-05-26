using Pim.Module.QuickNotes.Services;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNoteMarkdownReferenceTests
{
    [Fact]
    public void ExtractAttachmentIds_ReturnsIdsFromImageAndFileLinks()
    {
        var imageId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var fileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var markdown = $"""
        ![shot](/api/v1/quick-notes/attachments/{imageId}/download)
        [proposal.pdf](/api/v1/quick-notes/attachments/{fileId}/download)
        """;

        var ids = QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown);

        Assert.Equal(new[] { imageId, fileId }, ids);
    }

    [Fact]
    public void ExtractAttachmentIds_IgnoresDuplicatesAndInvalidUrls()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var markdown = $"""
        ![a](/api/v1/quick-notes/attachments/{id}/download)
        [same](/api/v1/quick-notes/attachments/{id}/download)
        [external](https://example.com/file.pdf)
        [bad](/api/v1/quick-notes/attachments/not-a-guid/download)
        """;

        var ids = QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown);

        var single = Assert.Single(ids);
        Assert.Equal(id, single);
    }

    [Fact]
    public void ExtractAttachmentIds_IgnoresExternalAbsoluteUrlsWithLocalAttachmentPath()
    {
        var externalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var localId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var markdown = $"""
        [external](https://example.com/api/v1/quick-notes/attachments/{externalId}/download)
        [local](/api/v1/quick-notes/attachments/{localId}/download)
        """;

        var ids = QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown);

        var single = Assert.Single(ids);
        Assert.Equal(localId, single);
    }

    [Fact]
    public void ExtractAttachmentIds_IgnoresUrlsWithDownloadSuffixes()
    {
        var invalidId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var localId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var markdown = $"""
        [invalid](/api/v1/quick-notes/attachments/{invalidId}/download-extra)
        [local](/api/v1/quick-notes/attachments/{localId}/download)
        """;

        var ids = QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown);

        var single = Assert.Single(ids);
        Assert.Equal(localId, single);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractAttachmentIds_ReturnsEmptyForBlankMarkdown(string? markdown)
    {
        var ids = QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown);

        Assert.Empty(ids);
    }
}
