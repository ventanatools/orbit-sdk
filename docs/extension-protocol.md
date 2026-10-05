# External companions: protocol 2

Orbit has one external extension contract: schema-2 manifests and protocol-2
sessions. A contribution can invoke an action, publish a live face, or do both.
Action-only contributions use the same sessions and authentication as live
displays. Version 1 declarations, pairing files and handshakes are rejected;
there is no compatibility runtime or automatic development-data migration.

The [.NET SDK preview](extension-sdk.md) consumes this contract through
`Orbit.Extensions.Protocol` and `Orbit.Extensions.Sdk`. The v2 client and
[console consumer](../samples/dotnet-extension/README.md) avoid copying
the codec or referencing the Orbit host. The independent Node/UXP sample uses
the same v2 contract. Local packing is not public publishing.

## Declarations and capabilities

A manifest has exactly `schemaVersion`, `id`, `name`, `description`, `version`,
`hosts`, and `contributions`, with `schemaVersion: 2`. Its numeric package
version is distinct from the manifest schema and wire protocol. `hosts` lists
supported host IDs; it does not establish compatibility with another product.
A contribution has `id`, `name`, `description`, `glyph`, `capabilities`, and
`settings`. Capabilities are `invoke`, `face`, or both.

An action-only contribution needs no face or settings:

```json
{
  "id": "example.commands/run",
  "name": "Run",
  "description": "Runs the example.",
  "glyph": "\uE768",
  "capabilities": ["invoke"],
  "settings": []
}
```

A choice setting declares `id`, `name`, `default`, and `choices`, whose entries
are `{value,name}`. The host validates and renders these choices. There is no
arbitrary argument string or custom inspector. Richer contribution kinds and
inspectors require separately versioned capabilities.

Importing a declaration or installing a local package runs nothing and leaves
it disabled. The person explicitly allows it and copies connection info to a
companion they start themselves. Orbit never loads or launches its code.
There is no Orbit HTTP listener, automatic prerequisite installation,
executable entry point or arbitrary shell dispatch in this contract.

The pipe is available only when the installed Orbit version enables extension
support, and remains off until consent. Store-signed compatibility is not yet
verified; this repository supplies no Orbit application build.
A link-only startup loads the catalog without opening listeners;
normal app activation can activate previously allowed registrations. An
explicit Enable or Copy connection info action, or a person-initiated summon,
can also activate the host after link-only startup. These actions do not grant
consent to other disabled registrations. A link cannot install, allow, pair or
invoke anything.

Authoring with a loose manifest requires **Extension developer mode** in
**Settings > Extensions**. Normal package install, enable, and pairing do not
require that toggle. It does not replace explicit registration consent.

## Local consent and pairing

Each allowed registration has a random GUID and a cryptographically random
32-byte secret minted by Orbit. Its local `extensions.json` keeps a DPAPI
current-user protected credential, separate from manifests, rings and exports.
The cleartext secret appears only when the person explicitly copies connection
info. Do not log, check in, publish or include that pairing file in a manifest.
Disable or Remove closes the connection and revokes the credential. Re-enabling
mints a new secret. Loose-manifest replacement still requires removal/reimport.
Reviewed [v2 package updates](extension-distribution.md) instead require the
same ID and a higher numeric version, retain ring data and revoke consent/pairing
before re-enable. Consent cannot silently acquire more commands or features.
Packaged requirements use local catalog version 2, preserving the older writer's
fail-closed boundary.

An unreadable store, an unsupported store version or a store containing rejected
older declarations is preserved. `LoadFailed` makes mutations read-only until
the store is repaired and explicitly loaded successfully. No failed load
rewrites old declarations as v2. An invalid
DPAPI credential disables that registration; it is never accepted as a blank key.

Connection info is JSON with exactly these useful values:

```json
{
  "protocolVersion": 2,
  "pipeName": "Orbit.Extensions.v2.dev.0123456789abcdef.00112233445566778899aabbccddeeff",
  "registrationId": "00112233445566778899aabbccddeeff",
  "extensionId": "example.commands",
  "secret": "BASE64_OF_32_RANDOM_BYTES"
}
```

