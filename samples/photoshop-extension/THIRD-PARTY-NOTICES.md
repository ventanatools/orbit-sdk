# Photoshop sample dependency notices

The sample source is covered by the repository's Apache-2.0 license. Its npm
dependencies retain their own licenses and copyright notices. The checked-in
lockfile pins these versions:

| Component | Use | License and attribution |
|---|---|---|
| `ws` 8.22.0 | Node companion WebSocket transport | [MIT](licenses/ws-MIT.txt); Einar Otto Stangvik, Arnout Kazemier and contributors, Luigi Pinca and contributors |
| `@noble/hashes` 2.4.0 | Bundled HMAC/SHA-256 for Adobe UXP | [MIT](licenses/noble-hashes-MIT.txt); Paul Miller |
| `esbuild` 0.28.2 | UXP development build tool | [MIT](licenses/esbuild-MIT.txt); Evan Wallace |

These complete license texts were copied from the packages identified in the
lockfile. `npm run build:uxp` also copies the noble-hashes license beside the
generated UXP bundle. Preserve that file and any bundled copyright comments
when distributing a prepared Adobe plugin. If you distribute Node dependencies
or build tools, preserve their applicable licenses and notices too.

Photoshop and Adobe UXP are separate Adobe products. They are not included,
licensed, or endorsed by this SDK repository.
