# Extension packages

Orbit versions with extension support enabled can review and install
`.orbitextension` packages. This is a direct local distribution preview.
SDK/tool/sample source is public under [Apache-2.0](../LICENSE); independently
created extensions can use their authors' chosen licenses. Publisher
verification, signatures, a marketplace, prerequisite installation, and automatic
companion startup remain future work. Real Store-signed host compatibility is
unverified, and this repository includes no Orbit app build. Lollipop
compatibility is not established.

## Package contract

A package is a bounded ZIP with these exact root names:

```text
extension.json       schema-2 declaration, identity, numeric major.minor.patch
package.json         schema-1 compatibility requirements
README.md            UTF-8 preparation/start instructions
payload/             optional inert companion binaries or source
```

The descriptor has exactly these fields:

```json
{"schemaVersion":1,"protocolVersion":2,"requiredCapabilities":["invoke","face","settings"]}
```

The descriptor's schema 1 is a package-format version, not extension protocol 1.
Manifest and wire protocol 2 are the only supported extension contract.
Action-only packages can require just `invoke`; faces and settings are optional.

Requirements are a nonempty unique subset of `invoke`, `face`, `settings` and
must cover all features the manifest uses. These name protocol features, not
Windows permissions or a process sandbox. Target application permissions belong
to its companion/adapter. The manifest must include `orbit` in `hosts` at install.
Unknown fields, versions and capabilities fail closed. There is no executable
entry-point declaration, URL installer or startup command in this format.

Limits: 32 MiB archive, 64 MiB actual expanded content, 16 MiB per payload file,
128 entries including explicit directories. Manifest/README each cap at 64 KiB;
descriptor caps at 4 KiB. ZIP64, split/encrypted archives and corrupt CRCs are
rejected. SHA-256 identifies reviewed bytes; it does not verify a publisher.

Only exact root files and paths below `payload/` are allowed. Paths use ASCII
letters/digits/dot/underscore/hyphen segments, max 80 characters each/max 240 total.
Traversal, absolute paths, backslashes, streams, Windows device names, trailing
dots/spaces, links/devices, duplicate names, case aliases and file/directory
conflicts are rejected. Extraction never restores archive filesystem attributes.

## Install, enable and update

1. Choose **Install package…** in Settings > Extensions. Review identity,
   version, required features and contributions before installing. Installation
   stores inert files and leaves the extension off.
2. **Open package folder** displays managed files in Explorer. Read its README
   and deliberately prepare/start its companion. Orbit never launches that code.
3. Explicitly **Enable…**, then **Copy connection info** to a private local file.
   Pass the file path to the companion. Keep credentials outside packages, source,
   screenshots and bug reports.
4. Use **Update package…** or Install with an existing identity. The candidate
   must have the same extension ID and a strictly higher numeric version.
   Downgrades, equal versions, mismatched identity and stale review are refused.
   Unsupported older declarations are refused rather than upgraded.
5. Updating commits new files/declarations, disconnects the old companion,
   revokes its credential and leaves the extension off. Review/enable again,
   copy fresh connection info and explicitly restart the companion. Removed or
   changed contributions/settings can need attention in existing cards; saved
   ring items and choices are preserved, not silently rewritten.
6. **Remove…** revokes access and removes the registration. Ring items stay.
   Managed-file cleanup is best effort: files in use or a tampered folder may
   remain inert. It never deletes external prerequisites or source folders.

Extraction uses a new app-managed GUID folder for every install/update, off the
UI thread. The registry saves atomically before replacing the live registration.
If preparation/persistence fails, previous files/consent remain and new files
are cleaned up when possible. A crash before commit can leave an orphan folder;
it is never enabled, loaded or executed. Saved package requirements use
`extensions.json` version 2 so older version 1 writers refuse to edit it.
Loose developer manifests require **Extension developer mode** in **Settings > Extensions**;
their import does not grant consent. Normal package install, enable, and pairing
do not require Developer mode.

Catalog format versions are separate from extension protocol versions. A catalog
containing an unsupported old declaration is preserved and enters the load-failed,
read-only state; install, enable, update and removal cannot overwrite it.
This preview supplies no automatic migration of earlier development data.

## Build the example packages

Build/test Countdown as its [README](../samples/countdown-extension/README.md)
describes. Then run the staging helper from the repository root:

```powershell
pwsh -File tools/extension-package/pack-example.ps1 -Sample countdown
pwsh -File tools/extension-package/pack-example.ps1 -Sample photoshop
```

Outputs are under ignored `artifacts/extension-distribution/` and contain no
pairing/bridge files or user rings. Countdown carries its built runtime-dependent
companion plus source; Photoshop carries its curated source/lockfile, which the
recipient prepares explicitly. Node dependencies and generated UXP bundles are
not included. The Photoshop npm `package.json` stays under payload; its separate
`orbit-package.json` becomes the root compatibility descriptor.

The [package tool](../tools/extension-package/README.md) can pack your own
explicit staging directory and verify a received package. It validates before
writing, preserves an existing output, and is reproducible with identical input
and the same runtime. Keep staging stable while packing.

See [SDK APIs](extension-sdk.md), [wire contracts](extension-protocol.md) and
[simple/complex walkthroughs](extension-examples.md), and the
[public developer site](https://dev.ventana.tools/orbit/guides/distribution/).
Outside-author use, independent authentication review, signing/update identity,
runtime bootstrap, Store-signed host verification, and common Orbit/Lollipop
distribution remain release work. Built example packages require manual runtime
preparation; they are developer examples rather than ready-to-install consumer
extensions.
