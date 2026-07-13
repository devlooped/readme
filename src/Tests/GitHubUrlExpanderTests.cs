using Xunit;

namespace Readme.Tests;

public class GitHubUrlExpanderTests
{
    const string RepoUrl = "https://github.com/devlooped/nugetizer";
    const string Commit = "abc123def";

    [Fact]
    public void Expand_RelativeLink_UsesRawGitHubUrl()
    {
        var result = GitHubUrlExpander.Expand("See [license](license.txt).", RepoUrl, "9dc2cb5de");

        Assert.Contains("[license](https://raw.githubusercontent.com/devlooped/nugetizer/9dc2cb5de/license.txt)", result);
    }

    [Fact]
    public void Expand_ImageWithTooltip_PreservesTitle()
    {
        var result = GitHubUrlExpander.Expand(
            "See ![avatar](avatars/user.png \"User Avatar\").",
            RepoUrl, Commit);

        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/avatars/user.png", result);
        Assert.Contains("User Avatar", result);
    }

    [Fact]
    public void Expand_AbsoluteUrl_DoesNotReplace()
    {
        var source = "[![badge](https://raw.githubusercontent.com/devlooped/sponsors/main/.github/avatars/user.png \"User\")](https://github.com/user)";
        var result = GitHubUrlExpander.Expand(source, RepoUrl, Commit);

        Assert.DoesNotContain(
            "https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/https://raw.githubusercontent.com",
            result);
        Assert.Contains("https://raw.githubusercontent.com/devlooped/sponsors/main/.github/avatars/user.png", result);
    }

    [Fact]
    public void Expand_RelativeImage_UsesRawUrl()
    {
        var result = GitHubUrlExpander.Expand("![Image](img/logo.png)", RepoUrl, Commit);

        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/img/logo.png", result);
        Assert.DoesNotContain("/blob/", result);
    }

    [Fact]
    public void Expand_RelativeImageAndLink_ExpandsBoth()
    {
        var result = GitHubUrlExpander.Expand("[![Image](img/logo.png)](osmf.txt)", RepoUrl, Commit);

        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/img/logo.png", result);
        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/osmf.txt", result);
        Assert.DoesNotContain("/blob/", result);
    }

    [Fact]
    public void Expand_ClickableImageBadgeWithRelativeHref_ReplacesHrefOnly()
    {
        var result = GitHubUrlExpander.Expand(
            "[![EULA](https://img.shields.io/badge/EULA-OSMF-blue)](osmfeula.txt)",
            RepoUrl, Commit);

        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/osmfeula.txt", result);
        Assert.Contains("https://img.shields.io/badge/EULA-OSMF-blue", result);
    }

    [Fact]
    public void Expand_StripsGitSuffixFromRepositoryUrl()
    {
        var result = GitHubUrlExpander.Expand(
            "See [license](license.txt).",
            "https://github.com/devlooped/nugetizer.git",
            Commit);

        Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/license.txt", result);
        Assert.DoesNotContain("nugetizer.git", result);
    }

    [Theory]
    [InlineData(null, Commit)]
    [InlineData("", Commit)]
    [InlineData(RepoUrl, null)]
    [InlineData(RepoUrl, "")]
    [InlineData("https://gitlab.com/org/repo", Commit)]
    [InlineData("not-a-url", Commit)]
    public void Expand_SkipsWhenUrlOrCommitMissingOrNotGitHub(string? url, string? commit)
    {
        var source = "See [license](license.txt).";
        var result = GitHubUrlExpander.Expand(source, url, commit);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Expand_EmptyMarkdown_ReturnsEmpty()
    {
        Assert.Equal("", GitHubUrlExpander.Expand("", RepoUrl, Commit));
        Assert.Null(GitHubUrlExpander.Expand(null!, RepoUrl, Commit));
    }

    [Fact]
    public void ProcessReadmeIncludesTask_ExpandsGitHubUrlsAfterTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-github-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "Package $id$. See [docs](docs/usage.md).\n");
            var output = Path.Combine(root, "out", "readme.md");

