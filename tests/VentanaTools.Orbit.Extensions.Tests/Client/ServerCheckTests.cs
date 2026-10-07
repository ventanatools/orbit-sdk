// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>
/// The pipe-label half of the server check (contract §7.1). The end-to-end cases, with a Low-integrity
/// squatter, are in <see cref="CompanionPeerTests"/>.
/// </summary>
public sealed class ServerCheckTests
{
    private const int Verified = (int)ServerCheck.Verified;
    private const int Unchecked = (int)ServerCheck.Unchecked;
    private const int Refused = (int)ServerCheck.Refused;
    private const uint Untrusted = 0x0000;
    private const uint Low = 0x1000;
    private const uint Medium = 0x2000;
    private const uint High = 0x3000;

    [Theory]
    [InlineData(Verified, Medium, Medium, Verified)]
    [InlineData(Verified, Medium, Low, Verified)]
    [InlineData(Verified, Low, Low, Verified)]
    [InlineData(Verified, Medium, High, Verified)] // A pipe created above Medium carries no label; its process passed.
    [InlineData(Verified, Low, Medium, Refused)] // The label, which its creator cannot raise, outranks a process id.
    [InlineData(Verified, Untrusted, Low, Refused)]
    [InlineData(Unchecked, Medium, Medium, Unchecked)] // Unverified until its challenge proof verifies.
    [InlineData(Unchecked, Low, Low, Unchecked)]
    [InlineData(Unchecked, Low, Medium, Refused)] // A Low squatter that denied everyone access to its own process.
    [InlineData(Unchecked, Medium, High, Refused)] // Without the process check, the label must reach the client's level.
    public void ThePipeLabelMustReachTheLevelTheProcessCheckLeaves(int process, uint label, uint own, int expected)
    {
        Assert.Equal((ServerCheck)expected, PipeNatives.WithLabel((ServerCheck)process, label, own));
    }

    [Fact]
    public void ALabelOrOwnLevelThatCannotBeReadRefusesTheServer()
    {
        Assert.Equal(ServerCheck.Refused, PipeNatives.WithLabel(ServerCheck.Verified, null, Medium));
        Assert.Equal(ServerCheck.Refused, PipeNatives.WithLabel(ServerCheck.Unchecked, null, Medium));
        Assert.Equal(ServerCheck.Refused, PipeNatives.WithLabel(ServerCheck.Verified, Medium, null));
        Assert.Equal(ServerCheck.Refused, PipeNatives.WithLabel(ServerCheck.Refused, High, Medium));
    }

    [WindowsTheory]
    [InlineData("D:(A;;GA;;;WD)", Medium)] // No label: Medium.
    [InlineData("S:", Medium)]
    [InlineData("S:(AU;SA;GA;;;WD)", Medium)] // Only an audit entry.
    [InlineData("S:(ML;;NW;;;LW)", Low)]
    [InlineData("S:(ML;;NW;;;S-1-16-0)", Untrusted)]
    [InlineData("S:(ML;;NWNR;;;HI)", High)]
    [InlineData("S:(ML;;NW;;;ME)(ML;;NW;;;LW)", Low)] // The lowest label counts.
    [InlineData("S:(ML;OIIO;NW;;;LW)", Medium)] // An inherit-only label applies to children, not to the object.
    public void TheLowestLabelThatAppliesToTheObjectIsItsLevel(string descriptor, uint expected)
    {
        Assert.Equal(expected, PipeNatives.LowestLabel(new RawSecurityDescriptor(descriptor)));
    }

    [WindowsFact]
    public async Task APipeThisProcessCreatesCarriesNoLabelAndItsServerIsVerified()
    {
        var name = "VentanaTools.Tests." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5_000);
        await accept;

        Assert.Equal(PipeNatives.MediumIntegrity, PipeNatives.PipeLabel(client.SafePipeHandle));
        Assert.Equal(ServerCheck.Verified, PipeNatives.CheckServer(client.SafePipeHandle));
    }

    [WindowsFact]
    public void AHandleWhoseLabelCannotBeReadHasNoLevel()
    {
        using var invalid = new SafeFileHandle(0, ownsHandle: false);
        Assert.Null(PipeNatives.PipeLabel(invalid));
    }
}
