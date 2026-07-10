using System.IO;
using Microsoft.Build.Framework;
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

    [Fact]
    public void HeadingFragment_FallsBackToGitHubAutoAnchor_WhenNoCommentAnchor()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include sections.md#usage -->\n");
        dir.Write("sections.md",
            "## Intro\n" +
            "intro-body\n" +
            "\n" +
            "## Usage\n" +
            "usage-body\n" +
            "details-here\n" +
            "\n" +
            "## Other\n" +
            "other-body\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(root, w => warnings.Add(w));

        // Auto-anchor slice always includes the matching heading line itself.
        Assert.Contains("## Usage", content);
        Assert.StartsWith("<!-- include sections.md#usage -->", content.TrimStart());
        Assert.Contains("## Usage\nusage-body", content.Replace("\r\n", "\n"));
        Assert.Contains("details-here", content);
        Assert.DoesNotContain("intro-body", content);
        Assert.DoesNotContain("other-body", content);
        Assert.DoesNotContain("## Other", content);
        Assert.DoesNotContain("## Intro", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CommentAnchor_CanOmitOrIncludeSectionTitleByPlacement()
    {
        // Explicit <!-- #fragment --> markup gives control over whether the section name is included.
        using var dir = new TempDir();
        var omit = dir.Write("omit.md", "<!-- include doc.md#usage -->\n");
        dir.Write("doc.md",
            "## Usage\n" +
            "<!-- #usage -->\n" +
            "body-only\n" +
            "<!-- #usage -->\n");

        var omitContent = IncludesResolver.Process(omit);
        Assert.Contains("body-only", omitContent);
        Assert.DoesNotContain("## Usage", omitContent);

        using var dir2 = new TempDir();
        var keep = dir2.Write("keep.md", "<!-- include doc.md#usage -->\n");
        dir2.Write("doc.md",
            "<!-- #usage -->\n" +
            "## Usage\n" +
            "with-title\n" +
            "<!-- #usage -->\n");

        var keepContent = IncludesResolver.Process(keep);
        Assert.Contains("## Usage", keepContent);
        Assert.Contains("with-title", keepContent);
    }

    [Fact]
    public void HeadingFragment_IncludesNestedLowerLevelHeadings()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include doc.md#usage -->\n");
        dir.Write("doc.md",
            "## Usage\n" +
            "usage-top\n" +
            "### Details\n" +
            "nested-details\n" +
            "## Next\n" +
            "next-body\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("## Usage", content);
        Assert.Contains("usage-top", content);
        Assert.Contains("### Details", content);
        Assert.Contains("nested-details", content);
        Assert.DoesNotContain("next-body", content);
        Assert.DoesNotContain("## Next", content);
    }

    [Fact]
    public void CommentAnchor_TakesPriorityOverHeadingWithSameFragmentName()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include doc.md#usage -->\n");
        dir.Write("doc.md",
            "## Usage\n" +
            "heading-usage-body\n" +
            "\n" +
            "<!-- #usage -->\n" +
            "comment-usage-body\n" +
            "<!-- #usage -->\n" +
            "\n" +
            "## Other\n" +
            "other\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("comment-usage-body", content);
        Assert.Contains("<!-- #usage -->", content);
        Assert.DoesNotContain("heading-usage-body", content);
        Assert.DoesNotContain("## Usage", content);
    }

    [Fact]
    public void MissingFragment_NoCommentOrHeading_WarnsAndLeavesMarker()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "before\n<!-- include doc.md#missing -->\nafter\n");
        dir.Write("doc.md", "## Usage\nusage-body\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(root, w => warnings.Add(w));

        Assert.Contains("before", content);
        Assert.Contains("after", content);
        Assert.Contains("<!-- include doc.md#missing -->", content);
        Assert.DoesNotContain("usage-body", content);
        Assert.Contains(warnings, w => w.Contains("#missing", StringComparison.Ordinal) &&
                                       w.Contains("doc.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeadingFragment_DuplicateHeadings_UseGitHubDisambiguation()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include doc.md#usage-1 -->\n");
        dir.Write("doc.md",
            "## Usage\n" +
            "first-usage\n" +
            "## Usage\n" +
            "second-usage\n" +
            "## Other\n" +
            "other\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("## Usage", content);
        Assert.Contains("second-usage", content);
        Assert.DoesNotContain("first-usage", content);
        Assert.DoesNotContain("other", content);
    }

    [Fact]
    public void HeadingFragment_SkipsHeadingsInsideFencedCodeBlocks()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include doc.md#usage -->\n");
        dir.Write("doc.md",
            "```\n" +
            "## Usage\n" +
            "code-usage\n" +
            "```\n" +
            "## Usage\n" +
            "real-usage\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("real-usage", content);
        Assert.DoesNotContain("code-usage", content);
    }

    [Fact]
    public void ExcludeCodeBlock_DoesNotResolveIncludesInside()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "before\n" +
            "```exclude\n" +
            "<!-- include part.md -->\n" +
            "```\n" +
            "after\n");
        dir.Write("part.md", "PART-BODY\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(root, w => warnings.Add(w));

        Assert.Contains("before", content);
        Assert.Contains("after", content);
        // Literal include syntax preserved for documentation
        Assert.Contains("```exclude", content);
        Assert.Contains("<!-- include part.md -->", content);
        Assert.DoesNotContain("PART-BODY", content);
        Assert.DoesNotContain("<!-- part.md -->", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExcludeCodeBlock_SamePathOutsideStillResolves()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "<!-- include part.md -->\n" +
            "\n" +
            "```exclude\n" +
            "<!-- include part.md -->\n" +
            "```\n");
        dir.Write("part.md", "PART-BODY\n");

        var content = IncludesResolver.Process(root);
        var normalized = content.Replace("\r\n", "\n");

        // Outside fence: expanded
        Assert.Contains("PART-BODY", normalized);
        Assert.Contains("<!-- part.md -->", normalized);

        // Inside exclude fence: still the bare marker (exactly one bare-only occurrence remains in the fence)
        var fenceStart = normalized.IndexOf("```exclude\n", StringComparison.Ordinal);
        Assert.True(fenceStart >= 0);
        var fenceEnd = normalized.IndexOf("\n```", fenceStart + 1, StringComparison.Ordinal);
        Assert.True(fenceEnd > fenceStart);
        var fenceBody = normalized.Substring(fenceStart, fenceEnd - fenceStart);
        Assert.Contains("<!-- include part.md -->", fenceBody);
        Assert.DoesNotContain("PART-BODY", fenceBody);
    }

    [Fact]
    public void ExcludeCodeBlock_TildeFence_DoesNotResolveIncludes()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "~~~exclude\n" +
            "<!-- include part.md -->\n" +
            "~~~\n");
        dir.Write("part.md", "PART-BODY\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("<!-- include part.md -->", content);
        Assert.DoesNotContain("PART-BODY", content);
    }

    [Fact]
    public void NonExcludeCodeBlock_StillResolvesIncludes()
    {
        // Only the `exclude` language opts out; other fences still expand includes.
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "```markdown\n" +
            "<!-- include part.md -->\n" +
            "```\n");
        dir.Write("part.md", "PART-BODY\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("PART-BODY", content);
        Assert.Contains("<!-- part.md -->", content);
    }

    [Fact]
    public void ExcludeCodeBlock_CaseInsensitiveLanguage()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "```Exclude\n" +
            "<!-- include part.md -->\n" +
            "```\n");
        dir.Write("part.md", "PART-BODY\n");

        var content = IncludesResolver.Process(root);

        Assert.Contains("<!-- include part.md -->", content);
        Assert.DoesNotContain("PART-BODY", content);
    }

    [Theory]
    [InlineData("Usage", "usage")]
    [InlineData("Hello World", "hello-world")]
    [InlineData("This'll be fine", "thisll-be-fine")]
    [InlineData("API / Overview", "api--overview")]
    [InlineData("C# Tips", "c-tips")]
    [InlineData("foo_bar", "foo_bar")]
    [InlineData("get_user_name", "get_user_name")]
    [InlineData("  Trimmed  ", "trimmed")]
    [InlineData("_Helpful_ Section", "helpful-section")]
    [InlineData("A **Bold** Title", "a-bold-title")]
    public void GitHubHeadingSlug_MatchesExpected(string heading, string expectedSlug)
    {
        Assert.Equal(expectedSlug, IncludesResolver.GitHubHeadingSlug(heading));
    }

    [Fact]
    public void HeadingFragment_SnakeCaseHeading_KeepsUnderscoresLikeGitHubSlugger()
    {
        // Regression: naive _…_ emphasis strip turned get_user_name → getusername and broke #get_user_name.
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include api.md#get_user_name -->\n");
        dir.Write("api.md",
            "## Intro\n" +
            "intro\n" +
            "## get_user_name\n" +
            "snake-body\n" +
            "## Other\n" +
            "other\n");

        var warnings = new List<string>();
        var content = IncludesResolver.Process(root, w => warnings.Add(w));

        Assert.Equal("get_user_name", IncludesResolver.GitHubHeadingSlug("get_user_name"));
        Assert.Contains("## get_user_name", content);
        Assert.Contains("snake-body", content);
        Assert.DoesNotContain("intro", content);
        Assert.DoesNotContain("other", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TryExtractHeadingSection_ReturnsFalseWhenNoMatch()
    {
        var ok = IncludesResolver.TryExtractHeadingSection("## Usage\nbody\n", "missing", out var section);
        Assert.False(ok);
        Assert.Equal("", section);
    }

    [Fact]
    public void TryExtractHeadingSection_IncludesHeadingLineInSlice()
    {
        var ok = IncludesResolver.TryExtractHeadingSection(
            "## Intro\nintro\n## Usage\nusage-body\n## Other\nother\n",
            "usage",
            out var section);

        Assert.True(ok);
        Assert.Equal("## Usage\nusage-body", section.Replace("\r\n", "\n"));
    }

    // --- Remote URL policy (scheme allowlist, base-relative, domain allowlist) ---

    static IncludeResolutionOptions RemoteOptions(
        IDictionary<string, string> store,
        string? schemes = "https",
        IEnumerable<string>? domains = null)
    {
        return new IncludeResolutionOptions
        {
            AllowedSchemes = schemes == null ? null : new[] { schemes },
            AllowedDomains = domains,
            FetchRemoteContent = uri =>
            {
                var key = uri.AbsoluteUri;
                if (store.TryGetValue(key, out var body))
                    return body;
                // Also try without trailing quirks
                foreach (var kv in store)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                throw new InvalidOperationException($"Test fixture missing content for {key}");
            },
        };
    }

    [Fact]
    public void Scheme_DefaultHttpsOnly_AllowsHttps_BlocksHttp()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md",
            "<!-- include https://cdn.example.com/ok.md -->\n" +
            "<!-- include http://cdn.example.com/plain.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://cdn.example.com/ok.md"] = "HTTPS-BODY",
            ["http://cdn.example.com/plain.md"] = "HTTP-BODY",
        };
        var warnings = new List<string>();
        // null schemes → default https only
        var options = RemoteOptions(store, schemes: null);

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("HTTPS-BODY", content);
        Assert.DoesNotContain("HTTP-BODY", content);
        Assert.Contains("<!-- include http://cdn.example.com/plain.md -->", content);
        Assert.Contains(warnings, w =>
            w.Contains("scheme", StringComparison.OrdinalIgnoreCase) &&
            w.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scheme_HttpAllowed_ResolvesHttp()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include http://cdn.example.com/plain.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://cdn.example.com/plain.md"] = "HTTP-OK",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store, schemes: "https;http");

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("HTTP-OK", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Scheme_Blocked_LeavesMarkerAndWarns()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "before\n<!-- include https://cdn.example.com/x.md -->\nafter\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://cdn.example.com/x.md"] = "SHOULD-NOT-APPEAR",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store, schemes: "http"); // https not allowed

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("before", content);
        Assert.Contains("after", content);
        Assert.Contains("<!-- include https://cdn.example.com/x.md -->", content);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", content);
        Assert.Contains(warnings, w => w.Contains("scheme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoteParent_RelativeInclude_ResolvesAgainstBaseUri()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://docs.example.com/guide/index.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://docs.example.com/guide/index.md"] =
                "INDEX\n<!-- include ../shared/footer.md -->\n<!-- include section.md -->\n",
            ["https://docs.example.com/shared/footer.md"] = "FOOTER-BODY",
            ["https://docs.example.com/guide/section.md"] = "SECTION-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store);

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("INDEX", content);
        Assert.Contains("FOOTER-BODY", content);
        Assert.Contains("SECTION-BODY", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LocalParent_AbsoluteRemote_ResolvesWithoutDomainAllowlist()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://unlisted.example.org/page.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://unlisted.example.org/page.md"] = "LOCAL-HOP-OK",
        };
        var warnings = new List<string>();
        // Empty domain list — still allowed because include is from a local file
        var options = RemoteOptions(store, domains: Array.Empty<string>());

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("LOCAL-HOP-OK", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RemoteParent_AbsoluteUrl_DisallowedHost_WarnsAndLeavesMarker()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://safe.example.com/a.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://safe.example.com/a.md"] =
                "A\n<!-- include https://evil.example.net/b.md -->\n",
            ["https://evil.example.net/b.md"] = "EVIL-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store); // no domains allowlisted

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("A", content);
        Assert.DoesNotContain("EVIL-BODY", content);
        Assert.Contains("<!-- include https://evil.example.net/b.md -->", content);
        Assert.Contains(warnings, w =>
            w.Contains("host", StringComparison.OrdinalIgnoreCase) &&
            w.Contains("evil.example.net", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoteParent_AbsoluteUrl_SameHost_Resolves()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://docs.example.com/a.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://docs.example.com/a.md"] =
                "A\n<!-- include https://docs.example.com/b.md -->\n",
            ["https://docs.example.com/b.md"] = "SAME-HOST-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store);

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("SAME-HOST-BODY", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RemoteParent_AbsoluteUrl_SubdomainOfParentHost_Resolves()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://example.com/a.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.com/a.md"] =
                "A\n<!-- include https://cdn.example.com/b.md -->\n",
            ["https://cdn.example.com/b.md"] = "SUBDOMAIN-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store);

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("SUBDOMAIN-BODY", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RemoteParent_AbsoluteUrl_AllowlistedDomain_Resolves()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://docs.example.com/a.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://docs.example.com/a.md"] =
                "A\n<!-- include https://assets.other.org/b.md -->\n",
            ["https://assets.other.org/b.md"] = "ALLOWLIST-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store, domains: new[] { "assets.other.org" });

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("ALLOWLIST-BODY", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RemoteParent_AbsoluteUrl_SubdomainOfAllowlistedDomain_Resolves()
    {
        using var dir = new TempDir();
        var root = dir.Write("root.md", "<!-- include https://docs.example.com/a.md -->\n");

        var store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://docs.example.com/a.md"] =
                "A\n<!-- include https://raw.cdn.trusted.org/b.md -->\n",
            ["https://raw.cdn.trusted.org/b.md"] = "ALLOWLIST-SUB-BODY",
        };
        var warnings = new List<string>();
        var options = RemoteOptions(store, domains: new[] { "trusted.org" });

        var content = IncludesResolver.Process(root, w => warnings.Add(w), options);

        Assert.Contains("ALLOWLIST-SUB-BODY", content);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IsSameHostOrSubdomain_RejectsSuffixTraps()
    {
        Assert.True(IncludesResolver.IsSameHostOrSubdomain("example.com", "example.com"));
        Assert.True(IncludesResolver.IsSameHostOrSubdomain("cdn.example.com", "example.com"));
        Assert.True(IncludesResolver.IsSameHostOrSubdomain("a.b.example.com", "example.com"));
        Assert.False(IncludesResolver.IsSameHostOrSubdomain("notexample.com", "example.com"));
        Assert.False(IncludesResolver.IsSameHostOrSubdomain("evil-example.com", "example.com"));
        Assert.False(IncludesResolver.IsSameHostOrSubdomain("example.com.evil.net", "example.com"));
    }

    [Fact]
    public void ProcessReadmeIncludesTask_AcceptsSchemeAndDomainProperties()
    {
        // Verify task property plumbing: schemes/domains flow into Process without error.
        using var dir = new TempDir();
        var localOnly = dir.Write("local.md", "<!-- include part.md -->\n");
        dir.Write("part.md", "PART\n");
        var output = Path.Combine(dir.Path, "out.md");

        var task = new ProcessReadmeIncludes
        {
            SourceFile = localOnly,
            OutputFile = output,
            AllowedSchemes = "https;http",
            AllowedDomains = new ITaskItem[]
            {
                new Microsoft.Build.Utilities.TaskItem("trusted.org"),
            },
            BuildEngine = new MockBuildEngine(),
        };

        Assert.True(task.Execute());
        Assert.Contains("PART", File.ReadAllText(output));
    }

    [Fact]
    public void PropsAndTargets_WireSchemeAndDomainDefaults()
    {
        // Structural evidence: pack path defaults and task args exist in shipped MSBuild files.
        var propsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Readme", "build", "Readme.props"));
        var targetsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Readme", "build", "Readme.targets"));

        // Fallback when running from different layouts: repo-relative from test source
        if (!File.Exists(propsPath))
        {
            propsPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Readme", "build", "Readme.props"));
            targetsPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Readme", "build", "Readme.targets"));
        }

        // Prefer project-referenced package layout next to the built task assembly copy path
        var altProps = Path.GetFullPath(Path.Combine(
            typeof(IncludesResolver).Assembly.Location, "..", "..", "..", "..", "build", "Readme.props"));
        if (!File.Exists(propsPath) && File.Exists(altProps))
        {
            propsPath = altProps;
            targetsPath = Path.Combine(Path.GetDirectoryName(altProps)!, "Readme.targets");
        }

        // Last resort: walk up from assembly location looking for src/Readme/build
        if (!File.Exists(propsPath))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Readme", "build", "Readme.props");
                if (File.Exists(candidate))
                {
                    propsPath = candidate;
                    targetsPath = Path.Combine(dir.FullName, "src", "Readme", "build", "Readme.targets");
                    break;
                }
                dir = dir.Parent;
            }
        }

        Assert.True(File.Exists(propsPath), $"Readme.props not found (tried near test output). Last: {propsPath}");
        Assert.True(File.Exists(targetsPath), $"Readme.targets not found. Last: {targetsPath}");

        var props = File.ReadAllText(propsPath);
        var targets = File.ReadAllText(targetsPath);

        Assert.Contains("ReadmeIncludeScheme", props);
        Assert.Contains("https", props);
        Assert.Contains("ReadmeIncludeDomain", props);
        Assert.Contains("AllowedSchemes=\"$(ReadmeIncludeScheme)\"", targets);
        Assert.Contains("AllowedDomains=\"@(ReadmeIncludeDomain)\"", targets);
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
