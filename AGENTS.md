# AGENTS.md

## Purpose

`Readme` is a development dependency NuGet package that, for packable projects:

1. Auto-packs a project `readme.md` / `$(PackageReadmeFile)` (opt-out: `PackReadme=false`).
2. Resolves `<!-- include ... -->` directives at pack time (local, nested, `#fragment`, HTTP(S)).
3. Writes the processed readme under `$(BaseIntermediateOutputPath)` and packs **that** file.

Compatible with both SDK Pack and NuGetizer. Does not require NuGetizer.

## Layout

| Path | Role |
|------|------|
| `src/Readme/IncludesResolver.cs` | Pure include-resolution logic (ported from NuGetizer) |
| `src/Readme/ProcessReadmeIncludes.cs` | Thin MSBuild task wrapping the resolver |
| `src/Readme/build/Readme.props` | Defaults: `PackReadme` (include processing is always on) |
| `src/Readme/build/Readme.targets` | Auto-pack item metadata + pre-pack process + NuGetizer `PackageFile` retarget |
| `src/Readme/buildTransitive/*` | Re-exports `build/` for transitive imports |
| `src/Tests/` | Unit tests on fixtures + SDK Pack / NuGetizer pack scenario tests |

## Design notes

- **Intermediate path packing**: after include expansion, the packed source is always `$(BaseIntermediateOutputPath)$(PackageReadmeFile)` so both pack engines consume identical content.
- **Dual hooks**: include processing always runs before `GenerateNuspec` / `_GetPackageFiles` (SDK Pack) and `GetPackageContents` (NuGetizer). `RetargetNuGetizerProcessedReadme` rewrites `@(PackageFile)` after NuGetizer inference because inference keys off project-directory identity, not intermediate paths.
- **Warnings not errors**: missing includes / anchors call `Log.LogWarning` and leave markers in place.
- **Package layout**: development dependency; task DLL + props/targets under `build/` (and `buildTransitive/`). Primary output is not under `lib/` (`IncludeBuildOutput=false`).
- **Self-pack caveat**: this repo’s `Directory.Build.targets` matches None items with Filename `readme` case-insensitively, which also hits `Readme.props` / `Readme.targets`. `FixBuildPackagePaths` in `Readme.csproj` restores correct `PackagePath` values before NuGetizer packs the package itself.
- **PackReadme=false**: `PackageReadmeFile` is inferred only in targets (after the project file). When `PackReadme=false`, evaluation-time and pre-pack targets clear `PackageReadmeFile` and set Pack=false on readme items so SDK Pack does not hit NU5039.

## Non-goals (not ported from NuGetizer)

- `$token$` replacement, GitHub relative-link rewriting, license-file include processing, analyzer diagnostics for missing readme.

<!-- exclude -->
