# readme

[![Version](https://img.shields.io/nuget/vpre/Readme.svg?color=royalblue)](https://www.nuget.org/packages/Readme)
[![Downloads](https://img.shields.io/nuget/dt/Readme.svg?color=darkmagenta)](https://www.nuget.org/packages/Readme)
[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](https://github.com/devlooped/oss/blob/main/osmfeula.txt)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/oss/blob/main/license.txt)

<!-- include https://github.com/devlooped/.github/raw/main/osmf.md -->
<!-- #content -->
## Usage

Adds automatic package readme packing and include-directive resolution at pack time.
Works with **SDK Pack** and **NuGetizer** (no NuGetizer required).

```xml
<PackageReference Include="Readme" Version="*" PrivateAssets="all" />
```

When the project is packable and a `readme.md` (or `$(PackageReadmeFile)`) is present:

1. The readme is included in the package automatically (`PackReadme=false` to opt out).
2. Include directives are resolved before pack (local files, nested includes, `#fragment` sections, HTTP(S) URLs).
3. The processed file is written under `$(BaseIntermediateOutputPath)` and **that** intermediate file is what is packed.

### Include syntax

Use an HTML comment starting with `include` and a path (relative file, `#fragment`, or `http(s)` URL). Nested includes are supported. Fragments use matching HTML comments as anchors (for example `#section`) in the included file.

Unresolved includes log a warning and leave the marker in place (pack does not fail).

### Properties

| Property | Default | Description |
|----------|---------|-------------|
| `PackReadme` | `true` | Auto-pack the package readme when present |
| `PackageReadmeFile` | `readme.md` if it exists | Package readme path / in-package filename |
<!-- #content -->
---
<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
