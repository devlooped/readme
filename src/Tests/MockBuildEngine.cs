using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.Framework;

namespace Readme.Tests;

/// <summary>Minimal IBuildEngine for exercising MSBuild tasks in unit tests.</summary>
sealed class MockBuildEngine : IBuildEngine
{
    public List<BuildWarningEventArgs> Warnings { get; } = new();
    public List<BuildErrorEventArgs> Errors { get; } = new();
    public List<BuildMessageEventArgs> Messages { get; } = new();

    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "";

    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        => throw new NotImplementedException();

    public void LogCustomEvent(CustomBuildEventArgs e) { }
    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
    public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);
    public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
}
