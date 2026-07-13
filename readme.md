# NuGet Readme

[![Version](https://img.shields.io/nuget/vpre/Readme.svg?color=royalblue)](https://www.nuget.org/packages/Readme)
[![Downloads](https://img.shields.io/nuget/dt/Readme.svg?color=darkmagenta)](https://www.nuget.org/packages/Readme)
[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](https://github.com/devlooped/oss/blob/main/osmfeula.txt)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/oss/blob/main/license.txt)

## Usage
<!-- #content -->
Adds automatic package readme packing and include-directive resolution at pack time.
Works with **SDK Pack** and **NuGetizer** (no NuGetizer required).

```xml
<PackageReference Include="Readme" Version="*" />
```

You don't need to set `PrivateAssets=all`: Readme is a development dependency and automatically excludes itself from packed dependencies.

When the project is packable and a `readme.md` (or `$(PackageReadmeFile)`) is present:

1. The readme is included in the package automatically (`PackReadme=false` to opt out).
2. Include directives are resolved before pack (local files, nested includes, `#fragment` sections, HTTP(S) URLs).
3. The processed file is written under `$(BaseIntermediateOutputPath)` and **that** intermediate file is what is packed.

### Example

This package's own project-level `readme.md` is essentially three includes:

```exclude
<!-- include ../../readme.md#content -->

<!-- include https://github.com/devlooped/.github/raw/main/osmf.md -->

<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
```

| Include | What it does |
|---------|----------------|
| `../../readme.md#content` | Local file + `#fragment` — pulls the docs body between matching `<!-- #content -->` … `<!-- #content -->` anchors in the repo readme |
| `https://…/osmf.md` | Remote HTTPS — shared Open Source Maintenance Fee / EULA notice |
| `https://…/footer.md` | Remote HTTPS — shared sponsors footer |

That keeps the package readme short, reuses the repo docs, and keeps EULA and sponsors in sync across packages.

### Include syntax

Use an HTML comment starting with `include` and a path (relative file, `#fragment`, or `http(s)` URL). Nested includes are supported.

Fragments resolve in this order:

1. **Explicit comment anchors** — matching `<!-- #section -->` … `<!-- #section -->` pairs (when present). The markers and everything between them is included, so you control whether a section title is in the range (for example put `## Usage` inside the pair to keep the title, or place the pair after the heading to omit it).
2. **GitHub heading auto-anchors** — otherwise the Markdown ATX heading whose [GitHub auto-anchor](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#section-links) matches the fragment (for example `## Usage` for `#usage`). The heading line itself is included, through the line before the next heading of the same or higher level.

Use explicit `<!-- #fragment -->` markup when you need maximum control over whether the section name is included; auto-anchors always include the matching heading.

Unresolved includes log a warning and leave the marker in place (pack does not fail).

To document the include syntax itself without expansion, put example directives in a fenced code block with language `exclude` (as in the example above). Includes in other code fences are still resolved.

### Remote includes

Absolute `http(s)` includes from a **local** file are always allowed (subject to scheme). Includes nested inside remote content resolve relative paths against that URL’s base. Absolute URLs from remote content are allowed only when the host is the same as (or a subdomain of) the including remote host, or is listed in `@(ReadmeIncludeDomain)` (subdomains of listed domains count too). Other schemes/hosts warn and stay unresolved.

### Properties

| Property / item | Default | Description |
|-----------------|---------|-------------|
| `PackReadme` | `true` | Auto-pack the package readme when present |
| `PackageReadmeFile` | `readme.md` if it exists | Package readme path / in-package filename |
| `ReadmeIncludeScheme` | `https` | Semicolon-separated URI schemes allowed for remote includes (add `http` only if needed) |
| `@(ReadmeIncludeDomain)` | _(empty)_ | Hosts allowed for absolute remote includes nested inside remote content |

```xml
<PropertyGroup>
  <!-- Optional: allow cleartext remote includes -->
  <ReadmeIncludeScheme>https;http</ReadmeIncludeScheme>
</PropertyGroup>
<ItemGroup>
  <ReadmeIncludeDomain Include="cdn.example.com" />
</ItemGroup>
```
<!-- #content -->
---
<!-- include https://github.com/devlooped/.github/raw/main/osmf.md -->
## Open Source Maintenance Fee

To ensure the long-term sustainability of this project, users of this package who generate 
revenue must pay an [Open Source Maintenance Fee](https://opensourcemaintenancefee.org). 
While the source code is freely available under the terms of the [License](license.txt), 
this package and other aspects of the project require [adherence to the Maintenance Fee](osmfeula.txt).

To pay the Maintenance Fee, [become a Sponsor](https://github.com/sponsors/devlooped) at the proper 
OSMF tier. A single fee covers all of [Devlooped packages](https://www.nuget.org/profiles/Devlooped).

<!-- https://github.com/devlooped/.github/raw/main/osmf.md -->
---
<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
# Sponsors 

<!-- sponsors.md -->
[![Clarius Org](https://avatars.githubusercontent.com/u/71888636?v=4&s=39 "Clarius Org")](https://github.com/clarius)
[![MFB Technologies, Inc.](https://avatars.githubusercontent.com/u/87181630?v=4&s=39 "MFB Technologies, Inc.")](https://github.com/MFB-Technologies-Inc)
[![SandRock](https://avatars.githubusercontent.com/u/321868?u=99e50a714276c43ae820632f1da88cb71632ec97&v=4&s=39 "SandRock")](https://github.com/sandrock)
[![DRIVE.NET, Inc.](https://avatars.githubusercontent.com/u/15047123?v=4&s=39 "DRIVE.NET, Inc.")](https://github.com/drivenet)
[![Keith Pickford](https://avatars.githubusercontent.com/u/16598898?u=64416b80caf7092a885f60bb31612270bffc9598&v=4&s=39 "Keith Pickford")](https://github.com/Keflon)
[![Thomas Bolon](https://avatars.githubusercontent.com/u/127185?u=7f50babfc888675e37feb80851a4e9708f573386&v=4&s=39 "Thomas Bolon")](https://github.com/tbolon)
[![Kori Francis](https://avatars.githubusercontent.com/u/67574?u=3991fb983e1c399edf39aebc00a9f9cd425703bd&v=4&s=39 "Kori Francis")](https://github.com/kfrancis)
[![Reuben Swartz](https://avatars.githubusercontent.com/u/724704?u=2076fe336f9f6ad678009f1595cbea434b0c5a41&v=4&s=39 "Reuben Swartz")](https://github.com/rbnswartz)
[![Jacob Foshee](https://avatars.githubusercontent.com/u/480334?v=4&s=39 "Jacob Foshee")](https://github.com/jfoshee)
[![](https://avatars.githubusercontent.com/u/33566379?u=bf62e2b46435a267fa246a64537870fd2449410f&v=4&s=39 "")](https://github.com/Mrxx99)
[![Eric Johnson](https://avatars.githubusercontent.com/u/26369281?u=41b560c2bc493149b32d384b960e0948c78767ab&v=4&s=39 "Eric Johnson")](https://github.com/eajhnsn1)
[![Jonathan ](https://avatars.githubusercontent.com/u/5510103?u=98dcfbef3f32de629d30f1f418a095bf09e14891&v=4&s=39 "Jonathan ")](https://github.com/Jonathan-Hickey)
[![Ken Bonny](https://avatars.githubusercontent.com/u/6417376?u=569af445b6f387917029ffb5129e9cf9f6f68421&v=4&s=39 "Ken Bonny")](https://github.com/KenBonny)
[![Simon Cropp](https://avatars.githubusercontent.com/u/122666?v=4&s=39 "Simon Cropp")](https://github.com/SimonCropp)
[![agileworks-eu](https://avatars.githubusercontent.com/u/5989304?v=4&s=39 "agileworks-eu")](https://github.com/agileworks-eu)
[![Zheyu Shen](https://avatars.githubusercontent.com/u/4067473?v=4&s=39 "Zheyu Shen")](https://github.com/arsdragonfly)
[![Vezel](https://avatars.githubusercontent.com/u/87844133?v=4&s=39 "Vezel")](https://github.com/vezel-dev)
[![ChilliCream](https://avatars.githubusercontent.com/u/16239022?v=4&s=39 "ChilliCream")](https://github.com/ChilliCream)
[![4OTC](https://avatars.githubusercontent.com/u/68428092?v=4&s=39 "4OTC")](https://github.com/4OTC)
[![domischell](https://avatars.githubusercontent.com/u/66068846?u=0a5c5e2e7d90f15ea657bc660f175605935c5bea&v=4&s=39 "domischell")](https://github.com/DominicSchell)
[![Adrian Alonso](https://avatars.githubusercontent.com/u/2027083?u=129cf516d99f5cb2fd0f4a0787a069f3446b7522&v=4&s=39 "Adrian Alonso")](https://github.com/adalon)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![Ryan McCaffery](https://avatars.githubusercontent.com/u/16667079?u=c0daa64bb5c1b572130e05ae2b6f609ecc912d4d&v=4&s=39 "Ryan McCaffery")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)
[![eska-gmbh](https://avatars.githubusercontent.com/devlooped-team?s=39 "eska-gmbh")](https://github.com/eska-gmbh)
[![Geodata AS](https://avatars.githubusercontent.com/u/5946299?v=4&s=39 "Geodata AS")](https://github.com/geodata-no)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
