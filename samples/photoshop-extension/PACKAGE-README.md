# Photoshop bridge sample

A developer sample: layer visibility actions and live layer status in the host app, through a
Node companion and an Adobe Photoshop UXP panel. Installing this package runs nothing.

The package holds source only, in the repository's layout: the sample in
`payload/samples/photoshop-extension/` and the Node SDK it uses in `payload/node/orbit-extensions/`.
You need Windows, Node.js 22 or later, Photoshop 25 or later and Adobe's UXP Developer Tool.

From `payload/samples/photoshop-extension/`:

```powershell
npm ci --ignore-scripts
npm run build:uxp
npm test
npm start
```

Before `npm start`, turn the extension on in the host app and choose **Save connection info**;
the companion finds the pairing file in your user profile. Then load
`payload/samples/photoshop-extension/uxp/manifest.json` with Adobe's UXP Developer Tool, open the
**Photoshop bridge sample** panel and choose the bridge file the companion names when it starts.
`README.md` in that folder explains setup, the design, tests and troubleshooting.

Never package or share pairing or bridge files. The sample source is MIT-0
(`payload/samples/photoshop-extension/LICENSE`); the Node SDK is Apache-2.0
(`payload/node/orbit-extensions/LICENSE`).
