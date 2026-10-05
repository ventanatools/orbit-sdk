# Photoshop developer reference package

This local preview contains source under `payload/source/`. It requires Windows,
Node 22+, Photoshop 25+ and Adobe UXP Developer Tool. Only Photoshop 27.9 was
checked in the installed-host pass. Orbit stores these files and runs nothing.
Read `payload/source/README.md` for setup, architecture, tests and open gates.

From `payload/source/`, deliberately prepare the companion and UXP bundle:

```powershell
npm ci --ignore-scripts
npm run build:uxp
npm run check
npm test
```

Enable Photoshop sample in Orbit, copy connection info to a private file outside
the package, and explicitly start Node from `payload/source/`:

```powershell
npm start -- --pairing "C:\private-folder\Orbit.pairing.json" --bridge-file "C:\private-folder\Photoshop.bridge.json"
```

Choose a new bridge filename; existing files are not overwritten. Configure
Adobe developer mode as described in the source README and load
`payload/source/uxp/manifest.json`. In its panel choose the new bridge file and
wait for Connected in both Photoshop and Orbit. The Adobe `.ccx` installation
path is separate; clean `.ccx` installation remains unverified. Store-signed
Orbit extension compatibility also remains unverified. This is a source
developer example, not a ready-to-install consumer integration.

Start with a disposable Photoshop document/layer. Add Selected layer visibility
items with Toggle, Show and Hide choices, plus a passive Layer status display.
Never package/share pairing or bridge files. After an Orbit package update,
re-enable and copy fresh connection info; restart Node, reload changed UXP code
and select its fresh bridge file. License terms are in `payload/LICENSE` and `payload/NOTICE`.
Third-party licenses are documented in the source README and copied next to
the generated UXP bundle. Public setup instructions are at
https://dev.ventana.tools/orbit/examples/photoshop/.
