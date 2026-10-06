using VentanaTools.Orbit.Extensions;

/// <summary>
/// The Tally widget: every session shows the shared count on its face, and every pick adds one.
/// </summary>
public sealed class TallyHandler : ContributionHandler
{
    private readonly Lock _gate = new();
    private readonly List<Session> _sessions = [];
    private int _count;

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        int count;
        lock (_gate)
        {
            _sessions.Add(session);
            count = _count;
        }

        try
        {
            Publish(session, count);

            // Keep running until the host stops the session, so the SDK renews the face.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(session);
            }
        }
    }

    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        Session[] sessions;
        int count;
        lock (_gate)
        {
            count = ++_count;
            sessions = [.. _sessions];
        }

        foreach (var session in sessions)
        {
            Publish(session, count);
        }

        return Task.FromResult(InvokeResult.Done);
    }

    private static void Publish(Session session, int count) => session.SetFace(new Face
    {
        Picture = FacePicture.Glyph("\uE710"),
        Line1 = count.ToString(session.UiCulture),
        Line2 = "Picked",
        Detail = count == 1 ? "Picked once." : "Picked " + count.ToString(session.UiCulture) + " times.",
        GoodFor = TimeSpan.FromMinutes(5),
        Renew = true,
    });
}
