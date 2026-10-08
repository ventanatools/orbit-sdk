# VentanaTools.Orbit.Extensions.Templates

`dotnet new` templates for extension companions built on
`VentanaTools.Orbit.Extensions`. Installing the package makes them appear in
`dotnet new` and in Visual Studio's New Project dialog.

| Short name | Creates |
|---|---|
| `orbit-ext-action` | A .NET companion with one action that has one `Choice` setting, its handler deriving from `ContributionHandler`, and an xUnit test project that uses `VentanaTools.Orbit.Extensions.Testing`. |
| `orbit-ext-widget` | The same, with one widget that publishes a face and can be invoked. |
| `orbit-ext-node` | A Node.js companion that uses `@ventanatools/orbit-extensions`, with `node --test` tests that use its test kit. |

Every template also writes `extension.json` (with `$schema` pointing to the
bundled copy in `.schemas/`), `extension.pack.json`, `PACKAGE-README.md`, a local
tool manifest pinning `VentanaTools.Orbit.Extensions.Tool` (command `orbit-ext`),
a `nuget.config` and a `.gitignore` that keeps pairing files out of source
control. The templates have no post-actions.

The easiest way to create a project is the tool, which prepares a package
folder and passes it to the template:

```powershell
orbit-ext new widget -n MyWidget --extension-id contoso.my-widget
```

## Parameters

| Option | Default | Meaning |
|---|---|---|
| `--extension-id` | `example.` and the lowercased project name | The extension id, `publisher.name` in lowercase letters, digits and single hyphens. |
| `--host` | the host's current id | The host id written into `hosts`. `orbit-ext new` accepts only an active host's id. |
| `--display-name` | The project name | The extension's name. |
| `--package-source` | the per-user feed | A folder of packages. The `nuget.config` restores the `VentanaTools.Orbit.Extensions*` packages from that folder only, and everything else from nuget.org. Without it (a plain `dotnet new`, or Visual Studio's New Project dialog) the folder is the per-user feed that `orbit-ext new` fills, `%LOCALAPPDATA%\VentanaTools\packages\<version>`, so restore fails until that folder exists and never takes a package with these names from nuget.org. `orbit-ext new` always passes its feed. |
| `--no-tests` | tests included | The .NET templates only: create no test project. |

The template engine cannot refuse an option value, so an `--extension-id`
outside the id grammar creates a project that does not build (.NET) or start
(Node) until `extension.json` is corrected; `orbit-ext new` refuses such an id
before it creates anything.

## Windows on Arm

`orbit-ext pack` publishes a .NET companion for `win-x64` unless
`extension.pack.json`'s `build` names another `runtime`, and the templates
publish it framework-dependent. On a Windows on Arm PC an x64 framework-dependent
program runs under emulation only when the x64 .NET runtime is installed; the
Arm64 runtime alone does not run it. The generated `PACKAGE-README.md` says so.

A package carries one runtime's build. To reach every PC with one package, set
`SelfContained` to `true` in the companion's project: the x64 build then carries
its runtime and runs on Windows on Arm under emulation with nothing else
installed, at the cost of a larger package. To build a native Arm64 companion
instead, set `"runtime": "win-arm64"` in the `build` step; that package then runs
only on Arm64 PCs.

## Licences

The templates are MIT-0: what they generate is yours, with no attribution
required. The SDK library your companion carries is Apache-2.0, and its licence
asks that its `LICENSE` and `NOTICE` travel with every copy you distribute, so
each generated project has them in `licenses/sdk/`, and its
`extension.pack.json` copies that folder into every package
(`payload/licenses/sdk/`). Keep the rule when you change the pack configuration.
