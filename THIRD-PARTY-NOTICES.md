# Third-party dependencies

The `VentanaTools.Orbit.Extensions` library has no third-party NuGet dependencies;
it uses the .NET runtime supplied by the recipient. This repository's licenses
(Apache-2.0, and MIT-0 for the samples) apply to its own code, not to
dependencies or externally installed apps.

The public tests restore `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.0 and
`Microsoft.NET.Test.Sdk` 18.10.1 from nuget.org, and the packable projects use the
build-time analyzer `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0. Their
package licenses and notices remain applicable; they are build and test
dependencies and are not included in the SDK NuGet packages. FluentAssertions is
not a public test dependency.

The Photoshop example uses `ws` 8.22.0, `@noble/hashes` 2.4.0 and `esbuild` 0.28.2.
Their MIT license texts are retained in
[the example's notices](samples/photoshop-extension/THIRD-PARTY-NOTICES.md) and
`samples/photoshop-extension/licenses/`. The UXP build also copies the noble
license alongside its generated crypto bundle and preserves bundled notices.
Example package staging includes the dependency notices and license texts.

Adobe Photoshop, its UXP tools, Node.js and .NET are separately obtained software
with their own terms. No Adobe software or product artwork is distributed here.
