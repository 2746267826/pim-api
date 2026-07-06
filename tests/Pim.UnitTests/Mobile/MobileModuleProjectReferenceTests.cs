using System.Xml.Linq;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileModuleProjectReferenceTests
{
    [Fact]
    public void ApiProject_ReferencesMobileModule()
    {
        var project = XDocument.Load(RepoPath("src", "Pim.Api", "Pim.Api.csproj"));
        var references = ProjectReferences(project);

        Assert.Contains(@"..\modules\Pim.Module.Mobile\Pim.Module.Mobile.csproj", references);
    }

    [Fact]
    public void UnitTestsProject_ReferencesMobileModule()
    {
        var project = XDocument.Load(RepoPath("tests", "Pim.UnitTests", "Pim.UnitTests.csproj"));
        var references = ProjectReferences(project);

        Assert.Contains(@"..\..\src\modules\Pim.Module.Mobile\Pim.Module.Mobile.csproj", references);
    }

    [Fact]
    public void Solution_IncludesMobileModule()
    {
        var solution = File.ReadAllText(RepoPath("Pim.sln"));

        Assert.Contains(@"src\modules\Pim.Module.Mobile\Pim.Module.Mobile.csproj", solution);
    }

    private static IReadOnlyList<string> ProjectReferences(XDocument project)
        => project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => value!.Replace('/', '\\'))
            .ToList();

    private static string RepoPath(params string[] parts)
        => Path.GetFullPath(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
