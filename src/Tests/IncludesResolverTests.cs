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
}
