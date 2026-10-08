// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>Pipe servers for the transport tests, secured the way a host secures its pipe (contract §7.1).</summary>
[SupportedOSPlatform("windows")]
internal static class TestPipes
{
    /// <summary>
    /// A host's pipe security: the current user's account, its token's user SID, as the only entry of a
    /// protected access-control list and, unless <paramref name="owner"/> names another principal, as the
    /// owner. A host sets the owner explicitly: <see cref="PipeOptions.CurrentUserOnly"/> would make the
    /// token's default owner the pipe's owner, and in an elevated process that is the Administrators
    /// group, whose pipe the SDK refuses.
    /// </summary>
    public static PipeSecurity HostSecurity(SecurityIdentifier? owner = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new PipeSecurity();
        security.SetOwner(owner ?? user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The first and only instance of the pipe <paramref name="name"/>, asynchronous and in byte mode, with
    /// <paramref name="security"/>, by default <see cref="HostSecurity"/>.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name, PipeSecurity? security = null) =>
        NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security ?? HostSecurity());
}
