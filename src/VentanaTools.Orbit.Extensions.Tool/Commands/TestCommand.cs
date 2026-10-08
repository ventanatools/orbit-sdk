// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>test [&lt;project-dir&gt;] [--host &lt;id&gt;] [-- &lt;args…&gt;]</c>: validates the project's
/// <c>extension.json</c> as <c>validate</c> does, then runs its tests: <c>npm test</c> for a Node
/// project (one with a <c>package.json</c>), otherwise <c>dotnet test</c>.
/// </summary>
internal static class TestCommand
{
    public static async Task<int> RunAsync(ToolConsole console, string? projectArgument, string? hostId, IReadOnlyList<string> passThrough,
        CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "test", json: false);
        var project = console.FullPath(projectArgument ?? ".");
        var manifestPath = Path.Combine(project, ExtensionFiles.ManifestName);
        if (!File.Exists(manifestPath))
        {
            console.Fail(console.FolderName(project) + " contains no " + ExtensionFiles.ManifestName + ".");
            return ExitCodes.InputOutput;
        }

        await ValidateCommand.ValidateLooseAsync(console, manifestPath, Path.Combine(project, ExtensionFiles.StringsFolder), hostId, report,
            cancellationToken).ConfigureAwait(false);
        report.Write();
        if (!report.Ok)
        {
            return ExitCodes.Failed;
        }

        var node = File.Exists(Path.Combine(project, "package.json"));
        var program = node ? "npm" : "dotnet";
        var arguments = new List<string> { "test" };
        if (passThrough.Count > 0)
        {
            if (node)
            {
                arguments.Add("--");
            }

            arguments.AddRange(passThrough);
        }

        int exitCode;
        try
        {
            exitCode = await ChildProcess.RunAsync(program, arguments, project, console.Out, console.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessStartException)
        {
            console.Fail(program + " test could not be started.");
            return ExitCodes.ProcessStart;
        }

        return exitCode == 0 ? ExitCodes.Success : ExitCodes.Failed;
    }
}
