// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// Runs an extension's contributions. For advanced use; derive from
/// <see cref="ContributionHandler"/> instead, so the compiler catches a misspelled method.
/// </summary>
/// <remarks>
/// Both methods run off the pipe reader, on the thread pool. An
/// <see cref="OperationCanceledException"/> thrown after the token was cancelled is a normal
/// completion. Any other exception raises <see cref="CompanionClient.HandlerFaulted"/>: a session
/// handler's face is cleared, and an invocation is answered <see cref="Outcome.Failed"/>.
/// </remarks>
public interface IContributionHandler
{
    /// <summary>
    /// Runs for the lifetime of one session. A widget publishes its face here and keeps running
    /// while it has updates; the token is cancelled when the host stops the session or the
    /// connection ends, and the handler must then end within 5 seconds.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancelled when the session ends.</param>
    /// <returns>A task that completes when the handler ends. The session stays invocable afterwards.</returns>
    Task RunSessionAsync(Session session, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one invocation. The token is cancelled when the host cancels it, the session stops,
    /// the connection ends, or the SDK's deadline (the host's <c>invokeTimeoutMs</c> minus 3
    /// seconds) passes. Check the token again just before any external change.
    /// </summary>
    /// <param name="invocation">The invocation and its session.</param>
    /// <param name="cancellationToken">Cancelled when the invocation is abandoned.</param>
    /// <returns>The result.</returns>
    Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// The base class for contribution handlers: override <see cref="RunSessionAsync"/> for a widget,
/// <see cref="InvokeAsync"/> for an action, or both. The defaults complete at once and answer
/// <see cref="InvokeResult.Unsupported"/>.
/// </summary>
/// <example>
/// <code>
/// return await CompanionApp.RunAsync(args, new Greeter());
///
/// sealed class Greeter : ContributionHandler
/// {
///     public override Task&lt;InvokeResult&gt; InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
///     {
///         var greeting = invocation.Session.Settings["greeting"];
///         Console.WriteLine($"{greeting}, world");
///         return Task.FromResult(InvokeResult.Done);
///     }
/// }
/// </code>
/// </example>
public abstract class ContributionHandler : IContributionHandler
{
    /// <inheritdoc/>
    /// <remarks>The default completes at once.</remarks>
    public virtual Task RunSessionAsync(Session session, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>The default answers <see cref="InvokeResult.Unsupported"/>.</remarks>
    public virtual Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(InvokeResult.Unsupported);
}

/// <summary>
/// Routes sessions and invocations to a handler per contribution id. Unmapped sessions complete
/// at once and unmapped invocations answer <see cref="InvokeResult.Unsupported"/>;
/// <see cref="CompanionApp"/> prints <see cref="FindUnmapped"/> at start.
/// </summary>
/// <remarks>Map everything before the router runs; mapping is not safe while sessions run.</remarks>
public sealed class ContributionRouter : IContributionHandler
{
    private readonly Dictionary<string, Func<Session, CancellationToken, Task>> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Invocation, CancellationToken, Task<InvokeResult>>> _invocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IContributionHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>Maps the session handler of one contribution.</summary>
    /// <param name="contributionId">The contribution id.</param>
    /// <param name="run">Runs for each session, like <see cref="IContributionHandler.RunSessionAsync"/>.</param>
    /// <returns>This router.</returns>
    /// <exception cref="ArgumentException">The contribution already has a session handler.</exception>
    public ContributionRouter MapSession(string contributionId, Func<Session, CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(contributionId);
        ArgumentNullException.ThrowIfNull(run);
        if (_handlers.ContainsKey(contributionId) || !_sessions.TryAdd(contributionId, run))
        {
            throw new ArgumentException("The contribution already has a session handler.", nameof(contributionId));
        }

        return this;
    }

    /// <summary>Maps the invocation handler of one contribution.</summary>
    /// <param name="contributionId">The contribution id.</param>
    /// <param name="invoke">Runs for each invocation, like <see cref="IContributionHandler.InvokeAsync"/>.</param>
    /// <returns>This router.</returns>
    /// <exception cref="ArgumentException">The contribution already has an invocation handler.</exception>
    public ContributionRouter MapInvoke(string contributionId, Func<Invocation, CancellationToken, Task<InvokeResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(contributionId);
        ArgumentNullException.ThrowIfNull(invoke);
        if (_handlers.ContainsKey(contributionId) || !_invocations.TryAdd(contributionId, invoke))
        {
            throw new ArgumentException("The contribution already has an invocation handler.", nameof(contributionId));
        }

        return this;
    }

    /// <summary>Maps both handlers of one contribution to <paramref name="handler"/>.</summary>
    /// <param name="contributionId">The contribution id.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This router.</returns>
    /// <exception cref="ArgumentException">The contribution is already mapped.</exception>
    public ContributionRouter Map(string contributionId, IContributionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(contributionId);
        ArgumentNullException.ThrowIfNull(handler);
        if (_sessions.ContainsKey(contributionId) || _invocations.ContainsKey(contributionId) || !_handlers.TryAdd(contributionId, handler))
        {
            throw new ArgumentException("The contribution is already mapped.", nameof(contributionId));
        }

        return this;
    }

    /// <summary>
    /// The manifest's contributions that lack a handler for what they provide: a session handler
    /// for <c>face</c>, an invocation handler for <c>invoke</c>.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The unmapped contribution ids, in manifest order.</returns>
    public IReadOnlyList<string> FindUnmapped(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var unmapped = new List<string>();
        foreach (var contribution in manifest.Contributions ?? [])
        {
            if (contribution?.Id is not { } id || _handlers.ContainsKey(id))
            {
                continue;
            }

            if (((contribution.Provides & Provides.Face) != 0 && !_sessions.ContainsKey(id))
                || ((contribution.Provides & Provides.Invoke) != 0 && !_invocations.ContainsKey(id)))
            {
                unmapped.Add(id);
            }
        }

        return unmapped.AsReadOnly();
    }

    /// <inheritdoc/>
    public Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_handlers.TryGetValue(session.ContributionId, out var handler))
        {
            return handler.RunSessionAsync(session, cancellationToken);
        }

        return _sessions.TryGetValue(session.ContributionId, out var run) ? run(session, cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var id = invocation.Session.ContributionId;
        if (_handlers.TryGetValue(id, out var handler))
        {
            return handler.InvokeAsync(invocation, cancellationToken);
        }

        return _invocations.TryGetValue(id, out var invoke) ? invoke(invocation, cancellationToken) : Task.FromResult(InvokeResult.Unsupported);
    }
}
