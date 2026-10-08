using VentanaTools.Orbit.Extensions;

/// <summary>The Greet action: writes the chosen greeting to the companion's console.</summary>
public sealed class GreetingHandler : ContributionHandler
{
    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        // Settings hold one of the choice values declared in extension.json.
        var greeting = invocation.Session.Settings["greeting"] switch
        {
            "good-morning" => "Good morning",
            _ => "Hello",
        };

        Console.WriteLine($"{greeting}, world");
        return Task.FromResult(InvokeResult.Done);
    }
}
