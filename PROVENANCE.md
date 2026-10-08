# Source provenance

This repository is the canonical source of Orbit's extension SDK: the author
package, the test kit, the tool, the templates, the Node SDK, the samples, the
fixtures, the schemas and the contract. Changes to any of them are made here
first.

The Orbit app consumes this repository as a Git submodule pinned to a commit of
it, and builds the author package from that source. The app keeps no copy of the
SDK's source, and only its proprietary host implementation and its host-to-SDK
interoperability tests stay in the app's own repository.

The repository began as a curated export of the public SDK components of the
Orbit app's protocol-2 implementation, with portable build scaffolding,
independent tests and author documentation added; contract generation 3 then
rewrote it. It contains no application history, application source, signing
material, credentials or proprietary host code, and no history from a private
repository.

Packages are built only from this public repository, so their SourceLink
information always points here.
