# Third-party notices

This repository's licences (Apache-2.0, and MIT-0 for the samples and templates)
cover its own code. Third-party packages keep their own licences, listed here.

## In the packages

- **`VentanaTools.Orbit.Extensions`** and **`VentanaTools.Orbit.Extensions.Testing`**
  have no third-party dependencies; they use the .NET runtime the recipient
  installs.
- **`VentanaTools.Orbit.Extensions.Hosting`** depends on
  `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 and the packages it brings,
  © .NET Foundation and Contributors, under the MIT licence
  (<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>). They are restored
  from nuget.org by the projects that use the add-on, not carried in it.
- **`VentanaTools.Orbit.Extensions.Tool`** carries, as a .NET tool does, the
  assembly it runs with: `System.CommandLine` 2.0.12, © .NET Foundation and
  Contributors, under the MIT licence
  (<https://github.com/dotnet/command-line-api/blob/main/LICENSE.md>). It also
  carries this repository's own author, Testing, Hosting and Templates packages
  and the Node SDK tarball, under the licences stated in them.
- **`VentanaTools.Orbit.Extensions.Templates`** contains only this repository's
  MIT-0 template content. The projects it creates restore `xunit`,
  `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` from nuget.org under
  their own licences.
- **`@ventanatools/orbit-extensions`** (the Node SDK) has no dependencies.

## Build and test dependencies

These are restored from nuget.org or npm to build and test this repository and
are not included in any package: `xunit` 2.9.3 and `xunit.runner.visualstudio`
4.0.0 (Apache-2.0, .NET Foundation), `Microsoft.NET.Test.Sdk` 18.10.1,
`Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0, `Microsoft.CodeAnalysis.CSharp`
5.9.0, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0,
`Microsoft.Extensions.FileSystemGlobbing` 10.0.12 (the tool's tests check that its
copy rule globs select what FileSystemGlobbing, which earlier tool versions used,
selected) and `Microsoft.Extensions.Hosting` 10.0.12 (the Generic Host add-on's
tests run it in a real host) (MIT, Microsoft and the .NET Foundation), and
`JsonSchema.Net` 9.4.0 (MIT, Greg Dennis). The SDK's tests use xUnit's own
assertions; FluentAssertions is not a dependency.

## Samples

The Photoshop sample uses `ws` 8.22.0, `@noble/hashes` 2.4.0 and `esbuild`
0.28.2, all under the MIT licence. Their licence texts are in
[the sample's notices](samples/photoshop-extension/THIRD-PARTY-NOTICES.md) and
`samples/photoshop-extension/licenses/`; its UXP build copies the noble licence
beside the crypto bundle it generates, and its package carries the notices and
licence texts.

Adobe Photoshop, its UXP tools, Node.js and .NET are obtained separately under
their own terms. No Adobe software or product artwork is distributed here.
