# Orbit.Extensions.Protocol

Local developer preview of Orbit's extension declarations, ID syntax, declared
choice settings, pairing reader, strict JSON helpers, and v2 framing and
authentication primitives. The package targets plain net10.0 AnyCPU and depends
only on the .NET runtime. It also validates bounded inert `.orbitextension`
ZIP packages and explicit protocol/capability requirements without extraction.

Orbit's host and companion SDK consume the same implementation. This package
does not contain Orbit's UI, ring model, consent store, DPAPI integration,
named-pipe listener, executable loader, or Photoshop integration.

There is one supported extension contract: schema-2 manifests and protocol-2
sessions. Contributions declare invoke, face, or both; action-only contributions
need no face or settings but use the same sessions. Older declarations, pairing
files and handshakes are rejected without automatic migration. Package descriptor
and local storage versions are independent formats. ID syntax can accept a
host-owned reserved-publisher policy; its default remains Orbit's policy. This
does not establish compatibility with another host.

ExtensionJson checks one object's fields at a time; readers must check every
nested object. ExtensionWire supplies framing primitives, not an authenticated
connection by itself. A connection must enforce the whole handshake deadline,
serialize writes, bound its queues, enforce write deadlines, and validate each
message's declared schema. Idle reads may wait, but started frames have a fixed
five-second deadline.

ExternalExtensionManifestReader.Snapshot validates and copies programmatic
manifests before a host or companion retains them. The original declarations
may be backed by mutable collections and must not remain the runtime authority.

Version `0.1.0-preview.2` is licensed under Apache-2.0, including LICENSE and
NOTICE in its NuGet package. Build packages with `pwsh -File tools/build.ps1`
at the repository root. A nuget.org release remains pending. The proprietary
Orbit app is distributed separately; actual Store-signed compatibility remains
unverified. This library never installs or starts extension programs. See the
[distribution guide](https://dev.ventana.tools/orbit/guides/distribution/).
