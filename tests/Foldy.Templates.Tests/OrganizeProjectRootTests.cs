using AwesomeAssertions;
using Foldy.Core.Organize;

namespace Foldy.Templates.Tests;

public class OrganizeProjectRootTests
{
    [Fact]
    public void Project_root_is_blocked_unless_allowed()
    {
        var dir = Directory.CreateTempSubdirectory("foldy-org-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            OrganizeScanner.CheckRoot(dir).Should().Be(OrganizeScanner.ProjectRootReason);
            OrganizeScanner.CheckRoot(dir, allowProject: true).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
