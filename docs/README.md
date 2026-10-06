# Notes in this repository

The author guides live on the developer site,
[dev.ventana.tools](https://dev.ventana.tools/): getting started, the manifest,
pairing, packaging, testing, the tool and the samples. This folder keeps only
what travels with the source:

- [The extension contract, generation 3](design/contract-v3.md): the normative
  specification of the manifest, diagnostics, package archive, pairing file,
  wire protocol, reason codes, the .NET and Node SDK surfaces, the tool and the
  versioning policy. When the code and the contract disagree, one of them has a
  bug; the fixtures in `fixtures/` settle what the code does today.
- [Release status](release-status.md): what this preview contains and what
  remains before a release.
- [Releasing](releasing.md): how a maintainer builds the release packages, and
  what has to happen before anything is published.

The [changelog](../CHANGELOG.md) lists every change to the packages, including
every break from the earlier preview.

To report a problem with the developer site or these notes, open a
**Documentation** issue in this repository. Report vulnerabilities privately, as
[SECURITY.md](../SECURITY.md) describes.
