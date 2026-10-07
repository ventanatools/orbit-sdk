# VentanaTools.Orbit.Extensions.Tool

`orbit-ext`, the author tool for Orbit extensions. It creates projects from the
templates, validates manifests, packs and verifies packages, runs a project's
tests, simulates Orbit on a real named pipe, runs a companion with restart on
change, and checks where a companion finds its pairing.

```powershell
dotnet tool install --global VentanaTools.Orbit.Extensions.Tool --prerelease
orbit-ext new widget -n MyWidget --extension-id contoso.my-widget
cd MyWidget
dotnet tool restore
dotnet orbit-ext test
```

The packages are not published yet. Until they are, install the tool from a
folder of locally built packages (`--add-source <folder>`). Where a NuGet
configuration uses package source mapping, `dotnet tool install` refuses
`--add-source`: add the folder to that configuration, map
`VentanaTools.Orbit.Extensions*` to it, and leave `--add-source` out. The SDK
repository's own `NuGet.config` already maps the family to `artifacts/packages`,
so from its root `dotnet tool install VentanaTools.Orbit.Extensions.Tool
--tool-path artifacts/tools --prerelease` is enough. The tool carries the
packages a new project needs, so `orbit-ext new` works offline: it copies them,
with its own package, into `%LOCALAPPDATA%\VentanaTools\packages\<version>\`
(or the folder you give with `--feed`), and the project it creates restores
from there.

## Commands

| Command | What it does |
|---|---|
| `orbit-ext new <action\|widget\|node> [-n <name>] [-o <dir>] [--extension-id <id>] [--host <id>] [--display-name <name>] [--no-tests] [--feed <dir>]` | Prepares the feed, then runs `dotnet new orbit-ext-<kind>` with the same options and `--package-source <feed>`. Refuses an invalid `--extension-id`, or a `--host` that is not an active host, with exit 2 before anything is created. When the template pack is not installed, prints the command that installs it from the feed and exits 3. |
| `orbit-ext validate [<path>] [--host <id>] [--json] [--warnings-as-errors]` | Validates an `extension.json`, a folder that contains one (with its `strings/` folder), or a package. |
| `orbit-ext pack [<project-dir>] [-o <output-dir>] [--host <id>] [--force] [--json]` | Builds `<id>-<version>.orbitextension` from `extension.pack.json`, running its `build` step first. The default output folder is `artifacts/` in the project. |
| `orbit-ext verify <package> [--host <id>] [--json]` | Verifies a package and prints its id, version, hosts, contributions, file count, size and package hash. |
| `orbit-ext test [<project-dir>] [--host <id>] [-- <args…>]` | Validates `extension.json`, then runs `dotnet test`, or `npm test` for a project with a `package.json`. |
| `orbit-ext simulate [--manifest <path>] [--script <file>] [--json] -- <command> [args…]` | Runs a fake Orbit on a real named pipe with a temporary registration and pairing file, starts the companion with `--manifest` and `--pairing` appended, and drives it from a prompt or a script. Windows only. |
| `orbit-ext run [--watch] -- <command> [args…]` | Runs the companion and prints its status lines; with `--watch`, restarts it when `extension.json`, the pairing file or the sources change. |
| `orbit-ext link [<path>] [--host <id>] [--json]` | Shows where the companion looks for its pairing, whether a valid one is there, and the Orbit action that writes it. Never prints the secret. |
| `orbit-ext schema [--kind <manifest\|strings\|package\|pairing\|pack\|simulation>] [--host <id>] [-o <file>]` | Writes a bundled JSON Schema, for editors that work offline. |

`--host` defaults to the first id in the manifest's `hosts` that is an active
host. A `dotnet run` companion needs a trailing `--`:
`orbit-ext simulate -- dotnet run --project src/MyWidget --`.

## Output

Each diagnostic is one line in the MSBuild form that Visual Studio, VS Code
problem matchers and CI annotations recognize, followed by the fix; a summary
line ends the output:

```text
extension.json(12,9): error json.member-renamed: This member was renamed in schema 3 (capabilities is now provides; manifestVersion is now schemaVersion). [/contributions/0/capabilities]
  fix: Use the new name.
