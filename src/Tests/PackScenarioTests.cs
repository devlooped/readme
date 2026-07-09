using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Readme.Tests;

public class PackScenarioTests
{
    static string RepoRoot { get; } = FindRepoRoot();
    static string ScratchRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "grok-goal-c80e1987bd14", "implementer");

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Readme.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "readme.md")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: src/Tests -> repo root
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Fact]
    public void SdkPack_ProcessesIncludes_IntoIntermediate_AndPackage()
    {
        var evidenceDir = Path.Combine(ScratchRoot, "sdk-pack-readme");
        Directory.CreateDirectory(evidenceDir);

        var work = PrepareScenario("SdkPack", evidenceDir);
        var packed = EnsureReadmePackage(evidenceDir);

        // Point scenario at locally packed Readme package via nuget.config + PackageReference
        InjectLocalFeedAndReference(work, packed, useNuGetizer: false);

        var packLog = Path.Combine(evidenceDir, "pack.log");
        var exit = RunDotnet($"pack \"{Path.Combine(work, "SdkPack.csproj")}\" -c Release -o \"{Path.Combine(work, "out")}\" -v:n", work, packLog);
        Assert.True(exit == 0, $"SDK pack failed. See {packLog}\n{File.ReadAllText(packLog)}");

        // Intermediate processed readme under BaseIntermediateOutputPath
        var intermediateReadme = Directory.GetFiles(Path.Combine(work, "obj"), "readme.md", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.True(intermediateReadme != null && File.Exists(intermediateReadme),
            "Expected processed readme under obj/ (BaseIntermediateOutputPath)");
        var intermediateContent = File.ReadAllText(intermediateReadme!);
        File.Copy(intermediateReadme!, Path.Combine(evidenceDir, "intermediate-readme.md"), overwrite: true);
        Assert.Contains("Intro body from include.", intermediateContent);
        Assert.Contains("Fragment-only content.", intermediateContent);
        Assert.DoesNotContain("Should not appear.", intermediateContent);
        Assert.DoesNotContain("<!-- include parts/intro.md -->\n\n<!-- include", intermediateContent); // expanded, not bare-only

        // nupkg contains processed readme
        var nupkg = Directory.GetFiles(Path.Combine(work, "out"), "*.nupkg").Single();
        File.Copy(nupkg, Path.Combine(evidenceDir, Path.GetFileName(nupkg)), overwrite: true);
        var packageReadme = ReadPackageEntry(nupkg, "readme.md");
        File.WriteAllText(Path.Combine(evidenceDir, "package-readme.md"), packageReadme);
        Assert.Contains("Intro body from include.", packageReadme);
        Assert.Contains("Fragment-only content.", packageReadme);
        Assert.DoesNotContain("Should not appear.", packageReadme);

        // PackageReadmeFile metadata present in nuspec
        var nuspec = ReadPackageEntry(nupkg, "SdkPackReadmeSample.nuspec");
        File.WriteAllText(Path.Combine(evidenceDir, "package.nuspec"), nuspec);
        Assert.Contains("<readme>readme.md</readme>", nuspec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuGetizerPack_ProcessesIncludes_WithoutFatalConflict()
    {
        var evidenceDir = Path.Combine(ScratchRoot, "nugetizer-pack-readme");
        Directory.CreateDirectory(evidenceDir);

        var work = PrepareScenario("NuGetizerPack", evidenceDir);
        var packed = EnsureReadmePackage(evidenceDir);
        InjectLocalFeedAndReference(work, packed, useNuGetizer: true);

        var packLog = Path.Combine(evidenceDir, "pack.log");
        var exit = RunDotnet($"pack \"{Path.Combine(work, "NuGetizerPack.csproj")}\" -c Release -o \"{Path.Combine(work, "out")}\" -v:n", work, packLog);
        Assert.True(exit == 0, $"NuGetizer pack failed. See {packLog}\n{File.ReadAllText(packLog)}");

        var intermediateReadme = Directory.GetFiles(Path.Combine(work, "obj"), "readme.md", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (intermediateReadme != null)
        {
            File.Copy(intermediateReadme, Path.Combine(evidenceDir, "intermediate-readme.md"), overwrite: true);
            var intermediateContent = File.ReadAllText(intermediateReadme);
            Assert.Contains("Shared include for NuGetizer pack scenario.", intermediateContent);
        }

        var nupkg = Directory.GetFiles(Path.Combine(work, "out"), "*.nupkg").Single();
        File.Copy(nupkg, Path.Combine(evidenceDir, Path.GetFileName(nupkg)), overwrite: true);
        var packageReadme = ReadPackageEntry(nupkg, "readme.md");
        File.WriteAllText(Path.Combine(evidenceDir, "package-readme.md"), packageReadme);
        Assert.Contains("Shared include for NuGetizer pack scenario.", packageReadme);
        Assert.DoesNotContain("<!-- include shared.md -->\r\n\r\n<!-- include", packageReadme);
        // Include should be expanded (body present; bare-only include without body is a failure)
        Assert.True(
            packageReadme.Contains("Shared include for NuGetizer pack scenario."),
            "Package readme should contain expanded include body");
    }

    [Fact]
    public void SdkPack_PackReadmeFalse_PacksWithoutReadme_NoNU5039()
    {
        var evidenceDir = Path.Combine(ScratchRoot, "pack-readme-false");
        Directory.CreateDirectory(evidenceDir);

        var work = PrepareScenario("PackReadmeFalse", evidenceDir);
        var packed = EnsureReadmePackage(evidenceDir);
        InjectLocalFeedAndReference(work, packed, useNuGetizer: false);

        // Ensure PackReadme=false survives injection (csproj already has it; assert disk still has readme.md)
        Assert.True(File.Exists(Path.Combine(work, "readme.md")), "Scenario must have a project readme on disk");
        var csprojText = File.ReadAllText(Directory.GetFiles(work, "*.csproj").Single());
        Assert.Contains("PackReadme>false", csprojText);

        var packLog = Path.Combine(evidenceDir, "pack.log");
        var exit = RunDotnet(
            $"pack \"{Path.Combine(work, "PackReadmeFalse.csproj")}\" -c Release -o \"{Path.Combine(work, "out")}\" -v:n",
            work, packLog);
        Assert.True(exit == 0, $"SDK pack with PackReadme=false failed (NU5039?). See {packLog}\n{File.ReadAllText(packLog)}");
        File.Copy(packLog, Path.Combine(evidenceDir, "pack-success.log"), overwrite: true);

        var nupkg = Directory.GetFiles(Path.Combine(work, "out"), "*.nupkg").Single();
        File.Copy(nupkg, Path.Combine(evidenceDir, Path.GetFileName(nupkg)), overwrite: true);

        using (var zip = ZipFile.OpenRead(nupkg))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            File.WriteAllLines(Path.Combine(evidenceDir, "nupkg-entries.txt"), entries);
            Assert.DoesNotContain(entries, e => e.Equals("readme.md", StringComparison.OrdinalIgnoreCase));
        }

        var nuspec = ReadPackageEntry(nupkg, "PackReadmeFalseSample.nuspec");
        File.WriteAllText(Path.Combine(evidenceDir, "package.nuspec"), nuspec);
        Assert.DoesNotContain("<readme>", nuspec, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NU5039", File.ReadAllText(packLog), StringComparison.OrdinalIgnoreCase);
    }

    string PrepareScenario(string name, string evidenceDir)
    {
        var source = Path.Combine(RepoRoot, "src", "Tests", "Scenarios", name);
        Assert.True(Directory.Exists(source), $"Scenario source missing: {source}");

        var work = Path.Combine(evidenceDir, "work");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        CopyDirectory(source, work);
        return work;
    }

    sealed record PackedReadme(string FeedDir, string Version);

    PackedReadme EnsureReadmePackage(string evidenceDir)
    {
        var feed = Path.Combine(ScratchRoot, "local-feed");
        Directory.CreateDirectory(feed);

        var packLog = Path.Combine(evidenceDir, "readme-package-pack.log");
        // Also keep durable copy at plan path
        var globalPackLog = Path.Combine(ScratchRoot, "pack-readme-package.log");

        var project = Path.Combine(RepoRoot, "src", "Readme", "Readme.csproj");
        var exit = RunDotnet($"pack \"{project}\" -c Release -o \"{feed}\" -v:n", RepoRoot, packLog);
        File.Copy(packLog, globalPackLog, overwrite: true);
        Assert.True(exit == 0, $"Packing Readme package failed.\n{File.ReadAllText(packLog)}");

        // Pack twice for consistency check (plan: more than once)
        var packLog2 = Path.Combine(evidenceDir, "readme-package-pack-2.log");
        exit = RunDotnet($"pack \"{project}\" -c Release -o \"{feed}\" -v:n", RepoRoot, packLog2);
        Assert.True(exit == 0, $"Second pack of Readme package failed.\n{File.ReadAllText(packLog2)}");
        File.AppendAllText(globalPackLog, "\n--- second pack ---\n" + File.ReadAllText(packLog2));

        // CI may stamp prerelease labels (e.g. 42.42.0-pr4.5); use whatever was packed.
        var nupkg = Directory.GetFiles(feed, "Readme*.nupkg")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.True(nupkg != null, $"No Readme*.nupkg in {feed}");
        var fileName = Path.GetFileNameWithoutExtension(nupkg!);
        Assert.True(fileName.StartsWith("Readme.", StringComparison.OrdinalIgnoreCase),
            $"Unexpected package file name: {fileName}");
        var version = fileName.Substring("Readme.".Length);
        File.WriteAllText(Path.Combine(evidenceDir, "readme-package-version.txt"), version);
        return new PackedReadme(feed, version);
    }

    static void InjectLocalFeedAndReference(string projectDir, PackedReadme packed, bool useNuGetizer)
    {
        // Clear packageSourceMapping so the local feed is considered (user-level mapping
        // would otherwise ignore unmapped sources).
        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{packed.FeedDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="local">
                  <package pattern="Readme" />
                  <package pattern="Readme.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(Path.Combine(projectDir, "nuget.config"), nugetConfig);

        var csprojPath = Directory.GetFiles(projectDir, "*.csproj").Single();
        var csproj = File.ReadAllText(csprojPath);

        // Isolate from repo Directory.Build.*
        File.WriteAllText(Path.Combine(projectDir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(projectDir, "Directory.Build.targets"), "<Project />");

        if (!csproj.Contains("PackageReference Include=\"Readme\""))
        {
            var packageRef = $"""
                  <ItemGroup>
                    <PackageReference Include="Readme" Version="{packed.Version}" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """;
            csproj = csproj.Replace("</Project>", packageRef);
        }

        // Ensure SDK Pack scenarios do not pull NuGetizer via transitive means
        if (!useNuGetizer)
        {
            // Force classic pack targets
            if (!csproj.Contains("ImportNuGetBuildTasksPackTargetsFromSdk"))
            {
                csproj = csproj.Replace("</PropertyGroup>",
                    "    <ImportNuGetBuildTasksPackTargetsFromSdk>true</ImportNuGetBuildTasksPackTargetsFromSdk>\n  </PropertyGroup>");
            }
        }

        File.WriteAllText(csprojPath, csproj);
    }

    static int RunDotnet(string args, string workingDir, string logPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        File.WriteAllText(logPath, $"$ dotnet {args}\nexit={p.ExitCode}\n\n{stdout}\n{stderr}");
        return p.ExitCode;
    }

    static string ReadPackageEntry(string nupkgPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase) ||
            e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        Assert.True(entry != null, $"Entry '{entryName}' not found in {nupkgPath}. Entries: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
