// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;

namespace VentanaTools.Orbit.Extensions;

/// <summary>What happens when a reason code is raised (contract §8.2).</summary>
public enum Disposition
{
    /// <summary>An <c>error</c> frame is sent when possible, then the connection closes.</summary>
    Close = 1,

    /// <summary>An <c>error</c> frame, and the connection stays open.</summary>
    Advisory = 2,

    /// <summary>The item (for example a session) is refused; the connection stays open.</summary>
    Refuse = 3,

    /// <summary>The frame is discarded and counted.</summary>
    Ignore = 4,

    /// <summary>Never on the wire; raised where it happens.</summary>
    Local = 5,
}

/// <summary>What the catalog says about one reason code (contract §8.3).</summary>
public sealed class ReasonCodeInfo
{
    private const string HelpRoot = "https://dev.ventana.tools/go/";

    /// <summary>The disposition; <see cref="Disposition.Close"/> for an unknown code.</summary>
    public required Disposition Disposition { get; init; }

    /// <summary>Whether a peer that has not yet verified the sender's proof may receive it; false for an unknown code.</summary>
    public required bool PreAuthentication { get; init; }

    /// <summary>Whether the code counts toward a host's penalty box (contract §7.12); false for an unknown code.</summary>
    public required bool IsViolation { get; init; }

    /// <summary>The author fix from <c>fixtures/codes/reason-codes.json</c>; null for an unknown code.</summary>
    public string? Fix { get; init; }

    /// <summary>The help anchor, the code with <c>.</c> replaced by <c>-</c> (for example <c>auth-proof-invalid</c>); null for an unknown code.</summary>
    public string? HelpAnchor { get; init; }

    /// <summary>
    /// The help link <c>https://dev.ventana.tools/go/&lt;host-id&gt;/codes#&lt;anchor&gt;</c>, or null
    /// when the code has no anchor.
    /// </summary>
    /// <param name="hostId">The host id (contract §2.4) whose documentation the link opens.</param>
    /// <exception cref="ArgumentException"><paramref name="hostId"/> does not match the host-id grammar.</exception>
    public Uri? HelpUri(string hostId)
    {
        if (!TextRules.IsHostId(hostId))
        {
            throw new ArgumentException("The host id does not match the host-id grammar.", nameof(hostId));
        }

        return HelpAnchor is null ? null : new Uri(HelpRoot + hostId + "/codes#" + HelpAnchor, UriKind.Absolute);
    }
}

