using System;
using System.IO;
using System.Linq;
using Markdig;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Readme;

/// <summary>
/// Expands relative Markdown links and images in a package readme to absolute
/// <c>raw.githubusercontent.com</c> URLs pinned to a repository commit.
/// Same algorithm as NuGetizer's pack-time readme rewriting.
/// </summary>
public static class GitHubUrlExpander
{
    /// <summary>
    /// Expand relative link/image URLs using the given GitHub repository URL and commit.
    /// Returns <paramref name="markdown"/> unchanged when url/commit is missing or the host is not github.com.
    /// </summary>
    public static string Expand(string markdown, string? repositoryUrl, string? repositoryCommit)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        if (string.IsNullOrWhiteSpace(repositoryUrl) || string.IsNullOrWhiteSpace(repositoryCommit))
            return markdown;

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            return markdown;

        // owner/repo — strip leading/trailing slashes and optional .git suffix
        // (trailing slash would leave "repo.git/" which fails EndsWith(".git"))
        var repoPath = uri.AbsolutePath.Trim('/');
        if (repoPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoPath = repoPath.Substring(0, repoPath.Length - 4);

        if (string.IsNullOrEmpty(repoPath))
            return markdown;

        var rawBaseUrl = $"https://raw.githubusercontent.com/{repoPath}";
        var commit = repositoryCommit!.Trim();

        var document = Markdown.Parse(markdown);
        var links = document.Descendants<LinkInline>().ToList();

        foreach (var link in links)
        {
            if (link.Url is not { Length: > 0 } linkUrl ||
                Uri.IsWellFormedUriString(linkUrl, UriKind.Absolute))
                continue;

            link.Url = $"{rawBaseUrl}/{commit}/{linkUrl.TrimStart('/')}";

            // Nested image inside a link: [![alt](img.png)](doc.txt)
            if (link.FirstChild is LinkInline img &&
                img.Url is { Length: > 0 } imgUrl &&
                !Uri.IsWellFormedUriString(imgUrl, UriKind.Absolute))
            {
                img.Url = $"{rawBaseUrl}/{commit}/{imgUrl.TrimStart('/')}";
            }
        }

        using var writer = new StringWriter();
        var renderer = new NormalizeRenderer(writer);
        renderer.Render(document);
        return writer.ToString();
    }
}
