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

- **The pipe.** A host creates each registration's named pipe owned by the
  current user's account and open to it only, with a Medium mandatory label
  that refuses lower-integrity processes every kind of open, rejecting remote
  clients, as the first and only instance of its name, so a sandboxed process
  of the same user can neither use that one instance nor hold it to keep the
  companion out. `orbit-ext simulate` creates its pipe the same way. Before it
  writes a byte, the .NET SDK checks that the pipe is owned by the current
  user's account (its user SID, checked explicitly: .NET's `CurrentUserOnly`
  option compares the token's default owner, which is the Administrators group
  when elevated), that the server process runs as the current user at an
  integrity level no lower than its own, and that the pipe's mandatory label
  does not show a lower-integrity creator.
  The label check still refuses a lower-integrity process that has denied
  everyone access to itself so that the process check cannot open it: Windows
  labels the pipes such a process creates, and it can neither raise nor remove
  that label. The SDK also requests identification-level impersonation, so a
  server can learn who the client is but cannot act as it. Together this
  protects .NET companions against other users, web content and lower-integrity
  or sandboxed processes, even when a pairing file lacks the label described
  below. Nothing protects against a program that already runs as the same user
  at the same integrity level
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
  by default to `%USERPROFILE%\.ventana\pairings\<host-id>\`. The contract
  requires the host to make it readable only by that person and to give it, and
  every folder it creates for it, a Medium mandatory label with no-read-up, so
  that lower-integrity processes, such as sandboxed browser renderers, cannot
  read it ([contract §6.2](docs/design/contract-v3.md#62-encoding));
  `orbit-ext simulate` does this for its temporary pairing file. An access-control list
  alone does not keep out a Low-integrity process of the same user: it can read
  a pairing file that lacks the label. The .NET SDK still refuses such a process
  as a server, but the Node SDK cannot (see above). The SDKs never log or print
  the secret, and `orbit-ext pack` refuses to pack a pairing file. Never commit
  or share one.
- **Packages are inert.** Installing a package never runs anything. Readers
  verify the whole archive (sizes, entry names, ZIP structure, CRCs, the SHA-256
  inventory and the manifest) before a host writes a file, and hosts carry the
  zone of origin (Mark-of-the-Web) from the package to every extracted file.
- **Untrusted text.** Every manifest and face string is extension-supplied text:
  readers refuse control, bidirectional-override and invisible format characters
  in declarations, and hosts clean face text before they show it.
