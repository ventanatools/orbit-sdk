# @ventanatools/orbit-extensions

The Node SDK for Orbit extension companions: the separate programs that implement an
extension's actions and widgets and connect to Orbit over a Windows named pipe. It follows the
extension contract, generation 3 (`docs/design/contract-v3.md` in this repository), section 10,
and behaves as the .NET SDK `VentanaTools.Orbit.Extensions` does: the same backoff and state
table, face cleaning and coalescing, renewal, replay window, session cap, ping handling and
handler rules. It runs the same golden test vectors as the .NET SDK (`fixtures/`).

- Node.js 22 or later; CommonJS with hand-written TypeScript declarations; no dependencies.
- **Not published.** The package is `"private": true` until a publication decision is made.
  Use it from this repository (`"file:../../node/orbit-extensions"`, as the Photoshop sample
  does) or from the tarball `npm pack` writes (`ventanatools-orbit-extensions-<version>.tgz`),
  which the `orbit-ext` tool copies into new Node projects (`orbit-ext new node`).
- Apache-2.0, © 2026 Ventana Tools LLC.

## Hello world

An action with one `Choice` setting, `greeting`:

```js
const { runCompanion, Outcome } = require("@ventanatools/orbit-extensions");

runCompanion(process.argv.slice(2), {
  async invoke(invocation) {
    console.log(`${invocation.session.settings.greeting}, world`);
    return Outcome.Done;
  },
}).then((code) => process.exit(code));
```

A widget that shows the time and keeps its face current without a timer of its own:

```js
const { runCompanion } = require("@ventanatools/orbit-extensions");

runCompanion(process.argv.slice(2), {
  async runSession(session, signal) {
    const now = new Date();
    session.setFace({ line1: now.toLocaleTimeString(session.uiLanguage), goodForSeconds: 60, renew: true });
    // Keep running until stopped (the signal may already be aborted).
    if (!signal.aborted) await new Promise((resolve) => signal.addEventListener("abort", resolve, { once: true }));
  },
}).then((code) => process.exit(code));
```

`runCompanion` finds `extension.json` (`--manifest`, else next to the entry module, else in the
current directory) and the pairing file (`--pairing`, else
`%USERPROFILE%\.ventana\pairings\<host-id>\<extension-id>.pairing.json` for each active host in
`hosts`, else `<extension-id>.pairing.json` next to the entry module, then in the current
directory). On first run it prints where it waits for the pairing file and which Orbit action
writes it (**Save connection info**), then connects when the file appears. It prints one status
line per change, for example
`ventana: waiting (host.not-running) Start Orbit and turn the extension on. https://dev.ventana.tools/go/orbit/codes#host-not-running`,
handles Ctrl+C, and resolves with the exit code: 0 stopped, 1 unexpected, 2 usage, 3 a file is
missing or invalid with `watchFiles: false`, 4 stopped with `watchFiles: false`.

## What the package exports

| Export | Purpose |
|---|---|
| `runCompanion(args, handler, options?)` | The companion program: discovery, status lines, watching, Ctrl+C, exit codes. |
| `CompanionClient` | One registration's connection: `new CompanionClient({ pairing, manifest, handler, retry? })`, `state`, `run(signal)`, events `status` and `handlerFaulted`. |
| `ContributionRouter` | `mapSession`, `mapInvoke`, `map` per contribution id; `findUnmapped(manifest)`. |
| `readManifestFile`, `readManifest`, `validateManifest` | The manifest reader with diagnostics (contract §3, §4). |
| `readPairingFile`, `readPairing`, `defaultPairingPath` | The pairing reader (contract §6). The secret is never a string, and `dispose()` zeroes it. |
| `computeManifestHash` | The manifest hash (contract §B.2). |
| `hosts`, `findHost` | The host registry, from `lib/hosts.json`. |
| `Outcome`, `FaceState`, `Failure`, `PublishResult`, `ConnectionState` | Frozen objects of the wire tokens. |
| `@ventanatools/orbit-extensions/wire` | Framing, `readMessage`, `writeMessage`, the transcript and proofs, pipe names, limits, `TokenBucket`, `ReasonCodes`. |
| `@ventanatools/orbit-extensions/testing` | `createTestSession`, `startTestHost`, `assertManifestValid`, `ManualClock`. |

### Handlers

