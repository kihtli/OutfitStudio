# Outfit Studio

An experimental Dalamud plugin by **kihtli** that refits Penumbra outfits from one body to another. Select the source body, an outfit, and one or more destination body mods; the result is a separate mod with destination size options.

**Testing prerelease: v0.1.10.** This is approximate geometry fitting. Check clipping, seams, shape toggles and movement in game before relying on a conversion.

## Install the testing release

1. Have Dalamud and Penumbra installed and running. This release targets **Dalamud API 15**, **Penumbra IPC API 5**, and Windows x64, including Dalamud under Wine.
2. Add this URL under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, enable it and save:

   ```text
   https://raw.githubusercontent.com/kihtli/plugins/main/repo.json
   ```

3. Enable Dalamud's plugin testing option, then open `/xlplugins`, search for **Outfit Studio**, and install its testing release.
4. Open the plugin with `/outfitstudio`.

Outfit Studio is **testing-only** in this repository. The complete Windows conversion worker is included; Blender and a separate .NET runtime installation are not needed. Source body mods, destination body mods and outfits are not included.

The [GitHub prerelease](https://github.com/kihtli/OutfitStudio/releases/tag/v0.1.10) also provides a portable ZIP. For manual development-plugin installation, extract the entire archive and add `OutfitStudio.dll` as a dev plugin location. Keep the complete `worker/` folder beside it. Avoid enabling both a manual and repository installation at the same time.

## Convert an outfit

1. Select **Source body** and **Outfit**. Under **Destination bodies**, tick the body mods whose sizes and styles you want.
2. Click **Find destination sizes** and review the available output choices. Source sizes are matched automatically.
3. Name the new mod and click **Create outfit**.
4. Enable the generated mod in Penumbra and choose its sizes. Inspect its fit in game.

The original mods are read-only inputs. Supported material and style options are retained. Chest and leg selections coordinate the fitting of clothes that span both regions, such as a dress. Generating all combinations can take time and disk space; analysis reports the model count before creation.

For **Néolithe Neobelly**, select both **MAIN - Neobelly** and **EXTRA - Neobelly**. These components supply one combined set of destination choices. Selecting fitting references does not enable those body mods or add their textures, additional body components or physics to the outfit.

**Accessories with several body fits:** choose a source body actually included in the accessory's labels. The converter uses that body's fits and retains each accessory style for every destination size. It identifies the fitted body region from the geometry, so an item in a ring slot can fit another body region. Connected accessory parts retain their rigid shape; flexible accessories may need advanced manual fitting. Fixed support models for other races remain unchanged. Ambiguous accessories spanning several body regions still require advanced references.

Symlinked Penumbra roots and Windows junctions are supported, including Wine drive mappings. Individual linked files inside a mod are rejected. The optional **Advanced conversion settings** provide manual body references and selected-options conversion when automatic size matching is unsuitable.

Recreate an outfit after a converter update to apply geometry changes. Updating the plugin does not rewrite existing converted mods.

## Capabilities and limits

- Maps compatible source and destination body surfaces using matching topology or UV correspondence.
- Processes stored outfit LODs and shape vertices while preserving materials, textures, mesh attributes and unrelated model data.
- Keeps close garment detail, smooths loose fabric fitting and reduces clipping around originally covered skin.
- Keeps compatible weights and adapts unsupported YAB/IVCS body influences while preserving garment-specific joints.
- Writes a separate output, supports cancellation, and records references and warnings in a local conversion report.

The source body, body region, race and bind pose must be appropriate. Ambiguous references and unsupported option layouts stop with an explanation. This does not provide cloth simulation, guaranteed clipping-free animation, arbitrary skeleton conversion, target skin replacement or physics installation. Embedded skin is deformed, not replaced wholesale. Existing body scales, skeletons and physics still affect the result.

Prioritized testing covers **YAB+ → RueXB+** and **YAB+ → Néolithe/Neobelly**. Other pairs need their own review. See [verification and known limits](docs/VERIFICATION.md) and the [changelog](CHANGELOG.md).

## Build and test

Install the **.NET 10 SDK** and current **Dalamud API 15 development assemblies**. Set `DALAMUD_HOME` if the SDK cannot find them. Linux packaging also needs Python 3.

```sh
dotnet test tests/OutfitStudio.Tests/OutfitStudio.Tests.csproj -c Release
bash scripts/build.sh --zip
```

On Windows use `scripts/build.ps1 -Zip`. The scripts publish a complete bundle to `release/` and optionally a versioned ZIP under `artifacts/`. Add the absolute path to `release/OutfitStudio.dll` as a Dalamud dev plugin location once. Future builds reuse that location: unload the plugin and stop conversions before rebuilding, then reload it.

The self-contained worker pins its tested .NET runtime version. If that version changes, update the corresponding license notices under `licenses/` before packaging. Release builds omit debug symbols and map source paths to a neutral location.

The committed tests generate synthetic model fixtures; no creator assets are required. Private fixture libraries, local diagnostic runs, generated outfits, compiled binaries and development settings are excluded from the source repository.

## Attribution

This project is licensed under [MIT](LICENSE). The bundled .NET runtime has its own [third-party notices](THIRD-PARTY-NOTICES.md).

Format and integration references include [Dalamud](https://github.com/goatcorp/Dalamud), [Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api), [Penumbra schemas](https://github.com/xivdev/Penumbra/tree/testing/schemas), [Lumina model structures](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/MdlStructs.cs), and [Penumbra.GameData](https://github.com/Ottermandias/Penumbra.GameData). Those projects and FFXIV remain independently owned.

Converted mods retain creator attribution. Check the original creators' permissions before distributing conversions. Conversion reports contain local paths: review them before attaching them to a public issue.
