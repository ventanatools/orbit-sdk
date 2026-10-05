# Photoshop actions and live state sample

The default `extension.json` is a schema-2 bundle for Orbit: **Selected layer visibility**
can toggle, show or hide one selected layer, while **Layer status** is passive. Each
placement has its own choice setting. Both can publish short live text faces through
a separately started companion and an Adobe UXP plugin. Manifest, named-pipe
and bridge protocol 2 are the only supported contract. Older declarations and
pairing files are rejected; there is no compatibility runtime or automatic
development-data migration.

The [developer walkthrough](../../docs/extension-examples.md)
uses this as the complex application-adapter reference alongside the simpler
Countdown SDK example. The [local distribution guide](../../docs/extension-distribution.md)
packages its curated source without credentials or runtime dependencies.
`orbit-package.json` becomes root package metadata; the npm `package.json`
remains inside the source payload. Reviewed v2 package updates revoke pairing
and stay off, preserving ring data. Unsupported older declarations remain
preserved and refused rather than silently becoming runnable v2 contributions.

The command changes the selected layer's own visibility property. It requires
exactly one selected layer, refuses a changed selection while waiting for a
modal scope, and never saves a document. Photoshop's normal Undo remains
available through an explicit history transaction; Show/Hide no-ops create no
history entry. Document names, paths, layer names and pixels stay in Photoshop.

The automated tests use a fake Photoshop document model. **Running Photoshop,
Adobe UXP loading, loopback permission behavior and `.ccx` installation are not
verified by those tests.** Installed Photoshop 27.9 with UXP Developer Tools
2.3.0 passed a native v2 check on 2026-10-05: mutual authentication through the
actual Node companion to scratch Orbit, independent Toggle/Show/Hide actions,
live visibility/selection faces, and Show followed by Photoshop Undo and Redo
with the layer preserved. Basic focus return, panel hiding, plugin reload and
explicit reconnection were exercised. Individual Adobe notification delivery,
deeper lifecycle/refusal paths, accessibility and `.ccx` installation remain
open. This is a development example, not an Adobe-endorsed integration or a
finished end-user extension.

## How it connects

```text
Orbit (action and face host)
  ↕ authenticated Windows named pipe, protocol 2
Node companion (started by you)
  ↕ authenticated WebSocket: UXP uses localhost:38475; listener binds 127.0.0.1
Photoshop UXP panel (connected by you)
  → executeAsModal → selectedLayer.visible
```

Protocol 2 receives action/request IDs, opaque session IDs and the declared
choice settings, and publishes bounded
text faces for those sessions. No ring contents, foreground-window identity, document
names, layer names, pixels, paths, scripts or arbitrary `batchPlay` commands cross
this sample's connection. Outcomes remain `done`, `refused`, `failed` or `unsupported`.

