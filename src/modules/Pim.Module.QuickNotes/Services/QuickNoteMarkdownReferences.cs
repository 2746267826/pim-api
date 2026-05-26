using System.Text.RegularExpressions;

namespace Pim.Module.QuickNotes.Services;

public static partial class QuickNoteMarkdownReferences
{
    public static IReadOnlyList<Guid> ExtractAttachmentIds(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Array.Empty<Guid>();

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (Match match in AttachmentUrlRegex().Matches(markdown))
        {
            if (!Guid.TryParse(match.Groups["id"].Value, out var id))
                continue;

            if (seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }

    [GeneratedRegex(@"(?:^|[\s(])(?<url>/api/v1/quick-notes/attachments/(?<id>[0-9a-fA-F-]{36})/download)(?=$|[\s)])", RegexOptions.Compiled)]
    private static partial Regex AttachmentUrlRegex();
}
