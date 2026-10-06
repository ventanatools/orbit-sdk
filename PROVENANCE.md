# Source provenance

This repository starts with a curated export of the public SDK components from
the Orbit schema-2/protocol-2 implementation, followed by portable repository
scaffolding, independent tests and public author documentation. No application
Git history, application source, signing material, credentials or proprietary
host implementation is included.

The initial runtime library behavior matches the implementation at Orbit source
revision `4cadbb0c835ea7d6a0c4fbe53d9f092aa33d8e0a`. The package metadata changes
to a preview version and Apache-2.0, source license headers and public test references
are intentional; versioned wire identifiers remain unchanged. Sample source behavior is preserved, with consumer references,
build instructions and license/notice metadata adapted for this repository.

The SDK and Protocol library copies in the application repository remain on the
same public license. Changes to their shared runtime sources must be synchronized
and verified against both standalone SDK tests and private host integration tests
until the application consumes independently released SDK/Protocol packages.
