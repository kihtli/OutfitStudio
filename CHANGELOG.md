# Changelog

## 0.1.10 — Accessory fitting (testing prerelease)

- Support accessories with explicit body, size and style labels, retaining each style in the destination choices.
- Infer an accessory's fitted body region from its geometry instead of its equipment slot.
- Preserve fixed support models for races outside the selected source body.
- Match Bibo body-shape and coverage variants explicitly and normalize size labels such as X-Large.
- Fit localized accessories using their original nearest body faces and adjacent surface, retaining strict correspondence checks on that surface.
- Preserve rigid accessory parts and use authored bone weights to keep mechanical assemblies together and place connecting links.
- Align uniformly shifted UV tiles without changing stored texture coordinates or increasing the correspondence tolerance.
- Accept unused offsets on empty model LOD ranges while continuing to validate nonempty ranges.

## 0.1.9 — Initial testing prerelease

- Convert installed Penumbra outfits using source and destination body references.
- Generate destination size options while retaining other supported outfit choices.
- Combine body components from multiple destination mods, including Neobelly MAIN and EXTRA.
- Coordinate chest and leg choices for garments that span both regions.
- Smooth loose fabric and hem fitting, preserve skin seams, and adapt unsupported body-bone influences.
- Support symlinked mod roots and Wine paths.
- Run conversion in a separate self-contained Windows worker with cancellation and isolated output.

This is the first public release. It is marked as a GitHub prerelease and offered only through the testing channel in the custom Dalamud repository. Conversion is approximate: inspect clipping, seams and animation in game. No creator mods, game files or body assets are bundled.
