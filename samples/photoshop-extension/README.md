# Photoshop bridge sample

> **Status: developer sample.** The automated tests use an in-memory Photoshop document.
> Running Photoshop, Adobe UXP loading, the panel's persistent file token and reconnection, and
> `.ccx` installation need checking in the installed Adobe host after this protocol-3 rewrite.
> This is not an Adobe-endorsed integration or a finished end-user extension.

## What it does

Two contributions for the host app, both driven by one selected Photoshop layer:

- **Selected layer visibility** toggles, shows or hides the one selected layer. Each placement
  keeps its own **When picked** choice. Its live face shows Shown or Hidden.
- **Layer status** is passive: its face shows the selected layer's visibility or how many layers
  are selected (None, One or Many). Picking it changes nothing.

The change happens inside Photoshop's modal scope with one named history step (**Change selected
layer visibility**), so Photoshop's normal Undo and Redo work. Show and Hide create no history
step when the layer is already in that state. Document names, paths, layer names and pixels stay
in Photoshop: only the short values above leave it.

## Run it

You need Windows, Node.js 22 or later, Photoshop 25 or later, Adobe's UXP Developer Tool, and a
build of the host app with extension support.

1. From this folder, install and test:

   ```powershell
   npm ci --ignore-scripts
   npm run check
   npm test
   ```

   `npm test` first builds `uxp/vendor/bridge-crypto.js` (`npm run build:uxp` does that alone).
   The companion uses the repository's Node SDK, `@ventanatools/orbit-extensions`, through
   `file:../../node/orbit-extensions`; the other dependencies are pinned by the lockfile.
2. In the host app, open the extension settings, turn on developer mode, import this folder's
   `extension.json`, and turn the extension on after reviewing it. Then choose **Save connection
   info**: the host app writes the pairing file to
   `%USERPROFILE%\.ventana\pairings\<host-id>\example.photoshop.pairing.json`, where the
   companion finds it. The pairing file is a credential: never commit, package or share it.
3. Start the companion:

   ```powershell
   npm start
   ```

   It prints one status line per change. It starts the bridge on port 38475 and writes the bridge
   file to `%LOCALAPPDATA%\VentanaTools\Samples\Photoshop\bridge.json` (deleted again when the
   companion stops). Ctrl+C stops it. `npm start -- --pairing <path>` uses a pairing file
   elsewhere.
4. In Adobe UXP Developer Tool, add `uxp/manifest.json` and load it into Photoshop (enable
   developer mode in both first, and restart Photoshop after changing that preference). Open
   **Photoshop bridge sample** from the Plugins menu and choose **Choose bridge file** once,
   picking the `bridge.json` above. The panel keeps the file through a persistent token: when
   the companion restarts, the panel reconnects by itself, waiting 1 second, then 2, 4 and so on
   up to 30 seconds between tries. **Disconnect** stops it until you choose the file again.
5. In the host app, add **Photoshop sample: Selected layer visibility** with different choices,
   and **Layer status**. Try them on a disposable document with one selected layer.

## How it works

```text
host app
  | authenticated named pipe, protocol 3 (the Node SDK)
Node companion (npm start)
  | authenticated WebSocket on 127.0.0.1:38475/photoshop-bridge (the panel connects to localhost)
Photoshop UXP panel
  -> executeAsModal -> the selected layer's visible property
```

UXP has a WebSocket client but no Node.js, so it cannot open the host app's pipe; the companion
bridges the two.

- **Host side.** `companion/main.cjs` runs `runCompanion` from the Node SDK with the bridge's
  handler. The SDK finds the pairing file, verifies the host's proof before it proves its own,
  reconnects with backoff, cleans and paces faces, and renews them while their sessions run. The
  status lines name the host from the registry entry of the id the host proved, never a literal.
