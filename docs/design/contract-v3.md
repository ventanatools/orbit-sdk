# Orbit extension contract, generation 3

This document is the normative specification for extensions that run as
separate programs (companions) beside Orbit, the radial launcher by Ventana
Tools. It covers the manifest, diagnostics, the package archive, the pairing
file, the wire protocol, reason codes, the .NET and Node SDK surfaces, the
author tool, and the versioning policy.

The key words MUST, MUST NOT, REQUIRED, SHALL, SHALL NOT, SHOULD, SHOULD NOT,
RECOMMENDED, NOT RECOMMENDED, MAY and OPTIONAL are to be interpreted as
described in BCP 14 (RFC 2119 and RFC 8174) when, and only when, they appear in
all capitals.

Sections marked *Informative* explain or give examples. Everything else is
normative.

## Contents

1. [Scope, status and terminology](#1-scope-status-and-terminology)
2. [Identifiers and naming](#2-identifiers-and-naming)
3. [Manifest schema 3](#3-manifest-schema-3)
4. [Diagnostics](#4-diagnostics)
5. [Package archive version 2](#5-package-archive-version-2)
6. [Pairing file version 3](#6-pairing-file-version-3)
7. [Wire protocol 3](#7-wire-protocol-3)
8. [Reason-code catalog](#8-reason-code-catalog)
9. [.NET SDK public API](#9-net-sdk-public-api)
10. [Node SDK](#10-node-sdk)
11. [Tooling](#11-tooling)
12. [Versioning and compatibility](#12-versioning-and-compatibility)
13. [Appendix A: golden test vectors](#appendix-a-golden-test-vectors)
14. [Appendix B: algorithms](#appendix-b-algorithms)

---

## 1. Scope, status and terminology

### 1.1 Scope

This specification defines the contract between Orbit and the third-party code
it runs out of process. Its wire and file-format identifiers are
product-neutral, and the product name is confined to the places §2.1 lists, so
renaming the product before release is a scripted, mechanical change (§12.4).

Orbit and Lollipop have separate SDKs. Lollipop does not use this contract, its
wire or its packages; it gets its own SDK later. What the two products share is
a set of conventions, so that developers understand the ecosystem (§2.7).
Orbit's SDK follows them, and Lollipop's separate SDK is expected to follow the
same conventions.

The contract does not cover code that runs inside a host process. No host
loads third-party code into its own process, in any tier.

### 1.2 Status

This is a **pre-1.0 preview**. Every format and API in this document MAY
change before the SDK reaches 1.0. Any incompatible change increments the
version number of the affected format, so a mismatch is always reported with a
stable code instead of failing silently. §12 gives the full policy.

| Format | Version in this document | Identified by |
|---|---|---|
| Manifest | schema 3 | `schemaVersion: 3` in `extension.json` |
| Localized strings file | schema 3 | `schemaVersion: 3` in `strings/<tag>.json` |
| Package archive | archive 2 | `archiveVersion: 2` in `extension.package.json` |
| Pack configuration | pack 1 | `packVersion: 1` in `extension.pack.json` |
| Pairing file | pairing 3 | `pairingVersion: 3` |
| Wire protocol | protocol 3 | negotiated in `hello` and `challenge` |
| Transport generation | v3 | the HMAC label and pipe prefix `Ventana.Extensions.v3` |

Version 2 of every format, and every earlier one, is withdrawn. A conforming
host or SDK MUST NOT accept a manifest with `schemaVersion` 2, a version-2
pairing file, a version-1 package descriptor named `package.json`, or a
protocol-2 `hello`.

### 1.3 Conformance classes

- A **host** is an application that admits manifests, owns consent, runs the
  pipe server and renders faces.
- A **companion** is a program, written in any language, that implements
  contributions and connects to a host.
- An **SDK** is a library a companion uses to implement this contract. The
  reference SDKs are `VentanaTools.Orbit.Extensions` (.NET) and
  `@ventanatools/orbit-extensions` (Node).
- A **tool** is a program that validates, packs or verifies extensions. The
  reference tool is `orbit-ext`.

A requirement addressed to "receivers" applies to hosts and companions alike.

### 1.4 Terminology

| Term | Meaning |
|---|---|
| **Extension** | The installable bundle: one manifest, optionally delivered in a package archive. People install, turn on, turn off and remove extensions. |
| **Contribution** | One entry in the manifest's `contributions` array. The umbrella word authors use; hosts do not show it to people. |
| **Action** | A contribution whose `provides` is exactly `["invoke"]`. Picking it runs something. |
| **Widget** | A contribution whose `provides` includes `"face"`. It shows live state, and it MAY also be invoked. |
| **Face** | The bounded live state a companion publishes for one session: a picture, up to two lines of text, a state, a sentence for assistive technology, and a lifetime. The host renders every part of it. |
| **Companion** | The separate program that implements an extension's contributions. People start it; a future host-managed activation may start it (§6.5). |
| **Host** | The application that admits extensions and runs the pipe server. Identified on the wire by its host id (§2.3). |
| **Registration** | A host's record of one admitted extension: its manifest, consent, credential and identity pin. One registration has one pipe and at most one connection. |
| **Pairing** | The credential file a host writes, on request, so a companion can authenticate to one registration (§6). |
| **Connection** | One authenticated pipe connection between a companion and a registration. |
| **Session** | A host-started instance of one contribution with a fixed, complete set of setting values. A session belongs to one connection and ends with it. |
| **Invocation** | One request to run an action, sent for a current session and answered with one result. |
| **Placement** | A host-side use of a contribution (for example a ring item). Placements are never visible on the wire. |
| **Capability** | A named, negotiated feature of the wire protocol or a host (§7.4). |
| **Diagnostic** | A finding about a static file: manifest, strings file, package or pack configuration (§4). |
| **Reason code** | A stable code that explains a runtime event: a refused connection, a dropped frame, a refused session (§8). |
| **Developer mode** | A host setting that unlocks author tooling. It never relaxes consent, authentication, limits or text cleaning. |
| **Verified server** | A pipe server a companion has checked, either because it proved knowledge of the secret in `challenge` (§7.3.2) or because the operating system confirmed that it runs as the current user at an integrity level no lower than the companion's and that its pipe was not created at a lower level (§7.1). Only a verified server can stop an SDK (§9.2). |

---

## 2. Identifiers and naming

### 2.1 Rules

1. **The product-name rule.** Wire and file-format identifiers are
   product-neutral. The product name appears only in (a) the package family
   `VentanaTools.<Product>.Extensions*` and its namespaces, assembly, folder
   and solution names, (b) the tool command and template short names, (c)
   `fixtures/hosts.json` (host id, display name, package file extension), and
   (d) display text. Before the first release, renaming Orbit to Pinwheel is a
   scripted, mechanical rename (§12.4).

   The Node package `@ventanatools/<product>-extensions` and its folder belong
   to the package family (a), and so does the SDK repository's name
   (`orbit-sdk`), which is renamed with the product. Values derived from
   `hosts.json` (c) are regenerated from it, never written by hand: the host id
   segment of pipe names, schema URLs, help links and the pairing folder, the
   `hosts` and `$schema` members of sample and template manifests, and the
   template `--host` default. Everything else on the wire and in files is
   product-neutral: the HMAC label and pipe prefix `Ventana.Extensions.v3`,
   the file names `extension.json`, `extension.package.json`,
   `extension.pack.json` and `strings/<language-tag>.json`, the pairing folder
   `%USERPROFILE%\.ventana\pairings\`, the schema URL pattern, diagnostic and
   reason codes, and capability ids.
2. Each implementation reads the host id, the host display name and the
   package file extension from **one** definition. In the SDK repository that
   definition is the data file `fixtures/hosts.json` (Appendix A). The .NET
   author package embeds it and exposes it as `HostRegistry` (§9.1), the Node
   SDK exports it as `hosts` (§10), the codename script regenerates every
   derived file in the repository from it (the Node copy, sample and template
   manifests, template defaults, the schema folder and the file's own pin),
   and the docs site's sync script generates its redirects and schema paths
   from it. A host application keeps its own id in one constant. The package
   family name reaches code only as namespace and assembly identifiers (§9),
   and the tool reads its own command name from its assembly (§11.1), so no
   source string names the product. A test enforces this (`ProductNameConfinementTests`,
   §2.8).
3. Assembly names equal package IDs, and each package's root namespace equals
   its ID. The author package has two further namespaces, `.Packaging` and
   `.Wire`, for hosts, tools and other-language implementations (§9).
4. Open registries of identifiers (capability ids, reason codes, diagnostic
   codes) use one grammar (§2.4). Closed enumerations use PascalCase tokens
   (§7.13).

### 2.2 Identifier table

The last column says whether an identifier carries the product name, and under
which part of the product-name rule (§2.1): (a) package family, (b) tool and
templates, (c) `hosts.json`.

| Identifier | Value | Grammar or notes | Product name |
|---|---|---|---|
| NuGet package: author package | `VentanaTools.Orbit.Extensions` | Library, Apache-2.0. Declarations, faces, failures, reason codes, diagnostics, pairing, the companion client, packaging and wire (§9.1, §9.2) | (a) |
| NuGet package: test kit | `VentanaTools.Orbit.Extensions.Testing` | Library, Apache-2.0 (§9.3) | (a) |
| NuGet package: Generic Host add-on | `VentanaTools.Orbit.Extensions.Hosting` | Library, Apache-2.0. Runs a companion as a hosted service of the .NET Generic Host (§9.5) | (a) |
| NuGet package: author tool | `VentanaTools.Orbit.Extensions.Tool` | .NET tool, Apache-2.0 (§11.1) | (a) |
| NuGet package: project templates | `VentanaTools.Orbit.Extensions.Templates` | Template pack, content MIT-0 (§11.4) | (a) |
| Namespaces | `VentanaTools.Orbit.Extensions` (author-facing), `VentanaTools.Orbit.Extensions.Packaging` and `VentanaTools.Orbit.Extensions.Wire` (hosts, tools and other-language implementations), `VentanaTools.Orbit.Extensions.Testing`, `VentanaTools.Orbit.Extensions.Hosting` | Root namespaces equal package IDs; the author package adds two (§9) | (a) |
| Assembly names | Equal to package IDs (the template pack has no assembly) | | (a) |
| Solution, project folders | `VentanaTools.Orbit.Extensions.slnx`; `src/VentanaTools.Orbit.Extensions*/`, `tests/VentanaTools.Orbit.Extensions*.Tests/`, `templates/` | | (a) |
| npm package (unpublished) | `@ventanatools/orbit-extensions`, in `node/orbit-extensions/` | Node SDK; `"private": true` until a publication decision (§10) | (a) |
| MSBuild property | `VentanaToolsImplicitUsings` | Opts in to the author package's global using (§9.2) | No |
| HMAC transcript label | `Ventana.Extensions.v3` | First line of the proof transcript (§7.3.4) | No |
| Pipe-name prefix | `Ventana.Extensions.v3.` | Followed by host id, edition, user hash and registration id (§2.5) | No |
| Host id (current host) | `orbit` | Host-id grammar (§2.4); current codename of the Orbit launcher; see §2.3 | (c) |
| Host display name | `Orbit` | Display text only | (c) |
| Package file extension | `.orbitextension` | Assigned per host by the registry | (c) |
| Manifest file name | `extension.json` | In a folder and at the archive root | No |
| Package descriptor | `extension.package.json` | Archive root only; generated by tools (§5.6) | No |
| Package readme | `README.md` | Archive root | No |
| Payload folder | `payload/` | Archive root | No |
| Localized strings folder | `strings/` | Archive root, or beside a loose `extension.json` | No |
| Pack configuration file | `extension.pack.json` | Author's project folder; read by `orbit-ext pack` (§11.3) | No |
| Pairing file name | `<extension-id>.pairing.json` | Written by a host's Save connection info; never packed (§11.3) | No |
| Per-user pairing folder | `%USERPROFILE%\.ventana\pairings\<host-id>\` | Default location of pairing files (§6.6) | The `<host-id>` segment (c) |
| JSON Schema URLs, host-keyed | `https://dev.ventana.tools/schemas/extensions/<host-id>/<name>.v<N>.json` | `<name>` is `manifest` or `strings` | The `<host-id>` segment (c) |
| JSON Schema URLs, host-neutral | `https://dev.ventana.tools/schemas/extensions/<name>.v<N>.json` | `<name>` is `package`, `pairing` or `pack` | No |
| Test host id | `example-host` | Used by every fixture and test vector (Appendix A); never a registry entry, so a rename never changes a vector | No |
| Help links in host UI | `https://dev.ventana.tools/go/<host-id>/<slug>` | Stable redirects; slugs in §8.4 | The `<host-id>` segment (c) |
| Tool command | `orbit-ext` | `ToolCommandName` of `VentanaTools.Orbit.Extensions.Tool` | (b) |
| Template short names | `orbit-ext-action`, `orbit-ext-widget`, `orbit-ext-node` | The tool command plus the kind (§11.4) | (b) |
| Template identities | `VentanaTools.Orbit.Extensions.Templates.Action.CSharp`, `VentanaTools.Orbit.Extensions.Templates.Widget.CSharp`, `VentanaTools.Orbit.Extensions.Templates.Node` | | (a) |
| Template `--host` default | `orbit` | Template parameter default, regenerated from `hosts.json` by the codename script | (c) |
| Environment variables | None | No environment variable is part of this contract | — |
| Copyright holder | Ventana Tools LLC | All packages, NOTICE and SPDX headers | — |

### 2.3 Host-id registry

A host id is a stable technical codename. It is not a display name and it
never appears in a URL path for documentation pages.

| Host id | Display name | Package file extension | Status |
|---|---|---|---|
| `orbit` | Orbit | `.orbitextension` | Active. The current codename of the radial launcher. The product may be renamed before release; §12.4 says how the id, display name and file extension change. |
| `lollipop` | Lollipop | (none) | Reserved. Lollipop has its own, separate extension SDK and does not use this contract (§1.1); the id is reserved so that no manifest or pairing under this contract can claim it. |
| `pinwheel` | — | — | Reserved. A candidate product name. |

A registry entry also carries an `aliases` list. It is empty in this version.
After a release, a rename adds the old id as an alias so existing manifests
keep working (§12.4).

The registry is the data file `fixtures/hosts.json`: for each entry its `id`,
`displayName`, `packageExtension` (absent while a host has none), `aliases` and
`status` (`Active` or `Reserved`). The file also lists `reservedIds`: every
id that no third party may claim, active or reserved, with or without an entry
(`orbit`, `lollipop` and `pinwheel` today). A reserved or former id never
leaves the reserved lists, so a renamed product's old id can never be taken by
a third party, and stays a product name for the confinement test (§2.8).

Fixtures and test vectors use the test host id `example-host` (§2.2), never a
registry id. Readers that warn about unknown host ids take the list of known
hosts as an option (§9.1), so tests pass a list containing `example-host`.

### 2.4 Shared grammars

| Name | Grammar (ABNF-like; all characters ASCII) | Max length |
|---|---|---|
| segment | `[a-z0-9]+ ( "-" [a-z0-9]+ )*` | 64 |
| extension id (third party) | `segment ( "." segment )+` | 128 |
| extension id (first party) | `segment` | 128 |
| contribution id | `extension-id ( "/" segment )+`, or the extension id itself for a lone leaf (§3.4) | 128 |
| reserved publisher | first `segment` of a third-party extension id; see §3.5 | — |
| host id | `[a-z] [a-z0-9]* ( "-" [a-z0-9]+ )*` | 32 |
| setting id | `[a-z] [A-Za-z0-9]*` | 32 |
| choice value | `segment` | 64 |
| dotted code (capability ids, reason codes, diagnostic codes) | `segment ( "." segment )+` | 64 |
| experimental capability id | `"x." segment ( "." segment )*` | 64 |
| language tag | `[A-Za-z]{2,3} ( "-" [A-Za-z0-9]{1,8} )*`, compared case-insensitively | 35 |
| package version | `num "." num "." num`, `num = "0" / [1-9][0-9]{0,8}` | 29 |
| GUID (wire form) | 32 lowercase hexadecimal digits, not all zero | 32 |
| nonce, secret, proof | canonical standard Base64 with padding of exactly 32 bytes (44 characters) | 44 |
| SHA-256 (hash form) | 64 lowercase hexadecimal digits | 64 |
| JSON member name | `[a-z] [A-Za-z0-9]*`, or one of `$type`, `$schema` | 32 |

### 2.5 Pipe name

```text
pipe-name     = "Ventana.Extensions.v3." host-id "." edition "." user-hash "." registration-id
edition       = 1*32( [a-z0-9] / "-" )
user-hash     = 16 lowercase hex digits: the first 16 of SHA-256(UTF-8 of the user's SID string)
registration-id = GUID (wire form)
```

- Clients connect to `\\.\pipe\` followed by the pipe name.
- The `v3` in the prefix names the transport generation, not the negotiated
  protocol version. A host that negotiates protocol 4 MAY keep this prefix.
- A host chooses the edition string. Orbit uses `store`, `sideload`, `dev` and
  `unknown`. Clients MUST treat the edition as opaque.
- A client MUST check that a pipe name matches this grammar, that its host id
  equals the pairing file's `hostId`, and that its registration id equals the
  pairing file's `registrationId`. A client MUST NOT compare any segment against
  a product literal.

### 2.6 Licences and headers

- Libraries, the tool, fixtures and schemas: Apache-2.0. Header:
  `SPDX-License-Identifier: Apache-2.0` and
  `SPDX-FileCopyrightText: 2026 Ventana Tools LLC`.
- Samples and templates: MIT-0, with a `LICENSE` file in each sample folder and
  in the template pack. Sample sources carry `SPDX-License-Identifier: MIT-0`.
  Files a template generates carry no header, so the author owns them outright.

### 2.7 Ventana conventions

*Informative.* Orbit and Lollipop have separate SDKs: no code, package, wire
protocol, pipe, pairing or package archive is shared between them. They share
conventions, so a developer who knows one product recognizes the other.
Orbit's SDK follows these conventions, and Lollipop's separate SDK is expected
to follow the same conventions.

| Convention | In this contract |
|---|---|
| Extension id grammar and the reserved publishers | §2.4, §3.4, §3.5; `fixtures/ids.json`, `fixtures/reserved-publishers.json` |
| The manifest identity block (`$schema`, `schemaVersion`, `id`, `name`, `description`, `version`, `hosts`) | §3.2 |
| The setting-row shape `{ id, kind, name, description, default, choices[{ value, name }] }`, with PascalCase kind tokens such as `Choice` | §3.3.1 |
| The declaration text rule | §3.6; `fixtures/text-rules.json` |
| The declaration text limits, in UTF-16 code units: a name (of an extension or a contribution) 80, a description 512, a label (the publisher's name, a setting's or a choice's name) 80, the same for each translation; and the warning `text.long` for a name or label longer than 32 text elements | §3.2, §3.3, §3.6, §3.10; `fixtures/text-rules.json` (`declarationLimits`) |
| The diagnostics shape (`code`, `path` as a JSON Pointer, `message`, `severity`, `line`, `column`) and dotted code names | §4; `fixtures/codes/diagnostics.json` |
| The failure vocabulary (`UnsupportedInput`, `NeedsSetup`, `Network`, `NoResult`, `AppUnavailable`, and capability-gated product tokens such as `LlmUnavailable`) | §7.8 |
| The reason-code grammar (dotted lowercase codes in an open registry) | §2.4, §8 |
| The model of live state: the host starts a session per placement, with a complete, fixed set of setting values and an opaque id, and ends every session with its connection, replaying nothing; each published value carries a lifetime that the host counts from its arrival, never a time of its own; and the host keeps a per-session budget that coalesces values to the newest, under message budgets that throttle before they close, cleaning text instead of refusing it. Lollipop's published signals are expected to reuse this model | §7.6, §7.7, §7.10 |
| The trust and consent shape: nothing runs until the person consents, extension-supplied text is shown as unverified, access growth asks again, and developer mode never relaxes consent, authentication, limits or text cleaning | §1.4, §5.8, §5.9 |
| Package and tool naming: `VentanaTools.<Product>.Extensions*` and a `<product>-ext` tool | §2.1, §2.2 |
| The dependency rule. The author package depends only on the base class library. The test kit and add-on packages also depend on their author package at the exact same version. An add-on's other direct dependencies are limited to `Microsoft.Extensions.*.Abstractions` packages and `Microsoft.Extensions.Options`. | §9, §9.5 |
| The tooling verbs `new`, `validate`, `pack`, `verify` and `test` | §11.1 |
| Test-kit naming (`<package>.Testing`, `RecordingSession`, `ContributionContractSuite`, `ExtensionConformance.AssertAuthoringContractAsync`, `ConformanceException`) | §9.3 |
| The licensing pattern: Apache-2.0 libraries and tools, MIT-0 samples and templates, Ventana Tools LLC as copyright holder | §2.6 |
| One documentation site, dev.ventana.tools, with a Platform section that documents these conventions once for both products | §8.4 |

### 2.8 Product-name confinement

The SDK repository enforces the product-name rule (§2.1) with
`ProductNameConfinementTests`. Its rule:

- **Product names** are every `id`, `displayName` and `reservedIds` entry in
  `fixtures/hosts.json`, and every `packageExtension` without its leading dot,
  matched case-insensitively anywhere in a token (so `OrbitHost`, `"orbit"`,
  `.orbitextension` and `orbit-ext` all match). The test reads them from the
  file and derives the package family name from the author package's assembly
  name, so it contains no product literal itself.
- **C# under `src/`.** Every tracked `*.cs` file under `src/` is parsed with
  Roslyn. No string literal of any form (regular, verbatim, raw, interpolated
  text, UTF-8), character literal, comment or XML documentation may contain a
  product name. An identifier token may contain one only when it is the
  product segment of the package family's qualified name, that is the
  `Orbit` in `VentanaTools.Orbit.Extensions` in a namespace declaration,
  `using` directive, alias or fully qualified name. Package names that code
  needs (for `InternalsVisibleTo`, sibling package IDs or the `hello` client
  name) come from MSBuild items or the assembly's own name, never a literal.
- **Other files under `src/`.** In every other tracked file under `src/`, a
  product name may appear only inside the package family name
  (`VentanaTools.Orbit.Extensions…`, in project files, `PublicAPI.*.txt` and
  MSBuild files) or the Node package's name (`@ventanatools/orbit-extensions`
  and its tarball `ventanatools-orbit-extensions-<version>.tgz`, which the
  tool package bundles, §11.1), in the tool project's `ToolCommandName`
  property, and in package readmes and package `Description` metadata
  (display text). No JSON
  or other data file under `src/` contains a product name: the product data
  reaches code only through the embedded `fixtures/hosts.json`, linked from
  outside `src/`.
- **Node.** In `node/orbit-extensions/`, a product name appears only in
  `lib/hosts.json` (the checked-in copy of `fixtures/hosts.json`), in the
  package name `@ventanatools/orbit-extensions` and its subpaths (in
  `package.json`, `package-lock.json`, `index.d.ts` module declarations and
  `require` calls), in `README.md`, and in `NOTICE`, the byte copy of the
  repository's `NOTICE` that the npm tarball carries.

Tests, fixtures, samples and templates are outside this test: fixtures and
vectors use `example-host` (Appendix A), and the repository's leak lint checks
samples and templates.

---

## 3. Manifest schema 3

### 3.1 File rules

- The manifest is a UTF-8 JSON document named `extension.json`. A UTF-8 byte
  order mark MAY be present and MUST be ignored by readers. A UTF-16 or UTF-32
  byte order mark is an error (`json.encoding`).
- Maximum size: 65,536 bytes. Maximum nesting depth of objects and arrays: 8.
- The document is one JSON object. Comments, trailing commas and trailing
  content are errors (`json.syntax`). Duplicate member names at any depth are
  errors (`json.duplicate-member`).
- Member names are compared ordinally, after JSON unescaping, both for
  duplicates and against the names defined below: `"\u0069d"` is `id`. Readers
  MUST NOT bind members case-insensitively.
- Members not defined below are errors (`json.unknown-member`), or
  `json.member-renamed` for a name a previous schema used (§4.4). The manifest
  is strict on purpose: authors learn about typos immediately, and new members
  arrive with a new schema version (§12).
- `null` is never a valid value (`json.null-not-allowed`). Leave optional
  members out instead.
- Integers are JSON numbers without a fraction or exponent.

### 3.2 Root object

| Member | Type | Required | Limits and rules | Default |
|---|---|---|---|---|
| `$schema` | string | No | ≤ 512 UTF-16 units. Ignored by hosts. SHOULD be the published manifest URL (§2.2) of a host listed in `hosts`, or a relative path or `file:` URL ending in `manifest.v3.json` (a local copy, as templates write). An absolute `http` or `https` URL that is not the published manifest URL of any listed host raises the warning `schema.uri-mismatch`. | — |
| `schemaVersion` | integer | Yes | Exactly `3`. | — |
| `id` | string | Yes | Third-party extension id (§2.4), not starting with a reserved publisher (§3.5), with no `/`. | — |
| `name` | string | Yes | Declaration text (§3.6), 1–80 UTF-16 units. | — |
| `description` | string | Yes | Declaration text, 1–512 units. | — |
| `version` | string | Yes | Package version (§2.4). Compared numerically, part by part. | — |
| `hosts` | array of string | Yes | 1–8 unique host ids (§2.4). A host admits the manifest only if its own id, or one of its aliases, is listed. | — |
| `glyph` | string | No | Glyph rule (§3.7). Shown for the extension as a whole. | The first contribution's glyph |
| `publisher` | object | No | §3.2.1. Shown as unverified, extension-supplied text. | — |
| `supportUrl` | string | No | URL rule (§3.8). | — |
| `defaultLanguage` | string | No | Language tag (§2.4) of the manifest's own text. | `en-US` |
| `disclosures` | object | No | §3.2.2. Declarative only; never enforced. | — |
| `requires` | object | No | §3.2.3. | — |
| `contributions` | array of object | Yes | 1–32 contributions (§3.3). Ids unique. | — |

#### 3.2.1 `publisher`

| Member | Type | Required | Rules |
|---|---|---|---|
| `name` | string | Yes | Declaration text, 1–80 units. |
| `url` | string | No | URL rule (§3.8). |

The publisher is a claim. No host verifies it in this version. Hosts MUST
label it as supplied by the extension and unverified.

#### 3.2.2 `disclosures`

| Member | Type | Required | Rules |
|---|---|---|---|
| `network` | string | Yes | One of `None`, `LocalNetwork`, `Internet`: whether the companion contacts other computers. |
| `privacyUrl` | string | No | URL rule (§3.8). |

Hosts SHOULD show disclosures in install review and consent. Hosts MUST say
they are the author's statement, not something the host checked. A companion
runs with the person's full rights, so no manifest member can restrict it.

#### 3.2.3 `requires`

| Member | Type | Required | Rules |
|---|---|---|---|
| `capabilities` | array of string | Yes | 1–16 unique capability ids (§7.4). |

A host MUST refuse to admit a manifest that requires a capability the host does
not implement (`requires.capability-unsupported`). Capability ids that start
with `x.` are experimental: a host MUST refuse them unless developer mode is on.
A tool without a host context reports unknown ids as the warning
`requires.capability-unknown`. A capability id listed twice is
`requires.capability-duplicate`, and one longer than 64 characters is
`string.too-long`. An id that breaks the dotted-code grammar can never be a
known or implemented capability, so it is `requires.capability-unknown` (a
tool) or `requires.capability-unsupported` (a host). Every baseline feature of
protocol 3 is implied by `schemaVersion: 3`, so a manifest that uses only
baseline features has no `requires` member.

### 3.3 Contribution object

| Member | Type | Required | Rules | Default |
|---|---|---|---|---|
| `id` | string | Yes | Contribution id (§2.4, §3.4). Unique in the manifest. | — |
| `name` | string | Yes | Declaration text, 1–80 units. | — |
| `description` | string | Yes | Declaration text, 1–512 units. | — |
| `glyph` | string | Yes | Glyph rule (§3.7). | — |
| `provides` | array of string | Yes | 1–2 unique values from `invoke`, `face`. Order is not significant. | — |
| `settings` | array of object | No | 0–16 setting rows (§3.3.1). Ids unique within the contribution. | `[]` |

- `provides: ["invoke"]` makes the contribution an action.
- `provides: ["face"]` makes it a passive widget: picking it changes nothing.
- `provides: ["invoke", "face"]` makes it a widget that can also be invoked.

Settings are stored by the host per placement: two placements of the same
contribution can have different values. Every session carries the complete set
of values (§7.6.1).

#### 3.3.1 Setting row

The setting row has the shape of the Ventana convention (§2.7), which
Lollipop's separate SDK is expected to use as well:
`{ id, kind, name, description, default, choices }`.

| Member | Type | Required | Rules |
|---|---|---|---|
| `id` | string | Yes | Setting id (§2.4). |
| `kind` | string | Yes | `Choice`. `Toggle`, `Text` and `Number` are reserved names for later schema versions; in schema 3 they raise `setting.kind-unsupported`, as does any other value. |
| `name` | string | Yes | Declaration text, 1–80 units. |
| `description` | string | No | Declaration text, 1–512 units. |
| `default` | string | Yes for `Choice` | MUST equal one choice `value`. |
| `choices` | array of object | Yes for `Choice` | 2–32 choices, values unique. |

Choice object:

| Member | Type | Required | Rules |
|---|---|---|---|
| `value` | string | Yes | Choice value (§2.4). Sent on the wire. |
| `name` | string | Yes | Declaration text, 1–80 units. Shown to people. |

There is no free-text setting, secret field or custom inspector in schema 3.

### 3.4 Id rules

- The extension id is a third-party id: at least one dot in its root, so it can
  never equal or sit under a host's own first-party ids.
- Every contribution id is the extension id, a `/`, and one or more segments
  joined by `/`.
- **Lone leaf.** A manifest with exactly one contribution MAY use the extension
  id itself as that contribution's id. A manifest with two or more
  contributions MUST NOT (`id.outside-namespace`).
- Ids are compared ordinally. They are claims: no host verifies who may use a
  publisher segment in this version.

### 3.5 Reserved publishers

A third-party extension id MUST NOT start with one of these publisher segments
(`id.root-reserved`):

`orbit`, `pinwheel`, `lollipop`, `ventana`, `ventanatools`, `ext`

The first five are current or candidate Ventana names. `ext` is the word a host
ring file uses for its own widget items. Matching is exact on the first segment:
`ventanatools-fan.widgets` is allowed and `ventanatools.widgets` is not. The list
is a Ventana convention (§2.7): Lollipop's separate SDK is expected to reserve
the same names. It is published as the conformance fixture
`fixtures/reserved-publishers.json` (Appendix A). A product's former id stays on
the list after a rename.

The first segment of an extension id also MUST NOT be a Windows device name:
`con`, `prn`, `aux`, `nul`, `com1`–`com9` or `lpt1`–`lpt9` (`id.root-reserved`).
The id begins file names (`<id>-<version>.orbitextension`, `<id>.pairing.json`),
and on Windows 10 a file name whose part before the first dot is a device name
opens the device. This check is part of the id grammar, not of the
reserved-publisher list, so a caller that passes its own list cannot turn it
off.

### 3.6 Declaration text rule

The same character rule applies to every human-readable string an extension
declares (`name`, `description`, `publisher.name`, setting and choice names
and descriptions, and every localized string), and the same character class is
removed from face text (§7.7.3).

A declaration string MUST NOT:

- be empty, or contain only white space or only format characters (`text.empty`,
  or for a `name` the matching `*.label-required` code, §4.2);
- start or end with a White_Space character (`text.whitespace`);
- contain any **disallowed character** (`text.invalid-character`).

Disallowed characters:

| Range | What it is |
|---|---|
| U+0000–U+001F, U+007F–U+009F | C0 and C1 controls, including tab and line feed |
| U+2028, U+2029 | Line and paragraph separators |
| U+202A–U+202E | Bidirectional embeddings and overrides |
| U+2066–U+2069 | Bidirectional isolates |
| U+FEFF | Zero-width no-break space (byte order mark) |
| U+FFFD | Replacement character |
| U+FDD0–U+FDEF, and every code point whose low 16 bits are FFFE or FFFF (including astral planes) | Noncharacters |
| unpaired surrogates | Invalid UTF-16 |
| every other code point with General_Category Cf, **except** the five below | Invisible format characters, including U+200B, U+2060, U+00AD and tag characters U+E0000–U+E007F |

Allowed format characters, because scripts and mixed-direction names need them:

| Code point | Name |
|---|---|
| U+200C | Zero-width non-joiner (ZWNJ) |
| U+200D | Zero-width joiner (ZWJ), including emoji sequences |
| U+200E | Left-to-right mark (LRM) |
| U+200F | Right-to-left mark (RLM) |
| U+061C | Arabic letter mark (ALM) |

Lengths are counted in UTF-16 code units after JSON unescaping. The JSON
Schemas cannot count them that way (§11.5), so readers alone enforce the limits
for text outside the Basic Multilingual Plane. The warning
`text.long` is raised when a `name` is longer than 32 text elements, because
hosts truncate longer names in compact places.

The rule is published as the conformance fixture `fixtures/text-rules.json`,
with the limits of §3.2, §3.3 and §3.3.1 as `declarationLimits`: 80 units for a
`name`, 512 for a `description`, 80 for a label (`publisher.name` and the name of
every setting and choice), and 32 text elements before `text.long`. A strings
file's translation has the limit of the member it translates (§3.10). The limits
are a Ventana convention (§2.7), so another product's SDK can run the same file.

### 3.7 Glyph rule

A glyph is a string of exactly one UTF-16 code unit in U+E000–U+F8FF, the
private-use range of the Windows symbol fonts. Hosts render it with Segoe Fluent
Icons on Windows 11 and Segoe MDL2 Assets on Windows 10. In JSON, write it as an
escape: `"\uE916"`. A missing or empty glyph raises `chrome.glyph-required`; any
other value raises `chrome.glyph-invalid`.

### 3.8 URL rule

A URL member MUST be an absolute URI with scheme `https`, an authority without
user information, only printable ASCII (internationalized host names in
Punycode), no white space, and at most 512 characters (`url.invalid`). Hosts MUST
NOT fetch a manifest URL automatically. They MAY open it only when the person
explicitly asks, in the default browser.

### 3.9 Worked example

```json
{
  "$schema": "https://dev.ventana.tools/schemas/extensions/orbit/manifest.v3.json",
  "schemaVersion": 3,
  "id": "example.countdown",
  "name": "Countdown sample",
  "description": "One local countdown shared by Start, Pause and Reset actions and a passive status widget.",
  "version": "0.3.0",
  "hosts": ["orbit"],
  "publisher": { "name": "Example Co", "url": "https://example.com" },
  "supportUrl": "https://example.com/support",
  "disclosures": { "network": "None" },
  "contributions": [
    {
      "id": "example.countdown/timer",
      "name": "Countdown",
      "description": "Start or resume, pause, or reset the shared countdown. Its face shows the time left.",
      "glyph": "\uE916",
      "provides": ["invoke", "face"],
      "settings": [
        {
          "id": "mode",
          "kind": "Choice",
          "name": "When picked",
          "default": "start",
          "choices": [
            { "value": "start", "name": "Start or resume" },
            { "value": "pause", "name": "Pause" },
            { "value": "reset", "name": "Reset" }
          ]
        },
        {
          "id": "duration",
          "kind": "Choice",
          "name": "Duration",
          "description": "Used when the countdown starts fresh or resets.",
          "default": "five-minutes",
          "choices": [
            { "value": "one-minute", "name": "1 minute" },
            { "value": "five-minutes", "name": "5 minutes" },
            { "value": "twenty-five-minutes", "name": "25 minutes" }
          ]
        }
      ]
    },
    {
      "id": "example.countdown/status",
      "name": "Countdown status",
      "description": "Shows the shared countdown. Picking it changes nothing.",
      "glyph": "\uE916",
      "provides": ["face"]
    }
  ]
}
```

Its manifest hash (§B.2) is
`1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f`.

The fixture copy, `fixtures/manifests/valid/countdown.json`, is this manifest
with `hosts` set to `["example-host"]` and no `$schema` (§2.3). Its hash is the
same, because the hash does not cover `hosts` or `$schema`.

### 3.10 Localized strings

Manifest text is the fallback. An extension MAY add one strings file per
language in a `strings/` folder at the archive root, or beside a loose
`extension.json` in a host's developer folder.

- File name: `<language-tag>.json`, for example `strings/de-DE.json`. At most 64
  files; each at most 65,536 bytes; no subfolders. Two files whose tags differ
  only in case are an error (`strings.language-duplicate`).
- Encoding and JSON rules: as §3.1.
- The same declaration text rule (§3.6) and the same length limits as the
  member being translated apply.
- Keys address manifest items by id, not by position, so reordering the
  manifest never attaches a string to the wrong item.

| Member | Type | Required | Rules |
|---|---|---|---|
| `$schema` | string | No | As in the manifest. |
| `schemaVersion` | integer | Yes | `3`. |
| `language` | string | Yes | Equal, case-insensitively, to the file name's tag (`strings.language-mismatch`). |
| `name` | string | No | Replaces the manifest `name`. |
| `description` | string | No | Replaces the manifest `description`. |
| `publisher` | object | No | `{ "name": string }`. |
| `contributions` | object | No | Keys are contribution ids; values are contribution string objects. |

Contribution string object: `{ "name"?, "description"?, "settings"?: { <setting id>: { "name"?, "description"?, "choices"?: { <choice value>: string } } } }`.
A key that names no item in the manifest is an error (`strings.target-unknown`).

```json
{
  "schemaVersion": 3,
  "language": "de-DE",
  "name": "Countdown-Beispiel",
  "contributions": {
    "example.countdown/timer": {
      "name": "Countdown",
      "settings": { "mode": { "name": "Beim Auswählen", "choices": { "start": "Starten oder fortsetzen" } } }
    }
  }
}
```

Hosts MAY apply strings files. A host that does picks the best match for its UI
language (a file whose tag equals it, case-insensitively; then a file whose tag
is the UI language's primary subtag alone, so `de` for `de-AT` and never another
region such as `de-DE`; then the manifest text) and applies it member by
member. Readers MUST validate strings files even when the host does not apply
them, so an author's mistakes surface everywhere.

---

## 4. Diagnostics

### 4.1 Shape

Every reader of a static file (manifest, strings file, package, pack
configuration, pairing file) reports findings as diagnostics. The shape is the
Ventana diagnostics convention (§2.7), which is Lollipop's `ManifestDiagnostic`
(`Code`, `Path`, `Message`, `Severity`, `Line`, `Column`), with one added
member, `file`.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `code` | string | Yes | Stable dotted code (§2.4) from the table in §4.4. Tools and docs key on it. |
| `path` | string | Yes | RFC 6901 JSON Pointer to the offending member or element in `file`, using the JSON member names; `""` is the document root. For archive structure problems, `""`. |
| `message` | string | Yes | Fixed English text for authors. It never contains any value from the file. Never parse it. |
| `severity` | string | Yes | `Error` (the file is refused) or `Warning` (reported, never blocks). |
| `file` | string | No | The archive entry or file the path refers to, for example `extension.json` or `strings/de-DE.json`. Absent when the reader was given a single file. |
| `line`, `column` | integer | No | 1-based line and 1-based UTF-8 byte column, as Lollipop's, of the token the path resolves to (for `json.syntax`, of the offending byte). Present for every diagnostic whose path resolves to a token in a JSON file; absent for archive-structure findings and for manifests built in code (§9.1 `Validate`). |

`line` and `column` are those of the value token the pointer resolves to: for
an unknown, renamed or repeated member, the member's value (for a repeat, the
second occurrence). `json.required-missing`, `id.required` and the other
presence codes for a missing member carry the position of the object that lacks
it, and a truncated unknown-member path carries its parent object's position.
`json.too-large`, `json.encoding` and archive-structure findings carry none;
`json.syntax` for invalid UTF-8 points at the first invalid byte. Positions are
counted after a UTF-8 byte order mark is removed.

Path escaping follows RFC 6901 (`~` as `~0`, `/` as `~1`). When a path would
end in an unknown member name that is longer than 64 characters or contains
anything other than printable ASCII, readers MUST use the parent's path
instead, so a hostile file cannot make a host display arbitrary text. The same
applies to the paths of `json.syntax`, `json.depth` and `json.duplicate-member`
through a member name that contains anything other than printable ASCII; deeper
paths stop at the same parent.

The C# type is `Diagnostic` (§9.1); its `ToString()` is
`"<code> <path>: <message>"`, as Lollipop's. The `--json` form of the tool uses
exactly the field names above. The deliberate differences from Lollipop's
current type (1-based severity values in C#, the `file` member, the `IdOrigin`
name, and messages that never echo file values) are recorded with the Ventana
conventions (§2.7), for Lollipop's separate SDK to weigh when it follows the
convention.

### 4.2 Semantics

- **Collect all.** Readers report every problem they find, up to 200
  diagnostics. The 200th is replaced by `diagnostics.truncated`.
- A syntax error stops parsing and yields one diagnostic. Every other problem
  is reported alongside the rest.
- A file is valid when no diagnostic has severity `Error`.
- Messages are author-facing English and are not localized.
- Hosts MUST NOT write paths or messages to logs. Hosts MAY log codes.
  Hosts MAY show paths and messages in developer surfaces, because there the
  reader is the author looking at their own file.
- **One diagnostic per path, the most specific code.** A reader reports at most
  one diagnostic for a given `file` and `path`. When several codes apply, it
  reports the first that applies in this order:
  1. presence: `id.required`, `setting.id-required`, `choice.value-required`,
     `chrome.glyph-required` and the other `*-required` codes of §4.4, then
     `json.required-missing`, `json.null-not-allowed`, `manifest.null`,
     `null.member` and `null.element`;
  2. JSON type: `json.type-mismatch` (wrong JSON type, or a number that is not
     an integer);
  3. a specific enumeration or grammar code (`setting.kind-unsupported`,
     `contribution.provides-unknown`, `id.grammar`, `setting.id-grammar`,
     `choice.value-grammar`, `chrome.glyph-invalid`, `manifest.version-invalid`,
     `manifest.host-invalid`, `language.invalid`, `url.invalid`), and otherwise
     `enum.undefined` for an unknown enumeration token;
  4. length: `string.too-long`, `id.too-long`, `list.too-long`,
     `list.too-short`;
  5. text: an empty or white-space-only `name` is `chrome.label-required`,
     `setting.label-required` or `choice.label-required`; every other
     declaration string uses `text.empty`, then `text.whitespace`, then
     `text.invalid-character`;
  6. cross-member checks (`setting.default-unknown`, `id.outside-namespace`,
     `id.duplicate`, `requires.capability-duplicate` and the like);
  7. warnings (`text.long`, `schema.uri-mismatch`, `manifest.host-unknown`,
     `requires.capability-unknown`).

  The complete order, including structural and package codes, is
  `fixtures/codes/precedence.json`, and every `*.expected.json` fixture follows
  it. It adds a first tier, structure (the JSON structure codes `json.syntax`,
  `json.depth`, `json.duplicate-member`, `json.unknown-member` and
  `json.member-renamed`, the `package.*` structure codes and the `pairing.*`
  file codes); places `schema.version-unsupported`, the `id.root-*` codes,
  `strings.language-invalid`, `pairing.version-unsupported`,
  `pairing.mode-unsupported`, `pairing.registration-invalid` and
  `pairing.secret-invalid` in the grammar tier (3); and places the remaining
  `strings.*`, `package.*`, `pack.*` and `pairing.*` codes among the
  cross-member checks (6). Because one diagnostic is reported per file and
  path, an archive diagnostic about one entry sets `file` to that entry's name
  (when it is printable ASCII of at most 241 characters), so problems with
  different entries are all reported.

### 4.3 Where each family comes from

| Prefix | Raised by |
|---|---|
| `json.*`, `schema.*`, `list.*`, `string.*`, `text.*`, `enum.*`, `null.*`, `diagnostics.*` | Every JSON reader |
| `id.*`, `chrome.*`, `manifest.*`, `contribution.*`, `setting.*`, `choice.*`, `requires.*`, `url.*`, `language.*` | Manifest reader |
| `strings.*` | Strings file reader |
| `package.*` | Package reader and installer |
| `pack.*` | `orbit-ext pack` |
| `pairing.*` | Pairing reader; these are reason codes (§8), reported in the diagnostic shape |

### 4.4 Code table

"Lollipop" marks codes whose string and meaning match Lollipop's existing
`ManifestDiagnosticCodes`: under the Ventana code-naming convention (§2.7),
Orbit reports the code Lollipop reports for the same fault.

| Code | Severity | Meaning (the message's gist) | Fix | Lollipop |
|---|---|---|---|---|
| `json.too-large` | Error | The file is larger than its limit. | Shorten it. | ✓ |
| `json.syntax` | Error | Not valid JSON: bad token, comment, trailing comma, trailing content or invalid UTF-8. | Fix the JSON at `line`/`column`. | ✓ |
| `json.encoding` | Error | The file starts with a UTF-16 or UTF-32 byte order mark. | Save it as UTF-8. | — |
| `json.depth` | Error | Objects and arrays nest deeper than allowed. | Flatten the document. | ✓ |
| `json.unknown-member` | Error | This member is not part of the schema (a typo, or a newer schema). | Remove or correct it. | ✓ |
| `json.duplicate-member` | Error | The same member appears twice in one object. | Keep one. | ✓ |
| `json.null-not-allowed` | Error | `null` where a value is required. | Give a value or leave the member out. | ✓ |
| `json.required-missing` | Error | A required member is missing. | Add it. | ✓ |
| `json.type-mismatch` | Error | Wrong JSON type, or a number that is not an integer. | Use the documented type. | ✓ |
| `json.member-renamed` | Error | This member was renamed in schema 3 (`capabilities` is now `provides`; `manifestVersion` is now `schemaVersion`). | Use the new name. | — |
| `enum.undefined` | Error | A string is not one of the documented values of a closed enumeration, and no more specific code applies. | Use a documented value. | ✓ |
| `manifest.null` | Error | The manifest object itself is null (a manifest built in code). | Pass a manifest. | ✓ |
| `null.member` | Error | A list or object member is null on a manifest built in code. | Give a value or an empty list. | ✓ |
| `null.element` | Error | A list entry is null on a manifest built in code. | Remove the entry. | ✓ |
| `json.internal-error` | Error | The reader failed in a way the contract does not describe. | Report it; the file is still refused. | ✓ |
| `schema.version-unsupported` | Error | `schemaVersion` (or `archiveVersion`, `packVersion`) is not one this reader supports. | Use the version in this document, or update the host. | ✓ |
| `schema.uri-mismatch` | Warning | `$schema` is an absolute `http` or `https` URL that is not the published manifest URL of any host in `hosts`. | Use a URL from §2.2, or a relative path to a local copy. | — |
| `list.too-long` | Error | An array has more entries than allowed. | Remove entries. | ✓ |
| `list.too-short` | Error | An array has fewer entries than required (for example an empty `provides`, or one choice). | Add entries. | — |
| `string.too-long` | Error | A string is longer than allowed. | Shorten it. | ✓ |
| `text.empty` | Error | Text other than a `name` is empty, or only white space or format characters. | Write the text. | — |
| `text.whitespace` | Error | Text starts or ends with white space. | Trim it. | — |
| `text.invalid-character` | Error | Text contains a disallowed character (§3.6). | Remove control, bidirectional-override and invisible characters. | — |
| `text.long` | Warning | A name is longer than 32 text elements and will be truncated. | Shorten it. | — |
| `diagnostics.truncated` | Error | Reporting stopped after 199 diagnostics. | Fix the reported problems and validate again. | ✓ |
| `id.required` | Error | The id is missing or blank. | Add it. | ✓ |
| `id.grammar` | Error | The id contains characters, segments or slashes the grammar does not allow. | Use lowercase letters, digits and single hyphens. | ✓ |
| `id.too-long` | Error | The id is longer than 128 characters. | Shorten it. | ✓ |
| `id.root-not-dotted` | Error | A third-party extension id must contain a dot (`publisher.name`). | Add your publisher segment. | ✓ |
| `id.root-dotted` | Error | A first-party id must not contain a dot. | Hosts only. | ✓ |
| `id.root-reserved` | Error | The id starts with a reserved publisher, or its first segment is a Windows device name (§3.5). | Use your own publisher segment. | ✓ |
| `id.outside-namespace` | Error | A contribution id does not start with the extension id and `/` (or is a lone leaf in a manifest with several contributions). | Prefix it with the extension id. | ✓ |
| `id.duplicate` | Error | Two contributions share an id. | Make ids unique. | ✓ |
| `id.taken` | Error | Another installed extension already uses this id. Raised by hosts at import or install. | Remove the other extension, or use Reload or Replace in developer mode. | ✓ |
| `chrome.label-required` | Error | `name` is empty, or only white space or format characters. | Write a name. | ✓ |
| `chrome.glyph-required` | Error | `glyph` is missing or empty. | Give one private-use glyph. | ✓ |
| `chrome.glyph-invalid` | Error | `glyph` is not exactly one character in U+E000–U+F8FF. | Pick a Segoe Fluent Icons glyph and write it as `\uXXXX`. | — |
| `manifest.version-invalid` | Error | `version` is not `MAJOR.MINOR.PATCH` with numbers. | Use a version like `1.2.3`. | — |
| `manifest.host-invalid` | Error | A host id does not match the host-id grammar. | Use an id from the host-id registry (§2.3). | — |
| `manifest.host-duplicate` | Error | A host id is listed twice. | Keep one. | — |
| `manifest.host-not-listed` | Error | This host's id is not in `hosts`. Raised by hosts. | Add the host id. | — |
| `manifest.host-unknown` | Warning | A host id is not an active registry entry. | Check the spelling. | — |
| `contribution.provides-unknown` | Error | `provides` contains a value other than `invoke` or `face`. | Use `invoke`, `face` or both. | — |
| `contribution.provides-duplicate` | Error | `provides` lists a value twice. | Keep one. | — |
| `setting.id-required` | Error | A setting id is missing or blank. | Add it. | ✓ |
| `setting.id-grammar` | Error | A setting id is not a lowercase letter followed by letters or digits. | Use an id like `mode` or `refreshRate`. | ✓ |
| `setting.id-duplicate` | Error | Two settings of one contribution share an id. | Make ids unique. | ✓ |
| `setting.label-required` | Error | A setting's `name` is empty, or only white space or format characters. | Write a name. | ✓ |
| `setting.kind-unsupported` | Error | `kind` is not `Choice`. | Use `Choice`. | — |
| `setting.choices-required` | Error | A `Choice` setting has no `choices`. | Add 2–32 choices. | ✓ |
| `setting.default-required` | Error | A `Choice` setting has no `default`. | Add one of its values. | — |
| `setting.default-unknown` | Error | `default` is not one of the choice values. | Use a declared value. | — |
| `choice.value-required` | Error | A choice value is missing or empty. | Add it. | ✓ |
| `choice.value-grammar` | Error | A choice value is not a lowercase segment. | Use a value like `five-minutes`. | — |
| `choice.value-duplicate` | Error | Two choices share a value. | Make values unique. | ✓ |
| `choice.label-required` | Error | A choice's `name` is empty, or only white space or format characters. | Write a name. | ✓ |
| `requires.capability-duplicate` | Error | A required capability id is listed twice. | Keep one. | — |
| `requires.capability-unknown` | Warning | A required capability id is not in the registry this tool knows. | Check the id, or update the tool. | — |
| `requires.capability-unsupported` | Error | This host does not implement a required capability. Raised by hosts. | Update the host, or remove the requirement. | — |
| `url.invalid` | Error | A URL is not an absolute `https` URL within the rules of §3.8. | Use a full `https://` address. | — |
| `language.invalid` | Error | A language tag does not match the tag grammar. | Use a tag like `en-US`. | — |
| `strings.language-invalid` | Error | A strings file name is not a language tag. | Rename it, for example `de-DE.json`. | — |
| `strings.language-mismatch` | Error | `language` differs from the file name's tag. | Make them equal. | — |
| `strings.language-duplicate` | Error | Two strings files have the same tag. | Keep one. | — |
| `strings.target-unknown` | Error | A key names a contribution, setting or choice the manifest does not have. | Correct the key. | — |
| `strings.too-many-files` | Error | More than 64 strings files. | Remove some. | — |
| `package.too-large` | Error | The archive is larger than 32 MiB. | Reduce the payload. | — |
| `package.expanded-too-large` | Error | Expanded contents exceed 64 MiB. | Reduce the payload. | — |
| `package.file-too-large` | Error | One file exceeds its limit (§5.3). | Reduce it. | — |
| `package.entries` | Error | More than 512 entries. | Bundle or trim the payload. | — |
| `package.archive` | Error | The ZIP structure is malformed or inconsistent: the central directory, a local header that disagrees with it, overlapping or non-contiguous entries, or bytes before the first entry (§5.5). | Rebuild it with `orbit-ext pack`. | — |
| `package.zip64` | Error | The archive uses ZIP64. | Rebuild it with `orbit-ext pack`. | — |
| `package.encrypted` | Error | An entry is encrypted. | Rebuild without encryption. | — |
| `package.crc` | Error | An entry's CRC-32 or size does not match its data. | Rebuild the archive. | — |
| `package.path` | Error | An entry name breaks the path grammar (§5.4). | Rename the file. | — |
| `package.path-reserved` | Error | A root entry is not one of the allowed names. | Move it under `payload/`. | — |
| `package.path-conflict` | Error | Two entries differ only in case, or a file and a folder share a name. | Rename one. | — |
| `package.link` | Error | An entry is marked as a symbolic link or device. | Include the real file. | — |
| `package.file-missing` | Error | `extension.json`, `extension.package.json` or `README.md` is missing. | Add it. | — |
| `package.descriptor` | Error | `extension.package.json` is invalid; its own diagnostics follow with `file` set. | Rebuild with `orbit-ext pack`. | — |
| `package.inventory-missing` | Error | The descriptor lists a file the archive does not contain. | Rebuild the archive. | — |
| `package.inventory-extra` | Error | The archive contains a file the descriptor does not list. | Rebuild the archive. | — |
| `package.hash-mismatch` | Error | A file's SHA-256 or size differs from the descriptor. | Rebuild the archive. | — |
| `package.manifest-hash` | Error | The descriptor's `manifestHash` differs from the manifest's. | Rebuild the archive. | — |
| `package.readme` | Error | `README.md` is empty, not UTF-8, contains NUL, or is too large. | Fix the readme. | — |
| `package.extension` | Error | The package file name does not end with this host's package file extension. | Use the extension in §2.3. | — |
| `package.version-not-newer` | Error | An update's version is not higher than the installed one. Raised by hosts. | Increase `version`. | — |
| `pack.config` | Error | `extension.pack.json` is invalid; its own diagnostics follow with `file` set. | Fix the pack configuration. | — |
| `pack.source-missing` | Error | A `from` path does not exist. | Correct the path, or add a `build` step (§11.3). | — |
| `pack.build-failed` | Error | The `build` step's `dotnet publish` failed. | Fix the build errors it printed. | — |
| `pack.link` | Error | A staged file or folder is a symbolic link or junction. | Stage the real file. | — |
| `pack.secret` | Error | A staged file looks like a pairing file. Pairing files are never packed. | Remove it from the payload. | — |
| `pack.output-exists` | Error | The output file exists. | Remove it, or pass `--force`. | — |

In a file, an escape that forms an unpaired surrogate is valid JSON: the value's
own rule reports it (`text.invalid-character` for declaration text, §3.6; the
grammar code for ids, versions, glyphs, URLs and other values with a grammar;
`json.unknown-member` at the parent's path for a member name), and every other
problem in the file is still reported. Members with no character rule (`$schema`,
and the descriptor's informational `createdBy`) accept it. Wire frames refuse it
(§7.2).

Pairing-file codes (`pairing.*`) are reason codes, listed once, in §8.3. When the
tool or SDK reads a pairing file it reports them in this same diagnostic shape.

The table is also published as `fixtures/codes/diagnostics.json`. If this
document and that file disagree, the file is correct and this document is
fixed. In that file, and in the C# catalog it is generated from, a fix that
names the tool writes it as the placeholder `{tool}` (for example
"Rebuild it with `{tool} pack`."), which the tool replaces with its own command
name, so no fixture or source string names the product (§2.8, Appendix A).

---

## 5. Package archive version 2

### 5.1 Purpose

A package delivers an extension's manifest, readme, optional localized strings
and inert payload files (companion binaries or sources) to a host. Installing a
package never runs anything.

### 5.2 Layout

```text
<id>-<version><package-file-extension>     for example example.countdown-0.3.0.orbitextension
├── extension.json              required: the manifest (§3)
├── extension.package.json      required: the descriptor (§5.6), generated by tools
├── README.md                   required: instructions people read before installing
├── strings/                    optional: <language-tag>.json files only (§3.10)
└── payload/                    optional: any inert files, in subfolders
```

No other entries are allowed at the root (`package.path-reserved`).

### 5.3 Limits

| Limit | Value |
|---|---|
| Archive size | 32 MiB |
| Expanded size, all files | 64 MiB |
| One payload file | 16 MiB |
| `extension.json` | 64 KiB |
| `extension.package.json` | 256 KiB |
| `README.md` | 64 KiB |
| One strings file | 64 KiB; at most 64 files |
| Entries (files and folders) | 512 |
| Entry name | 240 characters; each segment 1–80 characters |

512 entries allows a framework-dependent .NET publish; authors SHOULD prefer a
single-file or Native AOT companion. The limits are consistent with each other:
an inventory row for a 240-character path is about 360 bytes, so a descriptor
for 512 such entries fits in 256 KiB. The package fixtures include a generated
archive at every maximum at once (Appendix A).

### 5.4 Entry-name grammar

- Names use `/` as the separator, are relative, and contain no empty segment.
- Each segment matches `[A-Za-z0-9._-]+`, is not `.` or `..`, does not end with
  `.`, and is not a Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`,
  `LPT1`–`LPT9`, with or without an extension, any case).
- Entry names are unique case-insensitively, and no name is both a file and a
  folder (`package.path-conflict`).
- Folder entries are optional; a folder entry has zero length and CRC 0.
- Names are stored in UTF-8 (flag bit 11) or ASCII.

### 5.5 Archive format rules

- ZIP as described in PKWARE APPNOTE 6.3, restricted to: compression methods 0
  (stored) and 8 (deflate); a single disk; no ZIP64 structures or `0xFFFFFFFF`
  sizes or extra field `0x0001` (`package.zip64`); no encryption, so general
  purpose bits 0, 6 and 13 are clear (`package.encrypted`).
- The end-of-central-directory record is located and the whole central
  directory is validated before any entry is decompressed. Its entry count,
  offsets and sizes MUST agree with the file, and the record's comment MUST end
  exactly at the end of the file (`package.archive`).
- **Local headers agree with the central directory.** Each entry's local file
  header MUST carry the same name, compression method, general purpose flags
  and CRC-32 as its central record, and the same compressed and uncompressed
  sizes unless flag bit 3 (data descriptor) is set, in which case the local
  CRC-32 and sizes may be zero and the data descriptor's values, with or without
  its signature, MUST equal the central record's. A local header MUST NOT carry
  a ZIP64 extra field (`0x0001`) (`package.zip64`).
- An unsupported compression method and a nonzero disk number are
  `package.archive`; deflate data that cannot be decompressed is `package.crc`.
- **No hidden bytes.** The local records (header, data and any data descriptor)
  MUST appear in ascending offset order, MUST NOT overlap, and MUST be
  contiguous from offset 0 up to the start of the central directory. Bytes
  before the first local header, between records, or between the last record
  and the central directory are refused (`package.archive`). What a person
  inspects with any ZIP tool is then exactly what a host installs.
- External attributes MUST NOT mark a symbolic link or device. Unix file types
  other than regular file and directory are refused, as are DOS attributes other
  than read-only, hidden, system, directory, archive and normal (`package.link`).
  Accepted attributes are never applied to extracted files (§5.8).
- Every entry is decompressed with the per-file and total limits enforced while
  reading, and its CRC-32 and size MUST match (`package.crc`).

### 5.6 Descriptor: `extension.package.json`

The descriptor is generated by `orbit-ext pack` (or another conforming tool).
Authors never write it by hand. It was renamed from `package.json`, which
collides with npm's manifest in every Node or UXP companion project. The new
name:

- collides with no well-known file (npm, NuGet, UXP, VS Code or browser
  extensions);
- sorts beside `extension.json` and reads as its companion;
- contains no product name;
- carries an inventory with a SHA-256 for every file, so a host can check
  installed files later and a future signature needs to cover only this one
  small file.

| Member | Type | Required | Rules |
|---|---|---|---|
| `$schema` | string | No | As in the manifest. |
| `archiveVersion` | integer | Yes | `2`. |
| `manifestHash` | string | Yes | SHA-256 hash form; MUST equal the manifest hash of `extension.json` (§B.2). |
| `files` | array of object | Yes | One entry per file in the archive except the descriptor itself, sorted by `path` ordinally. |
| `createdBy` | object | No | `{ "name": string ≤ 64, "version": string ≤ 32 }`, informational. |

File entry: `{ "path": string, "size": integer, "sha256": string }`, where
`path` is the entry name and `size` its uncompressed length.

```json
{
  "archiveVersion": 2,
  "manifestHash": "1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f",
  "createdBy": { "name": "orbit-ext", "version": "0.1.0-preview.1" },
  "files": [
    { "path": "README.md", "size": 1834, "sha256": "…" },
    { "path": "extension.json", "size": 1650, "sha256": "…" },
    { "path": "payload/companion/Countdown.exe", "size": 74240, "sha256": "…" }
  ]
}
```

### 5.7 Verification

A reader verifies, in this order, and collects every diagnostic it can:

1. archive size; central directory, local headers and their layout (§5.5);
2. entry names, attributes and counts (§5.3, §5.4);
3. decompression with limits and CRC-32;
4. presence of the three required files;
5. the descriptor (JSON rules of §3.1, members of §5.6);
6. the inventory: every listed file present with equal size and SHA-256, and no
   unlisted file;
7. the manifest (§3) with `file` set to `extension.json`, and the descriptor's
   `manifestHash`;
8. strings files (§3.10);
9. `README.md`: UTF-8 (a byte order mark is stripped), not blank, no U+0000.

Which code a finding gets:

- A folder entry with data or a nonzero CRC-32 is `package.path`; an entry
  under `strings/` that is not `strings/<name>.json` is `package.path-reserved`.
- An over-long `README.md` is `package.readme`; any other over-long file is
  `package.file-too-large`.
- In the descriptor, a malformed `manifestHash` is `package.manifest-hash`, a
  malformed `sha256` `package.hash-mismatch`, a `path` outside the entry grammar
  `package.path`, and an unsorted, repeated or self-listing path
  `package.descriptor`, each with `file` `extension.package.json`; the summary
  `package.descriptor` has path `""` and no `file`.
- An entry refused for its name or attributes is not reported again as unlisted
  (`package.inventory-extra`), and a listed entry whose data failed is not
  reported again as missing (`package.inventory-missing`).
- Two strings files whose tags differ only in case cannot coexist in an archive
  (`package.path-conflict`), so `strings.language-duplicate` arises only for a
  loose folder.

The **package hash** is the SHA-256 of the whole archive file, in hash form.
Hosts SHOULD show it in install review and MUST record it with the
registration.

### 5.8 Host installation requirements

A host that installs a package:

1. MUST NOT execute, load, open with a handler, or register anything from the
   package: no process start, file association, COM class, service, scheduled
   task, startup entry or shell extension.
2. MUST verify the whole package (§5.7) before writing any file.
3. MUST write files only under a directory the host names. Only the relative
   entry names under `payload/` and `strings/` come from the archive. Even
   though the entry grammar (§5.4) already excludes traversal, the host MUST,
   for every file: combine the entry name with its install directory, take the
   full path (`Path.GetFullPath` or equivalent), check that the result is inside
   the install directory by an ordinal comparison of normalized paths, and
   create the file with create-new semantics (`FileMode.CreateNew`), so an
   existing file or a name that resolves elsewhere is refused.
4. MUST refuse reparse points in every directory it creates or writes, from its
   own data root downward. It MUST NOT check, or refuse because of, directories
   above its data root.
5. **MUST propagate the zone of origin (Mark-of-the-Web).** When the package
   file has an NTFS alternate data stream named `Zone.Identifier`:
   - Read it, at most 4,096 bytes, and parse `ZoneId`. Use zone 3 (Internet)
     when the stream is larger than 4,096 bytes, cannot be read or parsed, has
     no `ZoneId`, or has a `ZoneId` outside 0–4.
   - If the zone is 1 or higher, write a `Zone.Identifier` stream to **every
     extracted file** containing `[ZoneTransfer]`, `ZoneId=<zone>`, and
     `ReferrerUrl=` the package file's `file:` URI. Copy `HostUrl` too only when
     it is present, starts with `http://` or `https://`, is at most 2,048
     characters, and consists only of printable ASCII (U+0021–U+007E), so it can
     carry no line break and inject no extra line.
   - Record the zone with the registration, and say "Downloaded from the
     internet" (or equivalent) in install review when the zone is 3 or 4.

   Folders do not receive the stream: Windows evaluates zones on files.
6. MUST NOT apply attributes from the archive. Extracted files get the default
   attributes of a newly created file; hidden, system and read-only flags in
   the archive are ignored, so nothing is hidden from the person who opens the
   package folder.
7. MUST leave a newly installed extension turned off until the person consents.
8. MUST commit atomically with respect to consent: if anything fails, the
   catalog, the credential and the previous installation are unchanged.
9. MUST treat every manifest and readme string as unverified extension text,
   and MUST show `README.md` as plain text. It MUST NOT render Markdown in a
   way that loads remote content.
10. For an update (same id, higher `version`), MUST compare access as described
    in §5.9 and MUST ask for consent again when access grows.

### 5.9 Access comparison for updates and reloads

Access **grows** when the new manifest, compared with the consented one:

- adds a contribution id;
- adds `invoke` or `face` to an existing contribution's `provides`;
- adds a setting id to an existing contribution;
- adds an entry to `requires.capabilities`; or
- widens the declared network use, in the order `None` < `LocalNetwork` <
  `Internet`, where a manifest without `disclosures` counts as `Internet`.

When access does not grow, a host SHOULD keep consent and the credential. When
access grows, a host MUST show what was added and MUST NOT reconnect the
registration until the person consents again. The credential MAY be kept,
because it authenticates the companion program, not the access.

Every other change (names, descriptions, glyphs, choices, defaults, version,
and every removal) does not grow access. Some of those changes still matter to
the person's trust, so whether or not consent is kept, the update or reload
review MUST show these as highlighted **Changed** rows next to their old values:
`publisher.name`, `publisher.url`, `disclosures.privacyUrl`, `supportUrl`, and
for a package the new package hash. `publisher.name` is compared as the host
displays it, after strings files (§3.10): each manifest with its own strings
file for the UI language applied, so an update that renames the publisher only
in a strings file still gets the row, which shows the names as displayed. When
the displayed names are equal, the manifests' own names are compared. A review
that keeps consent MUST NOT say that nothing changed when any of these rows is
present; "No new access" is correct, "Nothing changed" is not.

---

## 6. Pairing file version 3

### 6.1 Purpose

A pairing file gives one companion the credential for one registration. A host
writes it only when the person explicitly asks (for example "Save connection
info" or "Copy connection info"). Its default location is per user (§6.6).

### 6.2 Encoding

- UTF-8, with or without a byte order mark; readers MUST strip a leading UTF-8
  byte order mark.
- A UTF-16 or UTF-32 byte order mark, or invalid UTF-8, is refused with
  `pairing.encoding`.
- At most 4,096 bytes (`pairing.too-large`).
- One JSON object, with the JSON rules of §3.1. Line endings MAY be LF or CRLF.
- Members are exactly those below. Unknown members are refused.

Hosts MUST write pairing files as UTF-8 without a byte order mark, with LF line
endings and two-space indentation. A host that saves the file SHOULD give it an
access-control list that grants access only to the current user, and MUST give
the file, and every folder it creates for it, a mandatory label at Medium
integrity with no-read-up, no-write-up and no-execute-up (SDDL
`S:(ML;;NRNWNX;;;ME)`, with `OICI` on folders), set when the file or folder is
created. Windows treats a file without a label as Medium with no-write-up only,
so a Low-integrity process of the same user can read it whatever its
access-control list says, learn the secret, and pose as the host to a client
that checks neither the server process nor the pipe's label (§7.1), such as the
Node SDK.

### 6.3 Members

| Member | Type | Required | Rules |
|---|---|---|---|
| `$schema` | string | No | ≤ 512 units; ignored. |
| `pairingVersion` | integer | Yes | `3` (`pairing.version-unsupported`). |
| `mode` | string | Yes | `Persistent`. `PerLaunch` is reserved (§6.5): readers that do not implement it MUST refuse it (`pairing.mode-unsupported`). |
| `hostId` | string | Yes | Host id (§2.4). MUST be listed in the companion's manifest `hosts` (`pairing.host-not-listed`). |
| `pipeName` | string | Yes | §2.5, with this file's host id and registration id (`pairing.pipe-name-invalid`). |
| `registrationId` | string | Yes | GUID wire form (`pairing.registration-invalid`). |
| `extensionId` | string | Yes | MUST equal the companion manifest's `id` (`pairing.extension-mismatch`). |
| `secret` | string | Yes | Canonical Base64 of 32 random bytes (`pairing.secret-invalid`). |

The example uses the test host id (§2.2), as the pairing fixtures do:

```json
{
  "pairingVersion": 3,
  "mode": "Persistent",
  "hostId": "example-host",
  "pipeName": "Ventana.Extensions.v3.example-host.store.a8c06b3027d3fc4a.00112233445566778899aabbccddeeff",
  "registrationId": "00112233445566778899aabbccddeeff",
  "extensionId": "example.countdown",
  "secret": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
}
```

### 6.4 Handling

- SDKs MUST NOT log, print or expose the secret, and SHOULD zero every buffer
  that held it once parsed.
- The secret never crosses the pipe (§7.3).
- A `Persistent` secret stays valid until the person revokes access or removes
  the extension. Turning an extension off does not invalidate it.
- Tools MUST refuse to pack a pairing file (`pack.secret`).

### 6.5 Reserved: per-launch pairing and package identity

Two features are reserved so host-managed activation can arrive inside
generation 3, without a new transport generation:

- **`PerLaunch` mode.** A host that starts a packaged companion itself passes a
  one-time pairing of the same shape, with `mode: "PerLaunch"` and three more
  members reserved for it: `credentialId` (a GUID naming this one-time
  credential), `expires` (RFC 3339 UTC time, at most 30 seconds after creation)
  and `packageFamilyName`. It is never written to disk; the delivery mechanism
  is decided with the activation design. Its use is announced with the
  capability `pairing.per-launch` (§7.4).
  - The companion names the credential it holds in `hello`, in the reserved
    member `credential` (§7.3.1): absent means the registration's persistent
    secret; `{ "$type": "perLaunch", "id": <credentialId> }` means that
    one-time secret. The host computes the challenge proof with the secret the
    member names. A registration can therefore hold a persistent pairing and a
    per-launch secret at the same time.
  - A per-launch secret is single-use. The host consumes it on the first
    `ready` it sends with it, or on the first failed proof that used it,
    whichever comes first. A consumed or unknown `credentialId` is
    `auth.proof-invalid`.
  - Expiry is enforced from the host's own record of when it minted the
    secret, never from the `expires` value in the delivered pairing, which is
    informational for the companion.
- **Package identity.** With the capability `identity.package`, a host checks
  that the connecting process's package family name equals the registration's
  before it sends `ready`, and refuses with `auth.package-mismatch` otherwise.

Implementations of this version MUST NOT send either feature. A host of this
version ignores a `credential` member in `hello` under §7.11 rule 2 and uses
the persistent secret, so the proofs simply fail if a companion sends one.

### 6.6 Default location

Pairing files live, by default, in a per-user folder outside every install,
package and synced folder:

```text
%USERPROFILE%\.ventana\pairings\<host-id>\<extension-id>.pairing.json
```

- A host's primary **Save connection info** action writes there directly,
  creating the folders with an access-control list that grants access only to
  the current user and the mandatory label of §6.2, and shows the path. A
  secondary **Save as…** action may write anywhere the person chooses; the file
  it writes carries the same access-control list and label.
- The folder is under the profile root rather than under `%LOCALAPPDATA%`
  because a packaged (MSIX) host's new files under `AppData` are redirected to
  a private per-package location that other programs cannot see. It is not
  under `Documents` or the desktop, which are often synced.
- SDKs search this location first (§9.2, §10), once for each active host id in
  the manifest's `hosts`, in order. Pairing files next to the companion's
  program or in its current directory remain fallbacks for portable setups.
- Templates and documentation teach this location. They never suggest saving a
  pairing inside a project folder.

---

## 7. Wire protocol 3

### 7.1 Transport

- A Windows named pipe in byte mode, created by the host with access for the
  current user only, a mandatory label at Medium that denies lower-integrity
  processes read, write and execute (`S:(ML;;NRNWNX;;;ME)`), rejecting remote
  clients, and as the first and only instance of its name. The host keeps the
  same server instance across reconnects, so the name never lapses while the
  registration listens.
- That single instance is a resource a read-only opener can hold. Under the
  default label, which denies a lower-integrity process only writes, a
  sandboxed process of the same user could open it for reading and hold it
  through each handshake timeout while the companion reports `host.pipe-busy`
  and backs off. The label refuses such a process every kind of open. A host
  sets it in the call that creates the pipe; .NET's `PipeSecurity` cannot carry
  a label, so a .NET host creates the pipe with `CreateNamedPipeW` and a
  security descriptor.
- One registration has one pipe, and the pipe accepts one connection at a time.
- **Clients verify the server before writing.** Before writing the first byte, a
  client MUST verify that the pipe is owned by the current user (in .NET,
  `PipeOptions.CurrentUserOnly`). It SHOULD also check the server process and
  the pipe's label, and a client that checks the process MUST also check the
  label:
  - *Process:* get its id (`GetNamedPipeServerProcessId`), open it with
    `PROCESS_QUERY_LIMITED_INFORMATION`, open its token with `TOKEN_QUERY`, and
    confirm that the token's user is the current user and its integrity level
    is no lower than the client's.
  - *Label:* read the pipe's mandatory label through the client's own handle
    (`GetSecurityInfo` with `LABEL_SECURITY_INFORMATION`). The lowest label
    that applies to the pipe itself counts, and a pipe without one counts as
    Medium. Windows labels an object with its creator's integrity level when
    that level is below Medium, and the creator can neither raise nor remove
    that label. The label therefore still shows a lower-integrity creator when
    that process has denied everyone access to itself, which any process can
    do without a privilege, so that the process check cannot run.

  The client then decides:
  - A server that passes the owner and process checks, and whose label is no
    lower than Medium or the client's integrity level, whichever is lower (a
    pipe created above Medium carries no label), is a **verified server**
    before any message is exchanged (§1.4).
  - A server whose process or token cannot be opened or read, and whose label
    is no lower than the client's integrity level, is unverified until its
    challenge proof verifies.
  - A client MUST NOT write to any other server: one that fails the owner
    check, one whose token it read and found to belong to another user or to
    run at a lower integrity level than the client, or one whose label is lower
    than these rules allow or cannot be read. It closes the pipe and reports
    `auth.server-unverified`. Such a server's challenge proof proves nothing,
    because a lower-integrity process of the same user may have read the
    pairing file (§6.2). A client that runs at a higher integrity level than
    its host, for example a companion started as administrator while the host
    is not, is refused the same way.
- Clients SHOULD request identification-level impersonation
  (`SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`), so a server can learn
  who the client is but cannot act as the client.
- A client whose runtime can neither verify the pipe's owner nor request
  identification-level impersonation MUST say so in its documentation and MUST
  treat every server as unverified until its challenge proof verifies (§9.2).
  The Node SDK is such a client (§10).
- The .NET SDK does all of the above. With it, the pipe protects against other
  users, web content and lower-integrity or sandboxed processes, even where a
  host has not yet given its pairing files the label of §6.2: such a process
  may then read the secret, but any pipe it creates carries its lower label.
  A client that checks neither the server process nor the pipe's label, such
  as the Node SDK, is protected against a lower-integrity process only while
  the pairing file carries the label of §6.2. Nothing protects against a
  program running as the same user at the same integrity level.

### 7.2 Framing

- Each message is one frame: a 4-byte unsigned little-endian length, then that
  many bytes of UTF-8 JSON. Length 1 to `limits.maxFrameBytes` inclusive, at
  most 65,536 (`frame.too-large`). Until `ready` has been sent or received,
  every frame in either direction MUST be at most 8,192 bytes
  (`frame.too-large`); every handshake message fits in that with room to spare.
  No byte order mark, no trailing newline.
- The body is exactly one JSON object; invalid UTF-8 is `frame.utf8-invalid`;
  bad JSON, non-object roots, trailing content and nesting deeper than 8 are
  `frame.json-invalid`; duplicate member names at any depth are
  `frame.json-duplicate`.
- Once the first byte of a frame arrives, the whole frame MUST arrive within 5
  seconds (`frame.timeout`). Waiting between frames is unbounded, apart from
  liveness (§7.9).
- A sender that cannot finish writing a frame within 5 seconds MUST close the
  connection (`frame.write-timeout`).
- `null` is never a valid value, anywhere in a frame, ignored members included.
  Integers have no fraction or exponent. Strings MUST NOT contain unpaired
  surrogates after unescaping: an escape that forms an unpaired surrogate, in a
  value or a member name, is `frame.json-invalid`.

### 7.3 Handshake

```text
companion                                   host
    | -- hello ------------------------------> |   negotiate version, check registration
    | <------------------------------ challenge |   host proof binds both offers
    | -- authenticate -----------------------> |   companion proof binds both offers
    |                                           |   check identity pin, manifest hash
    | <---------------------------------- ready |   (or error, then close)
```

The whole handshake MUST finish within 5 seconds of the connection being
accepted (`auth.timeout`). Before `ready`, the only other message either side
may send is `error`.

**Errors before the peer is verified.** Until a receiver has verified its
peer's proof (the companion verifies the challenge; the host verifies
`authenticate`), it accepts an `error` only when its code carries the Pre flag
of §8.3, or is unknown (§7.5). A known code without the Pre flag is
`protocol.unexpected`. Pre codes from a server that is not verified tell the
companion nothing it can trust, so they never stop an SDK (§9.2).

#### 7.3.1 `hello` (companion → host)

| Member | Type | Required | Rules |
|---|---|---|---|
| `type` | string | Yes | `"hello"` |
| `minVersion` | integer | Yes | 1–65535; lowest protocol version offered. |
| `maxVersion` | integer | Yes | `minVersion`–65535; highest offered. |
| `registrationId` | string | Yes | GUID; the pairing file's registration id. |
| `clientNonce` | string | Yes | 32 fresh random bytes, canonical Base64. |
| `capabilities` | array of string | Yes | 0–32 unique capability ids the companion supports. |
| `client` | object | Yes | `{ "name": string, "version": string }`; name matches `[A-Za-z0-9@._/+-]{1,64}`, version `[0-9A-Za-z.+-]{1,32}`. Informational. |
| `manifestHash` | string | Yes | Manifest hash (§B.2) of the manifest the companion loaded. |
| `credential` | object | Reserved | Names the credential the companion holds (§6.5). Absent means the persistent secret. Sent only with a per-launch pairing, which only a host that implements `pairing.per-launch` issues; this is the one member of `hello` gated by a capability the companion cannot yet know is effective. |

Unknown members in `hello` MUST be ignored under the rule of §7.11, so a later
version can add members and still negotiate down.

#### 7.3.2 `challenge` (host → companion)

The host checks `registrationId` (`auth.registration-mismatch`) and picks the
highest version in both its own supported range and `[minVersion, maxVersion]`.
If there is none, it sends `error` with `protocol.version-unsupported` and its
range in `supported`, then closes.

| Member | Type | Required | Rules |
|---|---|---|---|
| `type` | string | Yes | `"challenge"` |
| `serverNonce` | string | Yes | 32 fresh random bytes, canonical Base64. |
| `version` | integer | Yes | The negotiated version (3). |
| `capabilities` | array of string | Yes | 0–32 unique capability ids the host supports. |
| `host` | object | Yes | `{ "id": host id, "version": string matching [0-9A-Za-z.+-]{1,32} }` |
| `proof` | string | Yes | Server proof (§7.3.4). |

Before sending anything else, the companion MUST check that:

1. `version` lies within its own offer `[minVersion, maxVersion]` and is a
   version it implements (`protocol.version-unsupported`, raised locally);
2. `host.id` equals its pairing file's `hostId` (`auth.host-mismatch`); and
3. the proof verifies (`auth.server-proof-invalid`).

#### 7.3.3 `authenticate` (companion → host)

| Member | Type | Required | Rules |
|---|---|---|---|
| `type` | string | Yes | `"authenticate"` |
| `proof` | string | Yes | Client proof (§7.3.4). |

The host verifies the proof (`auth.proof-invalid`). It then checks the
connecting program against the registration's identity pin, when it keeps one
(`auth.identity-changed`), and compares `manifestHash` with its admitted
manifest's hash (`manifest.mismatch`). On any failure it sends `error` and
closes; otherwise it sends `ready`.

#### 7.3.4 Proofs and the transcript

```text
transcript = label LF role LF hostId LF hostVersion LF registrationId LF
             clientNonce LF serverNonce LF version LF minVersion LF maxVersion LF
             clientCapabilities LF hostCapabilities LF manifestHash
```

- `label` is `Ventana.Extensions.v3`. `LF` is U+000A. There is no final LF.
- `role` is `server` for the challenge proof and `client` for the
  authenticate proof.
- `hostId` and `hostVersion` are the challenge's `host` values.
- `version` is the negotiated version; `minVersion` and `maxVersion` are the
  hello's offer. All three are decimal with no leading zeros.
- `clientCapabilities` is the hello's list and `hostCapabilities` the
  challenge's list, each sorted by ordinal byte order and joined with `,`. An
  empty list is an empty string.
- `manifestHash` is the hello's value.
- proof = canonical Base64 of HMAC-SHA256(key = the 32 decoded secret bytes,
  message = UTF-8 of the transcript).
- Proofs MUST be compared in constant time over the decoded bytes.

Every field has a grammar that excludes LF and `,`, so the transcript is
unambiguous. Because both proofs cover both offers, the negotiated version, the
host identity, both nonces and the manifest hash, a party that altered `hello`
or `challenge` in transit cannot produce a valid proof: the protocol is
downgrade-proof. Distinct roles stop a proof being reflected. Fresh nonces on
both sides stop a handshake being replayed. The host id stops a credential for
one host authenticating to another.

#### 7.3.5 `ready` (host → companion)

| Member | Type | Required | Rules |
|---|---|---|---|
| `type` | string | Yes | `"ready"` |
| `version` | integer | Yes | Equal to the challenge's. |
| `capabilities` | array of string | Yes | Equal to the challenge's. |
| `host` | object | Yes | Equal to the challenge's. |
| `uiLanguage` | string | Yes | Language tag of the host's current UI language. Companions SHOULD write face text in it. |
| `limits` | object | Yes | §7.10; every member within its allowed range. |

The companion MUST check that `version`, `capabilities` and `host` equal the
challenge's, and that every `limits` member is within its range
(`protocol.message-invalid` otherwise). The **effective capabilities** of the
connection are the intersection of the hello's and the challenge's lists.

### 7.4 Capability registry

Every feature of protocol 3 listed in §7.5 is baseline: it is implied by the
negotiated version and never listed. Capabilities name optional features.

| Id | Status | Meaning |
|---|---|---|
| `pairing.per-launch` | Reserved | Per-launch pairing for host-managed activation (§6.5). |
| `identity.package` | Reserved | The host verifies the client's package family name (§6.5). |
| `face.image` | Reserved | Image pictures in faces. |
| `face.time` | Reserved | Time lines the host formats. |
| `invoke.progress` | Reserved | A companion may extend an invocation's deadline while it works. |
| `settings.toggle`, `settings.text`, `settings.number` | Reserved | Further setting kinds (with a later manifest schema). |
| `strings.localized` | Reserved | The host applies strings files (§3.10) and says which language it chose. |
| `failure.llm-unavailable` | Reserved | The failure token `LlmUnavailable` (§7.8). |
| `x.*` | Experimental | Developer-mode-only features. A host MUST NOT advertise an `x.` capability unless developer mode is on. `x.test-echo` is used by conformance tests and does nothing. |

Rules:

- A receiver MUST ignore capability ids it does not know, and they still enter
  the transcript as sent.
- A sender MUST NOT send a message type, member or value defined by a
  capability that is not effective for the connection
  (`protocol.capability-not-negotiated`). SDKs check this locally and refuse
  the call (§9.2), so an author's mistake never closes the connection.
- New capability ids are added to this registry and to
  `fixtures/wire/v3/capabilities.json`; an id is never reused for another
  meaning.

### 7.5 Message table

Post-handshake messages. "Dir." is the sender: H (host) or C (companion).

| `type` | Dir. | Members (all required unless marked optional) | Reply |
|---|---|---|---|
| `startSession` | H | `sessionId` GUID; `contributionId`; `settings` object (§7.6.1) | none, or `sessionRefused` |
| `stopSession` | H | `sessionId` | none |
| `invoke` | H | `requestId` GUID; `sessionId` | exactly one `result` unless cancelled |
| `cancel` | H | `requestId` | none (a late `result` is allowed and ignored) |
| `setFace` | C | `sessionId`; `face` object (§7.7) | none |
| `clearFace` | C | `sessionId` | none |
| `fail` | C | `sessionId`; `failure` (§7.8) | none |
| `result` | C | `requestId`; `outcome` (§7.8); optional `failure`, only when `outcome` is `Failed` | — |
| `sessionRefused` | C | `sessionId`; `code`: exactly one of `session.capacity`, `session.unknown-contribution`, `session.settings-invalid` (a closed set; any other value is `protocol.message-invalid`) | — |
| `ping` | H or C | `id` integer 1–2147483647 | `pong` within `limits.pongTimeoutMs` |
| `pong` | H or C | `id` equal to the ping's | — |
| `error` | H or C | `code`; optional `message`, `sessionId`, `requestId`, `retryAfterMs`, `supported` | the sender closes, unless the code is advisory |

`error` members:

| Member | Type | Rules |
|---|---|---|
| `code` | string | A reason code (§8). `error.code` is an **open registry**: a receiver accepts any value that matches the dotted-code grammar (§2.4), including codes it does not know. |
| `message` | string | Optional; 1–256 characters, printable ASCII only (`[\x20-\x7E]{1,256}`; `protocol.message-invalid` otherwise); fixed English text from the sender, never content received from the peer. Receivers MUST NOT display it as host chrome. SDKs and tools print the code and the catalog's fix, never this text; a verbose mode MAY show it. |
| `sessionId`, `requestId` | string | Optional; the session or request the error concerns. |
| `retryAfterMs` | integer | Optional; 0–300,000; sent with `host.paused`, `rate.throttled`, and any code added later that defines it. A receiver honours it for a code it does not know as well (§9.2). |
| `supported` | object | Optional; `{ "minVersion": int, "maxVersion": int }`, each 1–65535 with `minVersion` ≤ `maxVersion`; only with `protocol.version-unsupported`. |

**Unknown codes.** A receiver that does not know an `error` code treats it as a
code with disposition "close" that is never terminal, never a violation, never
advisory and has no host wording: an SDK moves to `Waiting` with normal
backoff, honouring `retryAfterMs` within its cap (§9.2); a host closes, shows
the registration as disconnected for an unrecognised reason, and logs the fixed
code `peer.unknown-code`, never the received string (§8.1). Adding a reason
code is therefore a compatible change (§12.3). Because an older receiver closes
on any code it does not know, a new advisory code, or a new code a sender
expects the connection to survive, is sent only when a capability says the peer
knows it (§7.4).

Advisory codes are not followed by a close. In protocol 3 the only advisory code
is `rate.throttled`. After every other `error`, the sender MUST close the
connection within 1 second.

### 7.6 Sessions

#### 7.6.1 Starting

- A host starts a session with `startSession` for a contribution in the
  manifest it admitted. `settings` is the **complete** effective set: one member
  per declared setting, each a declared choice value, and no other members.
  Defaults are filled in by the host.
- `settings` is a **map**, not a schema object, so §7.11 rule 2 does not apply
  to it. It has at most 16 members; each key is a setting id (§2.4) and each
  value a choice value (§2.4) (`protocol.message-invalid` otherwise). A key the
  companion's manifest does not declare for that contribution, a declared
  setting with no key, or a value that is not one of its choices is
  `session.settings-invalid`: the companion refuses the session and never
  drops or fills in a key silently.
- `sessionId` is new: never used before on this connection and minted at
  random, so it is never reused across connections either.
- A host MUST NOT have more than `limits.maxSessions` sessions open on one
  connection.
- Session ids are opaque. They do not identify a placement, and a host MUST NOT
  send anything that reveals when its UI is shown or what else it contains.
- Settings are immutable for a session. A settings change is a `stopSession`
  for the old session followed by a `startSession` with a new id.

#### 7.6.2 Refusing

A companion that cannot run a session sends `sessionRefused` with a code:

- `session.capacity`: the companion is at its own limit (for example handlers
  that have not yet ended after cancellation).
- `session.unknown-contribution`: the contribution is not in the companion's
  manifest. With matching manifest hashes this does not happen.
- `session.settings-invalid`: the settings do not validate against the
  companion's manifest (§7.6.1).

A host MUST treat a refused session as ended, MUST NOT send `invoke` for it,
MUST NOT start a replacement for the same placement until its demand changes or
the connection is re-established, and MUST surface the code. The connection
stays open.

#### 7.6.3 Stopping

- `stopSession` ends a session. After receiving it, a companion MUST NOT send
  `setFace`, `clearFace` or `fail` for that session, and SHOULD cancel the work
  behind it.
- A connection loss ends every session. Nothing is replayed on the next
  connection: the host starts new sessions with new ids.
- A host MUST ignore face messages and results that arrive for sessions and
  requests it no longer knows (`face.session-unknown`, `invoke.request-unknown`).
  Late output belongs only to the handle it named.
- Receivers keep a **replay window**: the last 1,024 ended session ids and the
  last 1,024 completed request ids per connection. A `startSession` or `invoke`
  that reuses a live id, or an id in that window, is a protocol violation
  (`session.replay`, `invoke.replay`).

#### 7.6.4 What a companion receives

A companion receives only: its own contribution ids, opaque session and request
ids, validated setting values, cancellations, the host's id and version, the UI
language and limits. It receives no host UI contents, no item metadata, no
events about when the host's UI is shown, no window or app identity, no licence
state, no other extension's data, and no power to show, pick or invoke anything.

### 7.7 Faces

#### 7.7.1 The `face` object

| Member | Type | Required | Rules | Default |
|---|---|---|---|---|
| `picture` | object | No | `{ "$type": "none" }` or `{ "$type": "glyph", "glyph": string }` with the glyph rule (§3.7; `face.glyph-invalid`). An unknown `$type` is `protocol.variant-unknown`. Further variants (for example `image`) arrive with capabilities (§7.4). | `{ "$type": "none" }` |
| `line1` | object | No | `{ "$type": "text", "text": string }`. Displayed up to 40 text elements and 160 UTF-16 units. Further variants (for example `time`) arrive with capabilities. | — |
| `line2` | object | No | As `line1`. Displayed up to 60 text elements and 240 units. | — |
| `state` | string | No | One of `None`, `Playing`, `Paused`, `On`, `Off` (`face.state-invalid`). | `None` |
| `detail` | string | No | The sentence assistive technology reads. Displayed up to 160 text elements and 640 units. | `line1`'s text |
| `goodForSeconds` | integer | Yes | 1–86,400 (`face.lifetime-invalid`). | — |

```json
{"type":"setFace","sessionId":"0f0e0d0c0b0a09080706050403020100","face":{"picture":{"$type":"glyph","glyph":"\uE916"},"line1":{"$type":"text","text":"4:59"},"line2":{"$type":"text","text":"Focus"},"state":"Playing","detail":"Four minutes and fifty-nine seconds left.","goodForSeconds":5}}
```

#### 7.7.2 Rules

- Only a contribution whose `provides` includes `face` may publish
  (`face.not-provided`).
- `setFace` replaces the session's face. `clearFace` removes it. `fail`
  replaces it with a failure the host words (§7.8).
- The host stamps each face when it arrives. The face is current until arrival
  plus `goodForSeconds`, then stale. A companion that wants a face to stay
  current republishes it before it expires (the SDKs can do this for the
  author, §9.2). A face never claims a time of its own, so no companion can make
  a face look newer than it is.
- A host coalesces faces above its per-session budget
  (`limits.faceChangesPerSecond`, burst `limits.faceBurst`): it keeps the newest
  and applies it when the budget allows. Coalescing is not a violation.
- Hosts decide presentation: which parts appear in compact places, in larger
  places, and to assistive technology. Each host documents its rules for
  authors.

#### 7.7.3 Text cleaning

Receivers MUST accept face text longer than the display limits and cut it; they
MUST NOT refuse a face because of what its text contains. Cleaning removes
every disallowed character of §3.6, except that line and paragraph breaks and
tabs (U+0009–U+000D, U+0085, U+2028, U+2029) become a space. A text element
longer than 16 UTF-16 units becomes U+FFFD. The result is trimmed and cut at a
text-element boundary by both the element and unit limits. Senders SHOULD stay
within the limits; the SDKs clean text with these same rules before sending, so
the frame never exceeds them (§9.2).

### 7.8 Invocations, outcomes and failures

- A host sends `invoke` only for a contribution whose `provides` includes
  `invoke`, and only for a session that is current on this connection.
- The host owns the deadline, `limits.invokeTimeoutMs` (15,000 ms in protocol
  3). At the deadline, or when the person's request is abandoned, it sends
  `cancel`.
- A companion answers each `invoke` with exactly one `result`, unless it saw
  `cancel` first, in which case it MAY still answer and the host ignores the
  answer.
- Cancellation cannot undo a side effect that already happened. Companions
  SHOULD check for cancellation again just before an external change.
- A disconnected request fails on the host. It is never replayed.

Outcome tokens (`result.outcome`):

| Token | C# value | Meaning to the host |
|---|---|---|
| `Done` | 1 | It worked. |
| `Refused` | 2 | The companion declined (for example a stale session); the host says it didn't run. |
| `Failed` | 3 | It ran and failed; `failure` MAY say why. |
| `Unsupported` | 4 | The companion does not implement this action. |

Failure tokens (`fail.failure` and `result.failure`). The vocabulary is a
Ventana convention (§2.7): Lollipop's separate SDK is expected to use the same
tokens on its own wire. The host always owns the words it shows.

| Token | C# value | Meaning | Lollipop's current token |
|---|---|---|---|
| `UnsupportedInput` | 1 | The request or input is not something this contribution handles. (Not to be confused with the outcome `Unsupported`: the action does not exist.) | `UnsupportedInput` (same) |
| `NeedsSetup` | 2 | The person must configure something first (in the companion or the app it connects to). | `SettingsRequired` |
| `Network` | 3 | A network request it depends on failed. | `Network` (same token) |
| `NoResult` | 4 | It ran but produced nothing to show. Replaces protocol 2's `NoData`. | `NoResult` (same token) |
| `AppUnavailable` | 5 | An app or device it works with is not running or not reachable on this PC. Added because adapters (for example a Photoshop bridge) otherwise misuse `Network`. | — |
| `LlmUnavailable` | 6, reserved | An on-device model is not installed, enabled or ready. Only with the capability `failure.llm-unavailable` (§7.4). | `LlmUnavailable` (same token) |

- The token set of protocol 3 is **closed**: `UnsupportedInput`, `NeedsSetup`,
  `Network`, `NoResult` and `AppUnavailable`. A host that receives any other
  token, or a capability-gated token whose capability is not effective, closes
  with `face.failure-invalid`.
- A product-specific token arrives only with a capability of its own, as
  `LlmUnavailable` will with `failure.llm-unavailable`. Its C# value is
  reserved now in `fixtures/wire/v3/enums.json`, so values never collide. The
  SDKs refuse locally (`ArgumentException`) to send a token whose capability is
  not effective on the connection.
- The C# values are this SDK's own. The Ventana convention covers the tokens,
  not their numbers: Lollipop's current `ActionFailure` numbers some tokens
  differently (its `LlmUnavailable` is 3, `Network` 4 and `NoResult` 5), a
  difference recorded with the Ventana conventions (§2.7). The wire carries
  tokens, never numbers.
- **A crashed handler is not a failure token.** When an author's session handler
  throws, the SDK sends `clearFace` for that session and raises a fault to the
  author (§9.2), rather than claiming `NoResult`. When an invocation handler
  throws, the SDK answers `result` `Failed` with no `failure` member. The host
  then words it as "didn't work", never as an empty result.

### 7.9 Liveness

- Either side MAY send `ping` after `limits.pingIntervalMs` (30,000 ms in
  protocol 3; a companion uses the value of §7.10) without receiving any frame.
  A host SHOULD do so.
- The receiver MUST answer `pong` with the same `id` within
  `limits.pongTimeoutMs` (10,000 ms in protocol 3), whatever else it is doing.
  The .NET and Node SDKs answer on their reader, without author code.
- **At most one ping is outstanding per direction.** A `ping` that arrives
  while the receiver's `pong` to that peer's previous ping is still unsent (not
  yet handed to the transport), or
  sooner than half of `ready.limits.pingIntervalMs` after the previous `ping`
  from that peer, is `protocol.unexpected`: the receiver closes, and a host
  counts it as a violation (§7.12). Both sides measure against the value in
  `ready`, whatever interval they use themselves.
- A `pong` whose `id` matches no outstanding ping is ignored and counted
  (`protocol.pong-unknown`).
- No `pong` in time means the peer is hung: close with `protocol.ping-timeout`.
  A host shows the registration as not responding rather than connected.

### 7.10 Limits and rate limiting

`ready.limits` members are all required integers. Protocol-3 hosts send the
values below. Each member has an allowed range; a `ready` with any member
outside its range, or breaking a cross-member rule, is refused
(`protocol.message-invalid`). The last column says which value a companion
uses: for capacity and rate members the smaller of the host's value and its
own default (the protocol-3 value), so a host can only make a companion
stricter; for the liveness timers the larger, so a host can never make a
companion ping more often or give up sooner than its own default.

| Member | Protocol-3 value | Allowed range | Meaning | Companion uses |
|---|---|---|---|---|
| `maxFrameBytes` | 65536 | 8192–65536 | §7.2 | smaller |
| `maxSessions` | 64 | 1–64 | Open sessions per connection | smaller |
| `maxPendingInvokes` | 32 | 1–32 | Unanswered invocations per connection | smaller |
| `invokeTimeoutMs` | 15000 | 4000–60000 | Host deadline per invocation (§7.8) | smaller |
| `faceChangesPerSecond` | 2 | 1–2 | Per-session face budget, refill rate | smaller |
| `faceBurst` | 4 | 1–4 | Per-session face budget, burst | smaller |
| `messageRate` | 128 | 1–128 | Soft bucket refill, tokens per second, companion → host | smaller |
| `messageBurst` | 256 | ⌈`maxFrameBytes` / 1024⌉–256 | Soft bucket capacity, tokens | smaller |
| `hardMessageRate` | 512 | `messageRate`–512 | Hard bucket refill, tokens per second, companion → host | smaller |
| `hardMessageBurst` | 1024 | `messageBurst`–1024 | Hard bucket capacity, tokens | smaller |
| `pingIntervalMs` | 30000 | 5000–300000, and greater than `pongTimeoutMs` | §7.9 | larger |
| `pongTimeoutMs` | 10000 | 1000–60000 | §7.9 | larger |

Rate limiting uses token buckets (algorithm in §B.3). **A frame costs one token
per started 1,024 bytes of its body**, so a frame of up to 1,024 bytes costs one
token and a 64 KiB frame costs 64. Frames are charged before they are parsed,
from the length prefix. Charging by size bounds the bytes a host parses as well
as the frames: at protocol-3 values, at most 512 KiB per second on a sustained
basis, against about 32 MiB per second if every frame cost one token.

1. **Per-session face budget** (host): over budget, faces are coalesced
   (§7.7.2). Never a violation.
2. **Soft bucket** (host, all companion frames): when it cannot pay for a
   frame, `setFace`, `clearFace` and `fail` are coalesced per session (the
   newest is kept and applied when tokens return). `result`, `sessionRefused`,
   `ping`, `pong` and `error` are always processed: a `result` or
   `sessionRefused` for a known id answers something the host sent and is
   bounded by `maxPendingInvokes` and `maxSessions`; a `ping` is bounded by the
   rule of §7.9; a `pong` answers the host's one outstanding ping; an `error`
   ends the connection. Frames of these types that name unknown ids are ignored,
   and only the hard bucket bounds them. The host sends the advisory `error`
   `rate.throttled`, with `retryAfterMs`, at most once every 5 seconds.
   Throttling is not a violation.
3. **Hard bucket** (host, all companion frames): when it cannot pay for a
   frame, the host sends `rate.exceeded` and closes. This is a violation
   (§7.12).
4. **Host → companion**: a host MUST NOT exceed 256 tokens per second with a
   burst of 512, costed the same way. SDKs enforce a hard bucket of 512 tokens
   per second, burst 1,024, and close with `rate.exceeded` when it cannot pay.

Output queues: a host's per-connection queue holds `maxSessions × 3 +
maxPendingInvokes × 2` frames (256 at protocol-3 values). When it is full, the
companion is not reading, and the host closes (`rate.queue-full`). The SDKs keep
at most one pending face command per session (the newest replaces it), so their
queues cannot fill with faces.

### 7.11 Unknown members and messages

These rules keep strict validation where it protects receivers, and allow
additive change within a protocol version.

1. **Always refused**, at every phase: duplicate member names
   (`frame.json-duplicate`); a known member with the wrong type, a value outside
   its grammar or limits, or a disallowed `null` (`protocol.message-invalid`,
   or the specific `face.*`/`invoke.*` code); a missing required member
   (`protocol.message-invalid`, or `protocol.hello-invalid` for every fault in a
   `hello`); an unknown `$type` in a discriminated object
   (`protocol.variant-unknown`); an unknown token of a closed enumeration (the
   specific code, for example `face.state-invalid`). `error.code` is not a
   closed enumeration: it is an open registry (§7.5). In detail: every fault in
   a `hello` (a missing member, a wrong type, a grammar violation, a `null`) is
   `protocol.hello-invalid`; a missing or non-string `type`, and after
   authentication a `type` outside the member-name grammar, is
   `protocol.message-invalid`; a `goodForSeconds` that is not an integer is
   `protocol.message-invalid`, and an integer outside its range
   `face.lifetime-invalid`; a `supported` member with a code other than
   `protocol.version-unsupported` is `protocol.message-invalid`. A reader also
   applies the frame-size rules of §7.2 (`frame.too-large`). It reads `ready`
   only after the peer's proof verified and before `ready`, `challenge`, `hello`
   and `authenticate` only before that, and `error` in every phase; the Pre check
   of §7.3 applies only before the peer's proof verified.
2. **Unknown members in a known object are ignored** when the name matches the
   JSON member-name grammar (`[a-z][A-Za-z0-9]{0,31}`) and does not equal a
   known member of that object under case folding. Any other unknown name,
   including any other `$`-prefixed name and a case variant of a known name
   such as `sessionid`, is refused (`protocol.member-invalid`). Ignored members
   still count toward the frame-size and depth limits. The `settings` map of
   `startSession` is not an object with known members, and this rule does not
   apply to it (§7.6.1).
3. **Unknown message types after authentication are ignored** when the type
   matches the member-name grammar. The receiver discards the frame and MAY
   count it (`protocol.type-unknown`). Before authentication the message order is
   fixed, and an unexpected type is refused (`protocol.unexpected`).
4. A known message in the wrong direction or phase is refused
   (`protocol.unexpected`).
5. Senders MUST NOT rely on rule 2 or 3 to deliver anything a receiver needs:
   features a peer must understand are gated by capabilities (§7.4).
6. **Matching.** Member names, `type` values and `$type` values are compared
   ordinally, after JSON unescaping, for duplicate checks and against known
   names alike: `"\u0073essionId"` is `sessionId`. Receivers MUST NOT bind
   members case-insensitively (for example with a case-insensitive JSON
   binder), because a validator and a binder that disagree about which member
   counts are a parser differential.

Why this is safe: the protections strictness provided came from bounded size
and depth, refusing duplicate members and case variants (which removes parser
disagreements), and validating everything that is used. Ignoring data that
nobody reads adds no attack surface beyond a parse that is already bounded by
size and by the token cost of §7.10, while it lets a host or SDK add an
optional member without breaking every peer in the field. Manifests, pairing
files and package descriptors stay fully strict, because they are versioned
files where typo detection matters more than additive change.

### 7.12 Violations and the penalty box

A **violation** is a connection that the host closes with a code whose
`isViolation` flag is set in `fixtures/codes/reason-codes.json`, which is
authoritative (or that ends in `auth.abandoned`); codes a companion sends never
count, because they say nothing about the companion's behavior and would let a
peer pause itself for the host's fault. The flag is set for every code whose
disposition (§8) is "close", except:

- `host.*` codes and `auth.timeout`;
- `manifest.mismatch` and `auth.identity-changed`, which wait on a person
  (Reload the manifest, Allow this program), not on a misbehaving peer; and
- unknown codes (§7.5).

`auth.abandoned` (a companion looping on an out-of-date pairing) is also a
violation, although it is local.

Hosts SHOULD keep a penalty box per registration:

- After 3 violations within 60 seconds, refuse new connections for 5 seconds,
  doubling for each further violation to at most 300 seconds.
- While paused, read `hello`, answer `error` `host.paused` with `retryAfterMs`,
  and close.
- Clear the box after 10 minutes without a violation, and whenever the person
  acts on the registration in a way that can fix the cause: **Retry**, **Allow
  this program**, **Revoke access**, **Save connection info**, **Reload
  manifest**, **Replace**, and a reload of its developer folder.

### 7.13 Casing and token rules

| Kind | Casing | Examples |
|---|---|---|
| JSON member names | camelCase ASCII | `sessionId`, `goodForSeconds` |
| `$`-prefixed members | only `$type` (discriminator) and `$schema` (files) | |
| `type` and `$type` values | camelCase | `startSession`, `setFace`, `glyph` |
| Closed enumeration tokens | PascalCase | `Playing`, `NeedsSetup`, `Done`, `Choice`, `Persistent`, `Internet`, `Error` |
| Open registries | dotted lowercase kebab | `auth.proof-invalid`, `json.unknown-member`, `identity.package` |
| `provides` values, host ids | lowercase | `invoke`, `face`, `example-host` |
| GUIDs | 32 lowercase hex | |
| Nonces, secrets, proofs | canonical standard Base64 | |
| Hashes | lowercase hex | |

Every enumeration crosses the wire through an explicit table in each
implementation, never through a language's enum-to-string conversion, and the
tables are pinned by `fixtures/wire/v3/enums.json`. In C#, every non-flags
enumeration starts at 1, so an uninitialized value is never a valid token.

### 7.14 Golden test vectors

The wire vectors live in `fixtures/wire/v3/` and are run by the .NET SDK, the
Node SDK and the host. Appendix A lists every file.

---

## 8. Reason-code catalog

### 8.1 Use

Reason codes explain runtime events. A host logs them, shows them on a
registration and in developer surfaces, and sends them in `error` frames. SDKs
raise them as status (§9.2) and print them with the catalog's fix and help link.
Codes are stable: a code is never reused for a different meaning. The catalog
is an **open registry**: new codes are additive, and every receiver accepts a
code it does not know (§7.5), so adding a code never breaks a peer of the same
protocol version.

**Logging rule.** Hosts MAY log, at Information level, the registration id, the
extension id, the extension version and a reason code from this catalog. A code
received from a peer that the host does not know is logged as the fixed code
`peer.unknown-code`, never as the received string. Hosts MUST NOT log setting
values, face text, file paths, pipe names, diagnostic paths or messages,
`error.message` text received from a peer, or any credential. Hosts SHOULD log a
repeated code for the same registration at most once every 10 seconds, with a
count of the suppressed repeats.

**Exceptions.** When extension code in a host logs an exception, it logs only
the exception's type name and `HResult`, never its `Message`, `StackTrace`,
inner exceptions or `ToString()`. Exception text routinely carries file paths
(which contain the user's profile name), pipe names, and parser messages that
quote the offending input, and support bundles hand logs to other people.

### 8.2 Columns

- **Disposition**: `close` (an `error` frame is sent when possible, then the
  connection closes); `advisory` (an `error` frame, no close); `refuse` (the
  item is refused, the connection stays open); `ignore` (the frame is discarded
  and counted); `local` (never on the wire; raised where it happens).

  A host that keeps listening on the same pipe instance waits, after writing
  the `error` frame of a `close` code, for the companion to close its end (at
  most 1 second) before it readies the instance again, because disconnecting a
  named pipe discards unread data; a companion closes its end once it has read
  an `error` with disposition `close`.
- **Seen in**: L = host log; R = host registration row; D = host developer
  details only; E = `error` frame; S = SDK status.
- **Pre** = may be sent to a peer that has not yet verified the sender's proof:
  by a host in place of `challenge`, or by a companion in place of
  `authenticate`. A receiver refuses a known code without this flag until it
  has verified its peer (§7.3).
- **Violation**: whether the code counts toward the penalty box (§7.12).

`fixtures/codes/reason-codes.json` carries, for each code, its disposition,
audiences, Pre and violation flags, the author fix below, and its help anchor
(§8.4). SDKs and tools read the fix and anchor from it (§9.2).

### 8.3 Catalog

| Code | Disposition | Seen in | Pre | Violation | Meaning | Author fix |
|---|---|---|---|---|---|---|
| `auth.registration-mismatch` | close | L R E S | ✓ | ✓ | The hello names a different registration than this pipe's. | Save connection info again and use the new file. |
| `auth.proof-invalid` | close | L R E S | — | ✓ | The companion's proof is wrong: its pairing is out of date or for another registration, or a per-launch credential was used up. | Save connection info again; restart the companion. |
| `auth.server-proof-invalid` | local | S | — | — | The host's proof failed verification: the pairing is out of date, or the pipe is not the host. | Save connection info again. |
| `auth.server-unverified` | local | S | — | — | The pipe is not owned by the current user, the process serving it runs as another user or at a lower integrity level than the companion, or the pipe was created at a lower integrity level, so the SDK wrote nothing to it (§7.1). Another program may hold the pipe name while the host is not running. | Start the host and run the companion without administrator rights; if this persists, restart the PC. |
| `auth.host-mismatch` | local | S | — | — | `challenge.host.id` differs from the pairing's `hostId`. | Use connection info from this host. |
| `auth.abandoned` | local | L R | — | ✓ | The companion closed after the challenge without authenticating; most often an out-of-date pairing. | Save connection info again. |
| `auth.timeout` | close | L R E S | ✓ | — | The handshake did not finish within 5 seconds. | Check the companion's handshake code. |
| `auth.identity-changed` | close | L R E S | — | — | A different program than the one this registration's identity pin trusts connected. | In the host, allow the new program or revoke access. |
| `auth.package-mismatch` | close | L R E S | — | ✓ | Reserved (§6.5). | — |
| `frame.too-large` | close | L R E S | ✓ | ✓ | Frame length 0, over `limits.maxFrameBytes`, or over 8,192 bytes before `ready`. | Send smaller messages. |
| `frame.timeout` | close | L R E S | ✓ | ✓ | A frame did not finish within 5 seconds. | Write each frame in one go. |
| `frame.utf8-invalid` | close | L R E S | ✓ | ✓ | The frame is not valid UTF-8. | Encode JSON as UTF-8. |
| `frame.json-invalid` | close | L R E S | ✓ | ✓ | Not one JSON object within the depth limit. | Send one object per frame. |
| `frame.json-duplicate` | close | L R E S | ✓ | ✓ | A member name appears twice in one object. | Remove the duplicate. |
| `frame.write-timeout` | close | L R S | ✓ | ✓ | The peer stopped reading for 5 seconds. | Keep reading the pipe. |
| `protocol.version-unsupported` | close | L R E S | ✓ | ✓ | No protocol version both sides support; `supported` gives the host's range. Also raised locally when a challenge picks a version outside the companion's offer. | Update the SDK or the host. |
| `protocol.hello-invalid` | close | L R E S | ✓ | ✓ | The hello is malformed or missing a member. | Use an SDK, or follow §7.3.1. |
| `protocol.message-invalid` | close | L R E S | ✓ | ✓ | A known message failed validation, including a malformed `challenge` and a `ready` whose limits are out of range. | Check the message against §7.5. |
| `protocol.member-invalid` | close | L R E S | ✓ | ✓ | An unknown member with a name outside the member grammar, or a case variant of a known member. | Remove it, or fix its spelling. |
| `protocol.variant-unknown` | close | L R E S | ✓ | ✓ | An unknown `$type`. | Use the documented variants. |
| `protocol.type-unknown` | ignore | D | — | — | An unknown message type after authentication was ignored. | None needed; check for typos. |
| `protocol.unexpected` | close | L R E S | ✓ | ✓ | A message in the wrong phase or direction, a known code without the Pre flag before verification, or a ping that broke the one-outstanding rule (§7.9). | Follow the handshake order; ping only when idle. |
| `protocol.capability-not-negotiated` | close | L R E S | — | ✓ | A feature of a capability that is not effective was used. | Check the effective capabilities in `ready`. |
| `protocol.ping-timeout` | close | L R E S | — | ✓ | No `pong` within the pong timeout. | Answer `ping` promptly; don't block the reader. |
| `protocol.pong-unknown` | ignore | D | — | — | A `pong` matched no outstanding ping and was ignored. | None needed; send `pong` only in answer to `ping`. |
| `face.lifetime-invalid` | close | L R E S | — | ✓ | `goodForSeconds` is not 1–86,400. | Use a finite lifetime. |
| `face.glyph-invalid` | close | L R E S | — | ✓ | The picture's glyph breaks the glyph rule. | Use one private-use glyph. |
| `face.state-invalid` | close | L R E S | — | ✓ | Unknown `state` token. | Use `None`, `Playing`, `Paused`, `On` or `Off`. |
| `face.failure-invalid` | close | L R E S | — | ✓ | Unknown `failure` token, or a capability-gated token whose capability is not effective. | Use a token from §7.8. |
| `face.not-provided` | close | L R E S | — | ✓ | A face for a contribution that does not provide `face`. | Add `face` to `provides`, or stop publishing. |
| `face.session-unknown` | ignore | D | — | — | A face for an ended or unknown session was ignored. | None needed after `stopSession`. |
| `invoke.outcome-invalid` | close | L R E S | — | ✓ | Unknown `outcome` token. | Use a token from §7.8. |
| `invoke.failure-unexpected` | close | L R E S | — | ✓ | `failure` with an outcome other than `Failed`. | Send `failure` only with `Failed`. |
| `invoke.replay` | close | L R E S | — | ✓ | An `invoke` reused a live or recent request id. | Hosts only. |
| `invoke.request-unknown` | ignore | D | — | — | A `result` for an unknown or completed request was ignored. | None needed after `cancel`. |
| `invoke.timeout` | local | L D | — | — | No result within the host's deadline; `cancel` was sent. | Finish faster, or honour cancellation. |
| `invoke.busy` | local | L R | — | — | `maxPendingInvokes` invocations were already pending; the person's request was refused. | Answer invocations promptly. |
| `invoke.not-connected` | local | L R | — | — | The person picked an item while the companion was not connected. | Start the companion. |
| `rate.throttled` | advisory | D E S | — | — | The soft budget is used up; faces are being coalesced. | Publish only on change. |
| `rate.exceeded` | close | L R E S | — | ✓ | The hard budget is used up. | Publish far less often, and in smaller frames. |
| `rate.queue-full` | close | L R S | — | ✓ | The receiver's output queue filled because the peer is not reading. | Keep reading the pipe. |
| `manifest.mismatch` | close | L R E S | — | — | The companion's manifest hash differs from the host's admitted manifest. | Reload the manifest in the host, or update the companion's `extension.json`. |
| `session.capacity` | refuse | L D S | — | — | The companion refused a session: it is at its limit. | Make handlers end promptly when cancelled. |
| `session.unknown-contribution` | refuse | L D S | — | — | The companion refused a session for a contribution it does not know. | Align both manifests. |
| `session.settings-invalid` | refuse | L D S | — | — | The companion refused a session whose settings do not match its manifest. | Align both manifests. |
| `session.replay` | close | L R E S | — | ✓ | A `startSession` reused a live or recent session id. | Hosts only. |
| `session.host-limit` | local | L R | — | — | The host has `maxSessions` sessions open and did not start this one. | Fewer placements. |
| `session.handler-faulted` | local | S | — | — | The author's handler threw. A session handler's face is cleared; an invocation answers `Failed` (§7.8). | Fix the exception (the SDK passes it to the author). |
| `session.handler-stalled` | local | S | — | — | A handler did not end within 5 seconds of cancellation. | Pass the cancellation token to every await. |
| `pairing.missing` | local | S | — | — | No pairing file was found in any searched location (§6.6). | In the host, choose Save connection info. |
| `pairing.too-large` | local | S | — | — | The pairing file is over 4,096 bytes. | Save connection info again. |
| `pairing.encoding` | local | S | — | — | Not UTF-8 (often UTF-16 from a shell redirect). | Use the host's Save connection info. |
| `pairing.version-unsupported` | local | S | — | — | `pairingVersion` is not 3. | Save connection info from a current host. |
| `pairing.mode-unsupported` | local | S | — | — | `mode` is not one this SDK implements. | Update the SDK. |
| `pairing.extension-mismatch` | local | S | — | — | `extensionId` differs from the manifest's id. | Use this extension's connection info. |
| `pairing.host-not-listed` | local | S | — | — | `hostId` is not in the manifest's `hosts`. | Add the host id to `hosts`. |
| `pairing.pipe-name-invalid` | local | S | — | — | `pipeName` breaks the grammar or does not match. | Save connection info again. |
| `pairing.registration-invalid` | local | S | — | — | `registrationId` is not a GUID in wire form. | Save connection info again. |
| `pairing.secret-invalid` | local | S | — | — | `secret` is not 32 bytes of canonical Base64. | Save connection info again. |
| `host.not-running` | local | S | — | — | The pipe does not exist, or did not accept within 5 seconds: the host is not running, or the extension is off. | Start the host and turn the extension on. |
| `host.pipe-busy` | local | S | — | — | Another companion is already connected to this registration. | Run one copy of the companion. |
| `host.listener-failed` | local | L R | — | — | The host could not create the pipe (its name is in use). | In the host, choose Retry. |
| `host.listener-faulted` | local | L | — | — | The host's listener stopped unexpectedly and is restarting. | None; report if repeated. |
| `host.edition-unknown` | local | L R | — | — | The host could not identify its own installation, so it does not open extension pipes. | Restart or reinstall the host. |
| `host.inactive` | local | R | — | — | The host started from a link and has not opened extension pipes yet. | Open the host normally. |
| `host.registration-limit` | local | L R | — | — | The host already has its maximum number of extensions (16 in Orbit), developer-folder extensions included; this one was not added. | Remove an extension first. |
| `host.paused` | close | L R E S | ✓ | — | The penalty box (§7.12) is refusing connections; see `retryAfterMs`. | Fix the violations shown; choose Retry in the host. |
| `host.shutting-down` | close | E S | ✓ | — | The host is closing. | None; the SDK reconnects. |
| `host.turned-off` | close | E S | — | — | The person turned the extension off. The credential stays valid. | None; it reconnects when turned on. |
| `host.access-revoked` | close | E S | — | — | The person revoked access. The credential is gone. | Get new connection info after access is allowed again. |
| `host.reloaded` | close | E S | — | — | The host reloaded the manifest in developer mode and closed the connection to apply it. | None; the SDK reconnects at once. |
| `host.catalog-recovered` | local | L R | — | — | The host's extension catalog was unreadable and was restored from its backup; every restored extension is off and needs consent again. | Turn each extension on again, and save new connection info. |
| `host.catalog-unreadable` | local | L R | — | — | The catalog and its backup are unreadable; extension management is read-only until reset. | In the host, choose Reset extensions. |
| `peer.unknown-code` | local | L D | — | — | The peer sent an `error` code this receiver does not know (§7.5). Logged in place of the received string. | Update the host or the SDK. |

The catalog is also published as `fixtures/codes/reason-codes.json`. If this
document and that file disagree, the file is correct.

### 8.4 Help links

Hosts and SDKs link reason codes to documentation through stable redirects:

- `https://dev.ventana.tools/go/<host-id>/learn-extensions`
- `https://dev.ventana.tools/go/<host-id>/build-extensions`
- `https://dev.ventana.tools/go/<host-id>/developer-mode`
- `https://dev.ventana.tools/go/<host-id>/codes#<anchor>`, where `<anchor>` is
  the code with `.` replaced by `-` (for example `auth-proof-invalid`).

---

## 9. .NET SDK public API

*The signatures below are normative for names and shapes. Implementation details
(private members, internal types) are not.*

.NET authors install one package, `VentanaTools.Orbit.Extensions`, and write
one `using VentanaTools.Orbit.Extensions;`. Its root namespace holds everything
an author names: declarations, faces, failures, outcomes, reason codes,
diagnostics, pairing, `CompanionApp`, `ContributionHandler`, the client,
sessions and invocations (§9.1, §9.2). Two further namespaces of the same
package serve hosts, tools and implementations in other languages:
`VentanaTools.Orbit.Extensions.Packaging` (the package archive, §5) and
`VentanaTools.Orbit.Extensions.Wire` (framing, pipe names, the handshake and
messages, §7). The pairing types live in the root namespace, beside the
`CompanionClient` that takes a `Pairing`: the pairing file is an author-visible
artifact and every `pairing.*` code is an author-facing reason code; their proof
methods delegate to `Wire.Handshake`. The test kit is the separate package
`VentanaTools.Orbit.Extensions.Testing` (§9.3).

The libraries target `net10.0`, AnyCPU, set `IsAotCompatible` (which implies
trimming compatibility), track their surface with PublicApiAnalyzers, enable
package validation, and require XML documentation on every public member (CS1591
is an error). The author package depends only on the base class library. The
test kit and add-on packages also depend on their author package at the exact
same version. An add-on's other direct dependencies are limited to
`Microsoft.Extensions.*.Abstractions` packages and `Microsoft.Extensions.Options`.
A concrete implementation, such as `Microsoft.Extensions.Hosting`, is the
application's to add: the Generic Host add-on `VentanaTools.Orbit.Extensions.Hosting`
(§9.5) depends on `Microsoft.Extensions.Hosting.Abstractions` and
`Microsoft.Extensions.Options`. The version
range is exact (`[x.y.z]`) because the test kit and the add-on use the author
package's internal seams (the in-memory transport and `CompanionApp`'s output).

The packages target `net10.0` only, with no `netstandard2.0` build. A companion
is a program that brings or names its own runtime, not a library loaded into an
older one, and the SDK is built on what .NET 10 provides: `TimeProvider` for every
delay and deadline, Native AOT and trimming analysis, `LibraryImport` for the
pipe's server checks (§7.1), and the current `System.Text.Json` and cryptography
APIs.

**Additive shapes.** No public type in these libraries is a positional record or
has a primary constructor. Public data types are sealed classes or records with
`required` or optional `init` properties, so adding an optional member, as the
wire allows within a protocol version (§7.11), is binary compatible (§12.3). A
test in each library fails on any public positional record or primary
constructor. Where this section shows several properties on one line, it is for
brevity only.

### 9.1 Declarations, diagnostics, pairing, packaging and wire

```csharp
namespace VentanaTools.Orbit.Extensions;

public sealed class ExtensionManifest
{
    public const int CurrentSchemaVersion = 3;
    public string? Schema { get; init; }                         // "$schema"
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Hosts { get; init; }
    public string? Glyph { get; init; }
    public Publisher? Publisher { get; init; }
    public string? SupportUrl { get; init; }
    public string? DefaultLanguage { get; init; }
    public Disclosures? Disclosures { get; init; }
    public ManifestRequirements? Requires { get; init; }
    public required IReadOnlyList<Contribution> Contributions { get; init; }
}

public sealed class Contribution
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required Provides Provides { get; init; }
    public IReadOnlyList<Setting> Settings { get; init; } = [];
}

[Flags] public enum Provides { None = 0, Invoke = 1, Face = 2 }

public sealed class Setting
{
    public required string Id { get; init; }
    public SettingKind Kind { get; init; } = SettingKind.Choice;
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Default { get; init; }
    public required IReadOnlyList<SettingChoice> Choices { get; init; }
}

public enum SettingKind { Choice = 1 }                          // Toggle, Text, Number reserved
public sealed class SettingChoice { public required string Value { get; init; } public required string Name { get; init; } }
public sealed class Publisher { public required string Name { get; init; } public string? Url { get; init; } }
public sealed class Disclosures { public required NetworkUse Network { get; init; } public string? PrivacyUrl { get; init; } }
public enum NetworkUse { None = 1, LocalNetwork = 2, Internet = 3 }
public sealed class ManifestRequirements { public required IReadOnlyList<string> Capabilities { get; init; } }

public enum FaceState { None = 1, Playing = 2, Paused = 3, On = 4, Off = 5 }
public enum Failure { UnsupportedInput = 1, NeedsSetup = 2, Network = 3, NoResult = 4, AppUnavailable = 5 }
                                                                // 6 is reserved for LlmUnavailable (§7.8)
public enum Outcome { Done = 1, Refused = 2, Failed = 3, Unsupported = 4 }

// Face parts, used by both the wire records and the client API (§7.7.1). Each variant mirrors a "$type".
// The hierarchies are closed: abstract classes whose only constructor is private protected, and sealed
// variants, so no other assembly can derive one (a non-sealed record cannot be closed: its copy
// constructor must be protected). Parts compare by value (Equals, GetHashCode, == and !=); new
// variants arrive with capabilities.
public abstract class FacePicture : IEquatable<FacePicture>
{
    public static FacePicture None { get; }                       // { "$type": "none" }
    public static GlyphPicture Glyph(string glyph);
    public abstract bool Equals(FacePicture? other);
}
public sealed class NoPicture : FacePicture { }
public sealed class GlyphPicture : FacePicture { public required string Glyph { get; init; } }    // "glyph"
public abstract class FaceLine : IEquatable<FaceLine>
{
    public static implicit operator FaceLine?(string? text);      // Line1 = "4:59" makes a TextLine
    public abstract bool Equals(FaceLine? other);
}
public sealed class TextLine : FaceLine { public required string Text { get; init; } }           // "text"

public sealed class HostIdentity { public required string Id { get; init; } public required string Version { get; init; } }

// Reason codes (§8). An open registry: any dotted code parses; IsKnown says whether this library's
// catalog has it. Unknown codes carry the disposition rules of §7.5.
public readonly record struct ReasonCode
{
    public string Value { get; }
    public bool IsKnown { get; }
    public ReasonCodeInfo Info { get; }
    public static ReasonCode AuthProofInvalid { get; }            // … one static property per code in §8.3 …
    public static bool TryParse(string? value, out ReasonCode code);   // true for any grammar-valid dotted code (§2.4)
    public override string ToString();                            // Value
}
public enum Disposition { Close = 1, Advisory = 2, Refuse = 3, Ignore = 4, Local = 5 }
public sealed class ReasonCodeInfo
{
    public required Disposition Disposition { get; init; }        // Close for an unknown code
    public required bool PreAuthentication { get; init; }         // false for an unknown code
    public required bool IsViolation { get; init; }               // false for an unknown code
    public string? Fix { get; init; }                             // author fix from reason-codes.json; null when unknown
    public string? HelpAnchor { get; init; }                      // "auth-proof-invalid"; null when unknown
    public Uri? HelpUri(string hostId);                           // https://dev.ventana.tools/go/<host-id>/codes#<anchor>
}

public sealed class ReadResult<T> where T : class
{
    public T? Value { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Succeeded { get; }                               // Value is not null and no Error
}

public sealed class Diagnostic                                   // the Ventana diagnostics shape (§4.1)
{
    public required string Code { get; init; }
    public required string Path { get; init; }                   // RFC 6901 JSON Pointer
    public required string Message { get; init; }
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;
    public string? File { get; init; }
    public long? Line { get; init; }
    public long? Column { get; init; }
    public override string ToString();                           // "code path: message"
}

public enum DiagnosticSeverity { Error = 1, Warning = 2 }
public static class DiagnosticCodes { public const string JsonSyntax = "json.syntax"; /* … every code in §4.4 … */ }
                                                                // pairing.* codes are ReasonCode values only (§8.3)

public sealed class ManifestReadOptions
{
    public string? HostId { get; init; }                         // enables manifest.host-not-listed
    public IReadOnlyCollection<string>? SupportedCapabilities { get; init; }  // enables requires.capability-unsupported
    public bool AllowExperimentalCapabilities { get; init; }
    public IdOrigin Origin { get; init; } = IdOrigin.ThirdParty;
    public IReadOnlyCollection<string> ReservedPublishers { get; init; } = ExtensionIds.ReservedPublishers;
    public IReadOnlyList<HostInfo> KnownHosts { get; init; } = HostRegistry.Known;  // manifest.host-unknown, schema.uri-mismatch
}

public static class ManifestReader
{
    public const int MaxBytes = 65_536;
    public static ReadResult<ExtensionManifest> Read(ReadOnlySpan<byte> utf8, ManifestReadOptions? options = null);
    public static Task<ReadResult<ExtensionManifest>> ReadFileAsync(string path, ManifestReadOptions? options = null,
        CancellationToken cancellationToken = default);         // bounded read, never past MaxBytes + 1
    public static ReadResult<ExtensionManifest> Validate(ExtensionManifest manifest, ManifestReadOptions? options = null);
}

public static class ManifestWriter
{
    public static byte[] Write(ExtensionManifest manifest, bool indented = true);
    public static string ComputeHash(ExtensionManifest manifest);  // §B.2
}

public sealed class StringsReadOptions
{
    public required ExtensionManifest Manifest { get; init; }
    public IReadOnlyList<HostInfo> KnownHosts { get; init; } = HostRegistry.Known;  // schema.uri-mismatch
}
public static class StringsReader
{
    public const int MaxBytes = 65_536;
    public static ReadResult<ExtensionStrings> Read(ReadOnlySpan<byte> utf8, string fileTag, StringsReadOptions options);
    public static Task<ReadResult<ExtensionStrings>> ReadFileAsync(string path, StringsReadOptions options,
        CancellationToken cancellationToken = default);         // the tag is the file name's
}
public sealed class ExtensionStrings                             // mirrors §3.10
{
    public string? Schema { get; init; }                         // "$schema"
    public int SchemaVersion { get; init; } = ExtensionManifest.CurrentSchemaVersion;
    public required string Language { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public PublisherStrings? Publisher { get; init; }
    public IReadOnlyDictionary<string, ContributionStrings> Contributions { get; init; }   // keyed by contribution id
}
public sealed class PublisherStrings { public required string Name { get; init; } }
public sealed class ContributionStrings
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, SettingStrings> Settings { get; init; }   // keyed by setting id
}
public sealed class SettingStrings
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string> Choices { get; init; }        // choice value → name
}

public enum IdOrigin { FirstParty = 1, ThirdParty = 2 }
public static class ExtensionIds
{
    public const int MaxLength = 128;
    public static IReadOnlyList<string> ReservedPublishers { get; }   // fixtures/reserved-publishers.json (§3.5)
    public static string? Classify(string? id, IdOrigin origin, IReadOnlyCollection<string>? reservedPublishers = null);
    public static bool IsValid(string? id, IdOrigin origin, IReadOnlyCollection<string>? reservedPublishers = null);
    public static IdOrigin OriginOf(string? id);
    public static bool IsInNamespace(string? id, string? root);
    public static bool IsSegment(ReadOnlySpan<char> value);
}                                                                // device-name first segments are refused whatever list is passed

public static class TextRules
{
    public static bool IsDisallowed(int codePoint);
    public static string? CheckDeclarationText(string? value, int maxUnits);   // null, or a text.* / string.* code
    public static string? Clean(string? text, int maxElements, int maxUnits);  // §7.7.3
    public static bool IsGlyph(string? value);
    public static bool IsSettingId(string? value);
    public static bool IsChoiceValue(string? value);
    public static bool IsHostId(string? value);
    public static bool IsLanguageTag(string? value);
    public static bool IsHttpsUrl(string? value);
}

public enum HostStatus { Active = 1, Reserved = 2 }
public sealed class HostInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? PackageExtension { get; init; }                // null while a host has none
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required HostStatus Status { get; init; }
}

public static class HostRegistry                                 // fixtures/hosts.json, embedded (§2.1, §2.3)
{
    public static IReadOnlyList<HostInfo> Known { get; }
    public static IReadOnlyList<string> ReservedIds { get; }
    public static HostInfo? Find(string id);                      // matches ids and aliases
    public static HostInfo? FirstActive(IEnumerable<string> hostIds, IReadOnlyList<HostInfo>? knownHosts = null);
}

public enum PairingMode { Persistent = 1, PerLaunch = 2 }
public sealed class PairingReadOptions
{
    public required string ExpectedExtensionId { get; init; }
    public IReadOnlyCollection<string>? ManifestHosts { get; init; }
}

public static class PairingReader
{
    public const int MaxBytes = 4096;
    public static ReadResult<Pairing> Read(ReadOnlySpan<byte> utf8, PairingReadOptions options);
    public static Task<ReadResult<Pairing>> ReadFileAsync(string path, PairingReadOptions options,
        CancellationToken cancellationToken = default);
    public static string DefaultPath(string hostId, string extensionId);   // §6.6
}

public sealed class Pairing : IDisposable                        // whoever reads it owns it and disposes it
{
    public int PairingVersion { get; }
    public PairingMode Mode { get; }
    public string HostId { get; }
    public string PipeName { get; }
    public string RegistrationId { get; }
    public string ExtensionId { get; }
    public string ComputeProof(Wire.HandshakeTranscript transcript, Wire.ProofRole role);
    public bool VerifyProof(string? proof, Wire.HandshakeTranscript transcript, Wire.ProofRole role);
    public void Dispose();                                        // zeroes the secret
    public override string ToString();                            // never includes the secret
}
```

Every `ReadFileAsync` reads at most its limit plus one byte. File-system failures
(a missing file, a sharing violation, denied access) surface as the file APIs'
own exceptions (`IOException` and its subclasses, `UnauthorizedAccessException`);
problems with the content are always diagnostics, never exceptions.

```csharp
namespace VentanaTools.Orbit.Extensions.Packaging;

public sealed class PackageDescriptor { public int ArchiveVersion { get; } public string ManifestHash { get; } public IReadOnlyList<PackageFileEntry> Files { get; } public PackageCreator? CreatedBy { get; } }
public sealed class PackageCreator { public required string Name { get; init; } public required string Version { get; init; } }   // createdBy (§5.6)
public sealed class PackageFileEntry { public required string Path { get; init; } public required long Size { get; init; } public required string Sha256 { get; init; } }
public sealed class PackageFile { public string Path { get; } public long Length { get; } public Stream OpenRead(); }
public sealed class ExtensionPackage
{
    public ExtensionManifest Manifest { get; }
    public PackageDescriptor Descriptor { get; }
    public IReadOnlyList<ExtensionStrings> Strings { get; }
    public string Readme { get; }
    public string Sha256 { get; }                                 // package hash
    public IReadOnlyList<PackageFile> Files { get; }
}
public sealed class PackageReadOptions { public ManifestReadOptions Manifest { get; init; } = new(); }
public static class PackageReader
{
    public const long MaxArchiveBytes = 32L * 1024 * 1024;
    public static ReadResult<ExtensionPackage> Read(ReadOnlyMemory<byte> archive, PackageReadOptions? options = null);
    public static Task<ReadResult<ExtensionPackage>> ReadFileAsync(string path, PackageReadOptions? options = null,
        CancellationToken cancellationToken = default);
}
public static class PackageWriter
{
    public static byte[] Write(PackageContents contents);       // writes extension.package.json itself
}
public sealed class PackageContents
{
    public required ReadOnlyMemory<byte> Manifest { get; init; }  // extension.json
    public required ReadOnlyMemory<byte> Readme { get; init; }    // README.md
    public IReadOnlyList<PackageContentFile> Strings { get; init; } = [];   // paths under strings/
    public IReadOnlyList<PackageContentFile> Payload { get; init; } = [];   // paths under payload/
    public PackageCreator? CreatedBy { get; init; }
}
public sealed class PackageContentFile { public required string Path { get; init; } public required ReadOnlyMemory<byte> Content { get; init; } }
public static class ZoneOfOrigin
{
    public static int? Read(string packagePath);                  // §5.8 item 5: null when no stream; 3 when unreadable or out of range
    public static string? ReadHostUrl(string packagePath);        // HostUrl, only when §5.8 item 5 allows copying it
    public static void Apply(string extractedFilePath, int zone, string packagePath, string? hostUrl);
}
```

`PackageWriter.Write` writes the descriptor itself, deterministically: entries
sorted ordinally, every timestamp 1980-01-01, external attributes 0 for files and
0x10 for folders, folder entries included, and a file deflated only when that
makes it smaller. It throws `ArgumentException` for an invalid manifest, a path
outside the entry grammar or conflicting paths; the limits of §5.3 are left to
verification.

```csharp
namespace VentanaTools.Orbit.Extensions.Wire;

public static class ProtocolVersions { public const int Min = 3; public const int Max = 3; public const string Label = "Ventana.Extensions.v3"; }
public static class Framing
{
    public const int MaxFrameBytes = 65_536;
    public const int MaxHandshakeFrameBytes = 8_192;              // every frame until ready (§7.2)
    public static readonly TimeSpan PartialFrameTimeout;          // 5 s
    public static ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, TimeProvider time,
        CancellationToken cancellationToken);
    public static ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}
public sealed class FrameException : IOException { public ReasonCode Code { get; } }   // internal constructor
                                                                // ReadFrameAsync: frame.too-large or frame.timeout
public static class PipeNames
{
    public const string Prefix = "Ventana.Extensions.v3.";
    public static string Create(string hostId, string edition, string userHash, string registrationId);
    public static bool TryParse(string? value, [NotNullWhen(true)] out PipeNameParts? parts);
    public static string UserHash(string userSid);
}
public sealed class PipeNameParts
{
    public required string HostId { get; init; }
    public required string Edition { get; init; }
    public required string UserHash { get; init; }
    public required string RegistrationId { get; init; }
}

public enum ProofRole { Server = 1, Client = 2 }
public sealed class HandshakeTranscript                          // §7.3.4
{
    public required string HostId { get; init; }
    public required string HostVersion { get; init; }
    public required string RegistrationId { get; init; }
    public required string ClientNonce { get; init; }
    public required string ServerNonce { get; init; }
    public required int Version { get; init; }
    public required int MinVersion { get; init; }
    public required int MaxVersion { get; init; }
    public required IReadOnlyCollection<string> ClientCapabilities { get; init; }
    public required IReadOnlyCollection<string> HostCapabilities { get; init; }
    public required string ManifestHash { get; init; }
    public byte[] ToBytes(ProofRole role);
}
public static class Handshake
{
    public static string NewNonce();
    public static int? Negotiate(int clientMin, int clientMax, int hostMin, int hostMax);
    public static string ComputeProof(ReadOnlySpan<byte> secret, HandshakeTranscript transcript, ProofRole role);
    public static bool VerifyProof(ReadOnlySpan<byte> secret, string? proof, HandshakeTranscript transcript, ProofRole role);
}
public static class Capabilities { public const string PairingPerLaunch = "pairing.per-launch"; public const string IdentityPackage = "identity.package"; /* … §7.4 … */ public static bool IsExperimental(string id); }

public abstract class WireMessage { public abstract string Type { get; } }   // closed: private protected constructor
```

Every message is a sealed class deriving from `WireMessage`, with one `required`
(or, for optional members, nullable `init`) property per JSON member of §7.5,
named as the member in PascalCase:

| Class | `Type` | Properties |
|---|---|---|
| `HelloMessage` | `hello` | `MinVersion`, `MaxVersion`, `RegistrationId`, `ClientNonce`, `Capabilities` (`IReadOnlyList<string>`), `Client` (`ClientInfo`), `ManifestHash` |
| `ChallengeMessage` | `challenge` | `ServerNonce`, `Version`, `Capabilities`, `Host` (`HostIdentity`), `Proof` |
| `AuthenticateMessage` | `authenticate` | `Proof` |
| `ReadyMessage` | `ready` | `Version`, `Capabilities`, `Host`, `UiLanguage`, `Limits` (`HostLimits`) |
| `StartSessionMessage` | `startSession` | `SessionId`, `ContributionId`, `Settings` (`IReadOnlyDictionary<string, string>`) |
| `StopSessionMessage` | `stopSession` | `SessionId` |
| `InvokeMessage` | `invoke` | `RequestId`, `SessionId` |
| `CancelMessage` | `cancel` | `RequestId` |
| `SetFaceMessage` | `setFace` | `SessionId`, `Face` (`WireFace`) |
| `ClearFaceMessage` | `clearFace` | `SessionId` |
| `FailMessage` | `fail` | `SessionId`, `Failure` |
| `ResultMessage` | `result` | `RequestId`, `Outcome`, `Failure?` |
| `SessionRefusedMessage` | `sessionRefused` | `SessionId`, `Code` (`ReasonCode`, one of the three of §7.5) |
| `PingMessage` | `ping` | `Id` |
| `PongMessage` | `pong` | `Id` |
| `ErrorMessage` | `error` | `Code` (`ReasonCode`), `Message?`, `SessionId?`, `RequestId?`, `RetryAfterMs?`, `Supported?` (`VersionRange`) |

The reserved `hello` member `credential` (§6.5) has no property in this version.

```csharp
public sealed class WireFace
{
    public FacePicture Picture { get; init; } = FacePicture.None;
    public FaceLine? Line1 { get; init; }
    public FaceLine? Line2 { get; init; }
    public FaceState State { get; init; } = FaceState.None;
    public string? Detail { get; init; }
    public required int GoodForSeconds { get; init; }
}
public sealed class ClientInfo { public required string Name { get; init; } public required string Version { get; init; } }
public sealed class VersionRange { public required int MinVersion { get; init; } public required int MaxVersion { get; init; } }
public sealed class HostLimits                                   // §7.10
{
    public required int MaxFrameBytes { get; init; }
    public required int MaxSessions { get; init; }
    public required int MaxPendingInvokes { get; init; }
    public required int InvokeTimeoutMs { get; init; }
    public required int FaceChangesPerSecond { get; init; }
    public required int FaceBurst { get; init; }
    public required int MessageRate { get; init; }
    public required int MessageBurst { get; init; }
    public required int HardMessageRate { get; init; }
    public required int HardMessageBurst { get; init; }
    public required int PingIntervalMs { get; init; }
    public required int PongTimeoutMs { get; init; }
    public static HostLimits Protocol3Defaults { get; }
    public bool IsWithinRanges();                                 // every range and cross-member rule of §7.10
    public HostLimits ForCompanion();                             // smaller or larger of each value and the defaults
}

public enum Sender { Host = 1, Companion = 2 }
public enum ConnectionPhase
{
    Handshake = 1,                                                // before the receiver verified its peer's proof
    PeerVerified = 2,                                             // after that, until ready
    Authenticated = 3,                                            // after ready
}
public sealed class MessageReadResult
{
    public WireMessage? Message { get; }
    public ReasonCode? Violation { get; }                         // the close code, when the frame is refused
    public ReasonCode? Ignored { get; }                           // protocol.type-unknown and the like, when discarded
}
public static class MessageReader { public static MessageReadResult Read(ReadOnlySpan<byte> frame, Sender sender, ConnectionPhase phase); }
public static class MessageWriter { public static byte[] Write(WireMessage message); }

public sealed class TokenBucket                                  // §B.3
{
    public TokenBucket(double ratePerSecond, int burst, TimeProvider time);
    public bool TryTake(int tokens = 1);
    public TimeSpan RetryAfter { get; }
    public static int CostOf(int frameBytes);                     // one token per started 1,024 bytes (§7.10)
}
```

`ReadFrameAsync` throws `FrameException` (`Code` `frame.too-large` or
`frame.timeout`) and `EndOfStreamException` when the stream ends inside a frame;
it returns null at a clean end of stream. `WriteFrameAsync` throws
`ArgumentOutOfRangeException` for an empty or over-long body, which is a caller
bug; callers enforce `frame.write-timeout` around it.
`HandshakeTranscript.ToBytes` refuses values that contain LF or a comma, and
`TokenBucket.CostOf` refuses lengths below 1.

`MessageWriter` writes the canonical form: compact JSON, `type` first, then the
members in the order of §7.5's tables, `picture` and `state` always written,
`settings` keys sorted ordinally, and every character outside printable ASCII
escaped as a JSON `\uXXXX` escape with uppercase hexadecimal digits, as in the
example of §7.7.1.

### 9.2 Companion client

```csharp
namespace VentanaTools.Orbit.Extensions;

public static class CompanionApp
{
    public static Task<int> RunAsync(string[] args, IContributionHandler handler,
        CompanionAppOptions? options = null, CancellationToken cancellationToken = default);
    public static CompanionArguments ParseArguments(string[] args);
}

public sealed class CompanionArguments
{
    public string? ManifestPath { get; init; }                    // --manifest <path>
    public string? PairingPath { get; init; }                     // --pairing <path>
    public bool Verbose { get; init; }                            // --verbose
    public IReadOnlyList<string> Remaining { get; init; } = [];   // everything else, for the author
}

public sealed class CompanionAppOptions
{
    public string? ManifestPath { get; init; }                    // overrides --manifest
    public string? PairingPath { get; init; }                     // overrides --pairing
    public bool WatchFiles { get; init; } = true;                 // pairing file and extension.json
    public TextWriter? Output { get; init; }                      // default Console.Error; TextWriter.Null to silence
    public Action<StatusChangedEventArgs>? StatusChanged { get; init; }
    public Action<HandlerFaultedEventArgs>? HandlerFaulted { get; init; }
    public CompanionClientOptions? Client { get; init; }
}

public sealed class CompanionClient
{
    public CompanionClient(Pairing pairing, ExtensionManifest manifest, IContributionHandler handler,
        CompanionClientOptions? options = null);                 // the caller keeps ownership of pairing
    public ConnectionState State { get; }
    public event EventHandler<StatusChangedEventArgs>? StatusChanged;
    public event EventHandler<HandlerFaultedEventArgs>? HandlerFaulted;
    public Task RunAsync(CancellationToken cancellationToken);   // completes when cancelled or Stopped
}

public sealed class CompanionClientOptions
{
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HostAbsentMaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);   // host.not-running, host.turned-off
    public double RetryJitter { get; init; } = 0.2;               // each delay × uniform(1 − j, 1 + j)
    public TimeSpan StableConnection { get; init; } = TimeSpan.FromSeconds(60);         // resets the backoff
    public TimeSpan HandlerStopTimeout { get; init; } = TimeSpan.FromSeconds(5);        // IgnoredCancellation after this
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public enum ConnectionState { NotStarted = 1, Connecting = 2, Connected = 3, Waiting = 4, Stopped = 5 }

public sealed class StatusChangedEventArgs : EventArgs
{
    public ConnectionState State { get; }
    public ReasonCode? Reason { get; }
    public TimeSpan? RetryIn { get; }
    public int Attempt { get; }
    public bool ServerVerified { get; }                           // §1.4, for the attempt that produced this status
    public HostIdentity? Host { get; }
    public int? ProtocolVersion { get; }
}

public enum HandlerFault { Exception = 1, IgnoredCancellation = 2, SessionCapacity = 3, InvalidResult = 4 }
public sealed class HandlerFaultedEventArgs : EventArgs
{
    public HandlerFault Kind { get; }
    public string ContributionId { get; }
    public string? SessionId { get; }
    public string? RequestId { get; }
    public Exception? Exception { get; }                          // the author's own exception, for the author only
}

public interface IContributionHandler                            // for advanced use; prefer ContributionHandler
{
    Task RunSessionAsync(Session session, CancellationToken cancellationToken);
    Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken);
}

public abstract class ContributionHandler : IContributionHandler
{
    public virtual Task RunSessionAsync(Session session, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(InvokeResult.Unsupported);
}

public sealed class ContributionRouter : IContributionHandler
{
    public ContributionRouter MapSession(string contributionId, Func<Session, CancellationToken, Task> run);
    public ContributionRouter MapInvoke(string contributionId, Func<Invocation, CancellationToken, Task<InvokeResult>> invoke);
    public ContributionRouter Map(string contributionId, IContributionHandler handler);
    public IReadOnlyList<string> FindUnmapped(ExtensionManifest manifest);   // CompanionApp prints these at start
}

public sealed class Session
{
    public string Id { get; }
    public string ContributionId { get; }
    public Provides Provides { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
    public string UiLanguage { get; }                             // ready.uiLanguage, as the host sent it
    public CultureInfo UiCulture { get; }                         // from UiLanguage; InvariantCulture when the runtime cannot create it
    public IReadOnlyCollection<string> HostCapabilities { get; }  // the connection's effective capabilities (§7.3.5)
    public bool Supports(string capabilityId);
    public TimeProvider Time { get; }                             // the client's TimeProvider, for testable handlers
    public bool IsActive { get; }
    public PublishResult SetFace(Face face);
    public PublishResult ClearFace();
    public PublishResult Fail(Failure failure);
}

public sealed class Invocation { public string RequestId { get; } public Session Session { get; } }

public readonly struct InvokeResult : IEquatable<InvokeResult>   // no public constructor beyond the implicit default
{
    public Outcome Outcome { get; }
    public Failure? Failure { get; }
    public static InvokeResult Done { get; }
    public static InvokeResult Refused { get; }
    public static InvokeResult Unsupported { get; }
    public static InvokeResult Failed(Failure? failure = null);
}

public sealed record Face
{
    public FacePicture Picture { get; init; } = FacePicture.None;
    public FaceLine? Line1 { get; init; }                         // a string converts: Line1 = "4:59"
    public FaceLine? Line2 { get; init; }
    public string? Detail { get; init; }
    public FaceState State { get; init; } = FaceState.None;
    public required TimeSpan GoodFor { get; init; }              // 1 s – 1 day; rounded up to whole seconds
    public bool Renew { get; init; }                              // see Faces below
}

public enum PublishResult { Accepted = 1, SessionEnded = 2 }
```

Behaviour the SDK guarantees:

- **Platform.** The library targets `net10.0`; `CompanionApp.RunAsync` and
  `CompanionClient.RunAsync` are marked `[SupportedOSPlatform("windows")]`
  because the transport is a Windows named pipe. Everything else in the
  package (declarations, diagnostics, pairing, packaging and wire) is portable.
- **Pipe and server verification.** The client connects with
  `PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous` and
  `TokenImpersonationLevel.Identification`, so it never writes to a pipe the
  current user does not own (`auth.server-unverified`). Before writing `hello`
  it checks the server process's user and integrity level and the pipe's
  mandatory label (§7.1). When both checks pass, the server is verified from
  the start. When it reads the server's token and finds another user or a
  lower integrity level, or the pipe's label is lower than §7.1 allows or
  cannot be read, it closes the pipe without writing anything
  (`auth.server-unverified`, then `Waiting` with normal backoff). When it
  cannot open or read the server process or its token but the label is no
  lower than the companion's own level, the server becomes verified only when
  its challenge proof verifies. `StatusChangedEventArgs.ServerVerified`
  reports which.
- **Status and backoff.** `StatusChanged` fires on every state change with the
  reason code. Normal backoff is 1 s, doubling, × uniform(0.8, 1.2), and never
  more than `MaxRetryDelay` (30 s): each delay is min(base × uniform(1 − j,
  1 + j), cap), where the cap is `MaxRetryDelay`, or `HostAbsentMaxRetryDelay`
  after `host.not-running` and `host.turned-off`. The backoff resets after 60
  seconds connected, which also restores `host.reloaded`'s one immediate retry.
  The retry delay starts before `Waiting` is raised, so `RetryIn` counts from
  the moment the status reports. `Connecting` and `Connected` carry no reason;
  `Host` and `ProtocolVersion` are set once the challenge verified. A connection
  that the peer closes without an `error`, and a client stopped by cancellation,
  have no reason code: `Reason` is null. The reason decides the next state:

  | Reason | Next state and retry |
  |---|---|
  | any `pairing.*` code | `Stopped`; `CompanionApp` waits for the pairing file to change |
  | `auth.registration-mismatch`, `protocol.version-unsupported`, `auth.host-mismatch`, `auth.server-proof-invalid` | from a verified server: `Stopped`, because retrying cannot help until the pairing or a version changes; from a server that is not verified: `Waiting`, normal backoff |
  | `auth.proof-invalid`, `host.access-revoked` | `Stopped` (they only arrive after the challenge verified, so the server is verified) |
  | `manifest.mismatch`, `auth.identity-changed` | `Waiting`, retrying every `MaxRetryDelay`; `CompanionApp` re-reads `extension.json` before each retry, and retries at once when `extension.json` or the pairing file changes. A re-read that fails, or finds an invalid file, leaves the current manifest; a stop or restart that lands during it ends the run with `Stopped` and no reason, never an error |
  | `host.not-running`, `host.turned-off` | `Waiting`, backoff capped at `HostAbsentMaxRetryDelay` (5 s), because probing a missing pipe costs the host nothing |
  | `host.paused`, or an unknown code with `retryAfterMs` | `Waiting` for at least `retryAfterMs`: from a verified server max(normal, min(`retryAfterMs`, 300,000 ms)); otherwise min(max(normal, `retryAfterMs`), `MaxRetryDelay`) |
  | `host.reloaded` | `Connecting` at once, once; then normal backoff |
  | a connection that closes without an `error` (no reason) | `Waiting`, normal backoff |
  | any other code, known or unknown, and `auth.server-unverified` | `Waiting`, normal backoff |
  | cancellation | `Stopped`, with no reason |

  A code that a server which is not verified sends can therefore never stop the
  SDK or keep it waiting longer than `MaxRetryDelay`: a program that squats on
  the pipe name while the host is not running gains nothing. A `Connecting`
  status carries no reason. After `host.reloaded` the SDK goes to `Connecting`
  without a `Waiting` status, so the code itself is not reported.
- **Faces.** `SetFace`, `ClearFace` and `Fail` check, in order:
  1. their arguments, throwing `ArgumentException`. `SetFace` validates only
     `GoodFor` (1 second to 1 day), the picture (the glyph rule of §3.7) and that
     `State` is a defined value. It never throws because of what `Line1`,
     `Line2` or `Detail` contain or how long they are: the SDK cleans them with
     `TextRules.Clean` and the limits of §7.7.1 before sending, exactly as a host
     would, so data-driven text (titles, calendar entries, flag emoji) can never
     crash a widget. `Fail` throws for a token whose capability is not effective
     (§7.8);
  2. that every variant used is available, throwing `NotSupportedException` for
     a picture or line variant whose capability (for example `face.image`) is not
     effective on this connection, so an author's mistake never closes the
     connection (§7.4);
  3. the contribution's `provides`, throwing `InvalidOperationException` when it
     lacks `face`;
  4. the session's state: they return `SessionEnded` after the session ended and
     `Accepted` otherwise.

  At most one face command per session is pending: a newer one replaces it, so
  backpressure never surfaces as a failure. The SDK paces face commands
  (`setFace`, `clearFace`, `fail`) to half the soft budget of §7.10 (64 tokens
  per second, burst 128, at protocol-3 values, and never a burst below 8 tokens,
  so a maximal face frame can always be paid for) and sends at most one face
  command per session every 1/`faceChangesPerSecond` seconds; so 100 quick
  `SetFace` calls send the first and the newest. Other frames (`result`,
  `sessionRefused`, `ping`, `pong`, `error`) go first and are not paced. A
  `rate.throttled` with `retryAfterMs` holds face commands for that long.
- **Renewal.** A face published with `Renew = true` stays current without a
  timer in author code: the SDK republishes it at 80% of its lifetime while
  (a) the session is active, (b) the session's `RunSessionAsync` is still
  running and its token is not cancelled, and (c) the author has published
  nothing newer for the session, and for at most one day after the author's
  `SetFace`, the longest lifetime one publish could claim. A handler that
  stops, throws or hangs before publishing again therefore lets the face go
  stale as it should. A handler that wants renewal keeps running, for example by
  awaiting its events and then `Task.Delay(Timeout.Infinite, cancellationToken)`.
- **Handlers.** Each session handler and invocation runs off the pipe reader.
  - An `OperationCanceledException` (including `TaskCanceledException`) that a
    handler throws after its token was cancelled is a **normal completion**: no
    `HandlerFaulted`, no face change, and an invocation that saw `cancel` sends
    no `result`.
  - Any other exception from a session handler raises `HandlerFaulted`
    (`Exception`) with the exception and, for a face contribution, sends
    `clearFace` for the session (§7.8). A session stays invocable after
    `RunSessionAsync` returns or throws, until the host stops it.
  - Any other exception from an invocation raises `HandlerFaulted` and answers
    `result` `Failed` with no `failure`.
  - An invalid `InvokeResult` (the default value, whose `Outcome` is 0) is
    answered as `Failed` and raises `HandlerFaulted` (`InvalidResult`). The SDK
    never sends a frame the host would close on, such as a failure with an
    outcome other than `Failed`.
  - An `invoke` for a session the companion does not know, for a contribution
    that does not provide `invoke`, beyond the companion's `maxPendingInvokes`,
    or while 32 invocation handlers still run across reconnects, is answered
    `result` `Refused`.
  - `stopSession` cancels that session's running invocations; one that then
    ends with `OperationCanceledException` is answered `Refused`. After the
    host's `cancel`, nothing is sent for that request, even if the handler
    returns a result. A `stopSession` for an unknown id is ignored, and refused
    session ids enter the replay window.
  - The face of a session handler that threw is cleared through the same pacing
    as the author's own face commands.
- **Invocation deadline.** The SDK's deadline is the companion's
  `invokeTimeoutMs` (§7.10) minus 3 seconds: 12 seconds at protocol-3 values,
  and never less than 1 second because the range starts at 4,000 ms. At its
  deadline the SDK cancels the handler's token and answers `result` `Failed`
  unless the handler has already returned. A handler still running
  `HandlerStopTimeout` (5 seconds) after cancellation raises `HandlerFaulted`
  (`IgnoredCancellation`). The Testing package uses the same values.
- **Session cap.** Running handlers are counted across reconnects, at most 64.
  At the cap the SDK sends `sessionRefused` `session.capacity` for that one
  session and raises `HandlerFaulted` (`SessionCapacity`). The connection stays
  up. A host that opens more than the companion's `maxSessions` sessions gets
  `sessionRefused` `session.capacity` too, with no `HandlerFaulted`.
- **Replay window** of 1,024 ids (§7.6.3); no cap that grows with uptime.
- **Liveness.** `ping` is answered on the reader. The SDK pings after its
  `pingIntervalMs` (§7.10) without a frame, and enforces the one-outstanding
  rule of §7.9 against the host.
- **Events.** `StatusChanged` and `HandlerFaulted` are raised on the thread
  pool, one at a time and in order, never on the pipe reader, so a slow
  subscriber cannot stall pongs. A subscriber that throws is caught, reported
  once (by `CompanionApp`, to `Output`), and never stops the client. A
  subscriber that touches UI must marshal to its own thread.
- **Implicit usings.** The package adds `VentanaTools.Orbit.Extensions` as a
  global using only when the project sets `VentanaToolsImplicitUsings` to
  `true`. The templates set it; nothing else in this document depends on it.
- **Client identity.** `hello.client` carries the author package's assembly
  name and informational version, read from the assembly at run time, without
  build metadata when the whole value does not fit `[0-9A-Za-z.+-]{1,32}` (a
  SourceLink build appends `+` and a 40-digit commit), and cut to its allowed
  characters and 32 of them if even that does not fit. The SDK offers
  `x.test-echo`, which does nothing, and no other capability, so a connection's
  effective capabilities are at most `x.test-echo` until a later SDK implements
  one.

**`CompanionApp`.**

- **Arguments.** It recognizes `--manifest <path>`, `--pairing <path>` and
  `--verbose`. Every other argument is left for the author
  (`ParseArguments(args).Remaining`). A recognized option without its value
  prints `ventana: <option> needs a value.` (for example
  `ventana: --manifest needs a value.`, the same line the Node SDK prints) and
  exits 2; `ParseArguments` throws `ArgumentException` for it.
- **Manifest.** `--manifest`, else `extension.json` in `AppContext.BaseDirectory`,
  else in the current directory. It is read with `ManifestReader.ReadFileAsync`.
  An invalid manifest prints one line per error diagnostic,
  `ventana: <file>: <code> <pointer>: <message>`; with `WatchFiles` on, the app
  then waits for the manifest to change.
- **Pairing.** `--pairing`, else the per-user default of §6.6 for each active
  host id in the manifest's `hosts`, in order, else `<extension-id>.pairing.json`
  in `AppContext.BaseDirectory`, then in the current directory. It is read with
  `PairingReader.ReadFileAsync`, and `CompanionApp` disposes it.
- **First run.** When no pairing file exists, or the one found is invalid, and
  `WatchFiles` is on, it prints the code (`pairing.missing` or the reader's
  code), the exact path it watches (the first default path), and the host
  action that writes it, for example `In Orbit, choose Save connection info`,
  then waits for the file to appear or change. It exits 3 only when
  `WatchFiles` is off.
- **Watching.** With `WatchFiles` on, it restarts the client when the pairing
  file changes after `Stopped`, and re-reads `extension.json` before each retry
  after `manifest.mismatch` and whenever the file changes. It polls the watched
  files (existence, last write time and length) every second on the client's
  `TimeProvider`, because the per-user pairing folder may not exist yet; a
  change counts from when the file was last read, and every file is stamped
  before it is read. While the client waits on `manifest.mismatch` or
  `auth.identity-changed`, a manifest that differs from the one read retries at
  once and a pairing that differs restarts the client with it, however long ago
  it changed.
- **Output.** One status line per change, to `Output`:
  `ventana: <state> (<code>) <fix> <help link>`, or `ventana: <state>` alone for
  a status without a reason, where the fix and help link come from
  `ReasonCodeInfo`. The catalog's "the host" is rendered with the display name
  (from `HostRegistry`) of the pairing's host id, or, before a pairing is read,
  of the manifest's first active host, for example
  `ventana: waiting (auth.identity-changed) In Orbit, allow the new
  program or revoke access. https://dev.ventana.tools/go/orbit/codes#auth-identity-changed`.
  When the registry knows neither (a mistyped id, or the test host), the line
  keeps "the host" and carries no help link, which would lead nowhere:
  `ventana: waiting (pairing.missing) In the host, choose Save connection info.`
  The Node SDK prints the same lines.
  For each `HandlerFaulted` it prints
  `ventana: fault <Kind> in <contribution-id> (<code>) <fix> <help link>`, with
  the code `session.handler-faulted`, `session.handler-stalled` or
  `session.capacity`, followed by the exception's `ToString()` (the author's own
  process and code). A `StatusChanged` or `HandlerFaulted` callback that throws
  is reported once. It never prints pairing contents, and never prints a peer's
  `error.message` except, with `--verbose`, as
  `ventana: host message: <text>`, cleaned to printable ASCII. It also calls the
  `StatusChanged` and `HandlerFaulted` callbacks of `CompanionAppOptions`.
- **Ctrl+C** cancels and exits 0.
- **Exit codes:** 0 stopped by Ctrl+C or cancellation; 1 unexpected; 2 usage;
  3 a file is missing or invalid with `WatchFiles` off; 4 `Stopped` with
  `WatchFiles` off.

### 9.3 `VentanaTools.Orbit.Extensions.Testing`

The package works with any test framework and depends on no assertion library.
Its names follow the Ventana test-kit convention (§2.7): it parallels Lollipop's
test kit where the concept is the same
(`RecordingExtensionContext` → `RecordingSession`, `DescriptionTranscript` →
`CompanionTranscript`, `ToolbarActionContractSuite` →
`ContributionContractSuite`, `ExtensionConformance` → `ExtensionConformance`),
and uses Lollipop's method names and shapes for the conformance entry points:
manifest objects in, an optional `CancellationToken` last,
`AssertAuthoringContractAsync`, and a failure exception that is an
`InvalidOperationException`. The remaining deliberate differences, recorded with
the Ventana conventions (§2.7), are: a suite covers one extension
(`Manifest` and `CreateHandler`) where Lollipop's lists `Extensions` and
`Actions`; validation options are `ManifestReadOptions` where Lollipop has
`ManifestValidationContext`; and file-path conveniences carry `File` in their
names.

```csharp
namespace VentanaTools.Orbit.Extensions.Testing;

public static class TestSessions
{
    public static RecordingSession FromManifest(ExtensionManifest manifest, string contributionId,
        IReadOnlyDictionary<string, string>? settings = null, TimeProvider? time = null, string uiLanguage = "en-US");
        // fills defaults, derives Provides from the manifest, validates the settings and the language tag
    public static RecordingSession Create(string contributionId, Provides provides,
        IReadOnlyDictionary<string, string>? settings = null, string uiLanguage = "en-US", TimeProvider? time = null);
}

public sealed class RecordingSession : IDisposable
{
    public Session Session { get; }
    public IReadOnlyList<RecordedPublication> Publications { get; }
    public Face? LastFace { get; }                                // as cleaned for sending
    public CancellationToken Cancellation { get; }
    public void Stop();                                           // ends the session as stopSession would
    public Task RunAsync(IContributionHandler handler, TimeSpan? timeout = null);
    public Task<Face> WaitForFaceAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    public Task WaitForPublicationsAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default);
    public void Dispose();
}
public enum PublicationKind { SetFace = 1, ClearFace = 2, Fail = 3, AfterStop = 4 }
public sealed class RecordedPublication
{
    public required PublicationKind Kind { get; init; }
    public Face? Face { get; init; }                              // the cleaned face; Face is a record, so it compares by value
    public Failure? Failure { get; init; }
    public required TimeSpan At { get; init; }
}

public static class TestInvocations
{
    public static Invocation Create(RecordingSession session, string? requestId = null);
}

public sealed class CompanionTestHostOptions
{
    public string? HostId { get; init; }                          // null: the first active registry host in the
                                                                  // manifest's hosts, else its first host (never a literal)
    public HostLimits Limits { get; init; } = HostLimits.Protocol3Defaults;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string UiLanguage { get; init; } = "en-US";
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed class CompanionTestHost : IAsyncDisposable          // a real CompanionClient over an in-memory duplex stream
{
    public static Task<CompanionTestHost> StartAsync(ExtensionManifest manifest, IContributionHandler handler,
        CompanionTestHostOptions? options = null, CancellationToken cancellationToken = default);
    public Task<TestHostSession> StartSessionAsync(string contributionId,
        IReadOnlyDictionary<string, string>? settings = null, CancellationToken cancellationToken = default);
    public TestInvocation StartInvoke(TestHostSession session);   // sends invoke and returns at once
    public Task CancelAsync(TestInvocation invocation);           // sends cancel for it
    public Task<TestInvokeOutcome> InvokeAsync(TestHostSession session,
        CancellationToken cancellationToken = default);           // StartInvoke, then Result; cancelling the token sends cancel
    public Task StopSessionAsync(TestHostSession session);
    public Task DisconnectAsync(ReasonCode? code = null);
    public Task<ReceivedFace> WaitForFaceAsync(TestHostSession session, TimeSpan timeout,
        CancellationToken cancellationToken = default);
    public CompanionTranscript Transcript { get; }
    public IReadOnlyList<StatusChangedEventArgs> Statuses { get; }
    public IReadOnlyList<HandlerFaultedEventArgs> Faults { get; }
}
public sealed class TestHostSession
{
    public string SessionId { get; }
    public string ContributionId { get; }
    public ReasonCode? RefusedCode { get; }                       // null when the companion accepted the session
}
public sealed class TestInvocation { public string RequestId { get; } public Task<TestInvokeOutcome> Result { get; } }
public enum TestInvokeOutcomeKind { Done = 1, Refused = 2, Failed = 3, Unsupported = 4, Cancelled = 5, TimedOut = 6 }
public sealed class TestInvokeOutcome
{
    public required TestInvokeOutcomeKind Kind { get; init; }     // Cancelled: cancel sent, no result counted;
    public Failure? Failure { get; init; }                        // TimedOut: no result within invokeTimeoutMs
}
public sealed class ReceivedFace
{
    public required Face Face { get; init; }                      // as a host would hold it after cleaning (§7.7.3)
    public required TimeSpan ArrivedAt { get; init; }
}
public sealed class CompanionTranscript { public IReadOnlyList<TranscriptEntry> Entries { get; } public string ToJsonLines(); }
public sealed class TranscriptEntry
{
    public required TimeSpan At { get; init; }
    public required Sender From { get; init; }
    public required string Type { get; init; }
    public string? SessionId { get; init; }
    public string? RequestId { get; init; }
}

public abstract class ContributionContractSuite
{
    protected abstract ExtensionManifest Manifest { get; }
    protected abstract IContributionHandler CreateHandler();
    protected virtual IEnumerable<string> InvokeContributions => [];          // runs the REAL action; opt in per id
    protected virtual IReadOnlyDictionary<string, string>? InvokeSettings(string contributionId) => null;  // null: defaults
    protected virtual TimeSpan InitialFaceTimeout => TimeSpan.FromSeconds(2);
    protected virtual TimeSpan CancellationBound => new CompanionClientOptions().HandlerStopTimeout;   // 5 s
    protected virtual int MaxSettingCombinations => 64;
    protected virtual ManifestReadOptions ManifestValidation => new();
    public Task AssertAllAsync(CancellationToken cancellationToken = default);   // throws ConformanceException listing every failure
    public Task<IReadOnlyList<string>> CollectFailuresAsync(CancellationToken cancellationToken = default);
}

public static class ExtensionConformance
{
    public static IReadOnlyList<string> CollectManifestFailures(ExtensionManifest manifest, ManifestReadOptions? options = null);
    public static void AssertManifestValid(ExtensionManifest manifest, ManifestReadOptions? options = null);
    public static IReadOnlyList<string> CollectManifestFileFailures(string manifestPath, ManifestReadOptions? options = null);
    public static void AssertManifestFileValid(string manifestPath, ManifestReadOptions? options = null);
    public static Task<IReadOnlyList<string>> CollectContractFailuresAsync(ExtensionManifest manifest,
        IContributionHandler handler, CancellationToken cancellationToken = default);
    public static Task AssertAuthoringContractAsync(ExtensionManifest manifest, IContributionHandler handler,
        CancellationToken cancellationToken = default);
    public static Task<IReadOnlyList<string>> CollectContractFileFailuresAsync(string manifestPath,
        IContributionHandler handler, ManifestReadOptions? options = null, CancellationToken cancellationToken = default);
    public static Task AssertAuthoringContractFileAsync(string manifestPath, IContributionHandler handler,
        ManifestReadOptions? options = null, CancellationToken cancellationToken = default);
}
public sealed class ConformanceException : InvalidOperationException { public IReadOnlyList<string> Failures { get; } }
                                                                  // also the three standard exception constructors
```

`RecordingSession.RunAsync` runs the handler up to its first `await` before it
returns, outside any synchronization context, and the rest on the thread pool,
so a face published before the first `await` is recorded before `RunAsync`
returns and a `Stop` right after it never comes first. The handler keeps
running after that, so tests wait before asserting: `WaitForFaceAsync` returns
the `SetFace` faces in publication order (each call the one after the face the
previous call returned) and `WaitForPublicationsAsync` waits until that many
publications of any kind are recorded. Both time out in real time, whatever the
session's clock, with a `TimeoutException`.

`StartSessionAsync` returns once the companion has accepted or refused the
session (and, when it refused, once the `sessionRefused` frame is in the
transcript); `RefusedCode` says which.

Manifest failures read `code path: message` for a manifest object and
`file(line,column): code path: message` for a file (`file: code path: message`
when the error has no position), where `file` is the path as given, as the Node
kit's `assertManifestValid` writes them. The `File` contract entry points read
and validate the file first: when it is invalid they report its errors that way
and run no other check, so the generated test project (§11.4) never fails for a
secondary reason.

`ExtensionConformance.CollectContractFailuresAsync` runs the
`ContributionContractSuite` checks with their defaults. By default the suite
checks **sessions only**, so an author's first `dotnet test` never runs their
real actions. For every contribution and every setting combination up to the
limit it checks that: the session is accepted; a face contribution publishes a
first face within the timeout with every default; every face has a finite
lifetime; nothing is published after stop; the session handler ends within the
cancellation bound after stop; stopping a session produces no `HandlerFaulted`
(an `OperationCanceledException` after cancellation is a normal completion,
§9.2); and an invoke-only contribution never publishes. A `ContributionRouter`'s
`FindUnmapped` result is reported as a failure.

Invocations run only for the contributions listed in `InvokeContributions`,
with the settings from `InvokeSettings`, and those checks are documented as
running the real action: each invocation settles before the SDK's deadline
(§9.2), and a cancelled invocation ends within the cancellation bound.

The suite drives the SDK's clock and `Session.Time` with a manual
`TimeProvider`. While a session check waits, it advances that clock in steps up
to the check's bound, so a handler that waits on `Session.Time` sees the bound
pass at once; a check fails only when its bound (`InitialFaceTimeout`,
`CancellationBound`) has also passed in real time, so real work such as a
network call before the first face or a save after cancellation counts against
the same bound, and a failing check takes that long in real time. While an
invocation listed in `InvokeContributions` runs, the clock follows real time
and never runs ahead of it, so the SDK's invocation deadline is real time too.
Failure texts name the bound and say "host and real time" (or "real time" for
the invocation deadline).

### 9.4 Hello world

Both programs compile with the one `using` shown, which also covers `Failure`,
`Outcome`, `FaceState`, `ReasonCode` and the face part types: every type an
author names is in the package's root namespace.

Action (`provides: ["invoke"]`, one setting `greeting`):

```csharp
using VentanaTools.Orbit.Extensions;

return await CompanionApp.RunAsync(args, new Greeter());

sealed class Greeter : ContributionHandler
{
    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        var greeting = invocation.Session.Settings["greeting"];
        Console.WriteLine($"{greeting}, world");
        return Task.FromResult(InvokeResult.Done);
    }
}
```

Widget (`provides: ["face"]`):

```csharp
using VentanaTools.Orbit.Extensions;

return await CompanionApp.RunAsync(args, new TimeWidget());

sealed class TimeWidget : ContributionHandler
{
    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), session.Time);
        do
        {
            var now = session.Time.GetLocalNow();
            session.SetFace(new Face
            {
                Line1 = now.ToString("t", session.UiCulture),
                Detail = now.ToString("F", session.UiCulture),
                GoodFor = TimeSpan.FromSeconds(60),
            });
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
```

When the host stops the session, `WaitForNextTickAsync` throws
`OperationCanceledException`; the SDK treats that as a normal completion
(§9.2). Deriving from `ContributionHandler` makes the compiler catch a
misspelled or mistyped handler method through `override`.

Companion projects target `net10.0-windows` (the templates do), so the platform
analyzer knows the pipe transport is available; in a plain `net10.0` project
these programs build with warning CA1416, because `CompanionApp.RunAsync` is
Windows-only (§9.2).

### 9.5 `VentanaTools.Orbit.Extensions.Hosting`

The Generic Host add-on runs a companion as a hosted service of the .NET Generic
Host, for companions whose handlers take services from dependency injection or
that run other hosted services. It depends on `Microsoft.Extensions.Hosting.Abstractions`,
on `Microsoft.Extensions.Options` and on exactly the same version of the author
package (§9); the application adds the host itself (`Microsoft.Extensions.Hosting`).
The name is product-neutral, `AddCompanion`, because no identifier under `src/`
may carry the product name (§2.8).

```csharp
namespace VentanaTools.Orbit.Extensions.Hosting;

public static class CompanionServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services) where THandler : class, IContributionHandler;
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services, Action<CompanionServiceOptions> configure) where THandler : class, IContributionHandler;
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services,
        Func<IServiceProvider, IContributionHandler> handlerFactory);
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services,
        Func<IServiceProvider, IContributionHandler> handlerFactory, Action<CompanionServiceOptions> configure);
}

public sealed class CompanionServiceOptions
{
    public IReadOnlyList<string>? Arguments { get; set; }         // null: the process's command line without the program
    public string? ManifestPath { get; set; }                     // overrides --manifest
    public string? PairingPath { get; set; }                      // overrides --pairing
    public bool WatchFiles { get; set; } = true;
    public bool StopApplicationOnExit { get; set; } = true;
    public Action<StatusChangedEventArgs>? StatusChanged { get; set; }
    public Action<HandlerFaultedEventArgs>? HandlerFaulted { get; set; }
    public CompanionClientOptions? Client { get; set; }
}
```

`CompanionServiceOptions` is an options type of `Microsoft.Extensions.Options`:
`AddCompanion` registers it with `AddOptions<CompanionServiceOptions>()` and adds
its `configure` delegate, when given, as a configuration step. So
`services.Configure<CompanionServiceOptions>(…)` and configuration binding (for
example `services.Configure<CompanionServiceOptions>(configuration.GetSection("Companion"))`)
apply as well, in the order they are registered, and binding sets every option but
the callbacks and the client's clock. The options therefore have settable
properties; like every public data type of the SDK they have no positional
constructor (§9). There are no overloads with optional parameters, so adding an
overload stays binary compatible.

- **Registration.** The generic overloads register `THandler` as a singleton,
  unless the application registered it already, so its constructor's parameters
  come from the container. The factory overloads call the factory once, when the
  host creates the hosted service. A process runs one companion: a second
  `AddCompanion` throws `InvalidOperationException`, and a factory that returns
  null fails the host's start with it.
- **Handler lifetime.** The handler lives as long as the companion. The generic
  overloads resolve `THandler` once and refuse a registration of it with another
  lifetime than singleton, made before or after `AddCompanion` (such as the
  transient typed client `AddHttpClient<THandler>()` adds): the host's start fails
  with `InvalidOperationException`, because a transient or scoped instance held
  for the life of the process (a typed `HttpClient`, a `DbContext`) would never
  recycle its connections or end. A handler takes shorter-lived services through
  `IHttpClientFactory` and `IServiceScopeFactory`, with a scope per session or
  invocation.
- **Validation.** A validator registered with the options checks them when the
  host starts (`ValidateOnStart`), never in `AddCompanion`, so values set later
  are checked too: a `Client` option outside the ranges `CompanionClient` accepts
  (§9.2), or a `ManifestPath` or `PairingPath` that is set but empty, fails the
  start with `OptionsValidationException`, whose message names the option (for
  example `CompanionServiceOptions.Client.InitialRetryDelay: The delay must be
  greater than zero and at most one day.`). The hosted service reads
  `IOptions<CompanionServiceOptions>` once, when the host creates it and before
  the handler, so invalid options never create one.
- **The run.** The hosted service runs exactly `CompanionApp.RunAsync`'s work
  (§9.2) on `Arguments`, with the options of the same name: the same manifest and
  pairing discovery, first-run wait, file watching, statuses, callbacks and exit
  codes. It never delays the host's start with file reads, and it does not hook
  Ctrl+C: the host owns shutdown.
- **The command line.** By default the companion and the host read the same
  command line, and the host's configuration takes it as `--key value` pairs, so
  a value-less `--verbose` followed by another option takes that option as its
  value (`--verbose --environment Development` leaves the host's environment
  unset). Authors put `--verbose` last or write `--verbose true`:
  `CompanionApp`'s parser (§9.2) reads the flag and leaves `true` among the
  arguments the add-on ignores.
- **Stopping.** When the host stops, the run is cancelled as `RunAsync` is: the
  connection closes, sessions end, their handlers' tokens are cancelled, and the
  hosted service completes with exit code 0. When the companion stops by itself
  (exit code 1, 2, 3 or 4, §9.2), the service logs the code and, with
  `StopApplicationOnExit` on, sets `Environment.ExitCode` to it and calls
  `IHostApplicationLifetime.StopApplication()`; with it off, the application keeps
  running. The stop never waits for a handler: the hosted service completes once
  the connection has closed and the status and fault callbacks already raised
  have run, waiting at most 5 seconds for a callback that blocks, so a handler
  that ignores its token cannot hold up the host's shutdown
  (`HostOptions.ShutdownTimeout`, 30 seconds by default). Such a handler runs on
  until it returns or the process exits, and is reported as `IgnoredCancellation`
  (event 17) once `HandlerStopTimeout` has passed, if the process still runs.
- **Logging.** Instead of status lines, the service logs each event `CompanionApp`
  prints through `ILogger`, under the category
  `VentanaTools.Orbit.Extensions.Hosting.CompanionService`, with a fixed event id
  and level: 1 `Connecting` (Debug), 2 `Connected` (Information, with the host's
  id, version and protocol), 3 `Waiting` (Information or Warning, below, with the
  reason code, the retry delay, the fix and the help link), 4 `Retrying` (Information), 5
  `PairingUnusable` (Warning), 6 `PairingDiagnostic` (Warning), 7 `Stopped`
  (Warning), 8 `StoppedQuietly` (Information), 10 and 11 `ManifestInvalid` (Error,
  with each error's code, line, column and fixed message), 12 `ManifestUnreadable`
  (Error), 13 `WatchingManifest` and 14 `WatchingPairing` (Information), 15
  `Unmapped` (Warning), 16 `HostMessage` (Debug, only with `--verbose`), 17
  `HandlerFaulted` (Error, with the author's exception), 18 `CallbackFaulted`
  (Warning, once), 19 `Usage` (Error), 20 `Unexpected` (Error, with the
  exception) and 21 `Exited` (Error, with the exit code). Fixes and help links are
  those of the status lines (§9.2). Entries never contain pairing contents, file
  paths (which name the person's profile), pipe names, setting values or face
  text; a manifest diagnostic is logged without its file and pointer.
- **Levels and repeats.** A `Waiting` entry is Information when its reason
  resolves itself: the catalog's fix for the code is "None" (§8.3:
  `host.shutting-down`, `host.turned-off`, `host.reloaded`), or the client treats
  the host as absent (`host.not-running`, `host.turned-off`, §9.2), so the
  companion connects by itself once the host is back. Every other reason needs a
  person and is a Warning: missing or out-of-date connection info, revoked
  access, another program, differing manifests, a paused or misbehaving peer,
  and a code the SDK does not know. The default host
  (`Host.CreateApplicationBuilder`) writes Warnings and above to the Windows event
  log, so a companion whose host is closed adds nothing there. A status that
  repeats the one logged before it (the same state and reason code, the retry
  delay and attempt aside) is not logged again, and neither are the `Connecting`
  attempt and the host's text that come with it, until the status changes; the
  `StatusChanged` callback still receives every status.

---

## 10. Node SDK

*Informative in its examples; normative for names.* The Node SDK is the package
`@ventanatools/orbit-extensions` in the SDK repository's `node/orbit-extensions`
folder. It is marked `"private": true` and is not published until a
publication decision is made. It targets Node.js 22 or later, has no runtime
dependencies, ships CommonJS with hand-written TypeScript declarations, and
uses the same golden vectors as the .NET SDK. Its host list is
`lib/hosts.json`, a checked-in copy of `fixtures/hosts.json` that the codename
script regenerates and a test compares byte for byte; nothing else in
`node/orbit-extensions/lib` names a host (§2.8). The diagnostic, reason-code,
precedence and capability catalogs are byte copies of the fixtures under
`lib/codes/`, which a test also compares; the reserved publishers are derived
from `lib/hosts.json` (its `reservedIds`, plus `ventana`, `ventanatools` and
`ext`). `hello.client` carries the package's own name and version, read from its
`package.json` at run time.

```js
// require("@ventanatools/orbit-extensions")
runCompanion(args: string[], handler: Handler, options?: RunOptions): Promise<number>   // same arguments, discovery and exit codes as CompanionApp
class CompanionClient extends EventEmitter {
  constructor({ pairing, manifest, handler, retry?: { initialMs, maxMs, hostAbsentMaxMs, jitter, stableMs } })
  readonly state: "NotStarted" | "Connecting" | "Connected" | "Waiting" | "Stopped"
  run(signal: AbortSignal): Promise<void>
  // events: "status" { state, reason?, retryInMs?, attempt, serverVerified, host?, negotiatedVersion? }
  //         "handlerFaulted" { kind, contributionId, sessionId?, requestId?, error? }
}
class ContributionRouter {                                     // implements Handler
  mapSession(contributionId, run): this
  mapInvoke(contributionId, invoke): this
  map(contributionId, handler): this
  findUnmapped(manifest): string[]
}
readManifestFile(path, options?): Promise<{ manifest?: Manifest, diagnostics: Diagnostic[] }>
readPairingFile(path, { expectedExtensionId, manifestHosts }): Promise<{ pairing?: Pairing, diagnostics: Diagnostic[] }>
defaultPairingPath(hostId, extensionId): string                // §6.6
computeManifestHash(manifest): string
hosts: readonly HostInfo[]; findHost(id): HostInfo | undefined // fixtures/hosts.json
const Outcome, FaceState, Failure, PublishResult, ConnectionState   // frozen objects of the wire tokens

interface Handler {
  runSession?(session: Session, signal: AbortSignal): Promise<void>
  invoke?(invocation: Invocation, signal: AbortSignal): Promise<Outcome | { outcome: Outcome, failure?: Failure }>
}
interface Session {
  readonly id, contributionId, settings, uiLanguage, provides, isActive, hostCapabilities
  supports(capabilityId: string): boolean
  setFace(face: { line1?, line2?, detail?, glyph?, state?, goodForSeconds, renew? }): "Accepted" | "SessionEnded"
  clearFace(): "Accepted" | "SessionEnded"
  fail(failure: Failure): "Accepted" | "SessionEnded"
}

// require("@ventanatools/orbit-extensions/testing")
createTestSession({ manifest, contributionId, settings?, uiLanguage? })   // records publications like RecordingSession, with
                                                                          // run, stop, waitForFace and waitForPublications
startTestHost({ manifest, handler, hostId?, limits?, capabilities? })     // a real CompanionClient over an in-memory duplex
assertManifestValid(path, options?)                                       // synchronous; throws an Error named
                                                                          // "ConformanceError" with a failures array

// require("@ventanatools/orbit-extensions/wire")
encodeFrame(record), class FrameReader, transcriptBytes(transcript, role), computeProof(secret, transcript, role),
verifyProof(secret, proof, transcript, role), negotiate(clientMin, clientMax, hostMin, hostMax),
readMessage(text, sender, phase), writeMessage(message), class TokenBucket, ReasonCodes
```

`negotiatedVersion` is the protocol version the handshake settled on (§7.3.2),
the counterpart of .NET's `ProtocolVersion`; like `host` (`{ id, version }`
from the challenge), it is present only once the host's challenge proof
verified. A companion names the host with `findHost(host.id).displayName`.
`testing` exports `createTestSession`, `startTestHost` and
`assertManifestValid(path)`, which throws an `Error` named `ConformanceError`
with a `failures` array, mirroring `ConformanceException`. A manifest given to
`validateManifest` as a plain object (not bytes) treats a `null` member of a list
(`hosts`, `contributions`, `settings`, `requires.capabilities`, `provides`) as
`null.member` and any other `null` member as absent, as the .NET object path
does. `runCompanion`'s test seams are not part of the public API.

```js
const { runCompanion, Outcome } = require("@ventanatools/orbit-extensions");

runCompanion(process.argv.slice(2), {
  async invoke(invocation) {
    console.log(`${invocation.session.settings.greeting}, world`);
    return Outcome.Done;
  },
}).then(code => process.exit(code));
```

The Node SDK behaves as §9.2 describes: the same backoff and state table, face
cleaning and coalescing, renewal, replay window, session cap, ping handling,
handler rules and validation order. Specifically:

- **Results.** An `invoke` handler's return value that is not a valid outcome,
  or a `failure` given with an outcome other than `Failed`, is answered as
  `Failed` and emits `handlerFaulted` with kind `InvalidResult`. The SDK never
  sends a frame the host would close on. A handler that rejects with an
  `AbortError` after its signal was aborted completes normally.
- **Default paths.** The manifest is found at `--manifest`, else
  `extension.json` in the directory of the entry module (`require.main`), else
  in the current directory. The pairing file is found at `--pairing`, else at
  `defaultPairingPath` for each active host id in the manifest's `hosts`, else
  `<extension-id>.pairing.json` in the entry module's directory, then in the
  current directory.
- **Server verification: a documented limitation.** Node's `net.connect`
  cannot check who owns a named pipe and does not request identification-level
  impersonation, so a server receives the default impersonation level. The Node
  SDK is therefore a client that can do neither (§7.1). It treats every server
  as unverified until the challenge proof verifies, so a program squatting on
  the pipe name cannot stop it or delay it beyond the maximum retry delay. It
  cannot prevent a squatter that holds `SeImpersonatePrivilege` (for example a
  compromised service account; such accounts are already highly privileged)
  from impersonating the person after reading `hello`, which carries no secret.
  Nor can it refuse a squatter that runs as the same user at a lower integrity
  level and has read the pairing file, whose challenge proof therefore
  verifies: against such a process it relies on the host's pairing-file label
  (§6.2). The package README and the security notes on the documentation site
  state this. An optional native check (`GetNamedPipeServerProcessId` plus the
  owner, integrity and label tests of §7.1) is a release gate to decide before
  the package is published.

---

## 11. Tooling

### 11.1 `orbit-ext`

Installed from the package `VentanaTools.Orbit.Extensions.Tool` (global or local
tool). Until packages are published, it is installed from a folder of locally
built packages. In the SDK repository, whose `NuGet.config` maps the package
family to `artifacts/packages`, run `dotnet tool install
VentanaTools.Orbit.Extensions.Tool --tool-path <folder> --prerelease` from the
repository root; elsewhere, add `--add-source <folder>`, or, where a NuGet
configuration uses package source mapping (the .NET SDK refuses `--add-source`
there), add the folder to that configuration and map the package family to it.

The tool package carries the packages a new project needs: the
`VentanaTools.Orbit.Extensions`, `VentanaTools.Orbit.Extensions.Testing`,
`VentanaTools.Orbit.Extensions.Hosting` and `VentanaTools.Orbit.Extensions.Templates`
packages of the same version, and the `npm pack` tarball of
`@ventanatools/orbit-extensions`. On first use, `new`
copies them, together with the tool's own package from its install store, into
a per-user, per-version feed, `%LOCALAPPDATA%\VentanaTools\packages\<version>\`
(or the folder given with `--feed <dir>`), so a generated project restores and
its local tool manifest restores before anything is published (§11.4).

The tool reads its own command name from its assembly (an `AssemblyMetadata`
item generated from `ToolCommandName`) and derives the sibling package IDs from
its assembly name, so its source names neither (§2.8). Its commands include the
Ventana tooling verbs `new`, `validate`, `pack`, `verify` and `test` (§2.7).

| Command | Purpose |
|---|---|
| `orbit-ext new <action\|widget\|node> [-n <name>] [-o <dir>] [--extension-id <id>] [--host <id>] [--display-name <name>] [--no-tests] [--feed <dir>]` | Prepares the feed, then runs `dotnet new orbit-ext-<kind>` with the same arguments plus `--package-source <feed>`; it also passes `--display-name` and `--no-tests` to the template. It refuses an `--extension-id` outside the third-party grammar with the id code, and a `--host` that is not an active registry host (`<id> is not an active host id; use one of: <active ids>. [--host]`), with exit 2 before anything is created. If the template pack is not installed, prints the install command, which installs it from the package file in the feed, and exits 3. After creating a Node project it copies the Node SDK tarball from the feed into `vendor/`, then prints the next commands, quoting the folder of the `cd` hint when it holds a space or a character PowerShell or cmd treats specially. |
| `orbit-ext validate [<path>] [--host <id>] [--json] [--warnings-as-errors]` | Validates an `extension.json`, a folder containing one (with its `strings/` folder), or a package file. Default path: the current directory. |
| `orbit-ext pack [<project-dir>] [-o <output-dir>] [--host <id>] [--force] [--json]` | Builds a package from `extension.pack.json` (§11.3), running its `build` step first when it has one. |
| `orbit-ext verify <package> [--host <id>] [--json]` | Verifies a package (§5.7) and prints its id, version, hosts, contributions, file count, size and package hash. |
| `orbit-ext test [<project-dir>] [--host <id>] [-- <args…>]` | Validates the project's `extension.json` as `validate` does, then runs its tests: `dotnet test` for a .NET project (the templates' test project uses the test kit, §9.3), `npm test` for a Node project, passing the arguments after `--` through. Exits 1 when validation reports an error or a test fails. |
| `orbit-ext simulate [--manifest <path>] [--script <file>] [--json] -- <command> [args…]` | Runs a pipe-level fake host: creates a temporary registration and pairing file, starts the companion with `--manifest` and `--pairing` appended, and drives it from an interactive prompt (`start <contribution> [key=value…]`, `invoke <session>`, `cancel <request>`, `stop <session>`, `faces`, `ping`, `disconnect`, `quit`) or a script. Prints the transcript. Because the arguments are appended, a `dotnet run` companion needs a trailing `--`: `orbit-ext simulate -- dotnet run --project . --`. |
| `orbit-ext run [--watch] -- <command> [args…]` | Runs the companion command and prints its status lines. With `--watch`, restarts it when `extension.json`, the pairing file, or files the command builds change (for `dotnet run`, the project's sources). |
| `orbit-ext link [<path>] [--host <id>] [--json]` | Checks the pairing for the project at `<path>`: prints where the SDK looks (§6.6, §9.2), whether a valid pairing file is there, and, when there is none, the host action that writes it. Exits 0 when a valid pairing is found, 1 otherwise. It never prints the secret. |
| `orbit-ext schema [--kind <manifest\|strings\|package\|pairing\|pack\|simulation>] [--host <id>] [-o <file>]` | Writes the bundled JSON Schema, for editors that work offline. |

`--host` defaults to the first id in the manifest's `hosts` that is an active
registry entry.

- **`link`** searches the per-user default for each active host in `hosts` (or
  for `--host`), then `<project folder>/<id>.pairing.json` as the companion's
  current-directory fallback (the program folder of an unbuilt companion is
  unknown). It reports `pairing.missing` only when no candidate exists; an
  invalid file reports its own codes. `--json` adds `"pairing": { "searched":
  [{ "path", "state": "Missing" | "Invalid" | "Valid" }], "found": <path> |
  null }`.
- **`run`** without `--watch` exits with the companion's exit code. `--watch`
  polls every second: `extension.json` (or the command's `--manifest`), the
  pairing files (the command's `--pairing`, else the per-user defaults and the
  current folder), and the sources (`.cs`, `.csproj`, `.props`, `.targets`,
  `.json`, `.js`, `.cjs`, `.mjs`, `.ts`, skipping `bin`, `obj`, `node_modules`,
  `.git`, `.vs` and `artifacts`) under the `dotnet run --project` folder or the
  current folder. When the companion exits on its own, it waits for a change.
- **`simulate`** uses as host id the first active registry host in `hosts`,
  else the first listed id. The simulated host advertises no capabilities,
  reports the tool's version, and the current UI language when it is a valid
  tag (else `en-US`). It applies every face as it arrives (no per-session
  coalescing, so the transcript shows every publish), enforces the hard bucket
  (`rate.exceeded`), sends `rate.throttled` at most every 5 seconds when the
  soft bucket cannot pay for a face command, pings after `pingIntervalMs` idle,
  and enforces §7.9 against the companion. A `start` step waits up to 90 seconds
  for a connection (`dotnet run` builds first); a `disconnect` step returns once
  the connection has ended, so the next `start` waits for the reconnection; the
  prompt's `disconnect` defaults to `host.reloaded`. The companion's output goes
  to standard error and the transcript to standard output; the transcript never
  contains nonces, proofs or the secret, and shows face text cleaned as a host
  would hold it. `simulate` needs Windows (exit 5 elsewhere).

Text output, one line per diagnostic in the MSBuild canonical form that Visual
Studio, VS Code problem matchers and CI annotations recognise, then a summary:

```text
extension.json(12,9): error json.member-renamed: This member was renamed in schema 3 (capabilities is now provides; manifestVersion is now schemaVersion). [/contributions/0/capabilities]
  fix: Use the new name.
extension.json(31,15): warning text.long: This name is longer than 32 characters and will be truncated. [/contributions/1/name]
  fix: Shorten it.
orbit-ext: 1 error, 1 warning
```

The form is `<file>(<line>,<column>): <severity> <code>: <message> [<path>]`; the column is the
diagnostic's UTF-8 byte column, which equals the character column on the ASCII lines manifests
almost always have (the columns in the examples here are illustrative).
When a diagnostic has no line (an archive-structure finding), the position is
left out: `example.countdown-0.3.0.orbitextension: error package.zip64: … []`.
Each line is followed by an indented `fix:` line with the catalog's fix (§4.4,
§8.3), with `{tool}` replaced by the command name and, for `pairing.*` reason
codes, "the host" replaced by the host's display name, as `CompanionApp` does.
Problem matchers ignore the extra lines.

`file` is the path as the author would type it: relative to the current
directory with `/` separators when it is inside it, else the full path; it is
both the MSBuild origin and the `--json` `file`. A finding about a package
entry is shown as `<package>!/<entry>` (for example
`example.countdown-0.3.0.orbitextension!/extension.json(4,11)`), and an
archive-structure finding as the package path alone. A finding about no file
(`tool.internal-error`, `json.internal-error`, `tool.io-error`, `tool.usage`)
uses the command name as its origin and has no `file`. `--json` omits `file`, `line` and `column` when they
are absent, and escapes every character outside printable ASCII.

`--json` writes one JSON object to standard output:

```json
{
  "tool": "orbit-ext",
  "version": "0.1.0-preview.1",
  "command": "validate",
  "ok": false,
  "diagnostics": [
    { "code": "json.member-renamed", "path": "/contributions/0/capabilities", "message": "This member was renamed in schema 3 (capabilities is now provides; manifestVersion is now schemaVersion).", "severity": "Error", "file": "extension.json", "line": 12, "column": 9 }
  ],
  "summary": { "errors": 1, "warnings": 0 }
}
```

`verify` and `pack` add `"package": { "path", "sha256", "id", "version", "hosts", "files", "bytes" }`.
`simulate --json` writes one transcript entry per line instead.

A failure always carries a code, so `"ok": false` never comes with an empty
`diagnostics` array. Besides the catalog's codes, the tool uses four tool-only
codes, each an `Error` with a fixed message and an empty `path`:
`tool.internal-error` (exit 4, below); `tool.path-missing` (exit 3), "The file
or folder does not exist.", with `file` the missing path as the author would
type it (a missing `<path>`, package, `extension.json`, `extension.pack.json` or
script); `tool.io-error` (exit 3), "A file or folder could not be read or
written.", with no `file`, because the operating system's message, which may
name paths, goes to standard error only; and `tool.usage` (exit 2), "The command
line is not valid.", written only when the command line asked for `--json`, so a
script that parses the output still gets one object. Each also prints its
sentence to standard error.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Diagnostics with severity `Error` (or `Warning` with `--warnings-as-errors`); for `simulate`, a script expectation failed; for `link`, no valid pairing; for `test`, a test failed. |
| 2 | Usage error. |
| 3 | Input or output problem: a path is missing or unreadable, the output exists, or a template pack is missing. |
| 4 | Internal error: a reader defect prints `json.internal-error`; any other defect prints the tool-only code `tool.internal-error`. |
| 5 | `simulate` or `run` could not start the companion process, `test` could not start `dotnet test` or `npm test`, or `new` could not start `dotnet`; `simulate` also exits 5 on a platform other than Windows. |

`new` maps the exit codes of `dotnet new`: 103 (template not found) and 73 (the
output exists) to 3, 127 (an invalid option) to 2, and any other failure to 1.
`pack` writes the build step's `dotnet publish` output to standard error, so its
`--json` output stays one object.

The tool prints diagnostic messages, paths and the author's own file names.
It never prints file contents or secrets.

### 11.2 Simulation scripts

A script is a JSON array of steps such as
`{ "start": "example.countdown/timer", "settings": { "mode": "pause" }, "as": "s1" }`,
`{ "invoke": "s1", "expect": "Done" }`, `{ "expectFace": "s1", "within": "2s" }`,
`{ "stop": "s1" }`, `{ "disconnect": "host.reloaded" }`. The schema is
`schemas/extensions/simulation.v1.json` (§11.5), published with the tool
(`orbit-ext schema --kind simulation`). A failed expectation exits 1.

Steps: `start` (with `settings` and `as`); `invoke` (with `as`, `expect`, one
of `Done`, `Refused`, `Failed`, `Unsupported`, `Cancelled` and `TimedOut`,
`failure`, and `wait`, default `true`); `cancel`; `stop`; `expectFace` (with
`within`, default `2s`, and `line1`, `line2` and `state`; it passes when the
current face matches or a matching one arrives in time); `disconnect` (with a
reason code); and `wait`. Durations are `<n>s` or `<n>ms`. A script has at most
1,000 steps, and its reader reports the codes of §4 (`enum.undefined` for an
unknown token, or a malformed duration or code).

`simulate` creates its pipe exactly as a host must (§7.1): access for the
current user only, the Medium mandatory label that denies lower-integrity
processes read, write and execute, rejecting remote clients, the first and only
instance of its name, one connection at a time. Its temporary pairing file and
the folder that holds it are created with an access-control list that grants
access only to the current user and the mandatory label of §6.2, and deleted on
exit.

### 11.3 Pack configuration: `extension.pack.json`

| Member | Type | Required | Rules |
|---|---|---|---|
| `$schema` | string | No | |
| `packVersion` | integer | Yes | `1`. |
| `readme` | string | Yes | Path of the file copied to `README.md`. |
| `strings` | string | No | Folder whose `*.json` files are copied to `strings/`. |
| `build` | object | No | A .NET publish step that `pack` runs first (below). |
| `payload` | array of object | No | Copy rules, applied in order. |

Build step: `{ "project": path, "runtime"?: RID, "configuration"?: string, "to"?: path }`.
`pack` runs `dotnet publish <project> -c <configuration> -r <runtime> -o <temporary folder>`
(defaults `Release`, `win-x64`, and `to` `companion`), then copies the publish
output to `payload/<to>/`, with the always-excluded names below, before the copy
rules run. A failed publish is `pack.build-failed`. With a build step, the copy
rules never need to name `bin/<configuration>/<framework>/<runtime>/publish`
folders, so changing the target framework or runtime cannot silently break a
pack.

Copy rule: `{ "from": path, "to": path, "include"?: [glob], "exclude"?: [glob] }`.
`from` is a file or folder relative to the pack file; `to` names a folder under
`payload/` (`""` or `.` for `payload/` itself), and files keep their names.
Later rules replace earlier files of the same name. Names that differ only in
case, or a file and a folder with one name, are `package.path-conflict`, and a
staged name outside the entry grammar is `package.path`, both with the source
file as `file`. The defaults are `include: ["**/*"]` and `exclude: []`. The
default output folder is `artifacts/` in the project folder.

Globs support `*`, `**` and `?`. A glob is a path below `from`, with `/` or `\`
between segments, and matches without regard to case:

- within a segment, `*` matches any run of characters and `?` exactly one;
- `**` as a whole segment matches any number of folders, including none; at the
  end of a glob it matches every file below (`src/**`), and so does a glob that
  ends with a separator (`src/`);
- `*.*` as a whole segment matches every name, a segment that starts with `**.`
  matches in any folder below (`**.js` is `**/*.js`), and elsewhere in a segment
  `**` is `*`;
- leading separators, empty segments and `.` segments are ignored;
- a glob with a `..` segment, or with no segment at all, is `package.path` at
  its position in `extension.pack.json`, since a glob never leaves `from`;
- an exclude glob that matches a folder, or that ends with `**` below it, leaves
  out the folder with everything in it.

The tool always:

- copies `extension.json` from the project folder and generates
  `extension.package.json`;
- excludes the names `.git`, `.vs`, `obj`, `node_modules/.cache`, `*.user`,
  `*.pairing.json` and `pairing.json`, whether they name a file or a folder (a
  `.git` file is a Git worktree's or submodule's pointer to its repository),
  wherever they appear on the way from the project folder to a staged file,
  including in the paths this file names (the build step's output included): a
  copy rule whose `from` is or lies in one copies nothing, a file named directly
  with a pairing file's name is `pack.secret`, and a readme in an excluded
  folder is `package.file-missing`;
- never stages its own output: a copied folder that contains the output folder
  leaves it out, and no package file (a name ending in a package file extension
  of `hosts.json`) directly in the output folder is staged, even when a copy
  rule's `from` is the output folder or the output folder is the project
  folder; a copy rule whose `from` lies inside the output folder still copies.
  The output folder is the folder `-o` names, however it is spelled: a path
  through a junction or symbolic link, a substituted drive or an 8.3 short name
  is compared by the final path the file system resolves it to. A junction or
  symbolic link inside a copied folder that resolves to the output folder (an
  `artifacts` folder redirected to another drive, say) is the output folder, so
  it is left out, not refused as a link;
- refuses (`pack.secret`) any staged file of at most 4,096 bytes that contains
  both a quoted `pipeName` and a quoted `secret` member, JSON or not;
- refuses symbolic links and junctions (`pack.link`) on the way from the project
  folder to every staged file and folder, including the readme and the strings
  folder, but does not inspect the project folder or the folders above it (for
  a path outside the project, the folders the two share). Inside a copied
  folder it never looks into a link and decides on the link itself, before the
  globs filter its contents: a link to a file is refused when the globs select
  it, and a link to a folder when the globs could select anything inside it,
  even if nothing there would match. A link that resolves to the output folder
  is left out (above), but a link to a folder that merely contains the output
  folder is refused. A link the globs cannot reach (such as the junction npm
  makes under a `node_modules` folder no glob reaches) is left out;
- checks per-file size, entry count and expanded size before it reads any
  content (`package.file-too-large`, `package.entries`,
  `package.expanded-too-large`);
- writes `<output-dir>/<id>-<version><package-file-extension>` atomically and
  refuses to replace an existing file without `--force`;
- verifies the result (§5.7) before reporting success.

`pack.source-missing` and `pack.build-failed` carry `file` `extension.pack.json`
and the position of the member; `pack.build-failed` also covers a `dotnet` that
cannot start. A pack configuration of another `packVersion` gets only
`schema.version-unsupported`, as manifests do. With no `--host` and no active
host in `hosts`, the package file extension is unknown, which is a usage error.

### 11.4 Templates

Package `VentanaTools.Orbit.Extensions.Templates`. Installing it makes the templates
appear in `dotnet new` and in Visual Studio's New Project dialog. The templates
have no post-actions.

| Short name | Identity | Creates |
|---|---|---|
| `orbit-ext-action` | `VentanaTools.Orbit.Extensions.Templates.Action.CSharp` | A .NET companion with one action that has one `Choice` setting, its handler deriving from `ContributionHandler`; an xUnit test project using `VentanaTools.Orbit.Extensions.Testing`; `extension.json`, `extension.pack.json` (with a `build` step, and a copy rule that puts `licenses/` into every package), the SDK's Apache-2.0 `LICENSE` and `NOTICE` in `licenses/sdk/` (the template pack stages them from the repository at pack time, because every package redistributes the SDK library), `PACKAGE-README.md`, a local tool manifest pinning `VentanaTools.Orbit.Extensions.Tool` (command `orbit-ext`), a `nuget.config` (below), a bundled schema copy at `.schemas/manifest.v3.json` that `extension.json`'s `$schema` points to, and `.gitignore` entries for pairing files. |
| `orbit-ext-widget` | `VentanaTools.Orbit.Extensions.Templates.Widget.CSharp` | The same, with one widget that publishes a face and can be invoked. |
| `orbit-ext-node` | `VentanaTools.Orbit.Extensions.Templates.Node` | A Node companion using `@ventanatools/orbit-extensions` from `file:./vendor/ventanatools-orbit-extensions-<version>.tgz` (the tarball copied from the feed into `vendor/`), with `node --test` tests using `@ventanatools/orbit-extensions/testing` (the manifest check, the action through `createTestSession` and `createTestInvocation`, and a session and an invocation through a real client on `startTestHost`), and the same JSON files and schema copy. |

Parameters (all templates):

| Parameter | Option | Default | Rules |
|---|---|---|---|
| `extensionId` | `--extension-id` | `example.` + the lowercased project name with non-segment characters replaced by `-` | Checked by `orbit-ext new` before the template runs; the template engine cannot refuse a parameter value, so a project created by `dotnet new` with an id outside the third-party grammar (§2.4) does not build (or, for Node, start) until it is corrected; the generated conformance test checks everything else. |
| `hostId` | `--host` | `orbit` | Written into `hosts`. Regenerated from `hosts.json` by the codename script. |
| `displayName` | `--display-name` | The project name | Written into `name`. |
| `packageSource` | `--package-source` | the per-user feed | A folder of packages. The template writes a `nuget.config` with `<clear/>`, that folder and nuget.org as sources, and `packageSourceMapping` that maps `VentanaTools.Orbit.Extensions*` to the folder only (the pattern matches the author package's bare ID as well as `.Testing` and `.Tool`). `orbit-ext new` always passes its feed. Without it (a plain `dotnet new`, or Visual Studio's New Project dialog, which cannot pass it) the folder is the per-user feed of §11.1, written `%LOCALAPPDATA%\VentanaTools\packages\<version>` (NuGet expands the variable), with the same mapping, so restore fails until that folder exists and nuget.org never serves a package of the family, published or squatted. |
| `includeTests` | `--no-tests` | tests included | .NET templates only. |

- C# templates target `net10.0-windows` (the .NET companion transport runs on
  Windows), publish single-file and framework-dependent (for `pack`'s default
  `win-x64`), set `VentanaToolsImplicitUsings` to `true`, and also write
  `using VentanaTools.Orbit.Extensions;` explicitly in source. Their
  `PACKAGE-README.md` says the companion needs the x64 .NET 10 Runtime, also on
  Windows on Arm, where the Arm64 runtime alone does not run an x64
  framework-dependent program; the templates README explains self-contained
  publishing and `"runtime": "win-arm64"`.
- No template writes a launch setting that depends on a pairing file in the
  project folder, and none suggests saving one there: `CompanionApp` finds the
  pairing in the per-user folder (§6.6).
- `extension.json`'s `$schema` is the relative path of the bundled copy, which
  Visual Studio and VS Code both resolve offline; `orbit-ext schema -o
  .schemas/manifest.v3.json` refreshes it. Authors may switch to the published
  URL (§2.2) once it is served.

### 11.5 JSON Schemas

| Schema | File in the SDK repository | URL |
|---|---|---|
| Manifest 3 | `schemas/extensions/<host-id>/manifest.v3.json` | `https://dev.ventana.tools/schemas/extensions/<host-id>/manifest.v3.json` |
| Strings 3 | `schemas/extensions/<host-id>/strings.v3.json` | `https://dev.ventana.tools/schemas/extensions/<host-id>/strings.v3.json` |
| Package descriptor 2 | `schemas/extensions/package.v2.json` | `https://dev.ventana.tools/schemas/extensions/package.v2.json` |
| Pairing 3 | `schemas/extensions/pairing.v3.json` | `https://dev.ventana.tools/schemas/extensions/pairing.v3.json` |
| Pack configuration 1 | `schemas/extensions/pack.v1.json` | `https://dev.ventana.tools/schemas/extensions/pack.v1.json` |
| Simulation script 1 | `schemas/extensions/simulation.v1.json` | `https://dev.ventana.tools/schemas/extensions/simulation.v1.json` |

The manifest and strings schemas are keyed by host id because a host may
constrain them further later; the other four contain nothing host-specific.
Each active host in `hosts.json` gets its folder, generated by the codename
script (`orbit` today). Schemas use JSON Schema 2020-12 and set `$id` to their
URL. A conformance test runs every fixture in `fixtures/manifests/` through both
the schema and the reader; schema-only rules (none may be stricter than the
reader) and reader-only rules (cross-member checks such as `default` ∈
`choices`) are listed in the test. The URL segment is the host id, never a
documentation slug.

Text lengths are one of the reader-only rules. A schema's `maxLength` counts
characters (code points), while the limits of §3.6 count UTF-16 code units, and a
character outside the Basic Multilingual Plane, such as an emoji, is two units. So
a name of 41 such characters (82 units) passes the schema's `maxLength` of 80 and
is refused by a reader with `string.too-long`; the schemas state this in their
descriptions. No pattern can close the gap portably, because JSON Schema
validators run ECMA-262 patterns over code points or over UTF-16 code units
depending on the implementation. A schema is never stricter than the readers,
which have the last word.

### 11.6 Building

The SDK repository builds and verifies with Windows PowerShell 5.1 or
PowerShell 7 (`tools/build.ps1`, `tools/verify.ps1`), and with plain `dotnet`
commands (`dotnet build`, `dotnet test`) for the libraries. Local builds stamp
packages `0.1.0-dev.<UTC yyyyMMddHHmmss>` and restore samples into a fresh
per-run package folder, so a cached package can never stand in for the one just
built; `tools/verify.ps1` checks that each sample's resolved author library has
the same SHA-256 as the one in the freshly packed package. `tools/build.ps1`
packs the libraries (the author package, the test kit and the Generic Host
add-on), then the templates, then the Node SDK tarball, then the tool, which
carries the others (§11.1).

---

## 12. Versioning and compatibility

### 12.1 Version numbers

- **Formats** (manifest schema, strings schema, archive, pack configuration,
  pairing, protocol) have integer versions. An incompatible change increments
  the number. A compatible addition does not: within a protocol version, new
  optional members and messages are allowed under §7.11 and capabilities
  (§7.4).
- **Packages** use SemVer 2.0. The five `VentanaTools.Orbit.Extensions*`
  packages (the author package, Testing, Hosting, Tool and Templates) and the
  Node SDK version in lockstep.

### 12.2 Before 1.0

- Any minor version MAY break. Every break is listed in `CHANGELOG.md`.
- Hosts and SDKs support only the current version of each format. A mismatch
  is reported with a stable code (`schema.version-unsupported`,
  `protocol.version-unsupported`, `pairing.version-unsupported`).
- Experimental capabilities (`x.*`) work only in developer mode.

### 12.3 From 1.0

- A host accepts the current (N) and previous (N−1) protocol version, manifest
  schema and pairing version for at least 12 months after N ships.
- SDK packages follow SemVer: a minor release is binary compatible, enforced by
  package validation against the previous release; breaking changes wait for a
  major release. Because public data types have no positional constructors
  (§9), adding an optional wire member is a minor release.
- Compatible within a protocol version: new optional members and message types
  (§7.11), new capability ids (§7.4), and new reason codes, which every
  receiver accepts as unknown codes (§7.5). New closed-enumeration tokens are
  not compatible unless a capability gates them (§7.8).
- Deprecation: an API is marked `[Obsolete]` with the replacement for at least
  one minor release before removal in the next major release. A wire feature is
  announced in release notes and kept for the N/N−1 window.
- Diagnostic codes, reason codes and capability ids are never reused for a
  different meaning.
- Every release publishes a compatibility matrix: host version → protocol
  range, manifest schema, archive version and SDK range.

### 12.4 Renaming a product

Before the first public release, renaming the product (for example from Orbit
to Pinwheel) is a scripted, mechanical rename with no alias:

1. In the SDK repository, run `eng/rename-product.ps1 -From Orbit -To
   Pinwheel` (optional `-HostId`, `-PackageExtension`; the defaults are the
   lowercased new name and `.<host id>extension`). It renames everything the
   product-name rule (§2.1) allows to carry the name:
   - the package family: package IDs, assembly names, root namespaces, project
     folders and files, and the solution (`VentanaTools.Orbit.Extensions*` →
     `VentanaTools.Pinwheel.Extensions*`), including the template identities;
   - the Node package and its folder (`@ventanatools/orbit-extensions` in
     `node/orbit-extensions/` → `@ventanatools/pinwheel-extensions` in
     `node/pinwheel-extensions/`);
   - the tool command and template short names (`orbit-ext` →
     `pinwheel-ext`);
   - the `hosts.json` entry, through the codename script (step 2);
   - the repository URL (`VentanaRepositoryUrl` and docs links), because the
     repository is renamed with the product (`orbit-sdk` → `pinwheel-sdk`;
     GitHub redirects the old name);
   - the display name in the repository's docs (every Markdown file), `NOTICE`,
     package readmes and package descriptions, and, in those docs, the old host
     id and package file extension where they appear as whole tokens or URL and
     path segments; and
   - what is left of the old name in every other tracked text file (test
     literals and comments, build comments), keeping its case, so that after the
     rename the old name appears only in the reserved lists.

   It refuses to run on a working tree with changes, prints every renamed path
   and the number of replacements per token, and ends by running
   `ProductNameConfinementTests`. It never edits the fixtures (the codename
   script edits the one registry entry), `fixtures/reserved-publishers.json`,
   the `reservedIds` of `hosts.json` or of its Node copy, `CHANGELOG.md`
   history, or the normative design documents in `docs/design/`, which name
   both the old and the new product and are updated by hand in the same change.
   The script itself names no product: every token derives from `-From` and
   `-To`.
2. The codename script, `tools/Set-HostCodename.ps1`, changes the product's
   entry in `fixtures/hosts.json` (§2.3): id, display name and package file
   extension. The old id stays in `reservedIds` and in the reserved-publisher
   list (§3.5); the script never edits either list, and warns when the new id
   is missing from them (they are edited by hand, in the same change). It then
   regenerates every file derived from `hosts.json`: the Node SDK's copy, sample
   and template manifests (`hosts`, `$schema`), template defaults, the schema
   folder (`schemas/extensions/<new-id>/`, moved with `git mv`), the pin of
   `hosts.json` in the test project's `FixturePins.txt`, and, when the package
   file extension changes, the ignored package files in `.gitignore`. Fixtures
   and test vectors use the test host id `example-host` (§2.3) and name no
   product, package or tool, so no fixture other than `hosts.json`, no pin other
   than its own, and no proof changes. `ProductNameConfinementTests` (§2.8)
   fails if any source still names the old product outside the allowed places.
3. The host changes its single host-id constant and replaces the package family
   name in its project references and `using` directives.
4. Docs move to the new slug with a permanent redirect from the old one; the
   docs site generates its `/go/<host-id>/` redirects and schema paths from
   `hosts.json`.
5. Pairing files saved before the rename name the old host id and pipe name, so
   they fail with `auth.host-mismatch` or `host.not-running`; authors save
   connection info again. Nothing outside development has a pairing before
   release.

Wire and file-format identifiers contain no product name, so a rename never
changes them: the HMAC label, the pipe prefix, the file names, the schema URL
pattern, the pairing folder, diagnostic and reason codes, and capability ids.
Only the host-id segments they embed change, from `hosts.json`.

After a public release, a rename adds the old id to the entry's `aliases`:
readers accept either id in `hosts`, hosts accept both package file extensions
for at least 12 months, and the old schema URLs keep resolving. Pairings keep
working: a host records, with each registration, the host id its pairing was
issued under, and keeps using that id in the registration's pipe name and in
`challenge.host.id` until the person saves connection info again, which issues
the current id. A registration created after the rename uses the new id.
Package IDs cannot be renamed on nuget.org, so a rename after release publishes
the new package family and deprecates the old packages, naming the new ones as
their alternatives.

---

## Appendix A: golden test vectors

All vectors live in the SDK repository's `fixtures/` folder, are UTF-8 without a
byte order mark with LF line endings, and are pinned by SHA-256 in tests. The
.NET SDK, the Node SDK and the host run the same files. Apart from `hosts.json`
and `reserved-publishers.json`, which define the product names and the
reserved ones, every fixture and vector uses the test host id `example-host`
(§2.3), never a registry id, and names no product, package or tool: `hello`
frames use the client name `example-client`, package fixtures carry no
`createdBy`, and code fixes that name the tool use the `{tool}` placeholder
(§4.4). A product rename therefore changes no fixture but `hosts.json`, no pin
but its own, and no proof. The
fixtures for the Ventana conventions (`ids.json`, `reserved-publishers.json`,
`text-rules.json`, `codes/diagnostics.json`) also state those conventions
(§2.7) in testable form.

| File | Contents |
|---|---|
| `fixtures/hosts.json` | The host-id registry of §2.3: the single product definition. With `reserved-publishers.json`, the only fixture that names products, and the only one the codename script edits (its `reservedIds` excepted). |
| `fixtures/ids.json` | Id grammar cases with the expected code for each origin, including Windows device names (`con`, `nul`, `com1`, `lpt9`, any case) as a first segment. Its reserved-publisher cases use only the reserved names that are not products (`ventana`, `ventanatools`, `ext`); tests iterate `reserved-publishers.json` for the others. |
| `fixtures/reserved-publishers.json` | The reserved publisher list of §3.5. |
| `fixtures/text-rules.json` | Declaration-text accept/reject cases (including ZWJ, ZWNJ, LRM, RLM, ALM accepted and U+200B, U+202E, U+FEFF, astral noncharacters refused), the declaration text limits (`declarationLimits`, §3.6), and face-text cleaning cases with expected output, including over-long text, U+200B and tag-sequence flag emoji passed through `SetFace` (which must not throw, §9.2). |
| `fixtures/codes/diagnostics.json` | The diagnostic code table of §4.4. |
| `fixtures/codes/precedence.json` | The one-diagnostic-per-path precedence of §4.2, with a case for each overlap (unknown enumeration value, empty name, missing id). |
| `fixtures/codes/reason-codes.json` | The reason-code catalog of §8.3, with disposition, audiences, Pre and violation flags, fix and help anchor. |
| `fixtures/manifests/valid/*.json` | Valid manifests, including the §3.9 example (as `countdown.json`, §3.9) and a lone leaf. |
| `fixtures/manifests/invalid/*.json` with `*.expected.json` | One fault per file and the exact expected diagnostics (code, path, severity, line, column), including renamed members and manifests built in code with null members. |
| `fixtures/strings/` | Valid and invalid strings files against a fixed manifest. |
| `fixtures/packages/` | A generator description plus golden archives: valid; one at every limit of §5.3 at once (512 entries with 240-character names); ZIP64, including a ZIP64 extra field in a local header only; encrypted; symlink; path traversal; case conflict; a local header whose name, method, flags, CRC or sizes differ from its central record; overlapping entries; bytes before the first entry and between entries; inventory and hash mismatches. |
| `fixtures/pairing/` | Valid pairing files (with and without a UTF-8 byte order mark, CRLF) and invalid ones (UTF-16, unknown member, `PerLaunch`, bad pipe name) with expected codes. |
| `fixtures/wire/v3/transcript.json` | Transcript and proof vectors (below), including a capability list case and a tampered-offer case. |
| `fixtures/wire/v3/manifest-hash.json` | Manifests and their hashes, including order-only differences that hash equal. |
| `fixtures/wire/v3/messages-valid.json` | One or more canonical frames for every message type. |
| `fixtures/wire/v3/messages-invalid.json` | Frames with the expected reason code: duplicate members, including duplicates that differ only in escaping; a case variant of a known member (`sessionid`); unknown `$type`; bad enum tokens; `null`; bad ids; oversize, including a handshake frame over 8,192 bytes; `ready` frames with each limit out of range and with broken cross-member rules; `error.message` with a control character, a bidirectional control or a non-ASCII character; `supported` out of range or with `minVersion` > `maxVersion`; `settings` with more than 16 members or a key outside the setting-id grammar; a `sessionRefused` code outside its set. |
| `fixtures/wire/v3/messages-ignored.json` | Frames with unknown members and types that must be accepted or ignored, and an `error` with a grammar-valid code no catalog has (accepted as an unknown code), with and without `retryAfterMs`. |
| `fixtures/wire/v3/sessions.json` | `startSession` settings against a fixed manifest: an undeclared key, a missing key and an undeclared value, each `session.settings-invalid`. |
| `fixtures/wire/v3/handshake.json` | Scripted handshakes: success, version mismatch with `supported`, a challenge whose version lies outside the offer, stale proof, host mismatch, manifest mismatch, identity change, timeout; liveness (a ping flood and a ping sooner than half the interval, each `protocol.unexpected`; a stray `pong`, ignored); and squatter cases, each run against an unverified and a verified server with the expected SDK state: an `error` before the challenge carrying `auth.proof-invalid`, `host.access-revoked`, `auth.registration-mismatch`, `protocol.version-unsupported` or `host.paused` with `retryAfterMs` 300,000; and a challenge with a garbage proof. |
| `fixtures/wire/v3/enums.json` | Every enumeration token and its C# value, including reserved values (`LlmUnavailable` = 6). |
| `fixtures/wire/v3/capabilities.json` | The capability registry of §7.4. |
| `fixtures/wire/v3/token-bucket.json` | Arrival sequences with frame sizes and the expected take/refuse decisions and `retryAfterMs`, including multi-token frames. |

**Transcript vector** (from `transcript.json`). Secret, in Base64:
`AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=` (bytes 0x00–0x1F). Host
`example-host` version `2026.10.1`, registration
`00112233445566778899aabbccddeeff`, client nonce
`QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE=`, server nonce
`QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=`, version 3, offer 3–3, no
capabilities, manifest hash of §3.9.

| Case | Server proof | Client proof |
|---|---|---|
| No capabilities | `eKdrI1jrc3Bicq7QMMaNRq3Ag1bURFuIqMPBb6r/Pfo=` | `/UEufRCAUwJZWK0QCrk6/tMoSGLasVQxQEMWxnLWE4w=` |
| Both sides list `x.test-echo` | `BCmR8OP6wx2emYIPUh7AxfXBZQpW8DLrexmsJI6BMWk=` | `oqgAzzTIYecUU29y9FrmbyUXpQ7iqESyQxE3b4OPvRc=` |
| Offer tampered to 3–4 | `yewRo8g86EGY3ltzMQV7gUKZ2pRG+YqzviCgMXOSiPw=` (differs, so the client refuses) | — |

User-hash vector: SID `S-1-5-21-1111111111-2222222222-3333333333-1001` →
`a8c06b3027d3fc4a`.

---

## Appendix B: algorithms

### B.1 JSON Pointer for diagnostics

Paths are built while walking the document, from member names and array
indices, escaped per RFC 6901, together with the line and column of the token
they resolve to. The truncation rule of §4.1 applies to unknown member names.

### B.2 Manifest hash

The manifest hash covers only the parts of a manifest that change what crosses
the wire, so a companion and a host disagree exactly when the protocol would
break, not when a description changed.

1. Take a valid manifest (§3).
2. Build its **contract projection**, a JSON object with exactly:
   - `schemaVersion`: the integer;
   - `id`: the extension id;
   - `contributions`: the contributions sorted by `id`, each
     `{ "id", "provides": sorted, "settings": sorted by id, each { "id", "kind", "choices": sorted choice values } }`,
     omitting `settings` when there are none.
3. Serialize it with the JSON Canonicalization Scheme (RFC 8785).
4. Hash the UTF-8 bytes with SHA-256 and write 64 lowercase hexadecimal digits.

Sorting is by ordinal UTF-16 code unit order. For the §3.9 example the
canonical form is:

```json
{"contributions":[{"id":"example.countdown/status","provides":["face"]},{"id":"example.countdown/timer","provides":["face","invoke"],"settings":[{"choices":["five-minutes","one-minute","twenty-five-minutes"],"id":"duration","kind":"Choice"},{"choices":["pause","reset","start"],"id":"mode","kind":"Choice"}]}],"id":"example.countdown","schemaVersion":3}
```

and the hash is
`1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f`.

### B.3 Token bucket

```text
state: tokens (real), last (monotonic time)
init:  tokens = burst; last = now
take(n): (n ≥ 1 tokens; a frame costs ceil(length / 1024), §7.10)
       tokens = min(burst, tokens + rate × (now − last) in seconds); last = now
       if tokens ≥ n: tokens = tokens − n; return accepted
       else: retryAfter = ceil((n − tokens) / rate × 1000) ms; return refused
```

Implementations use a monotonic clock (in .NET, `TimeProvider`), never
wall-clock time. The ranges of §7.10 guarantee that a bucket's burst is at
least the cost of the largest allowed frame, so every frame can eventually be
paid for.