Connect to `\\.\pipe\` followed by `pipeName`. The name contains the edition,
the first 16 lowercase SHA-256 hex characters of the Windows user SID, and the
registration GUID in lowercase N format. Edition is 1–32 lowercase ASCII letters,
digits or hyphens. There are at most 16 registrations and one connection per
registration.

The server uses `CurrentUserOnly | FirstPipeInstance | Asynchronous`, byte mode,
and keeps that same server instance alive through reconnects. A squatted name
fails closed. `CurrentUserOnly` supplies the owner-only SID ACL; a separately
passed ACL would be ignored. See the [.NET ACL contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0).
The restriction is to the user and elevation. It is not a security boundary
against a hostile full-trust program running as that same user. The program path
is obtained from the connected process for display and is labeled unverified;
neither a publisher name nor that path is the authentication credential.

## Framing and authentication

Every message is a 4-byte unsigned little-endian byte count followed by that many
UTF-8 bytes of JSON. Length is 1–65,536 inclusive. There is no trailing newline.
Partial frames must finish within five seconds; an authenticated idle connection
can remain open. Malformed UTF-8, duplicate, missing or unknown members, wrong
types, unsupported protocol versions and unknown messages close the connection.
The handshake as a whole has a five-second deadline.

1. Companion sends `{"type":"hello","protocolVersion":2,"registrationId":"REG","clientNonce":"CN"}`.
2. Orbit sends `{"type":"challenge","serverNonce":"SN","proof":"P"}`.
3. After verifying Orbit's proof, companion sends `{"type":"authenticate","proof":"P"}` with its own proof.
4. Orbit verifies it and sends `{"type":"ready"}`. Nothing else is sent before authentication succeeds.

CN and SN are independent fresh 32-byte cryptographic random nonces, encoded as
canonical standard Base64 (44 characters). The proof is Base64 HMAC-SHA256, keyed
by the decoded secret, over the UTF-8 bytes of the following exact transcript,
with LF separators and no final newline:

```text
Orbit.Extensions.v2
ROLE
REG
CN
SN
```

ROLE is `server` in step 2 and `client` in step 3. REG is the lowercase N-format
registration GUID. Comparisons are constant-time. Both parties verify the other;
the pairing secret itself never crosses the pipe. Domain-separated roles and
fresh nonces prevent reflecting a server proof or replaying an earlier handshake.

## Invoking commands

After a real pick and after its ring closes, Orbit may send:

```json
{"type":"invoke","requestId":"GUID_N","actionId":"example.commands/run","sessionId":"SESSION_GUID_N","settings":{}}
```

Only invoke-capable contributions in this authenticated registration's admitted
manifest are sent. Every invoke belongs to a current session and carries that
session's validated settings. An action without declared settings still receives
`settings: {}`. The session starts before its invoke, as described below.
The companion answers:

```json
{"type":"result","requestId":"GUID_N","outcome":"done"}
```

`outcome` is exactly `done`, `refused`, `failed` or `unsupported`. No result text,
exception, URL or program request is accepted. The host supplies all notices.
The host owns the 15-second deadline. Cancellation or timeout sends
`{"type":"cancel","requestId":"GUID_N"}` when the connection remains usable.
Companions should stop promptly, but cancellation cannot undo an already finished
external side effect. A late answer is ignored. Disconnect fails outstanding
picks and never replays them after reconnect. A disconnected pick is refused;
Orbit does not start or wait for a program to appear.

Each connection has at most 32 outstanding invokes and a 256-message bounded
serialized output queue. A peer that sends more than 128 messages in a one-second
window, stops reading for five seconds, or fills the queue is disconnected.
Reconnect with fresh nonces and the same current pairing information; use backoff
so a disabled or absent host is not polled in a busy loop.

The companion receives its declared action IDs, opaque request/session IDs and
validated choices. It receives no ring contents, item metadata, summon events,
app/window identity,
license state, settings from other extensions, or power to pick/summon anything.
Its own work happens in its separate process with its own permissions.


## Sessions and live faces

An authenticated host starts an instance with:

```json
{"type":"startSession","sessionId":"GUID_N","actionId":"example.photoshop/toggle-layer","settings":{"mode":"hide"}}
```

The session ID is opaque and short-lived, not a ring/item identifier. Settings
include their declared defaults. The host replaces the ID after settings change
or reconnect; a stopped ID is never reused. Two placements can have independent
settings. Action-only contributions also receive sessions, even when they have
no settings and cannot publish a face. The host ends a subscription with
`{type:"stopSession",sessionId}`.
There are no callbacks revealing when a ring appears or what else it contains.

A face-capable contribution may publish:

```json
{"type":"face","sessionId":"GUID_N","command":{"$type":"setFace","face":{"picture":{"$type":"none"},"line1":{"$type":"text","value":"Shown"},"state":"On","detail":"The selected layer is visible.","goodForSeconds":5}}}
```

The first slice permits no-picture or glyph pictures and text lines, with a
finite lifetime from 1 to 86,400 seconds. State is `None`, `Playing`, `Paused`,
`On`, or `Off`. `clearFace` removes state; `fail` carries `NeedsSetup`, `NoData`,
`Network`, or `Unsupported`. Images, time lines and Orbit-owned art are not
admitted by this slice. The host stamps arrival, cleans/caps text, applies its
face update budget and ignores retired-session messages. Publishing state does
not confer permission to invoke or enable anything.

An invoke-capable contribution receives:

```json
{"type":"invoke","requestId":"GUID_N","actionId":"example.photoshop/toggle-layer","sessionId":"GUID_N","settings":{"mode":"hide"}}
```

The session must match that contribution and configuration. A passive face has no
invoke capability. Result/cancel records, host deadlines and the no-replay rule
follow the command rules above. A temporary invocation session can be started and stopped around
a command when no durable subscription exists. The companion must cancel work
when its session ends and check cancellation again before an external mutation.

The host owns registration consent and session demand. No foreign code runs in
Orbit, no process is automatically launched, and cold-link activation remains
gated. Runtime faces and pairing material are not portable ring settings. This
is a public source preview; SDK packages are built locally and have not been
published to NuGet. Store-signed extension verification and consumer companion
activation remain release work. See [developer documentation](https://dev.ventana.tools/orbit/reference/protocol/).
