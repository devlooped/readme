using System.IO;
using Xunit;

namespace Readme.Tests;

public class IncludesResolverTests
{
    static string ContentPath(string relative)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Content", relative));

    [Fact]
    public void ResolveIncludes_ExpandsFileFragmentAndNested()
    {
        var content = IncludesResolver.Process(ContentPath("readme.md"));

        Assert.Contains("the-header", content);
        Assert.Contains("the-footer", content);

        Assert.Contains("section#1", content);
        Assert.DoesNotContain("section#2", content);
        Assert.Contains("section#3", content);
        Assert.Contains("@kzu", content);

        // Include markers remain as wrappers around expanded content
        Assert.Contains("<!-- include Common/header.md -->", content);
        Assert.Contains("<!-- Common/header.md -->", content);
    }

    [Fact]
    public void ResolveUrlInclude()
    {
        var content = IncludesResolver.Process(ContentPath("url.md"));

        Assert.Contains("Daniel Cazzulino", content);
        Assert.Contains("Sponsors", content);
    }

    [Fact]
    public void ResolveNonExistingInclude_ReportsWarning()
    {
        var path = Path.GetTempFileName();
        try
        {
            var include = "<!-- include foo.md#bar -->";
            File.WriteAllText(path, include);

            string? failed = default;
            var content = IncludesResolver.Process(path, s => failed = s);

            Assert.NotNull(failed);
            Assert.Contains("foo.md#bar", failed);
            // Unresolved include left in place (warning, not hard failure)
            Assert.Contains("<!-- include foo.md#bar -->", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessReadmeIncludesTask_WritesProcessedFile()
    {
        var output = Path.Combine(Path.GetTempPath(), "readme-tests", Guid.NewGuid().ToString("N"), "readme.md");
        try
        {
            var task = new ProcessReadmeIncludes
            {
                SourceFile = ContentPath("readme.md"),
                OutputFile = output,
                BuildEngine = new MockBuildEngine(),
            };

            Assert.True(task.Execute());
            Assert.True(File.Exists(output));

            var content = File.ReadAllText(output);
            Assert.Contains("the-header", content);
            Assert.Contains("section#1", content);
            Assert.DoesNotContain("section#2", content);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
            var dir = Path.GetDirectoryName(output);
            if (dir != null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MultiLevelNestedIncludes_ExpandAllBodies()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.md", "A-start\n<!-- include b.md -->\nA-end\n");
        dir.Write("b.md", "B-start\n<!-- include c.md -->\nB-end\n");
        dir.Write("c.md", "C-body\n");

        var content = IncludesResolver.Process(a);

        Assert.Contains("A-start", content);
        Assert.Contains("B-start", content);
        Assert.Contains("C-body", content);
        Assert.Contains("B-end", content);
        Assert.Contains("A-end", content);
        Assert.Contains("<!-- include b.md -->", content);
        Assert.Contains("<!-- include c.md -->", content);
    }

    [Fact]
    public void MultiLevelNestedIncludes_WithFragment_ExpandsFragmentOnly()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.md", "<!-- include b.md#mid -->\n");
        dir.Write("b.md", "B-outer\n<!-- include c.md -->\n");
        dir.Write("c.md", "before\n<!-- #mid -->\nmiddle\n<!-- #mid -->\nafter\n");

        // a includes b#mid, but mid lives in c which b includes — fragment is applied to b's fully expanded content
        var content = IncludesResolver.Process(a);

        Assert.Contains("middle", content);
        // Fragment slice starts at the anchor inside expanded b (from c)
        Assert.Contains("<!-- #mid -->", content);
    }

    [Fact]
    public void SelfInclude_WarnsAndLeavesMarker()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.md", "intro\n<!-- include a.md -->\noutro\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(a, w => warnings.Add(w));

        Assert.Contains("intro", content);
        Assert.Contains("outro", content);
        Assert.Contains("<!-- include a.md -->", content);
        // Must not wrap as expanded (no closing marker from successful expand of a.md)
        Assert.DoesNotContain("<!-- a.md -->", content);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("Circular", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("a.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MutualInclude_A_B_WarnsAndLeavesCyclicMarker()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.md", "A-body\n<!-- include b.md -->\n");
        dir.Write("b.md", "B-body\n<!-- include a.md -->\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(a, w => warnings.Add(w));

        Assert.Contains("A-body", content);
        Assert.Contains("B-body", content);
        // Cyclic edge from B back to A left as unresolved include marker
        Assert.Contains("<!-- include a.md -->", content);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("Circular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LongerCycle_A_B_C_A_WarnsWithoutThrowing()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.md", "A\n<!-- include b.md -->\n");
        dir.Write("b.md", "B\n<!-- include c.md -->\n");
        dir.Write("c.md", "C\n<!-- include a.md -->\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(a, w => warnings.Add(w));

        Assert.Contains("A", content);
        Assert.Contains("B", content);
        Assert.Contains("C", content);
        Assert.Contains("<!-- include a.md -->", content);
        Assert.Contains(warnings, w => w.Contains("Circular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiamondInclude_DoesNotWarnAsCycle_AndExpandsSharedLeaf()
    {
        // A → B, A → C, B → D, C → D  (D may expand twice; not a cycle)
        using var dir = new TempDir();
        var a = dir.Write("a.md", "A\n<!-- include b.md -->\n<!-- include c.md -->\n");
        dir.Write("b.md", "B\n<!-- include d.md -->\n");
        dir.Write("c.md", "C\n<!-- include d.md -->\n");
        dir.Write("d.md", "D-shared\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(a, w => warnings.Add(w));

        Assert.Contains("A", content);
        Assert.Contains("B", content);
        Assert.Contains("C", content);
        Assert.Equal(2, CountOccurrences(content, "D-shared"));
        Assert.DoesNotContain(warnings, w => w.Contains("Circular", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(warnings);
    }

    [Fact]
    public void CycleViaNormalizedRelativePath_IsDetected()
    {
        // a.md includes sub/b.md which includes ../a.md — same file by canonical path
        using var dir = new TempDir();
        var a = dir.Write("a.md", "root\n<!-- include sub/b.md -->\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        dir.Write(Path.Combine("sub", "b.md"), "nested\n<!-- include ../a.md -->\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(a, w => warnings.Add(w));

        Assert.Contains("root", content);
        Assert.Contains("nested", content);
        Assert.Contains("<!-- include ../a.md -->", content);
        Assert.Contains(warnings, w =>
            w.Contains("Circular", StringComparison.OrdinalIgnoreCase) &&
            w.Contains("a.md", StringComparison.OrdinalIgnoreCase));
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Ephemeral directory of markdown fixtures for hermetic include-graph tests.</summary>
    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "readme-include-tests", Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Write(string relative, string contents)
        {
            var full = System.IO.Path.Combine(Path, relative);
            var parent = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(full, contents);
            return full;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