/// <summary>
/// A stable code that explains a runtime event (contract §8). The catalog is an open registry:
/// any code that matches the dotted-code grammar parses, and <see cref="IsKnown"/> says whether
/// this library's catalog has it. An unknown code carries the rules of contract §7.5: it closes,
/// and it is never terminal, a violation or advisory.
/// </summary>
public readonly record struct ReasonCode
{
    private readonly string? _value;

    private ReasonCode(string value) => _value = value;

    /// <summary>The code, for example <c>auth.proof-invalid</c>; empty for the default value.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Whether this library's catalog has the code.</summary>
    public bool IsKnown => _value is not null && ReasonCodeCatalog.Find(_value) is not null;

    /// <summary>The catalog's information, or the rules for an unknown code.</summary>
    public ReasonCodeInfo Info => (_value is null ? null : ReasonCodeCatalog.Find(_value)?.Info) ?? ReasonCodeCatalog.Unknown;

    /// <summary>The hello names a different registration than this pipe's.</summary>
    public static ReasonCode AuthRegistrationMismatch { get; } = new("auth.registration-mismatch");

    /// <summary>The companion's proof is wrong.</summary>
    public static ReasonCode AuthProofInvalid { get; } = new("auth.proof-invalid");

    /// <summary>The host's proof failed verification.</summary>
    public static ReasonCode AuthServerProofInvalid { get; } = new("auth.server-proof-invalid");

    /// <summary>The pipe is not owned by the current user, so nothing was written to it.</summary>
    public static ReasonCode AuthServerUnverified { get; } = new("auth.server-unverified");

    /// <summary>The challenge's host id differs from the pairing's.</summary>
    public static ReasonCode AuthHostMismatch { get; } = new("auth.host-mismatch");

    /// <summary>The companion closed after the challenge without authenticating.</summary>
    public static ReasonCode AuthAbandoned { get; } = new("auth.abandoned");

    /// <summary>The handshake did not finish within 5 seconds.</summary>
    public static ReasonCode AuthTimeout { get; } = new("auth.timeout");

    /// <summary>A different program than the one the registration's identity pin trusts connected.</summary>
    public static ReasonCode AuthIdentityChanged { get; } = new("auth.identity-changed");

    /// <summary>Reserved: the connecting process's package family name differs from the registration's.</summary>
    public static ReasonCode AuthPackageMismatch { get; } = new("auth.package-mismatch");

    /// <summary>A frame's length is 0, over the frame limit, or over 8,192 bytes before <c>ready</c>.</summary>
    public static ReasonCode FrameTooLarge { get; } = new("frame.too-large");

    /// <summary>A frame did not finish within 5 seconds.</summary>
    public static ReasonCode FrameTimeout { get; } = new("frame.timeout");

    /// <summary>A frame is not valid UTF-8.</summary>
    public static ReasonCode FrameUtf8Invalid { get; } = new("frame.utf8-invalid");

    /// <summary>A frame is not one JSON object within the depth limit.</summary>
    public static ReasonCode FrameJsonInvalid { get; } = new("frame.json-invalid");

    /// <summary>A member name appears twice in one object of a frame.</summary>
    public static ReasonCode FrameJsonDuplicate { get; } = new("frame.json-duplicate");

    /// <summary>The peer stopped reading for 5 seconds.</summary>
    public static ReasonCode FrameWriteTimeout { get; } = new("frame.write-timeout");

    /// <summary>No protocol version both sides support.</summary>
    public static ReasonCode ProtocolVersionUnsupported { get; } = new("protocol.version-unsupported");

    /// <summary>The hello is malformed or missing a member.</summary>
    public static ReasonCode ProtocolHelloInvalid { get; } = new("protocol.hello-invalid");

    /// <summary>A known message failed validation.</summary>
    public static ReasonCode ProtocolMessageInvalid { get; } = new("protocol.message-invalid");

    /// <summary>An unknown member with a name outside the member grammar, or a case variant of a known member.</summary>
    public static ReasonCode ProtocolMemberInvalid { get; } = new("protocol.member-invalid");

    /// <summary>An unknown <c>$type</c>.</summary>
    public static ReasonCode ProtocolVariantUnknown { get; } = new("protocol.variant-unknown");

    /// <summary>An unknown message type after authentication was ignored.</summary>
    public static ReasonCode ProtocolTypeUnknown { get; } = new("protocol.type-unknown");

    /// <summary>A message in the wrong phase or direction, a known code without the Pre flag before verification, or a ping that broke the one-outstanding rule.</summary>
    public static ReasonCode ProtocolUnexpected { get; } = new("protocol.unexpected");

    /// <summary>A feature of a capability that is not effective was used.</summary>
    public static ReasonCode ProtocolCapabilityNotNegotiated { get; } = new("protocol.capability-not-negotiated");

    /// <summary>No <c>pong</c> within the pong timeout.</summary>
    public static ReasonCode ProtocolPingTimeout { get; } = new("protocol.ping-timeout");

    /// <summary>A <c>pong</c> matched no outstanding ping and was ignored.</summary>
    public static ReasonCode ProtocolPongUnknown { get; } = new("protocol.pong-unknown");

    /// <summary><c>goodForSeconds</c> is not 1 to 86,400.</summary>
    public static ReasonCode FaceLifetimeInvalid { get; } = new("face.lifetime-invalid");

    /// <summary>The picture's glyph breaks the glyph rule.</summary>
    public static ReasonCode FaceGlyphInvalid { get; } = new("face.glyph-invalid");

    /// <summary>Unknown <c>state</c> token.</summary>
    public static ReasonCode FaceStateInvalid { get; } = new("face.state-invalid");

    /// <summary>Unknown <c>failure</c> token, or a capability-gated token whose capability is not effective.</summary>
    public static ReasonCode FaceFailureInvalid { get; } = new("face.failure-invalid");

    /// <summary>A face for a contribution that does not provide <c>face</c>.</summary>
    public static ReasonCode FaceNotProvided { get; } = new("face.not-provided");

    /// <summary>A face for an ended or unknown session was ignored.</summary>
    public static ReasonCode FaceSessionUnknown { get; } = new("face.session-unknown");

    /// <summary>Unknown <c>outcome</c> token.</summary>
    public static ReasonCode InvokeOutcomeInvalid { get; } = new("invoke.outcome-invalid");

    /// <summary><c>failure</c> with an outcome other than <c>Failed</c>.</summary>
    public static ReasonCode InvokeFailureUnexpected { get; } = new("invoke.failure-unexpected");

    /// <summary>An <c>invoke</c> reused a live or recent request id.</summary>
    public static ReasonCode InvokeReplay { get; } = new("invoke.replay");

    /// <summary>A <c>result</c> for an unknown or completed request was ignored.</summary>
    public static ReasonCode InvokeRequestUnknown { get; } = new("invoke.request-unknown");

    /// <summary>No result within the host's deadline; <c>cancel</c> was sent.</summary>
    public static ReasonCode InvokeTimeout { get; } = new("invoke.timeout");

    /// <summary>Too many invocations were already pending; the request was refused.</summary>
    public static ReasonCode InvokeBusy { get; } = new("invoke.busy");

    /// <summary>The person picked an item while the companion was not connected.</summary>
    public static ReasonCode InvokeNotConnected { get; } = new("invoke.not-connected");

    /// <summary>The soft budget is used up; faces are being coalesced.</summary>
    public static ReasonCode RateThrottled { get; } = new("rate.throttled");

    /// <summary>The hard budget is used up.</summary>
    public static ReasonCode RateExceeded { get; } = new("rate.exceeded");

    /// <summary>The receiver's output queue filled because the peer is not reading.</summary>
    public static ReasonCode RateQueueFull { get; } = new("rate.queue-full");

    /// <summary>The companion's manifest hash differs from the host's admitted manifest.</summary>
    public static ReasonCode ManifestMismatch { get; } = new("manifest.mismatch");

    /// <summary>The companion refused a session: it is at its limit.</summary>
    public static ReasonCode SessionCapacity { get; } = new("session.capacity");

    /// <summary>The companion refused a session for a contribution it does not know.</summary>
    public static ReasonCode SessionUnknownContribution { get; } = new("session.unknown-contribution");

    /// <summary>The companion refused a session whose settings do not match its manifest.</summary>
    public static ReasonCode SessionSettingsInvalid { get; } = new("session.settings-invalid");

    /// <summary>A <c>startSession</c> reused a live or recent session id.</summary>
    public static ReasonCode SessionReplay { get; } = new("session.replay");

    /// <summary>The host has its maximum number of sessions open and did not start this one.</summary>
    public static ReasonCode SessionHostLimit { get; } = new("session.host-limit");

    /// <summary>The author's handler threw.</summary>
    public static ReasonCode SessionHandlerFaulted { get; } = new("session.handler-faulted");

    /// <summary>A handler did not end within 5 seconds of cancellation.</summary>
    public static ReasonCode SessionHandlerStalled { get; } = new("session.handler-stalled");

    /// <summary>No pairing file was found in any searched location.</summary>
    public static ReasonCode PairingMissing { get; } = new("pairing.missing");

    /// <summary>The pairing file is over 4,096 bytes.</summary>
    public static ReasonCode PairingTooLarge { get; } = new("pairing.too-large");

    /// <summary>The pairing file is not UTF-8.</summary>
    public static ReasonCode PairingEncoding { get; } = new("pairing.encoding");

    /// <summary><c>pairingVersion</c> is not 3.</summary>
    public static ReasonCode PairingVersionUnsupported { get; } = new("pairing.version-unsupported");

    /// <summary><c>mode</c> is not one this SDK implements.</summary>
    public static ReasonCode PairingModeUnsupported { get; } = new("pairing.mode-unsupported");

    /// <summary><c>extensionId</c> differs from the manifest's id.</summary>
    public static ReasonCode PairingExtensionMismatch { get; } = new("pairing.extension-mismatch");

    /// <summary><c>hostId</c> is not in the manifest's <c>hosts</c>.</summary>
    public static ReasonCode PairingHostNotListed { get; } = new("pairing.host-not-listed");

    /// <summary><c>pipeName</c> breaks the grammar or does not match.</summary>
    public static ReasonCode PairingPipeNameInvalid { get; } = new("pairing.pipe-name-invalid");

    /// <summary><c>registrationId</c> is not a GUID in wire form.</summary>
    public static ReasonCode PairingRegistrationInvalid { get; } = new("pairing.registration-invalid");

    /// <summary><c>secret</c> is not 32 bytes of canonical Base64.</summary>
    public static ReasonCode PairingSecretInvalid { get; } = new("pairing.secret-invalid");

    /// <summary>The pipe does not exist, or did not accept within 5 seconds.</summary>
    public static ReasonCode HostNotRunning { get; } = new("host.not-running");

    /// <summary>Another companion is already connected to this registration.</summary>
    public static ReasonCode HostPipeBusy { get; } = new("host.pipe-busy");

    /// <summary>The host could not create the pipe.</summary>
    public static ReasonCode HostListenerFailed { get; } = new("host.listener-failed");

    /// <summary>The host's listener stopped unexpectedly and is restarting.</summary>
    public static ReasonCode HostListenerFaulted { get; } = new("host.listener-faulted");

    /// <summary>The host could not identify its own installation.</summary>
    public static ReasonCode HostEditionUnknown { get; } = new("host.edition-unknown");

    /// <summary>The host started from a link and has not opened extension pipes yet.</summary>
    public static ReasonCode HostInactive { get; } = new("host.inactive");

    /// <summary>The host already has its maximum number of extensions.</summary>
    public static ReasonCode HostRegistrationLimit { get; } = new("host.registration-limit");

    /// <summary>The penalty box is refusing connections.</summary>
    public static ReasonCode HostPaused { get; } = new("host.paused");

    /// <summary>The host is closing.</summary>
    public static ReasonCode HostShuttingDown { get; } = new("host.shutting-down");

    /// <summary>The person turned the extension off.</summary>
    public static ReasonCode HostTurnedOff { get; } = new("host.turned-off");

    /// <summary>The person revoked access.</summary>
    public static ReasonCode HostAccessRevoked { get; } = new("host.access-revoked");

    /// <summary>The host reloaded the manifest in developer mode and closed the connection to apply it.</summary>
    public static ReasonCode HostReloaded { get; } = new("host.reloaded");

    /// <summary>The host's extension catalog was restored from its backup.</summary>
    public static ReasonCode HostCatalogRecovered { get; } = new("host.catalog-recovered");

    /// <summary>The host's extension catalog and its backup are unreadable.</summary>
    public static ReasonCode HostCatalogUnreadable { get; } = new("host.catalog-unreadable");

    /// <summary>The peer sent an <c>error</c> code this receiver does not know; logged in place of the received string.</summary>
    public static ReasonCode PeerUnknownCode { get; } = new("peer.unknown-code");

    /// <summary>
    /// Parses any code that matches the dotted-code grammar (contract §2.4), known or not.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="code">The code, when the text is grammar-valid.</param>
    /// <returns>Whether <paramref name="value"/> is a grammar-valid dotted code.</returns>
    public static bool TryParse(string? value, out ReasonCode code)
    {
        if (!Grammars.IsDottedCode(value))
        {
            code = default;
            return false;
        }

        code = new ReasonCode(ReasonCodeCatalog.Find(value)?.Code ?? value);
        return true;
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    public override string ToString() => Value;
}

/// <summary>One row of the reason-code catalog (contract §8.3).</summary>
internal sealed class ReasonCodeEntry
{
    public required string Code { get; init; }

    /// <summary>Where the code is seen: L host log, R registration row, D developer details, E error frame, S SDK status.</summary>
    public required string SeenIn { get; init; }

    public required string Meaning { get; init; }

    public required ReasonCodeInfo Info { get; init; }
}

/// <summary>
/// The reason-code catalog of contract §8.3. The fixture <c>fixtures/codes/reason-codes.json</c>
/// is generated from this table.
/// </summary>
internal static class ReasonCodeCatalog
{
    public static ReasonCodeInfo Unknown { get; } = new()
    {
        Disposition = Disposition.Close,
        PreAuthentication = false,
        IsViolation = false,
    };

    public static IReadOnlyList<ReasonCodeEntry> All { get; } =
    [
        C("auth.registration-mismatch", "L R E S", true, true, "The hello names a different registration than this pipe's.", "Save connection info again and use the new file."),
        C("auth.proof-invalid", "L R E S", false, true, "The companion's proof is wrong: its pairing is out of date or for another registration, or a per-launch credential was used up.", "Save connection info again; restart the companion."),
        L("auth.server-proof-invalid", "S", false, "The host's proof failed verification: the pairing is out of date, or the pipe is not the host.", "Save connection info again."),
        L("auth.server-unverified", "S", false, "The pipe is not owned by the current user, so the SDK wrote nothing to it. Another program may hold the pipe name while the host is not running.", "Start the host; if this persists, restart the PC."),
        L("auth.host-mismatch", "S", false, "The challenge's host id differs from the pairing's hostId.", "Use connection info from this host."),
        L("auth.abandoned", "L R", true, "The companion closed after the challenge without authenticating; most often an out-of-date pairing.", "Save connection info again."),
        C("auth.timeout", "L R E S", true, false, "The handshake did not finish within 5 seconds.", "Check the companion's handshake code."),
        C("auth.identity-changed", "L R E S", false, false, "A different program than the one this registration's identity pin trusts connected.", "In the host, allow the new program or revoke access."),
        C("auth.package-mismatch", "L R E S", false, true, "Reserved: the connecting process's package family name differs from the registration's.", null),
        C("frame.too-large", "L R E S", true, true, "Frame length 0, over limits.maxFrameBytes, or over 8,192 bytes before ready.", "Send smaller messages."),
        C("frame.timeout", "L R E S", true, true, "A frame did not finish within 5 seconds.", "Write each frame in one go."),
        C("frame.utf8-invalid", "L R E S", true, true, "The frame is not valid UTF-8.", "Encode JSON as UTF-8."),
        C("frame.json-invalid", "L R E S", true, true, "Not one JSON object within the depth limit.", "Send one object per frame."),
        C("frame.json-duplicate", "L R E S", true, true, "A member name appears twice in one object.", "Remove the duplicate."),
        C("frame.write-timeout", "L R S", true, true, "The peer stopped reading for 5 seconds.", "Keep reading the pipe."),
        C("protocol.version-unsupported", "L R E S", true, true, "No protocol version both sides support; supported gives the host's range. Also raised locally when a challenge picks a version outside the companion's offer.", "Update the SDK or the host."),
        C("protocol.hello-invalid", "L R E S", true, true, "The hello is malformed or missing a member.", "Use an SDK, or follow the hello message definition."),
        C("protocol.message-invalid", "L R E S", true, true, "A known message failed validation, including a malformed challenge and a ready whose limits are out of range.", "Check the message against the message table."),
        C("protocol.member-invalid", "L R E S", true, true, "An unknown member with a name outside the member grammar, or a case variant of a known member.", "Remove it, or fix its spelling."),
        C("protocol.variant-unknown", "L R E S", true, true, "An unknown $type.", "Use the documented variants."),
        I("protocol.type-unknown", "D", "An unknown message type after authentication was ignored.", "None needed; check for typos."),
        C("protocol.unexpected", "L R E S", true, true, "A message in the wrong phase or direction, a known code without the Pre flag before verification, or a ping that broke the one-outstanding rule.", "Follow the handshake order; ping only when idle."),
        C("protocol.capability-not-negotiated", "L R E S", false, true, "A feature of a capability that is not effective was used.", "Check the effective capabilities in ready."),
        C("protocol.ping-timeout", "L R E S", false, true, "No pong within the pong timeout.", "Answer ping promptly; don't block the reader."),
        I("protocol.pong-unknown", "D", "A pong matched no outstanding ping and was ignored.", "None needed; send pong only in answer to ping."),
        C("face.lifetime-invalid", "L R E S", false, true, "goodForSeconds is not 1 to 86,400.", "Use a finite lifetime."),
        C("face.glyph-invalid", "L R E S", false, true, "The picture's glyph breaks the glyph rule.", "Use one private-use glyph."),
        C("face.state-invalid", "L R E S", false, true, "Unknown state token.", "Use None, Playing, Paused, On or Off."),
        C("face.failure-invalid", "L R E S", false, true, "Unknown failure token, or a capability-gated token whose capability is not effective.", "Use a documented failure token."),
        C("face.not-provided", "L R E S", false, true, "A face for a contribution that does not provide face.", "Add face to provides, or stop publishing."),
        I("face.session-unknown", "D", "A face for an ended or unknown session was ignored.", "None needed after stopSession."),
        C("invoke.outcome-invalid", "L R E S", false, true, "Unknown outcome token.", "Use a documented outcome token."),
        C("invoke.failure-unexpected", "L R E S", false, true, "failure with an outcome other than Failed.", "Send failure only with Failed."),
        C("invoke.replay", "L R E S", false, true, "An invoke reused a live or recent request id.", "Hosts only."),
        I("invoke.request-unknown", "D", "A result for an unknown or completed request was ignored.", "None needed after cancel."),
        L("invoke.timeout", "L D", false, "No result within the host's deadline; cancel was sent.", "Finish faster, or honour cancellation."),
        L("invoke.busy", "L R", false, "maxPendingInvokes invocations were already pending; the person's request was refused.", "Answer invocations promptly."),
        L("invoke.not-connected", "L R", false, "The person picked an item while the companion was not connected.", "Start the companion."),
        A("rate.throttled", "D E S", "The soft budget is used up; faces are being coalesced.", "Publish only on change."),
        C("rate.exceeded", "L R E S", false, true, "The hard budget is used up.", "Publish far less often, and in smaller frames."),
        C("rate.queue-full", "L R S", false, true, "The receiver's output queue filled because the peer is not reading.", "Keep reading the pipe."),
        C("manifest.mismatch", "L R E S", false, false, "The companion's manifest hash differs from the host's admitted manifest.", "Reload the manifest in the host, or update the companion's extension.json."),
        R("session.capacity", "L D S", "The companion refused a session: it is at its limit.", "Make handlers end promptly when cancelled."),
        R("session.unknown-contribution", "L D S", "The companion refused a session for a contribution it does not know.", "Align both manifests."),
        R("session.settings-invalid", "L D S", "The companion refused a session whose settings do not match its manifest.", "Align both manifests."),
        C("session.replay", "L R E S", false, true, "A startSession reused a live or recent session id.", "Hosts only."),
        L("session.host-limit", "L R", false, "The host has maxSessions sessions open and did not start this one.", "Fewer placements."),
        L("session.handler-faulted", "S", false, "The author's handler threw. A session handler's face is cleared; an invocation answers Failed.", "Fix the exception (the SDK passes it to the author)."),
        L("session.handler-stalled", "S", false, "A handler did not end within 5 seconds of cancellation.", "Pass the cancellation token to every await."),
        L("pairing.missing", "S", false, "No pairing file was found in any searched location.", "In the host, choose Save connection info."),
        L("pairing.too-large", "S", false, "The pairing file is over 4,096 bytes.", "Save connection info again."),
        L("pairing.encoding", "S", false, "The pairing file is not UTF-8 (often UTF-16 from a shell redirect).", "Use the host's Save connection info."),
        L("pairing.version-unsupported", "S", false, "pairingVersion is not 3.", "Save connection info from a current host."),
        L("pairing.mode-unsupported", "S", false, "mode is not one this SDK implements.", "Update the SDK."),
        L("pairing.extension-mismatch", "S", false, "extensionId differs from the manifest's id.", "Use this extension's connection info."),
        L("pairing.host-not-listed", "S", false, "hostId is not in the manifest's hosts.", "Add the host id to hosts."),
        L("pairing.pipe-name-invalid", "S", false, "pipeName breaks the grammar or does not match.", "Save connection info again."),
        L("pairing.registration-invalid", "S", false, "registrationId is not a GUID in wire form.", "Save connection info again."),
        L("pairing.secret-invalid", "S", false, "secret is not 32 bytes of canonical Base64.", "Save connection info again."),
        L("host.not-running", "S", false, "The pipe does not exist, or did not accept within 5 seconds: the host is not running, or the extension is off.", "Start the host and turn the extension on."),
        L("host.pipe-busy", "S", false, "Another companion is already connected to this registration.", "Run one copy of the companion."),
        L("host.listener-failed", "L R", false, "The host could not create the pipe (its name is in use).", "In the host, choose Retry."),
        L("host.listener-faulted", "L", false, "The host's listener stopped unexpectedly and is restarting.", "None; report if repeated."),
        L("host.edition-unknown", "L R", false, "The host could not identify its own installation, so it does not open extension pipes.", "Restart or reinstall the host."),
        L("host.inactive", "R", false, "The host started from a link and has not opened extension pipes yet.", "Open the host normally."),
        L("host.registration-limit", "L R", false, "The host already has its maximum number of extensions, developer-folder extensions included; this one was not added.", "Remove an extension first."),
        C("host.paused", "L R E S", true, false, "The penalty box is refusing connections; see retryAfterMs.", "Fix the violations shown; choose Retry in the host."),
        C("host.shutting-down", "E S", true, false, "The host is closing.", "None; the SDK reconnects."),
        C("host.turned-off", "E S", false, false, "The person turned the extension off. The credential stays valid.", "None; it reconnects when turned on."),
        C("host.access-revoked", "E S", false, false, "The person revoked access. The credential is gone.", "Get new connection info after access is allowed again."),
        C("host.reloaded", "E S", false, false, "The host reloaded the manifest in developer mode and closed the connection to apply it.", "None; the SDK reconnects at once."),
        L("host.catalog-recovered", "L R", false, "The host's extension catalog was unreadable and was restored from its backup; every restored extension is off and needs consent again.", "Turn each extension on again, and save new connection info."),
        L("host.catalog-unreadable", "L R", false, "The catalog and its backup are unreadable; extension management is read-only until reset.", "In the host, choose Reset extensions."),
        L("peer.unknown-code", "L D", false, "The peer sent an error code this receiver does not know. Logged in place of the received string.", "Update the host or the SDK."),
    ];

    private static readonly FrozenDictionary<string, ReasonCodeEntry> ByCode =
        All.ToFrozenDictionary(entry => entry.Code, StringComparer.Ordinal);

    public static ReasonCodeEntry? Find(string code) => ByCode.GetValueOrDefault(code);

    private static ReasonCodeEntry C(string code, string seenIn, bool pre, bool violation, string meaning, string? fix) =>
        Entry(code, seenIn, Disposition.Close, pre, violation, meaning, fix);

    private static ReasonCodeEntry L(string code, string seenIn, bool violation, string meaning, string? fix) =>
        Entry(code, seenIn, Disposition.Local, false, violation, meaning, fix);

    private static ReasonCodeEntry I(string code, string seenIn, string meaning, string? fix) =>
        Entry(code, seenIn, Disposition.Ignore, false, false, meaning, fix);

    private static ReasonCodeEntry A(string code, string seenIn, string meaning, string? fix) =>
        Entry(code, seenIn, Disposition.Advisory, false, false, meaning, fix);

    private static ReasonCodeEntry R(string code, string seenIn, string meaning, string? fix) =>
        Entry(code, seenIn, Disposition.Refuse, false, false, meaning, fix);

    private static ReasonCodeEntry Entry(string code, string seenIn, Disposition disposition, bool pre, bool violation,
        string meaning, string? fix) => new()
    {
        Code = code,
        SeenIn = seenIn,
        Meaning = meaning,
        Info = new ReasonCodeInfo
        {
            Disposition = disposition,
            PreAuthentication = pre,
            IsViolation = violation,
            Fix = fix,
            HelpAnchor = code.Replace('.', '-'),
        },
    };
}
