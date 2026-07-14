using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Readme;

/// <summary>
/// MSBuild task that resolves <c>&lt;!-- include ... --&gt;</c> directives in a package readme,
/// applies <c>$token$</c> replacements, optionally expands GitHub relative URLs, and writes the
/// processed content to an output path (typically under BaseIntermediateOutputPath).
/// Does not warn on unknown <c>$token$</c> placeholders (readme content may document that syntax).
/// </summary>
public class ProcessPackageReadme : Task
{
    /// <summary>Source readme file path (project readme before include expansion).</summary>
    [Required]
    public string SourceFile { get; set; } = "";

    /// <summary>Output path for the processed readme (packed from this path).</summary>
    [Required]
    public string OutputFile { get; set; } = "";

    /// <summary>
    /// Semicolon-separated URI schemes allowed for remote includes (default <c>https</c>).
    /// Maps from <c>$(ReadmeIncludeScheme)</c>.
    /// </summary>
    public string AllowedSchemes { get; set; } = "https";

    /// <summary>
    /// Hosts allowed for absolute remote includes from remote content.
    /// Maps from <c>@(ReadmeIncludeDomain)</c>. Local-file → remote hops ignore this list.
    /// </summary>
    public ITaskItem[]? AllowedDomains { get; set; }

    /// <summary>
    /// Token name/value pairs for <c>$token$</c> replacement after include expansion.
    /// Maps from <c>@(PackageReplacementToken)</c> (metadata <c>Value</c>).
    /// Duplicate names keep the last value (coexists with NuGetizer contributions).
    /// </summary>
    public ITaskItem[]? ReplacementTokens { get; set; }

    /// <summary>
    /// When true (default), expand relative Markdown links/images to raw.githubusercontent.com
    /// URLs when <see cref="RepositoryUrl"/> / <see cref="RepositoryCommit"/> allow it.
    /// Maps from <c>$(ReadmeExpandGitHubUrls)</c>.
    /// </summary>
    public bool ExpandGitHubUrls { get; set; } = true;

    /// <summary>
    /// Repository URL used for GitHub relative-link expansion (e.g. <c>https://github.com/org/repo</c>).
    /// Maps from <c>$(RepositoryUrl)</c> (or SourceLink private URL when published).
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// Commit SHA (or short SHA) used to pin expanded raw.githubusercontent.com URLs.
    /// Maps from <c>$(RepositoryCommit)</c> / <c>$(RepositorySha)</c> / <c>$(SourceRevisionId)</c>.
    /// </summary>
    public string? RepositoryCommit { get; set; }

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(SourceFile) || !File.Exists(SourceFile))
        {
            Log.LogError("Source readme file not found: {0}", SourceFile);
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputFile))
        {
            Log.LogError("OutputFile is required.");
            return false;
        }

        var options = new IncludeResolutionOptions
        {
            AllowedSchemes = string.IsNullOrWhiteSpace(AllowedSchemes)
                ? new[] { "https" }
                : new[] { AllowedSchemes },
            AllowedDomains = AllowedDomains?
                .Select(i => i.ItemSpec)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray(),
        };

        var content = IncludesResolver.Process(SourceFile, message => Log.LogWarning(message), options);

        var tokens = ReplacementTokens?
            .Select(i => (i.ItemSpec, i.GetMetadata("Value")))
            ?? Enumerable.Empty<(string, string)>();
        content = TokenReplacer.Replace(content, tokens);

        if (ExpandGitHubUrls)
        {
            var expanded = GitHubUrlExpander.Expand(content, RepositoryUrl, RepositoryCommit);
            if (!ReferenceEquals(expanded, content) && !string.Equals(expanded, content, StringComparison.Ordinal))
            {
                Log.LogMessage(MessageImportance.Low,
                    "Expanded GitHub relative URLs in package readme using {0}@{1}",
                    RepositoryUrl, RepositoryCommit);
                content = expanded;
            }
        }

        // NuGetizer CreatePackage always re-runs its own IncludesResolver on the package
        // readme (older algorithm: no ```exclude, IndexOf fragments). Neutralize include
        // openers so that second pass is a no-op while docs still read as include syntax
        // (zero-width space is invisible). Same for our own expansion wrappers.
        content = NeutralizeIncludeOpeners(content);

        var directory = Path.GetDirectoryName(OutputFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputFile, content);
        Log.LogMessage(MessageImportance.Low, "Processed package readme: {0} -> {1}", SourceFile, OutputFile);
        return !Log.HasLoggedErrors;
    }

    /// <summary>
    /// Inserts a zero-width space after the <c>include</c> keyword in
    /// <c>&lt;!-- include … --&gt;</c> openers so a subsequent IncludesResolver pass
    /// (e.g. NuGetizer CreatePackage) will not re-expand them, while the text still
    /// appears as normal include syntax to readers.
    /// </summary>
    public static string NeutralizeIncludeOpeners(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Match the same opener shape IncludesResolver uses; inject U+200B after "include".
        return Regex.Replace(
            content,
            @"<!--(\s?)include(\s)",
            "<!--$1include\u200B$2",
            RegexOptions.CultureInvariant);
    }
}
