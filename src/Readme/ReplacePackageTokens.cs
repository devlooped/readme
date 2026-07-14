using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Readme;

/// <summary>
/// MSBuild task that replaces NuGet-style <c>$token$</c> placeholders in an arbitrary file.
/// Pass <c>Tokens="@(PackageReplacementToken)"</c> after <c>CollectReplacementTokens</c>.
/// Emits warning <c>RDM001</c> for any remaining unknown placeholders (suppressible via NoWarn).
/// </summary>
public class ReplacePackageTokens : Task
{
    /// <summary>Warning code for an unknown <c>$token$</c> placeholder after replacement.</summary>
    public const string UnknownTokenWarningCode = "RDM001";

    /// <summary>Source file path.</summary>
    [Required]
    public string InputFile { get; set; } = "";

    /// <summary>Destination path (parent directories are created). May equal <see cref="InputFile"/>.</summary>
    [Required]
    public string OutputFile { get; set; } = "";

    /// <summary>
    /// Token name/value pairs (<c>Include</c> = name, metadata <c>Value</c>).
    /// Maps from <c>@(PackageReplacementToken)</c>. Duplicate names keep the last value.
    /// </summary>
    public ITaskItem[]? Tokens { get; set; }

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(InputFile) || !File.Exists(InputFile))
        {
            Log.LogError("Input file not found: {0}", InputFile);
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputFile))
        {
            Log.LogError("OutputFile is required.");
            return false;
        }

        var content = File.ReadAllText(InputFile);
        var tokens = Tokens?
            .Select(i => (i.ItemSpec, i.GetMetadata("Value")))
            ?? Enumerable.Empty<(string, string)>();
        content = TokenReplacer.Replace(content, tokens);

        foreach (var name in TokenReplacer.FindPlaceholders(content))
        {
            Log.LogWarning(
                subcategory: null,
                warningCode: UnknownTokenWarningCode,
                helpKeyword: null,
                file: InputFile,
                lineNumber: 0,
                columnNumber: 0,
                endLineNumber: 0,
                endColumnNumber: 0,
                message: "Unknown package replacement token '${0}$' in '{1}'. Add a PackageReplacementToken item or remove the placeholder.",
                messageArgs: new object[] { name, InputFile });
        }

        var directory = Path.GetDirectoryName(OutputFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(OutputFile, content);
        Log.LogMessage(MessageImportance.Low, "Replaced package tokens: {0} -> {1}", InputFile, OutputFile);
        return !Log.HasLoggedErrors;
    }
}
