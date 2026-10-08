// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>Which watched file a <see cref="ICompanionAppOutput.Watching"/> report is about.</summary>
internal enum AppFile
{
    Manifest = 1,
    Pairing = 2,
}

/// <summary>
/// Where <see cref="CompanionApp"/> reports what it does. The default writes the status lines of
/// contract §9.2 to <see cref="CompanionAppOptions.Output"/>; the Generic Host add-on logs the same
/// events instead (contract §9.5), so the two can never report different things.
/// </summary>
internal interface ICompanionAppOutput
{
    /// <summary>A recognized option has no value; <paramref name="usage"/> is the sentence, such as <c>--manifest needs a value.</c></summary>
    void Usage(string usage);

    /// <summary>The run failed in a way the contract does not describe; it ends with exit code 1.</summary>
    void Unexpected(Exception error);

    /// <summary>The manifest file could not be read.</summary>
    void ManifestUnreadable(string path, Exception error);

    /// <summary>One error diagnostic of an invalid manifest file.</summary>
    void ManifestInvalid(string path, Diagnostic diagnostic);

    /// <summary>The app waits for <paramref name="path"/> to appear or change.</summary>
    void Watching(string path, AppFile file);

    /// <summary>A <see cref="ContributionRouter"/> maps no handler for a contribution of the manifest.</summary>
    void Unmapped(string contributionId);

    /// <summary>
    /// The app's own status while no valid pairing file is found. <paramref name="detail"/> is the
    /// reader's first error when its code is not a reason code the catalog knows.
    /// </summary>
    void PairingStatus(ConnectionState state, ReasonCode code, Diagnostic? detail, HostInfo? host);

    /// <summary>A status of the client.</summary>
    void Status(StatusChangedEventArgs status, HostInfo? host);

    /// <summary>The peer's fixed error text, cleaned to printable ASCII; reported only with <c>--verbose</c>.</summary>
    void HostMessage(string text);

    /// <summary>An author's handler misbehaved.</summary>
    void Fault(HandlerFaultedEventArgs fault, HostInfo? host);

    /// <summary>A <see cref="CompanionAppOptions.StatusChanged"/> or <see cref="CompanionAppOptions.HandlerFaulted"/> callback threw; reported once.</summary>
    void CallbackFaulted(Exception error);
}

/// <summary>The status lines of contract §9.2, written to a <see cref="TextWriter"/>.</summary>
internal sealed class TextWriterAppOutput : ICompanionAppOutput
{
    private readonly TextWriter _writer;

    public TextWriterAppOutput(TextWriter writer) => _writer = writer;

    // The usage sentence alone, as the Node SDK prints it.
    public void Usage(string usage) => _writer.WriteLine("ventana: " + usage);

    public void Unexpected(Exception error)
    {
        _writer.WriteLine("ventana: unexpected " + error.GetType().FullName + ": " + error.Message);
        _writer.WriteLine(error.StackTrace);
    }

    public void ManifestUnreadable(string path, Exception error) =>
        _writer.WriteLine("ventana: cannot read the manifest " + path + " (" + error.GetType().Name + ")");

    public void ManifestInvalid(string path, Diagnostic diagnostic) => _writer.WriteLine("ventana: " + path + ": " + diagnostic);

    public void Watching(string path, AppFile file) => _writer.WriteLine("ventana: watching " + path);

    public void Unmapped(string contributionId) => _writer.WriteLine("ventana: no handler is mapped for " + contributionId);

    public void PairingStatus(ConnectionState state, ReasonCode code, Diagnostic? detail, HostInfo? host) =>
        _writer.WriteLine(AppRunner.StatusLine(state, code, host?.Id, host?.DisplayName) + (detail is null ? string.Empty : " " + detail));

    public void Status(StatusChangedEventArgs status, HostInfo? host) =>
        _writer.WriteLine(AppRunner.StatusLine(status.State, status.Reason, host?.Id, host?.DisplayName));

    public void HostMessage(string text) => _writer.WriteLine("ventana: host message: " + text);

    public void Fault(HandlerFaultedEventArgs fault, HostInfo? host)
    {
        _writer.WriteLine(AppRunner.FaultLine(fault, host?.Id, host?.DisplayName));
        if (fault.Exception is { } exception)
        {
            _writer.WriteLine(exception.ToString());
        }
    }

    public void CallbackFaulted(Exception error) =>
        _writer.WriteLine("ventana: a status or fault callback threw " + error.GetType().FullName + ": " + error.Message);
}