- **Bridge.** `companion/bridge.cjs` listens only on `127.0.0.1`. It accepts the exact Origin
  `file://` (Photoshop's) or no Origin, and refuses every other Origin and path. Sockets that
  have not authenticated are counted apart from the one authenticated panel (at most four), must
  say hello within 1 second and authenticate within 5. Each launch uses a fresh random bridge
  key, `HMAC-SHA256(random 32 bytes, "Example.Photoshop.Bridge.Key.v3\n")`, which is unrelated to
  the pairing secret: the bridge file never carries a host credential.
- **Bridge handshake.** The panel sends `{ type: "hello", bridgeVersion: 3, clientNonce }`; the
  companion answers `{ type: "challenge", serverNonce, proof }`; after verifying it, the panel
  sends `{ type: "authenticate", proof }`; the companion answers `{ type: "ready" }`. Each proof is
  HMAC-SHA256 with the bridge key over this ASCII transcript, LF-separated with no final newline:

  ```text
  Example.Photoshop.Bridge.v3
  ROLE
  CLIENT_NONCE
  SERVER_NONCE
  ```

  `ROLE` is `server` or `client`; nonces and proofs are lowercase hexadecimal. Both peers refuse
  unknown or duplicate members and unexpected messages, and bound sizes and rates.
- **Sessions and picks.** Every session the host app starts is forwarded to the panel; the panel
  publishes a face only when it changes (events are coalesced, and a 2-second reconciliation
  catches missed notifications). A pick is forwarded with its session's settings; the selection
  is captured when it arrives and checked again inside the modal scope. A busy Photoshop, no
  document, several selected layers, a changed selection or cancellation refuses the pick; a
  concurrent pick is refused, not queued. Nothing is replayed after a reconnection.
- **When Photoshop goes away**, every face fails as `AppUnavailable` and a pending pick fails the
  same way, so the host app says the app is unavailable rather than that the network failed.

`uxp/constants.js` holds the port, path and labels for both sides.

## Extend it

- Add a contribution to `extension.json`, give it settings in `uxp/wire.js` (`settingsFor`) and
  handle it in `uxp/platform.js`. Keep faces to short values that never contain document content.
- Use your own extension id, UXP plugin id and labels in a fork.
- `test/` shows how to test each layer: `flow.test.cjs` runs the real SDK client against the
  SDK's in-memory test host (`@ventanatools/orbit-extensions/testing`), the bridge, a real
  WebSocket and the panel's client; `interop-peer.cjs` is a peer a host team's tests can drive
  (`node test/interop-peer.cjs <pairing file>`: it prints `READY`, then `INVOKED <count>` after
  each pick that changed the document, and stops on `STOP`).
- To share it, validate and pack this folder with the SDK's tool (the repository README shows
  how to install it). The package mirrors the repository layout: the sample under
  `payload/samples/photoshop-extension/` and the Node SDK under `payload/node/orbit-extensions/`,
  so the `file:` dependency resolves. To share a prepared Adobe plugin, build the UXP bundle and
  package `uxp/` as `.ccx` with Adobe's tooling. Never include a pairing or bridge file.

## Troubleshooting

- **`Port 38475 is in use`.** Another copy of the companion, or another program, listens there.
  Stop it and start the companion again.
- **`ventana: waiting (pairing.missing)`** in the companion: the host app has not saved
  connection info yet. Choose **Save connection info** for this extension; the companion
  connects when the file appears.
- **`Could not connect (stage/kind).`** in the panel names a fixed setup step. For `token`, choose
  the bridge file again. For `metadata`, `read` or `config`, check that the companion is running
  (it writes the file at start) and that you chose its `bridge.json`, not the pairing file. For
  `socket`, check the manifest permission `ws://localhost:38475`; after changing permissions,
  unload and load the plugin. For `platform` or `session`, rebuild the UXP bundle and reload the
  plugin. Messages never contain exception text, paths, file contents or keys.
- **`Waiting for the companion; trying again in N s.`** The panel is reconnecting; start the
  companion. Choose **Disconnect** to stop.

## Licence and dependencies

The sample source is [MIT-0](LICENSE); the Node SDK is Apache-2.0. Complete dependency licence
texts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): `ws` (MIT) for the companion's
WebSocket, `@noble/hashes` (MIT) bundled into the UXP panel, and `esbuild` (MIT) to build that
bundle. Photoshop and Adobe UXP are Adobe products, not included in or endorsed by this
repository.