orbit-ext: 1 error, 0 warnings
```

A finding inside a package names the entry after `!/`, for example
`example.countdown-0.3.0.orbitextension!/extension.json(4,11)`. With `--json`,
the tool writes one object: `tool`, `version`, `command`, `ok`, `diagnostics`
(`code`, `path`, `message`, `severity`, `file`, `line`, `column`) and `summary`;
`pack` and `verify` add `package`, `link` adds `pairing`. `simulate --json`
writes one transcript entry per line. A failure always carries a code: a missing
path is `tool.path-missing` (with `file`), a file that cannot be read or written
is `tool.io-error`, and a command line the tool cannot run is `tool.usage`
(written when you asked for `--json`).

The tool prints diagnostic messages, paths and your own file names. It never
prints file contents, pairing secrets, nonces or proofs.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Error diagnostics (or warnings with `--warnings-as-errors`); a failed simulation expectation; no valid pairing (`link`); a failed test (`test`). |
| 2 | Usage error (`tool.usage` with `--json`). |
| 3 | A path is missing or unreadable (`tool.path-missing`, `tool.io-error`), the output exists, or the template pack is missing. |
| 4 | Internal error: `json.internal-error` from a reader, `tool.internal-error` otherwise. |
| 5 | `simulate`, `run` or `test` could not start the program. |

Without `--watch`, `run` exits with the companion's own exit code.

## Packing

`extension.pack.json` lists what goes into a package:

```json
{
  "$schema": ".schemas/pack.v1.json",
  "packVersion": 1,
  "readme": "PACKAGE-README.md",
  "strings": "strings",
  "build": { "project": "src/MyWidget/MyWidget.csproj" },
  "payload": [
    { "from": "assets", "to": "companion/assets", "include": ["**/*.png"] }
  ]
}
```

The `build` step runs `dotnet publish <project> -c Release -r win-x64` into a
temporary folder and copies the output to `payload/companion/` (`configuration`,
`runtime` and `to` change those). Copy rules then copy files or folders, relative
to the pack file, into folders under `payload/`. The tool always copies
`extension.json`, generates `extension.package.json`, leaves out `.git`, `.vs`,
`obj`, `node_modules/.cache`, `*.user`, `*.pairing.json` and `pairing.json`,
refuses links and pairing files among the staged files, writes the package
atomically and verifies it before it reports success. Packing the same files
twice gives the same bytes.

A copy rule's `include` (default `["**/*"]`) and `exclude` globs are paths below
its `from`, matched without regard to case. Within a name `*` matches any run of
characters and `?` exactly one; `**` as a whole segment matches any number of
folders, including none, and at the end of a glob every file below, as a glob
ending in `/` does (`src/**` and `src/` are the same). An exclude glob that
matches a folder, or ends with `**` below it, leaves out the whole folder. A glob
with a `..` segment, or an empty one, is refused (`package.path`) with its
position in `extension.pack.json`.

The left-out names apply whether they name a file or a folder (a `.git` file is
how a Git worktree or submodule points to its repository). They and the link
check apply to every folder on the way from the project folder to each staged
file, including the paths the configuration names: a copy rule whose `from` is,
or lies in, a left-out name copies nothing; a file named directly with a pairing
file's name is refused (`pack.secret`); a readme in a left-out folder leaves the
package without one (`package.file-missing`); and a link anywhere on the way is
refused (`pack.link`). The project folder and the folders above it are never
checked.

Inside a copied folder the tool never looks into a link; it decides on the link
itself, before the globs filter what is inside it. A link to a file is refused
when the globs select it, and a link to a folder when the globs could select
anything inside it, even if nothing there would match. A link the globs cannot
reach is left out, so the junction npm makes under `node_modules` for a `file:`
dependency does not stop a pack whose globs never reach `node_modules` (or that
excludes `node_modules/**`).

`pack` never packs its own output. A copied folder that contains the output
folder (by default `artifacts/` in the project folder) leaves it out, and package
files directly in the output folder are never copied, even by a copy rule of the
output folder itself or, with `-o .`, of the project folder. The output folder is
recognized however `-o` spells it, including through a junction or symbolic
link, a substituted drive or an 8.3 short name. When the output folder is itself
a junction or symbolic link inside a copied folder (an `artifacts` folder
redirected to a Dev Drive, say), it is left out like any other output folder
rather than refused as a link. A link that only contains the output folder is
still refused.

## Simulation scripts

A script is a JSON array of steps, run in order (schema:
`orbit-ext schema --kind simulation`):

```json
[
  { "start": "contoso.my-widget/tally", "as": "s1" },
  { "expectFace": "s1", "within": "2s" },
  { "invoke": "s1", "expect": "Done" },
  { "expectFace": "s1", "within": "2s", "line1": "1" },
  { "stop": "s1" },
  { "disconnect": "host.reloaded" }
]
```

Steps: `start` (with `settings` and `as`), `invoke` (with `expect`, `failure`,
`as` and `wait`), `cancel`, `stop`, `expectFace` (with `within`, `line1`,
`line2` and `state`), `disconnect` and `wait`. A failed expectation exits 1.
The simulated host creates its pipe as Orbit does: for the current user only,
with a Medium mandatory label that keeps lower-integrity (sandboxed) processes
out, rejecting remote clients, as the first and only instance of its name. Its
temporary pairing file is readable only by you and is deleted when the
simulation ends.

## Licence

Apache-2.0. Copyright 2026 Ventana Tools LLC.