A handler is an object with `runSession(session, signal)` and/or `invoke(invocation, signal)`.

- `session.setFace({ line1?, line2?, detail?, glyph?, state?, goodForSeconds, renew? })`,
  `session.clearFace()` and `session.fail(failure)` check their arguments, then that the
  contribution provides `face`, then the session's state: they return `"SessionEnded"` after the
  session ended. Text is cleaned with the display limits before sending; `setFace` never throws
  because of what the text says or how long it is. At most one face command per session is
  pending: a newer one replaces it.
- `invoke` returns an `Outcome`, or `{ outcome: Outcome.Failed, failure }`. Any other value, or a
  `failure` with another outcome, is answered `Failed` and raises `handlerFaulted`
  (`InvalidResult`). A thrown error is answered `Failed` and raises `handlerFaulted`
  (`Exception`).
- A handler that rejects with an `AbortError` after its signal was aborted completes normally.
  A handler still running 5 seconds after its signal was aborted raises `handlerFaulted`
  (`IgnoredCancellation`).

## Testing

```js
const test = require("node:test");
const assert = require("node:assert/strict");
const { assertManifestValid, createTestSession, startTestHost } = require("@ventanatools/orbit-extensions/testing");

test("the manifest is valid", () => {
  assertManifestValid(require("node:path").join(__dirname, "..", "extension.json"));
});

test("the widget's first face shows the time", async () => {
  const manifest = assertManifestValid(require("node:path").join(__dirname, "..", "extension.json"));
  const recording = createTestSession({ manifest, contributionId: "example.clock/time" });
  const run = recording.run(require("../handler.js"));
  const face = await recording.waitForFace(2000);
  assert.ok(face.line1);
  recording.stop();
  await run;
});

test("the widget publishes a face through a real client", async () => {
  const manifest = assertManifestValid(require("node:path").join(__dirname, "..", "extension.json"));
  const host = await startTestHost({ manifest, handler: require("../handler.js") });
  try {
    const session = await host.startSession("example.clock/time");
    const { face } = await host.waitForFace(session, 2000);
    assert.ok(face.line1);
  } finally {
    await host.close();
  }
});
```

`createTestSession` records what a session handler publishes without a connection; await
`waitForFace` (or `waitForPublications`) before asserting, because the handler keeps running
after `run` returns.
`startTestHost` runs a real `CompanionClient` against an in-memory host that speaks real frames;
pass a `ManualClock` to drive backoff, deadlines, pings and renewal without waiting.

## Security

- The pairing file is a credential. Never commit, package or share it; `orbit-ext pack` refuses
  to pack one. The SDK never logs, prints or exposes the secret.
- **Server verification: a documented limitation.** Node's `net.connect` cannot check who owns a
  named pipe and does not request identification-level impersonation, so the server receives the
  default impersonation level. The Node SDK therefore treats every server as unverified until
  its challenge proof shows that it knows the pairing secret: a program that squats on the pipe
  name while Orbit is not running can neither stop the SDK nor keep it waiting longer than the
  maximum retry delay, and never receives the companion's proof. It cannot prevent a squatter
  that holds `SeImpersonatePrivilege` (for example a compromised service account; such accounts
  are already highly privileged) from impersonating the person after reading `hello`, which
  carries no secret. Nor can it refuse a squatter that runs as the same user at a lower
  integrity level (for example a sandboxed process outside an AppContainer) and has read the
  pairing file: that squatter's challenge proof verifies, and the SDK would then run its
  sessions and invocations. Against such a process the Node SDK relies on the host writing
  pairing files with a mandatory label that lower-integrity processes cannot read. The .NET SDK
  verifies the pipe's owner and the server process, and refuses a lower-integrity server,
  before it writes anything. An optional native check for the Node SDK is a release gate to decide before
  the package is published.

## Developing this package

```powershell
npm ci --ignore-scripts
npm test
```

The tests read the repository's `fixtures/` folder: every file that applies to this SDK (ids,
text rules, codes, manifests, pairing files and the `wire/v3` vectors). The strings files and the
package archives are not read by this SDK, so their fixtures do not apply here. `lib/hosts.json`
and `lib/codes/*.json` are checked-in copies of `fixtures/hosts.json`, `fixtures/codes/*.json` and
`fixtures/wire/v3/capabilities.json`; a test compares them byte for byte, and the codename script
regenerates `lib/hosts.json`.
