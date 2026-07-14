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
    public void FindPlaceholders_ReturnsDistinctNames()
    {
        var names = TokenReplacer.FindPlaceholders("A $id$ and $Version$ and $id$ again.");
        Assert.Equal(new[] { "id", "Version" }, names);
    }

    [Fact]
    public void FindPlaceholders_EmptyOrNone_ReturnsEmpty()
    {
        Assert.Empty(TokenReplacer.FindPlaceholders(""));
        Assert.Empty(TokenReplacer.FindPlaceholders("no tokens here"));
        Assert.Empty(TokenReplacer.FindPlaceholders(null!));
    }

    [Fact]
    public void ProcessPackageReadmeTask_AppliesTokensAfterIncludes()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "Package $id$ v$version$.\n<!-- include part.md -->\n");
            File.WriteAllText(Path.Combine(root, "part.md"), "Included $product$.\n");
            var output = Path.Combine(root, "out", "readme.md");

            var task = new ProcessPackageReadme
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
            // Include openers neutralized so NuGetizer CreatePackage won't re-expand them.
            Assert.DoesNotMatch(@"<!--\s?include\s", content);
            Assert.Contains("include\u200B", content);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProcessPackageReadme_DoesNotWarnOnUnknownToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "Document `$docs-example$` and package $id$.\n");
            var output = Path.Combine(root, "out", "readme.md");
            var engine = new MockBuildEngine();

            var task = new ProcessPackageReadme
            {
                SourceFile = source,
                OutputFile = output,
                BuildEngine = engine,
                ReplacementTokens =
                [
                    new Microsoft.Build.Utilities.TaskItem("Id", new Dictionary<string, string> { ["Value"] = "Pkg" }),
                ],
            };

            Assert.True(task.Execute());
            var content = File.ReadAllText(output);
            Assert.Contains("package Pkg.", content);
            Assert.Contains("$docs-example$", content);
            Assert.DoesNotContain(engine.Warnings, w => w.Code == ReplacePackageTokens.UnknownTokenWarningCode);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void NeutralizeIncludeOpeners_BreaksIncludeRegexButKeepsReadableText()
    {
        var input = "before\n<!-- include foo.md -->\n<!--include bar.md-->\nafter\n";
        var result = ProcessPackageReadme.NeutralizeIncludeOpeners(input);

        Assert.DoesNotMatch(@"<!--\s?include\s", result);
        Assert.Contains("<!-- include\u200B foo.md -->", result);
        Assert.Contains("<!--include\u200B bar.md-->", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void ReplacePackageTokensTask_ReplacesAndWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "eula.txt");
            File.WriteAllText(input, "License for $id$ version $version$.\n");
            var output = Path.Combine(root, "out", "EULA.txt");
            var engine = new MockBuildEngine();

            var task = new ReplacePackageTokens
            {
                InputFile = input,
                OutputFile = output,
                BuildEngine = engine,
                Tokens =
                [
                    new Microsoft.Build.Utilities.TaskItem("Id", new Dictionary<string, string> { ["Value"] = "My.Lib" }),
                    new Microsoft.Build.Utilities.TaskItem("Version", new Dictionary<string, string> { ["Value"] = "2.0.0" }),
                ],
            };

            Assert.True(task.Execute());
            Assert.Equal("License for My.Lib version 2.0.0.\n", File.ReadAllText(output));
            Assert.Empty(engine.Warnings);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReplacePackageTokensTask_WarnsOnUnknownToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "eula.txt");
            File.WriteAllText(input, "Package $id$ and $unknown$ and $unknown$.\n");
            var output = Path.Combine(root, "out", "EULA.txt");
            var engine = new MockBuildEngine();

            var task = new ReplacePackageTokens
            {
                InputFile = input,
                OutputFile = output,
                BuildEngine = engine,
                Tokens =
                [
                    new Microsoft.Build.Utilities.TaskItem("Id", new Dictionary<string, string> { ["Value"] = "Pkg" }),
                ],
            };

            Assert.True(task.Execute());
            Assert.Equal("Package Pkg and $unknown$ and $unknown$.\n", File.ReadAllText(output));
            var unknown = Assert.Single(engine.Warnings);
            Assert.Equal(ReplacePackageTokens.UnknownTokenWarningCode, unknown.Code);
            Assert.Contains("$unknown$", unknown.Message);
            Assert.Contains(input, unknown.Message);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildTargets_WirePackageReplacementTokens()
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

        Assert.Contains("Target Name=\"CollectReplacementTokens\"", targets);
        Assert.Contains("Target Name=\"ProcessPackageReadme\"", targets);
        Assert.Contains("DependsOnTargets=\"CollectReplacementTokens;$(ProcessPackageReadmeDependsOn)\"", targets);
        Assert.Contains("UsingTask TaskName=\"Readme.ProcessPackageReadme\"", targets);
        Assert.Contains("UsingTask TaskName=\"Readme.ReplacePackageTokens\"", targets);
        Assert.Contains("PackageReplacementToken Include=\"Id\"", targets);
        Assert.Contains("PackageReplacementToken Include=\"Version\"", targets);
        Assert.Contains("PackageReplacementToken Include=\"Author\"", targets);
        Assert.Contains("PackageReplacementToken Include=\"Authors\"", targets);
        Assert.Contains("PackageReplacementToken Include=\"Product\"", targets);
        Assert.Contains("ReplacementTokens=\"@(PackageReplacementToken)\"", targets);
    }
}
