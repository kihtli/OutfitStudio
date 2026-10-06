# Third-party notices

Outfit Studio's own source is covered by [LICENSE](LICENSE). The notices below
describe separate components and development dependencies; their original
licenses continue to apply.

## Redistributed .NET components

The Windows x64 conversion worker is self-contained and includes Microsoft .NET
10.0.12 runtime and host components from these NuGet packages:

| Package | Version | Included notices |
| --- | --- | --- |
| [Microsoft.NETCore.App.Runtime.win-x64](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12) | 10.0.12 | [MIT license](licenses/dotnet-runtime/LICENSE.TXT), [third-party notices](licenses/dotnet-runtime/THIRD-PARTY-NOTICES.TXT) |
| [Microsoft.NETCore.App.Host.win-x64](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.12) | 10.0.12 | [MIT license](licenses/dotnet-host/LICENSE.TXT), [third-party notices](licenses/dotnet-host/THIRD-PARTY-NOTICES.TXT) |

These files are copied verbatim from the corresponding packages. They retain the
.NET Foundation and Contributors copyright notice and the upstream notices for
components included in .NET. The source project is
[dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.12).
The version files beside the notices identify the package versions covered by
them. A release using a different runtime must update these notices from the
matching packages.

## Build and test dependencies

These dependencies are restored during development and are not included as
assemblies in the plugin release. The licenses listed here are those declared by
the corresponding NuGet package metadata.

| Dependency | Version | Declared license |
| --- | --- | --- |
| [Dalamud.NET.Sdk](https://www.nuget.org/packages/Dalamud.NET.Sdk/15.0.0) | 15.0.0 | [MIT](https://licenses.nuget.org/MIT) |
| [DalamudPackager](https://www.nuget.org/packages/DalamudPackager/15.0.0) | 15.0.0 | [EUPL-1.2](https://licenses.nuget.org/EUPL-1.2) |
| [DotNet.ReproducibleBuilds](https://www.nuget.org/packages/DotNet.ReproducibleBuilds/1.2.39) | 1.2.39 | [MIT](https://licenses.nuget.org/MIT) |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) | 17.14.1 | [MIT](https://licenses.nuget.org/MIT) |
| [xunit](https://www.nuget.org/packages/xunit/2.9.3) | 2.9.3 | [Apache-2.0](https://licenses.nuget.org/Apache-2.0) |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.1) | 3.1.1 | [Apache-2.0](https://licenses.nuget.org/Apache-2.0) |

## External integrations and format references

[Dalamud](https://github.com/goatcorp/Dalamud) is a separately installed plugin
host. [Penumbra](https://github.com/xivdev/Penumbra) is a separately installed mod
manager; Outfit Studio calls its public IPC interface. Neither is bundled here.

The implementation uses the published
[Penumbra API](https://github.com/Ottermandias/Penumbra.Api),
[Penumbra package format](https://github.com/xivdev/Penumbra/tree/testing/schemas),
[Lumina MDL layout](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/MdlStructs.cs),
and [Penumbra.GameData model structures](https://github.com/Ottermandias/Penumbra.GameData/tree/main/Files/ModelStructs)
as interoperability references. Their source files and binaries are not included
in this repository or release bundle.

No FFXIV game assets or creator body/outfit models, materials, or textures are
included. Offline development checks used separately installed mods;
the committed unit tests construct synthetic fixtures. The Outfit Studio license does not
grant redistribution rights to assets read from those mods or retained in a
converted outfit.
