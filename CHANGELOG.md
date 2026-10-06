# Changelog

## 0.1.9 — Initial testing prerelease

- Convert installed Penumbra outfits using source and destination body references.
- Generate destination size options while retaining other supported outfit choices.
- Combine body components from multiple destination mods, including Neobelly MAIN and EXTRA.
- Coordinate chest and leg choices for garments that span both regions.
- Smooth loose fabric and hem fitting, preserve skin seams, and adapt unsupported body-bone influences.
- Support symlinked mod roots and Wine paths.
- Run conversion in a separate self-contained Windows worker with cancellation and isolated output.

This is the first public release. It is marked as a GitHub prerelease and offered only through the testing channel in the custom Dalamud repository. Conversion is approximate: inspect clipping, seams and animation in game. No creator mods, game files or body assets are bundled.