UXP has a WebSocket **client**, but it does not run Node.js. A normal UXP plugin
cannot use Node's `net` module to open Orbit's pipe. The companion bridges the
two supported transports. Orbit itself opens no WebSocket/HTTP endpoint and
does not launch either companion or Photoshop. See Adobe's [UXP runtime](https://developer.adobe.com/uxp/guides/explanation/tech-stack/)
and [network APIs](https://developer.adobe.com/uxp/guides/how-to/recipes/network/).

## Prepare the sample

Use Windows, Node.js 22 or newer, and an Orbit version with
**Settings > Extensions** enabled. Store-signed extension compatibility has not
yet been verified; this repository does not include an Orbit build. For the real application test, also
install Photoshop 25 or newer and Adobe's UXP Developer Tool. Version 25 is the
sample's declared minimum, not a claim that every supported version has been
tested. The panel requires `crypto.getRandomValues` and manifest v5 permissions.

Before loading a development plugin, enable Developer Mode in both Adobe UXP
Developer Tool and Photoshop. Keep Photoshop running while loading the sample.
Apply the Photoshop preference and restart Photoshop before choosing **Load**
again. In the installed 27.9 check, loading succeeded after that restart without
changing the manifest. See Adobe's
[development prerequisites](https://developer.adobe.com/uxp/faq/).

From this folder:

```powershell
npm ci --ignore-scripts
npm run check
npm test
```

`npm test` first builds `uxp/vendor/bridge-crypto.js` and copies its MIT license.
`npm run build:uxp` does the same preparation without tests. Generated files and
`node_modules` are ignored; do this before loading or packaging `uxp/`.
The lockfile pins all dependencies. Nothing is downloaded at Orbit runtime.

The manifest allows `ws://localhost:38475`, and the panel connects to exactly
`ws://localhost:38475/orbit-photoshop`. In the installed Photoshop 27.9 check,
its permission matcher rejected the IPv4 literal but accepted `localhost`.
The companion still binds only `127.0.0.1`, and its generated bridge file
retains `ws://127.0.0.1:38475/orbit-photoshop`. The panel's parser requires that
canonical value; do not edit the file to change its host or port.

## Connect to Orbit and Photoshop

1. In Orbit, open **Settings → Extensions**, enable **Extension developer mode**, choose
   **Import manifest…**, and select this folder's `extension.json`.
   Normal package installation does not require that toggle.
   On **Photoshop sample**, choose
   **Enable…**, review its two contributions, and confirm **Enable**. Importing
   the manifest runs nothing.
2. Use **Copy connection info** for that registration. Save that JSON as, for
   example, `Orbit.pairing.json` in a private local folder outside this repository.
   This file is a credential. Do not share it with a ring, sample or bug report.
3. Start the companion with the file's path and a **new** output file path:

   ```powershell
   npm start -- --pairing "C:\private-folder\Orbit.pairing.json" --bridge-file "C:\private-folder\Photoshop.bridge.json"
   ```

   Replace both example paths. The output directory must already exist. An
   existing bridge file is never overwritten. The companion uses port 38475;
   another listener there causes startup to fail.
4. In Adobe UXP Developer Tool, add `uxp/manifest.json`, then load it into
   Photoshop. Open the **Orbit Photoshop sample** panel from Photoshop's
   Plugins menu. If loading fails, open **Details** in the **Plugin Load Failed**
   notification and inspect the logs. Photoshop may have its own developer-mode
   prompt open even after Developer Tool's setup is complete.
5. Choose **Choose bridge file** in that panel and pick the newly created
   `Photoshop.bridge.json`. Do not give Photoshop the Orbit pairing file.
   Wait for the panel's **Connected.** status as well as Orbit's **Connected**
   state. Orbit's state confirms its named-pipe connection to Node; the panel
   confirms the separate authenticated Photoshop-to-Node connection.
6. In Orbit's ring editor, add an item, choose **Extensions**, then
   **Photoshop sample: Selected layer visibility**. Its **When picked** choice
   offers Toggle visibility, Show layer and Hide layer. Add a second placement
   with a different choice to check that settings remain independent. Add
   **Layer status** for a passive face; choose visibility or selection count.
   With a disposable document and one selected layer, a visibility action should
   make the requested change once.

Hiding the panel keeps its authenticated connection and active sessions. Explicit
**Disconnect**, plugin destruction or companion exit ends it. After disconnecting,
choose the current bridge file to connect again. The companion retries an unavailable
Orbit once a second, but never queues or replays commands across reconnects.

After restarting the companion, choose a new bridge-file path; the previous
derived credential has expired. Remove old bridge files when no longer needed.
Disabling/removing the extension in Orbit revokes the Orbit credential. After
re-enabling, explicitly copy fresh connection info and restart the companion.

### If the panel cannot connect

After changing source, rebuild the UXP bundle and reload the plugin in
Developer Tool. After changing manifest permissions, **Unload** then **Load**
so Adobe applies them. Use the current Node companion and its fresh bridge file.

**Could not connect (stage/kind)** identifies a local setup step using fixed
categories. For `metadata`, `read` or `config`, choose the bridge file generated
by the running companion, not the Orbit pairing file. For `socket`, check the
exact manifest permission and endpoint above. For `platform` or `session`,
confirm the generated bundle is present and reload the plugin. Messages omit
exception prose, paths, bridge contents, secrets and authentication values.
Failed setup closes partially created socket and platform resources.

**Disconnected. Choose the bridge file again.** can follow a failed handshake
or a lost connection. Confirm the companion is running and choose its current
bridge file. A companion restart requires a new bridge file; re-enabling the
Orbit registration requires fresh Orbit pairing information first.

Adobe's [Developer Tool guide](https://developer.adobe.com/photoshop/uxp/guides/devtool/)
covers loading and debugging plugins. The command uses the documented
[Layer.visible](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/classes/layer)
property inside [executeAsModal](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/executeasmodal).

## Authentication and command lifetime

Orbit pairing uses [protocol 2](../../docs/extension-protocol.md):
bounded little-endian length-prefixed JSON, fresh nonces and mutual
HMAC-SHA256. The companion strictly checks the Photoshop extension ID, pipe
shape, schema, Base64 and fields. It verifies Orbit before sending its own
proof. The Orbit secret never crosses a wire or enters Photoshop.

The companion derives a **different per-launch secret** using
`HMAC-SHA256(orbitSecret, ASCII("Orbit.Photoshop.Bridge.Key.v2\n") || random32)`.
Only this secret and the loopback URL enter the bridge file. The file remains
sensitive: possession authorizes this sample's local Photoshop command.
Choose a private directory; Windows directory ACLs, rather than Node's Unix
file-mode argument, control file access. Do not use shared or synced folders.

The WebSocket leg has its own mutual HMAC exchange. It uses lowercase hex
nonces/proofs rather than the pipe's Base64. The plugin sends
`{type:"hello", protocolVersion:2, clientNonce}`. The companion answers
`{type:"challenge", serverNonce, proof}`; after verifying the server, the
plugin sends `{type:"authenticate", proof}`. The companion answers
`{type:"ready"}` only after verifying the client. Each proof is HMAC-SHA256
over this ASCII transcript with LF separators and **no final newline**:

```text
Orbit.Photoshop.Bridge.v2
ROLE
CLIENT_NONCE
SERVER_NONCE
```

`ROLE` is `server` or `client`. Fresh nonces and separate roles prevent proof
reflection and replay. No raw secret is sent. Authenticated command/result/
cancel messages use the same fields as Orbit's pipe, encoded as text WebSocket
messages. Both peers reject unknown or duplicate fields and unexpected messages.

The listener binds only `127.0.0.1`, accepts one authenticated Photoshop peer,
bounds payloads and rates, and gives authentication five seconds. The bridge
admits absent Origin, literal `null` and exact `file://` (observed from
installed Photoshop); all require mutual per-launch HMAC authentication.
HTTP(S) web origins and other Origin values are rejected. Origin is not a
credential. This is not a boundary against a hostile program running as
the same Windows user with access to the user's private files/processes.

Orbit's deadline is 15 seconds; the bridge and plugin cancel pending work after
12 seconds. Selection is captured when the UXP command arrives and checked
again inside the modal scope. Busy Photoshop, no document, multiple selected
layers, a stale selection or cancellation refuses the command. A concurrent
command is refused, not queued. Cancellation cannot undo a mutation that
already completed; the sample never automatically retries that mutation.

The adapter wraps visibility changes in Photoshop's
[`suspendHistory`/`resumeHistory`](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/executeasmodal)
scope for a named Undo step. After awaiting history setup it rechecks
cancellation, the captured document/layer and the current session.
Exceptions escape the modal callback so Photoshop rolls back a
still-suspended change. Success is reported only after the history commit.

## Verification

`npm test` exercises real local named-pipe and WebSocket connections with a fake
Orbit host and an in-memory Photoshop model. It checks mutual authentication,
the checked-in HMAC vector, partial/coalesced frames, invalid UTF-8, duplicate
JSON fields, payload/rate/deadline limits, command round trips, refusal,
cancellation, disconnects, duplicate-invoke rejection, independent settings,
state renewal, retired modal targets, event-listener cleanup and reconnect
without replay. Transport tests cover exact
`file://` authentication, rejection of foreign origins, unauthenticated peers,
history commits, cancellation during history setup and rollback on
setter/commit failures and Show/Hide no-ops. Panel tests cover lifecycle registration, fixed
diagnostics and partial-resource cleanup. `test/interop-v2-peer.cjs`
is the v2 peer for the real C# host; Photoshop remains an in-memory model.
Windows transport
tests are skipped on other operating systems; parser and command tests run there.

`test/interop-v2-peer.cjs` supplies a peer for a host team's interoperability
harness. This public repository does not include the proprietary Orbit host
tests. The peer connects through the actual Node bridge and UXP client to the
same fake document model. It prints `READY`, then `INVOKED` with the mutation
count after each successful action; `STOP` on stdin shuts it down. Its command-line
argument is a temporary pairing-file path, never a credential value.

Before distributing a real Photoshop integration, verify in the installed
Adobe host:

- UXP loading and authenticated connection passed on Photoshop 27.9 with UXP
  Developer Tools 2.3.0, using the fixed `localhost` endpoint and exact `file://`
  Origin. Other supported host versions need checks. Do not remove
  authentication or allow arbitrary web origins.
- Independent Toggle, Show and Hide placements changed one selected layer in
  a new disposable document. Native passive faces showed Shown/Hidden and One.
  After the history fix, Show changed hidden to shown, Ctrl+Z hid the layer
  without removing it, and Ctrl+Shift+Z showed it again; live faces followed.
  Other action/history combinations and no-document, multiple-selection,
  changed-selection, cancellation and modal-busy paths need installed checks.
- Repeated native-ring hotkeys and actions worked without manually activating
  Photoshop between actions, providing a basic focus-return pass. Broader
  focus scenarios remain open.
- Closing the floating panel kept live faces active. Plugin reload disconnected;
  choosing the current bridge file reconnected without replaying an action.
  Full plugin destruction, companion exit, Orbit disable/restart and
  stale-session/refusal cases need deeper installed lifecycle checks.
- Keyboard navigation and screen-reader status work in the UXP panel, then
  test `.ccx` installation on a clean supported Photoshop/Creative Cloud setup.
  Clean `.ccx` installation remains unverified.

## Share and extend

Share source plus `extension.json`; the recipient registers and pairs their
own local instance. To share a prepared Adobe plugin, build the UXP bundle and
package `uxp/` as `.ccx` using Adobe's tooling. Adobe supports
[independent distribution](https://developer.adobe.com/uxp/guides/how-to/distribution/independent-distribution/)
as well as its reviewed marketplace. Use your own stable plugin/extension IDs
for a published fork. Do not include either connection file or any secret.
Sharing an Orbit ring never installs or allows a companion. This milestone's
ring importer leaves external command items out with an explanation; after
registering and enabling the extension, the recipient adds its command locally.

Keep the plugin-level `create` callback in `entrypoints.setup`, even when it
does no initialization work; Adobe requires it for this lifecycle registration.
Plugin `destroy` disconnects and retires pending file selections. Panel hide
or panel destruction keeps the plugin's shared session connected.

The v2 adapter uses Adobe's [action notifications](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/photoshopaction)
and documented [event codes](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/eventcodes):
`select`, `show`, `hide`, `open`, `close`, `make`, `delete`, `set`, and `undoEvent`,
plus the documented [modal-exit event](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/executeasmodal).
These names are checked against Adobe's documentation. Installed live faces
followed visibility changes and Undo/Redo, but that does not isolate notification
delivery from the reconciliation fallback. Notification-specific registration,
delivery and cleanup checks remain open.
Events coalesce to at most two refreshes per second (slower at the instance cap).
An initial refresh and a refresh after each command complement a two-second
reconciliation tick while at least one session exists. This catches missed or
suppressed notifications, including history changes. Every text face expires
after five seconds. The last session stopping removes listeners and timers.

The short values are Shown/Hidden or None/One/Many; no document content is sent.
Orbit may use its fallback glyph when a value does not fit a slot; its selected
status retains the full description. A session ID is replaced when settings
change or Orbit reconnects, so retired-session updates are ignored. Reconnecting
Photoshop restores subscriptions but never replays a command.

The bundle declares `hosts: ["orbit"]`; it does not claim Lollipop compatibility.
Lollipop support, a marketplace, public NuGet publication, richer settings
inspectors, and other contribution types remain separately versioned work.
This sample is a source developer example, not a ready-to-install consumer
extension. Visit the [developer site](https://dev.ventana.tools/orbit/examples/photoshop/)
for the public walkthrough. SDK/sample source uses [Apache-2.0](../../LICENSE);
Orbit remains proprietary. The dependency licenses below remain applicable.

## Dependencies

Complete license texts and attribution are retained in
[third-party notices](THIRD-PARTY-NOTICES.md). Keep these notices and the
generated bundle's license when distributing a prepared integration.

The companion uses [ws 8.22.0](https://github.com/websockets/ws), MIT. The UXP
bundle uses [@noble/hashes 2.4.0](https://github.com/paulmillr/noble-hashes), MIT;
its complete license is copied beside the generated bundle. The build tool is
[esbuild 0.28.2](https://esbuild.github.io/), MIT. The application does not ship
or run Node, these npm packages, the companion, or the Adobe plugin as part of
loading an extension manifest.
