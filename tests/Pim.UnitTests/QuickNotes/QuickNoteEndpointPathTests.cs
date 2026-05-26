using Pim.Module.QuickNotes;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNoteEndpointPathTests
{
    [Fact]
    public void QuickNoteEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/quick-notes", QuickNoteEndpointPaths.Root);
        Assert.Equal("/api/v1/quick-notes/11111111-1111-1111-1111-111111111111", QuickNoteEndpointPaths.Note("11111111-1111-1111-1111-111111111111"));
        Assert.Equal("/api/v1/quick-notes/attachments", QuickNoteEndpointPaths.Attachments);
        Assert.Equal("/api/v1/quick-notes/attachments/22222222-2222-2222-2222-222222222222/download", QuickNoteEndpointPaths.AttachmentDownload("22222222-2222-2222-2222-222222222222"));
    }
}
