using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Readme;

/// <summary>
/// MSBuild task that resolves <c>&lt;!-- include ... --&gt;</c> directives in a package readme
/// and writes the processed content to an output path (typically under BaseIntermediateOutputPath).
/// </summary>
public class ProcessReadmeIncludes : Task
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

        var directory = Path.GetDirectoryName(OutputFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputFile, content);
        Log.LogMessage(MessageImportance.Low, "Processed package readme includes: {0} -> {1}", SourceFile, OutputFile);
        return !Log.HasLoggedErrors;
    }
}
