# Contributing

Report SDK issues or propose changes through this repository. Include the SDK,
manifest and protocol versions, operating system, a minimal reproduction, and
the output of `pwsh -File tools/verify.ps1` where relevant.

Do not upload connection info, pairing files, bridge credentials, private
documents, application logs containing personal data, or proprietary app code.

Use .NET 10 and PowerShell 7; the Photoshop tests also need Node.js 22 or newer.
Run `tools/build.ps1` and `tools/verify.ps1` before submitting a pull request.
Keep the runtime libraries independent of the proprietary application and UI.
Retain protocol identifiers when changing branding and document any contract
change. Add focused tests for changed behavior, especially authentication,
cancellation, session lifetime, framing and untrusted package input.

Contributions intentionally submitted for inclusion are under Apache-2.0 as
described in section 5 of LICENSE. Contributors retain their copyright; no
copyright assignment is required. This policy concerns contributions to the
SDK repository, not independently distributed third-party extensions.
