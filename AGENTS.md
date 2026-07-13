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
| `src/Readme/TokenReplacer.cs` | Pure `$token$` replacement (same algorithm as NuGetizer) |
| `src/Readme/ProcessReadmeIncludes.cs` | Thin MSBuild task wrapping resolver + token replacement |
| `src/Readme/build/Readme.props` | Defaults: `PackReadme` (include processing is always on) |
| `src/Readme/build/Readme.targets` | Auto-pack item metadata + pre-pack process + NuGetizer `PackageFile` retarget |
| `src/Readme/buildTransitive/*` | Re-exports `build/` for transitive imports |
| `src/Tests/` | Unit tests on fixtures + SDK Pack / NuGetizer pack scenario tests |

## Design notes

- **Intermediate path packing**: after include expansion, the packed source is always `$(BaseIntermediateOutputPath)$(PackageReadmeFile)` so both pack engines consume identical content.
- **Dual hooks**: include processing always runs before `GenerateNuspec` / `_GetPackageFiles` (SDK Pack) and `GetPackageContents` (NuGetizer). `RetargetNuGetizerProcessedReadme` rewrites `@(PackageFile)` after NuGetizer inference because inference keys off project-directory identity, not intermediate paths.
- **Warnings not errors**: missing includes / anchors call `Log.LogWarning` and leave markers in place.
- **` ```exclude ` fences**: includes inside fenced code blocks with language `exclude` are left literal (for documenting include syntax).
- **Fragment resolution**: explicit `<!-- #fragment -->` pairs win (placement controls whether a section title is included); otherwise GitHub heading auto-anchors match and **include the heading line** (e.g. `## Usage` for `#usage`).
- **Token replacement**: after includes, `$token$` placeholders are replaced via `@(ReadmeReplacementToken)` (official NuGet: Id/Version/Author/Title/Description/Copyright/Configuration; plus Authors and Product; consumer-extensible). Case-insensitive names; last value wins for duplicates. Named separately from NuGetizer's `@(PackageReplacementToken)` to avoid item conflicts.
- **Package layout**: development dependency (`DevelopmentDependency=true`); task DLL + props/targets under `build/` (and `buildTransitive/`). Primary output is not under `lib/` (`IncludeBuildOutput=false`).
- **Self-private asset** (NuGetizer pattern): `build/Readme.targets` does `<PackageReference Update="Readme" PrivateAssets="all" Pack="false" />` so consumers never need to set `PrivateAssets` and pack never emits Readme as a dependency.
- **Self-pack caveat**: this repo’s `Directory.Build.targets` matches None items with Filename `readme` case-insensitively, which also hits `Readme.props` / `Readme.targets`. `FixBuildPackagePaths` in `Readme.csproj` restores correct `PackagePath` values before NuGetizer packs the package itself.
- **PackReadme=false**: `PackageReadmeFile` is inferred only in targets (after the project file). When `PackReadme=false`, evaluation-time and pre-pack targets clear `PackageReadmeFile` and set Pack=false on readme items so SDK Pack does not hit NU5039.

## Non-goals (not ported from NuGetizer)

- GitHub relative-link rewriting, license-file include/token processing, analyzer diagnostics for missing readme.

<!-- exclude -->
