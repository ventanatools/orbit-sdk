# Third-party dependencies

The production SDK and Protocol libraries have no third-party NuGet dependencies;
they use the .NET runtime supplied by the recipient. This repository's Apache-2.0
license applies to its own code, not to dependencies or externally installed apps.

The public tests restore `xunit` 2.9.3, `xunit.runner.visualstudio` 2.8.2 and
`Microsoft.NET.Test.Sdk` 17.14.1 from nuget.org. Their package licenses and notices
remain applicable; they are test dependencies and are not included in the SDK
NuGet packages. FluentAssertions is not a public test dependency.

The Photoshop example uses `ws` 8.22.0, `@noble/hashes` 2.4.0 and `esbuild` 0.28.2.
Their MIT license texts are retained in
[the example's notices](samples/photoshop-extension/THIRD-PARTY-NOTICES.md) and
`samples/photoshop-extension/licenses/`. The UXP build also copies the noble
license alongside its generated crypto bundle and preserves bundled notices.
Example package staging includes the dependency notices and license texts.

Adobe Photoshop, its UXP tools, Node.js and .NET are separately obtained software
with their own terms. No Adobe software or product artwork is distributed here.
