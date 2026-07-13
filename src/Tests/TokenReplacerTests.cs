using Xunit;

namespace Readme.Tests;

public class TokenReplacerTests
{
    [Fact]
    public void Replace_SubstitutesKnownTokens_CaseInsensitive()
    {
        var result = TokenReplacer.Replace(
            "Package $ID$ v$Version$ by $authors$.",
            [
                ("Id", "My.Package"),
                ("Version", "1.2.3"),
                ("Authors", "Alice"),
            ]);

        Assert.Equal("Package My.Package v1.2.3 by Alice.", result);
    }

    [Fact]
    public void Replace_LeavesUnknownTokens()
    {
        var result = TokenReplacer.Replace(
            "Hello $id$ and $unknown$.",
            [("Id", "pkg")]);

        Assert.Equal("Hello pkg and $unknown$.", result);
    }

    [Fact]
    public void Replace_EmptyTokens_ReturnsOriginal()
    {
        var source = "Package $id$.";
        Assert.Equal(source, TokenReplacer.Replace(source, []));
    }

    [Fact]
    public void Replace_NullOrEmptyText_ReturnsInput()
    {
        Assert.Equal("", TokenReplacer.Replace("", [("Id", "x")]));
        Assert.Null(TokenReplacer.Replace(null!, [("Id", "x")]));
    }

    [Fact]
    public void Replace_DuplicateNames_LastValueWins()
    {
        var result = TokenReplacer.Replace(
            "$id$",
            [("Id", "first"), ("id", "second")]);

        Assert.Equal("second", result);
    }

    [Fact]
    public void Replace_DoesNotTouchBareDollarOrPartial()
    {
        var result = TokenReplacer.Replace(
            "$ id $ $id $id$ $id",
            [("Id", "X")]);

        Assert.Equal("$ id $ $id X $id", result);
    }

    [Fact]
    public void ProcessReadmeIncludesTask_AppliesTokensAfterIncludes()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "Package $id$ v$version$.\n<!-- include part.md -->\n");
            File.WriteAllText(Path.Combine(root, "part.md"), "Included $product$.\n");
            var output = Path.Combine(root, "out", "readme.md");

            var task = new ProcessReadmeIncludes
            {
                SourceFile = source,
                OutputFile = output,
                BuildEngine = new MockBuildEngine(),
                ReplacementTokens =
                [
                    new Microsoft.Build.Utilities.TaskItem("Id", new Dictionary<string, string> { ["Value"] = "Tok.Pkg" }),
                    new Microsoft.Build.Utilities.TaskItem("Version", new Dictionary<string, string> { ["Value"] = "9.9.9" }),
                    new Microsoft.Build.Utilities.TaskItem("Product", new Dictionary<string, string> { ["Value"] = "Widget" }),
                ],
            };

            Assert.True(task.Execute());
            var content = File.ReadAllText(output);
            Assert.Contains("Package Tok.Pkg v9.9.9.", content);
            Assert.Contains("Included Widget.", content);
            Assert.DoesNotContain("$id$", content);
            Assert.DoesNotContain("$product$", content);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildTargets_WireReadmeReplacementTokens()
    {
        var targetsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Readme", "build", "Readme.targets"));

        var alt = Path.GetFullPath(Path.Combine(
            typeof(TokenReplacer).Assembly.Location, "..", "..", "..", "..", "build", "Readme.targets"));
        if (!File.Exists(targetsPath) && File.Exists(alt))
            targetsPath = alt;

        if (!File.Exists(targetsPath))
        {
            var walk = new DirectoryInfo(AppContext.BaseDirectory);
            while (walk != null)
            {
                var candidate = Path.Combine(walk.FullName, "src", "Readme", "build", "Readme.targets");
                if (File.Exists(candidate))
                {
                    targetsPath = candidate;
                    break;
                }
                walk = walk.Parent;
            }
        }

        Assert.True(File.Exists(targetsPath), $"Readme.targets not found: {targetsPath}");
        var targets = File.ReadAllText(targetsPath);

        Assert.Contains("ReadmeReplacementToken Include=\"Id\"", targets);
        Assert.Contains("ReadmeReplacementToken Include=\"Version\"", targets);
        Assert.Contains("ReadmeReplacementToken Include=\"Author\"", targets);
        Assert.Contains("ReadmeReplacementToken Include=\"Authors\"", targets);
        Assert.Contains("ReadmeReplacementToken Include=\"Product\"", targets);
        Assert.Contains("ReplacementTokens=\"@(ReadmeReplacementToken)\"", targets);
    }
}
