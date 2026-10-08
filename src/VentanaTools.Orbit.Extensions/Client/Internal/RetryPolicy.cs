// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>What the client does after a connection attempt ends.</summary>
internal enum RetryKind
{
    /// <summary>Wait <see cref="RetryDecision.Delay"/>, then connect again.</summary>
    Wait = 1,

    /// <summary>Connect again at once.</summary>
    Immediate = 2,

    /// <summary>Stop: retrying cannot help until something outside the client changes.</summary>
    Stop = 3,
}

/// <summary>The next state after an attempt.</summary>
internal readonly record struct RetryDecision(RetryKind Kind, TimeSpan Delay);

/// <summary>
/// The reason to state table and the backoff of contract §9.2. A code from a server that is not
/// verified can never stop the client or keep it waiting longer than
/// <see cref="CompanionClientOptions.MaxRetryDelay"/>.
/// </summary>
internal sealed class RetryPolicy
{
    private static readonly TimeSpan VerifiedRetryAfterCap = TimeSpan.FromMilliseconds(300_000);
    private readonly CompanionClientOptions _options;
    private readonly Func<double> _random;
    private int _failures;
    private bool _reloadUsed;

    public RetryPolicy(CompanionClientOptions options, Func<double>? random = null)
    {
        _options = options;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>
    /// Whether <paramref name="code"/> says the host is absent: it is not running, or the extension is
    /// turned off. The client then probes every few seconds at most
    /// (<see cref="CompanionClientOptions.HostAbsentMaxRetryDelay"/>) and connects once the host is back.
    /// </summary>
    internal static bool IsHostAbsent(ReasonCode code) => code == ReasonCode.HostNotRunning || code == ReasonCode.HostTurnedOff;

    /// <summary>A connection stayed up for <see cref="CompanionClientOptions.StableConnection"/>: the backoff starts again.</summary>
    public void Reset()
    {
        _failures = 0;
        _reloadUsed = false;
    }

    public RetryDecision Next(ReasonCode? reason, int? retryAfterMs, bool serverVerified)
    {
        if (reason is not { } code)
        {
            return Wait(Normal());
        }

        if (code.Value.StartsWith("pairing.", StringComparison.Ordinal))
        {
            return new RetryDecision(RetryKind.Stop, TimeSpan.Zero);
        }

        if (code == ReasonCode.AuthRegistrationMismatch || code == ReasonCode.ProtocolVersionUnsupported
            || code == ReasonCode.AuthHostMismatch || code == ReasonCode.AuthServerProofInvalid
            || code == ReasonCode.AuthProofInvalid || code == ReasonCode.HostAccessRevoked)
        {
            // auth.proof-invalid and host.access-revoked are only accepted after the challenge
            // verified, so they always come from a verified server; the check costs nothing.
            return serverVerified ? new RetryDecision(RetryKind.Stop, TimeSpan.Zero) : Wait(Normal());
        }

        if (code == ReasonCode.ManifestMismatch || code == ReasonCode.AuthIdentityChanged)
        {
            return Wait(_options.MaxRetryDelay);
        }

        if (IsHostAbsent(code))
        {
            return Wait(Min(Normal(), _options.HostAbsentMaxRetryDelay));
        }

        if (code == ReasonCode.HostPaused || (!code.IsKnown && retryAfterMs is not null))
        {
            var normal = Normal();
            var retryAfter = TimeSpan.FromMilliseconds(Math.Clamp(retryAfterMs ?? 0, 0, 300_000));
            return Wait(serverVerified
                ? Max(normal, Min(retryAfter, VerifiedRetryAfterCap))
                : Min(Max(normal, retryAfter), _options.MaxRetryDelay));
        }

        if (code == ReasonCode.HostReloaded && !_reloadUsed)
        {
            _reloadUsed = true;
            return new RetryDecision(RetryKind.Immediate, TimeSpan.Zero);
        }

        return Wait(Normal());
    }

    /// <summary>The normal backoff: the initial delay doubling per failure up to the maximum, jittered, never above the maximum.</summary>
    private TimeSpan Normal()
    {
        var exponent = Math.Min(_failures, 30);
        _failures++;
        var baseTicks = Math.Min(_options.InitialRetryDelay.Ticks * Math.Pow(2, exponent), _options.MaxRetryDelay.Ticks);
        var jitter = 1 + (_options.RetryJitter * ((2 * _random()) - 1));
        var ticks = Math.Min(baseTicks * jitter, _options.MaxRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)Math.Max(0, ticks));
    }

    private static RetryDecision Wait(TimeSpan delay) => new(RetryKind.Wait, delay);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
