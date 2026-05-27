using System.Globalization;
using System.Xml.Linq;
using Pim.Core.Exceptions;

namespace Pim.Module.Files.Providers;

public static class NextcloudDavXmlParser
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace OwnCloud = "http://owncloud.org/ns";
    private static readonly XNamespace Nextcloud = "http://nextcloud.org/ns";

    public static IReadOnlyList<ProviderFileItem> ParseItems(string xml, string hrefPrefix, string requestedPath)
    {
        var responses = ResponseProperties(xml)
            .Select(response => ParseItem(response.Href, response.Prop, hrefPrefix))
            .ToList();
        var byPath = responses.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);

        return responses
            .Select(item => item with { ParentExternalFileId = ParentFileId(item.Path, byPath) })
            .ToList();
    }

    public static IReadOnlyList<ProviderTrashItem> ParseTrashItems(string xml, string hrefPrefix)
        => ResponseProperties(xml)
            .Select(response => ParseTrashItem(response.Href, response.Prop, hrefPrefix))
            .Where(item => item is not null)
            .Cast<ProviderTrashItem>()
            .ToList();

    public static IReadOnlyList<ProviderFileVersion> ParseVersions(string xml, string hrefPrefix)
        => ResponseProperties(xml)
            .Select(response => ParseVersion(response.Href, response.Prop, hrefPrefix))
            .Where(version => version is not null)
            .Cast<ProviderFileVersion>()
            .OrderByDescending(version => version.ModifiedAt)
            .ToList();

    private static ProviderFileItem ParseItem(string href, XElement prop, string hrefPrefix)
    {
        var path = NormalizePath(RemovePrefix(DecodeHref(href), hrefPrefix));
        var fileId = ElementValue(prop, OwnCloud + "fileid");
        if (string.IsNullOrWhiteSpace(fileId))
            throw new DomainException(5201, "Nextcloud response did not include a file id");

        var resourcetype = prop.Element(Dav + "resourcetype");
        var itemType = resourcetype?.Element(Dav + "collection") is null ? "file" : "folder";
        var modifiedAt = ParseHttpDate(ElementValue(prop, Dav + "getlastmodified"));

        return new ProviderFileItem(
            fileId,
            null,
            path,
            NameFromPath(path),
            itemType,
            ElementValue(prop, Dav + "getcontenttype"),
            ParseLong(ElementValue(prop, Dav + "getcontentlength")),
            ElementValue(prop, Dav + "getetag"),
            ElementValue(prop, OwnCloud + "permissions"),
            modifiedAt);
    }

    private static ProviderTrashItem? ParseTrashItem(string href, XElement prop, string hrefPrefix)
    {
        var decodedHref = DecodeHref(href);
        var trashId = NormalizePath(RemovePrefix(decodedHref, hrefPrefix)).Trim('/');
        if (string.IsNullOrWhiteSpace(trashId))
            return null;

        var name = ElementValue(prop, OwnCloud + "trashbin-filename")
            ?? ElementValue(prop, Nextcloud + "trashbin-filename")
            ?? Path.GetFileName(trashId);
        var originalLocation = ElementValue(prop, OwnCloud + "trashbin-original-location")
            ?? ElementValue(prop, Nextcloud + "trashbin-original-location")
            ?? "/";
        var deletionTime = ElementValue(prop, OwnCloud + "trashbin-deletion-time")
            ?? ElementValue(prop, Nextcloud + "trashbin-deletion-time");
        var resourcetype = prop.Element(Dav + "resourcetype");
        var itemType = resourcetype?.Element(Dav + "collection") is null ? "file" : "folder";

        return new ProviderTrashItem(
            trashId,
            NormalizePath(originalLocation),
            name,
            itemType,
            ParseLong(ElementValue(prop, Dav + "getcontentlength")),
            ParseUnixTime(deletionTime));
    }

    private static ProviderFileVersion? ParseVersion(string href, XElement prop, string hrefPrefix)
    {
        var decodedHref = DecodeHref(href);
        var externalVersionId = NormalizePath(RemovePrefix(decodedHref, hrefPrefix)).Trim('/');
        if (string.IsNullOrWhiteSpace(externalVersionId))
            return null;

        return new ProviderFileVersion(
            externalVersionId,
            ElementValue(prop, Dav + "getetag"),
            ParseLong(ElementValue(prop, Dav + "getcontentlength")),
            ParseHttpDate(ElementValue(prop, Dav + "getlastmodified")),
            "nextcloud",
            false);
    }

    private static IEnumerable<(string Href, XElement Prop)> ResponseProperties(string xml)
    {
        var document = XDocument.Parse(xml);
        return document
            .Descendants(Dav + "response")
            .Select(response => (
                Href: ElementValue(response, Dav + "href") ?? string.Empty,
                Prop: SelectPropstat(response)?.Element(Dav + "prop")))
            .Where(response => !string.IsNullOrWhiteSpace(response.Href) && response.Prop is not null)
            .Select(response => (response.Href, response.Prop!));
    }

    private static XElement? SelectPropstat(XElement response)
    {
        var propstats = response.Elements(Dav + "propstat").ToList();
        return propstats.FirstOrDefault(IsSuccessfulPropstat)
            ?? propstats.FirstOrDefault(propstat => ElementValue(propstat, Dav + "status") is null)
            ?? propstats.FirstOrDefault();
    }

    private static bool IsSuccessfulPropstat(XElement propstat)
    {
        var status = ElementValue(propstat, Dav + "status");
        if (status is null)
            return false;

        if (status.Contains(" 200 ", StringComparison.Ordinal))
            return true;

        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Length == 3
            && part[0] == '2'
            && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    private static string DecodeHref(string href)
        => Uri.UnescapeDataString(href);

    private static string RemovePrefix(string href, string hrefPrefix)
    {
        var normalizedPrefix = hrefPrefix.TrimEnd('/');
        return href.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            ? href[normalizedPrefix.Length..]
            : href;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return "/";

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = $"/{normalized}";

        return normalized.TrimEnd('/');
    }

    private static string NameFromPath(string path)
    {
        if (path == "/")
            return "/";

        var slashIndex = path.LastIndexOf('/');
        return slashIndex >= 0 ? path[(slashIndex + 1)..] : path;
    }

    private static string? ParentFileId(
        string path,
        IReadOnlyDictionary<string, ProviderFileItem> byPath)
    {
        var parentPath = ParentPath(path);
        return parentPath is null || !byPath.TryGetValue(parentPath, out var parent)
            ? null
            : parent.ExternalFileId;
    }

    private static string? ParentPath(string path)
    {
        if (path == "/")
            return null;

        var slashIndex = path.LastIndexOf('/');
        return slashIndex <= 0 ? "/" : path[..slashIndex];
    }

    private static string? ElementValue(XElement element, XName name)
    {
        var value = element.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset ParseHttpDate(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset ParseUnixTime(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;
}
