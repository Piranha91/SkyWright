using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Each skill the module's plugin ships (claude-plugin/skills/*/SKILL.md) opens with a frontmatter block that
/// names the skill and describes it, the two fields an agent uses to find and choose a skill. houseCARL tested this of
/// its own skills (PluginValidateFrontmatterTests.AShippedSkillFrontmatterParsesWithANameAndADescription); two of those
/// skills now live here, so the subject is tested in the repository that owns the files.</summary>
public class PluginSkillFrontmatterTests
{
    static string Checkout()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SkyWright.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no checkout above the test folder");
    }

    static string Skills() => Path.Combine(Checkout(), "integration", "housecarl", "claude-plugin", "skills");

    public static IEnumerable<object[]> SkillFiles() =>
        Directory.GetFiles(Skills(), "SKILL.md", SearchOption.AllDirectories)
            .Select(f => new object[] { Path.GetRelativePath(Skills(), f).Replace('\\', '/') }).OrderBy(r => (string)r[0], StringComparer.Ordinal);

    [Fact]
    public void The_plugin_ships_its_three_skills()
    {
        var names = SkillFiles().Select(r => (string)r[0]).ToList();
        Assert.Equal(["driving-skywright/SKILL.md", "evaluation-reporting/SKILL.md", "world-edit-compatibility/SKILL.md"], names);
    }

    [Theory]
    [MemberData(nameof(SkillFiles))]
    public void AShippedSkillFrontmatterParsesWithANameAndADescription(string skill)
    {
        var lines = File.ReadAllLines(Path.Combine(Skills(), skill));
        Assert.True(lines.Length > 0 && lines[0].Trim() == "---", $"{skill} does not open with a frontmatter block (---)");
        int end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        Assert.True(end > 0, $"{skill}'s frontmatter block is never closed (---)");
        string? Field(string key)
        {
            var line = lines.Skip(1).Take(end - 1).FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            return line?[(key.Length + 1)..].Trim().Trim('"');
        }
        string folder = skill.Split('/')[0];
        Assert.Equal(folder, Field("name"));
        Assert.False(string.IsNullOrWhiteSpace(Field("description")), $"{skill} has no description");
    }
}
