# Verification and known limits

## Initial testing prerelease — 0.1.9

This release has automated and offline mesh verification. It is not certified as clipping-free or suitable for every outfit/body combination. Live Dalamud UI, Penumbra interaction, physics and animated fit still need in-game review.

### Repeatable source tests

The Release suite passes **244 tests**, with no skips on Linux. It covers binary model parsing and writing, LODs and shapes, bone palettes and weight transfer, geometry fitting, loose-fabric rotation/stretch interpolation, binding limits, clothing clearance, skin seams, automatic size planning, conditional option regeneration, multiple destination owners, filesystem safety, archive extraction, transactional output, cancellation and worker communication. Process-boundary fixtures use a POSIX shell and are skipped on Windows.

Run the committed synthetic fixtures with:

```sh
dotnet test tests/OutfitStudio.Tests/OutfitStudio.Tests.csproj -c Release
```

### Offline checks with separately installed creator assets

The development checks used privately installed YAB+, RueXB+, Néolithe and Neobelly references with Limerence, Pathos and In Bloom outfits. These assets, local paths, generated models, renders and diagnostic logs are not distributed in this repository or release.

- **Limerence → Neobelly:** 100 models cover all 36 chest styles with SFW Large legs and all 15 leg styles with SFW Almond L, for 50 unique selected pairs. All 50 top models improve the previously reproduced lower-hem kink. The largest measured hem displacement jump falls from 34.0 to 5.03 mm; the fixed-region audit finds no reversed hem faces. Selected-pair and embedded-skin sampling finds no measured penetrations. This covers every option, not every possible chest/leg combination.
- **Pathos → RueXB+:** the full package generates 144 linked models. All 72 linked leg variants pass the original thigh-seam check. Nine representative leg files retain the earlier accepted geometry exactly. Eight chest options retain sampled coverage and close side fitting. One outward ray at a cutout edge changes classification after approximately 3 micrometres of cloth movement; this is retained as a limitation rather than silently counted as clear.
- **In Bloom → Neobelly:** the representative Pushup L top remains byte-identical to the earlier accepted fit. The Large leg/skirt model retains embedded skin, lower legs and authored skirt influences, with no new collapsed or reversed cloth faces relative to that fit.
- **Conditional settings:** independent checks of Penumbra 1.7.2.1's native settings rules confirm that fixed single-option mapping groups do not add visible checkbox categories. The private MAIN/EXTRA package retains its outfit controls and resolves exactly one model per enabled region.
- **File integrity:** 275 final models pass an independent audit using unmodified Penumbra.GameData model parsing. This checks binary structure, topology, model metadata and permitted changes.
- **Windows/Wine:** the final self-contained worker recreates 144 Pathos and 12 MAIN/EXTRA models under an isolated Wine prefix using a symlinked input library. All 156 models, automatic plans, normalized option metadata and reference hashes match native output.

The release publication rebuild omits debug symbols, adds redistributable license notices and uses portable metadata. It does not change the conversion algorithm.

### Practical limits

Skin sampling does not cover every triangle interior. Boundary, opposed-normal, degenerate and missing samples are recorded separately in local development diagnostics and are not proof of clearance. Offline rendering does not exercise textures, animation, game skeleton behavior or physics. Source-to-target UV correspondence does not by itself establish anatomical compatibility.

The loose-fabric transition distances are general fitting heuristics checked against these examples. They cannot guarantee a nonfolding deformation for arbitrary bodies. Incorrect source-body selection, unusual skeletons, unsupported model structures and incompatible option layouts remain possible reasons for failure.

Keep the original outfit, test the new mod separately and inspect all sizes and movement you intend to use. Report the plugin version, body pair, affected outfit part and whether the problem appears at rest or in motion. Do not attach creator assets or unsanitized local conversion reports to public issues.
