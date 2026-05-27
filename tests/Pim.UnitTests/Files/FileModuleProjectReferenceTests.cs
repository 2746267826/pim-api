using System.Xml.Linq;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileModuleProjectReferenceTests
{
    [Fact]
    public void ApiProject_ReferencesFilesModule()
    {
        var apiProjectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Pim.Api",
            "Pim.Api.csproj"));

        var project = XDocument.Load(apiProjectPath);
        var references = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => value!.Replace('/', '\\'))
            .ToList();

        Assert.Contains(@"..\modules\Pim.Module.Files\Pim.Module.Files.csproj", references);
    }
}
