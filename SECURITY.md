# Security

## Reporting a vulnerability

Report vulnerabilities privately through GitHub's private vulnerability
reporting: on this repository's **Security** tab, choose **Report a
vulnerability**. Private reporting is enabled on this repository, and it is the
only channel for security reports. Never put vulnerability details, credentials
or exploit code in a public issue, discussion or pull request.

Reports about the SDK, the tool, the samples, the contract and the wire protocol
are all welcome here, including problems you find in a host app's handling of
extensions; we route those to the right team.

Include the affected package and contract versions, the host app and its version
when relevant, your operating system, a minimal reproduction, and what you
expected and observed. Use disposable test credentials and synthetic documents.
**Never attach a real pairing file**, connection info, bridge credential, log
with personal data, or private document.

This is a preview: only the current version is maintained, and response-time
commitments and a stable support window have not been set yet.

## What the SDK protects, and what it cannot

A companion is an ordinary program that runs with the signed-in person's full
rights. Authentication proves which program holds a registration's credential;
it does not sandbox the companion, and no manifest member can restrict what it
does. People should install only extensions they trust, and hosts say so in
install review and consent.

- **The pipe.** A host creates each registration's named pipe for the current
  user only, as the first and only instance of its name. The .NET SDK refuses to
  write to a pipe the current user does not own, or whose server process runs as
  another user or at a lower integrity level than its own, and requests
  identification-level impersonation, so a server can learn who the client is
  but cannot act as it. Together this protects against other users, web content
  and lower-integrity or sandboxed processes. Nothing protects against a program
  that already runs as the same user at the same integrity level
  ([contract §7.1](docs/design/contract-v3.md#71-transport)).
- **The handshake.** The secret never crosses the pipe. Both sides prove
  knowledge of it with HMAC-SHA256 over a transcript that binds both offers, the
  negotiated version, the host's id and version, fresh nonces on both sides and
  the manifest hash, so a tampered offer, a replayed handshake or a reflected
  proof fails ([contract §7.3](docs/design/contract-v3.md#73-handshake)). A
  server that is not verified can never stop the SDK or keep it waiting longer
  than its maximum retry delay.
- **The Node SDK's limitation.** Node's `net.connect` cannot check who owns a
  named pipe and does not request identification-level impersonation. The Node
  SDK therefore treats every server as unverified until the server's challenge
  proof verifies, so a program that squats on the pipe name while the host is not
  running can neither stop it nor delay it beyond the maximum retry delay. It
  cannot prevent a squatter that holds `SeImpersonatePrivilege` (an account that
  is already highly privileged, such as a compromised service account) from
  impersonating the person after reading `hello`, which carries no secret. Nor
  can it refuse a squatter that runs as the same user at a lower integrity level
  and has read the pairing file: against such a process it relies on the host
  labelling pairing files so that lower-integrity processes cannot read them.
  Whether to ship an optional native check is decided before the Node SDK is
  published ([contract §10](docs/design/contract-v3.md#10-node-sdk)).
- **Pairing files are credentials.** A host writes one only when the person asks,
  by default to `%USERPROFILE%\.ventana\pairings\<host-id>\`, readable only by
  that person and labelled so that lower-integrity processes, such as sandboxed
  browser renderers, cannot read it
  ([contract §6.2](docs/design/contract-v3.md#62-encoding)). The SDKs never log or print the secret, and `orbit-ext pack`
  refuses to pack a pairing file. Never commit or share one.
- **Packages are inert.** Installing a package never runs anything. Readers
  verify the whole archive (sizes, entry names, ZIP structure, CRCs, the SHA-256
  inventory and the manifest) before a host writes a file, and hosts carry the
  zone of origin (Mark-of-the-Web) from the package to every extracted file.
- **Untrusted text.** Every manifest and face string is extension-supplied text:
  readers refuse control, bidirectional-override and invisible format characters
  in declarations, and hosts clean face text before they show it.
