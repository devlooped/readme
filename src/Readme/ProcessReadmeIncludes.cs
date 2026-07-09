using System.IO;
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

        var content = IncludesResolver.Process(SourceFile, message => Log.LogWarning(message));

        var directory = Path.GetDirectoryName(OutputFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputFile, content);
        Log.LogMessage(MessageImportance.Low, "Processed package readme includes: {0} -> {1}", SourceFile, OutputFile);
        return !Log.HasLoggedErrors;
    }
}
