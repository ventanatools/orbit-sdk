# Extension examples: countdown and Photoshop

Start with Countdown for the .NET SDK, then study Photoshop for an adapter that
controls another application. Both use the single v2 contract for actions and
finite live faces. An action can also declare only `invoke`, with no face or
settings, while using the same session lifecycle. A passive display has no action digit; placing a face in the middle does
not make the middle invoke it.

| | Countdown | Photoshop |
|---|---|---|
| Runtime | .NET 10 companion using the SDK NuGet package | Independent Node companion and Adobe UXP plugin |
| State | One timer in companion memory | Selected layer state in Photoshop |
| Action | Start/resume, Pause, Reset | Toggle, Show, Hide |
| Passive face | Remaining time/status | Visibility/selection count |
| External mutation | None | Layer visibility within modal/history scope |
| Start here | [Countdown README](../samples/countdown-extension/README.md) | [Photoshop README](../samples/photoshop-extension/README.md) |

Use the [SDK guide](extension-sdk.md) for API/consent, the
[protocol guide](extension-protocol.md) for interoperability, and
[distribution guide](extension-distribution.md) for local install/update.

## Countdown walkthrough

`example.countdown/timer` declares invoke and face, with per-placement `mode`
(start/pause/reset) and `duration` (one-minute/five-minutes/twenty-five-minutes).
`example.countdown/status` declares only face, without settings. All placements
observe one shared timer; changing one card's settings does not send a command.

Build the sample from the exact local SDK packages, enable and pair it, then add
three actions and a passive status. Configure Start, Pause and Reset for a
one-minute trial. Start a ready/finished timer; pause it; resume with Start;
reset to Ready. Start while running does nothing. Start while paused keeps its
remaining time. Reset selects its own duration but does not start it. Start on
Ready uses the Start placement's chosen duration.

Read `Countdown.cs`: monotonic timestamps determine remaining time rather than
counting publications. No sessions are needed for time to elapse. Wall-clock
changes and delayed updates do not cause drift. Read `CountdownHandler.cs`:
`RunSessionAsync` owns an initial face and finite renewals for one opaque session.
It stops on retirement, cancellation or rejected publication. `InvokeAsync`
routes closed choices and checks the captured session before a serialized change.

Each face expires after five seconds. Renewals normally occur once per second
and slow toward two at the active-session cap. Restarting the companion resets
memory; reconnect within the same process retains the timer and creates fresh
session handles without replaying actions. Undo changes card choices, not timer
commands. The 13 deterministic checks cover these model/handler behaviors;
native UI/accessibility checks remain separate.

## Photoshop reference walkthrough

```text
Orbit ↔ authenticated Windows named pipe ↔ Node companion
      ↔ separately authenticated local WebSocket ↔ Photoshop UXP
      → executeAsModal → history transaction → layer visibility
```

UXP cannot open Orbit's named pipe, so Node handles that boundary. Node keeps the
Orbit pairing file and creates a different per-launch credential in a private
bridge file. Wait for Connected in both Orbit and the Photoshop panel: Orbit's
state proves only its connection to Node. Orbit runs neither process/plugin.

| File | Responsibility |
|---|---|
| `extension.json` | Contributions, capabilities, closed native choices |
| `companion/main.cjs` | Bounded pairing read, startup, private bridge file |
| `companion/bridge-v2.cjs` | Authenticated relay, requests and session lifetime |
| `uxp/index.js` | Picker, fixed diagnostics and plugin/panel lifecycle |
| `uxp/client-v2.js` | Strict messages, authentication, retirement/cancellation |
| `uxp/platform.js` | Selection, modal/history changes and finite live state |

The action's `mode` is Toggle/Show/Hide; passive `display` is visibility or
selection count. Each card keeps its own choices. The adapter captures the
document/single layer, refuses concurrent work, waits for the modal scope, and
rechecks cancellation/target/session. It never redirects an old request to a
new selection. It suspends history, rechecks after awaiting, changes visibility
and commits history before reporting success. Already-matching Show/Hide are
successful no-ops. Exceptional modal exit rolls back still-suspended work;
cancellation cannot generally undo a completed external change.

Visibility sends Shown/Hidden; selection count sends None/One/Many. Document
names, layer names, paths and pixels stay in Photoshop. Initial/post-command
refreshes, coalesced notifications and bounded two-second reconciliation renew
five-second faces. The last session stops timers and requests listener removal.
Hiding a panel keeps plugin-wide work alive; Disconnect/plugin destruction stop
it. Reload disconnects, and a new Node launch needs a fresh bridge file.

No single layer maps to NeedsSetup for visibility; read failures map to NoData.
Missing/stale targets, canceled sessions and busy modal work refuse commands;
unexpected operation failures use a fixed Failed outcome. Malformed traffic or
authentication failure closes the connection. Setup diagnostics never include
raw exceptions or credentials, and partial setup closes created resources.

The fixed localhost permission/socket, canonical IPv4 bridge URL and narrow
Origin rules are explained in the sample README; do not broaden them to fix a
connection. The sample has one v2 runtime; obsolete pairing and wire versions
are rejected rather than routed through a compatibility implementation.

## Verification and release boundary

Installed Photoshop 27.9/UXP Developer Tools 2.3.0 passed mutual authentication
through the real Node v2 bridge to scratch Orbit on 2026-10-05. A disposable
document/layer exercised independent Toggle/Show/Hide, visibility/selection
faces and Show→Undo→Redo while preserving the layer. Basic focus return, panel
hiding, reload and explicit reconnect also passed. These checks used a
development Orbit host; they do not establish Store-signed compatibility.
Reproduce the automated public sample tests with `pwsh -File tools/verify.ps1`
from the repository root. See the [public walkthroughs](https://dev.ventana.tools/orbit/examples/countdown/).

Those live updates do not isolate Adobe notifications from reconciliation.
Notification-specific registration/delivery/cleanup, deeper refusal/lifecycle,
other history cases, host versions, UXP accessibility and `.ccx` installation
remain release gates. Native Narrator, high contrast, large text and mixed-DPI
checks are separate. Clean Adobe `.ccx` installation remains unverified.
Local packages start nothing. SDK/sample source uses [Apache-2.0](../LICENSE);
NuGet publication, signing, bootstrap, Store-signed Orbit validation, and an
Orbit/Lollipop marketplace remain future work. Lollipop compatibility is not
established.
