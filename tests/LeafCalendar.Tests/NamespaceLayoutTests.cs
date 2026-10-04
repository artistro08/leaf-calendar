using System.Text.RegularExpressions;

namespace LeafCalendar.Tests;

/// <summary>
/// Checks that every C# file's namespace is its project's root namespace plus its folder path, the .NET
/// equivalent of PHP's PSR-4. The IDE0130 analyzer misses partial classes (every XAML code-behind), so this
/// test reads the files directly.
/// </summary>
public partial class NamespaceLayoutTests
{
    [GeneratedRegex(@"^namespace\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex FileScopedNamespace();

    public static TheoryData<string> Projects =>
    [
        "src/LeafCalendar.Core",
        "src/LeafCalendar.App",
        "tests/LeafCalendar.Tests",
        "tests/LeafCalendar.UITests",
        "tests/LeafCalendar.LiveTests",
    ];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Namespaces_MatchFolders(string project)
    {
        var folder = Path.Combine(FindRepoRoot(), project);
        var rootName = Path.GetFileName(project);
        var mismatches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(relative) ?? "";
            var expected = directory.Length == 0 ? rootName : rootName + "." + directory.Replace(Path.DirectorySeparatorChar, '.');
            var match = FileScopedNamespace().Match(File.ReadAllText(file));
            if (!match.Success || match.Groups[1].Value != expected)
            {
                mismatches.Add($"{relative}: {(match.Success ? match.Groups[1].Value : "no file-scoped namespace")} (expected {expected})");
            }
        }

        Assert.Empty(mismatches);
    }

    // Walks up from the test binaries to the folder holding the solution file
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeafCalendar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("LeafCalendar.slnx not found above the test binaries.");
    }
}