            var task = new ProcessReadmeIncludes
            {
                SourceFile = source,
                OutputFile = output,
                BuildEngine = new MockBuildEngine(),
                ExpandGitHubUrls = true,
                RepositoryUrl = RepoUrl,
                RepositoryCommit = Commit,
                ReplacementTokens =
                [
                    new Microsoft.Build.Utilities.TaskItem("Id", new Dictionary<string, string> { ["Value"] = "My.Pkg" }),
                ],
            };

            Assert.True(task.Execute());
            var content = File.ReadAllText(output);
            Assert.Contains("Package My.Pkg.", content);
            Assert.Contains("https://raw.githubusercontent.com/devlooped/nugetizer/abc123def/docs/usage.md", content);
            Assert.DoesNotContain("$id$", content);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProcessReadmeIncludesTask_OptOut_DoesNotExpand()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-github-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "See [docs](docs/usage.md).\n");
            var output = Path.Combine(root, "out", "readme.md");

            var task = new ProcessReadmeIncludes
            {
                SourceFile = source,
                OutputFile = output,
                BuildEngine = new MockBuildEngine(),
                ExpandGitHubUrls = false,
                RepositoryUrl = RepoUrl,
                RepositoryCommit = Commit,
            };

            Assert.True(task.Execute());
            var content = File.ReadAllText(output);
            Assert.Contains("[docs](docs/usage.md)", content);
            Assert.DoesNotContain("raw.githubusercontent.com", content);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProcessReadmeIncludesTask_MissingCommit_LeavesRelativeUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "readme-github-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "readme.md");
            File.WriteAllText(source, "See [docs](docs/usage.md).\n");
            var output = Path.Combine(root, "out", "readme.md");

            var task = new ProcessReadmeIncludes
            {
                SourceFile = source,
                OutputFile = output,
                BuildEngine = new MockBuildEngine(),
                ExpandGitHubUrls = true,
                RepositoryUrl = RepoUrl,
                RepositoryCommit = null,
            };

            Assert.True(task.Execute());
            var content = File.ReadAllText(output);
            Assert.Contains("[docs](docs/usage.md)", content);
            Assert.DoesNotContain("raw.githubusercontent.com", content);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildTargets_WireGitHubUrlExpansion()
    {
        var targetsPath = FindBuildFile("Readme.targets");
        var propsPath = FindBuildFile("Readme.props");

        Assert.True(File.Exists(targetsPath), $"Readme.targets not found: {targetsPath}");
        Assert.True(File.Exists(propsPath), $"Readme.props not found: {propsPath}");

        var targets = File.ReadAllText(targetsPath);
        var props = File.ReadAllText(propsPath);

        Assert.Contains("ReadmeExpandGitHubUrls", props);
        Assert.Contains("ExpandGitHubUrls=\"$(ReadmeExpandGitHubUrls)\"", targets);
        Assert.Contains("RepositoryUrl=\"$(_ReadmeRepositoryUrl)\"", targets);
        Assert.Contains("RepositoryCommit=\"$(_ReadmeRepositoryCommit)\"", targets);
    }

    [Fact]
    public void TaskAssembly_DoesNotShipSeparateMarkdig()
    {
        // After ILRepack, Markdig is internalized into Readme.dll; no Markdig.dll next to it.
        var assemblyDir = Path.GetDirectoryName(typeof(GitHubUrlExpander).Assembly.Location)!;
        Assert.False(File.Exists(Path.Combine(assemblyDir, "Markdig.dll")),
            "Markdig.dll should be ILRepacked into Readme.dll, not shipped separately.");

        // Expand still works without a satellite Markdig.dll (merged + internalized).
        var sample = GitHubUrlExpander.Expand("[a](b.md)", RepoUrl, Commit);
        Assert.Contains("raw.githubusercontent.com", sample);
    }

    static string FindBuildFile(string fileName)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Readme", "build", fileName)),
            Path.GetFullPath(Path.Combine(typeof(TokenReplacer).Assembly.Location, "..", "..", "..", "..", "build", fileName)),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        var walk = new DirectoryInfo(AppContext.BaseDirectory);
        while (walk != null)
        {
            var candidate = Path.Combine(walk.FullName, "src", "Readme", "build", fileName);
            if (File.Exists(candidate))
                return candidate;
            walk = walk.Parent;
        }

        return candidates[0];
    }
}
