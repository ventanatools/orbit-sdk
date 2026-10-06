// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.CommandLine;
using System.CommandLine.Parsing;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// The command line of contract §11.1. Arguments after the first <c>--</c> belong to the program
/// <c>simulate</c>, <c>run</c> or <c>test</c> starts and are split off before parsing, so they are
/// never read as the tool's own options.
/// </summary>
internal static class ToolApplication
{
    public static async Task<int> RunAsync(string[] args, ToolConsole console, CancellationToken cancellationToken)
    {
        var separator = Array.IndexOf(args, "--");
        var own = separator < 0 ? args : args[..separator];
        var passThrough = separator < 0 ? [] : args[(separator + 1)..];
        var root = Build(console, passThrough, separator >= 0);
        var result = root.Parse(own);
        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                console.Fail(error.Message);
            }

            console.Error.WriteLine("Run '" + ToolIdentity.CommandName + " --help' for usage.");
            return ExitCodes.Usage;
        }

        return await result.InvokeAsync(new InvocationConfiguration
        {
            Output = console.Out,
            Error = console.Error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Command Build(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var root = new Command(ToolIdentity.CommandName, "Create, validate, pack, verify, test, simulate, run and link extensions for the host.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption { Action = new VersionAction(console) });

        root.Subcommands.Add(New(console, passThrough, hasSeparator));
        root.Subcommands.Add(Validate(console, passThrough, hasSeparator));
        root.Subcommands.Add(Pack(console, passThrough, hasSeparator));
        root.Subcommands.Add(Verify(console, passThrough, hasSeparator));
        root.Subcommands.Add(Test(console, passThrough));
        root.Subcommands.Add(Simulate(console, passThrough, hasSeparator));
        root.Subcommands.Add(Run(console, passThrough, hasSeparator));
        root.Subcommands.Add(Link(console, passThrough, hasSeparator));
        root.Subcommands.Add(Schema(console, passThrough, hasSeparator));
        return root;
    }

    private static Command New(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var kind = new Argument<string>("kind") { Description = "The kind of project: action, widget or node." };
        kind.AcceptOnlyFromAmong([.. NewCommand.Kinds]);
        var name = new Option<string?>("--name", "-n") { Description = "The project name.", HelpName = "name" };
        var output = new Option<string?>("--output", "-o") { Description = "The folder to create the project in.", HelpName = "dir" };
        var extensionId = new Option<string?>("--extension-id") { Description = "The extension id, publisher.name in lowercase.", HelpName = "id" };
        var host = HostOption("The host id written into hosts.");
        var displayName = new Option<string?>("--display-name") { Description = "The extension's name. Default: the project name.", HelpName = "name" };
        var noTests = new Option<bool>("--no-tests") { Description = "Create no test project (.NET templates)." };
        var feed = new Option<string?>("--feed")
        {
            Description = "The package folder to prepare and restore from. Default: the per-user feed of this version.",
            HelpName = "dir",
        };
        var hive = new Option<string?>("--debug:custom-hive") { Description = "Passed to dotnet new.", HelpName = "dir", Hidden = true };
        var command = new Command("new", "Create an extension project from the templates.") { kind, name, output, extensionId, host, displayName, noTests, feed, hive };
        command.SetAction((result, token) => Guard(console, "new", false, passThrough, hasSeparator, allowPassThrough: false,
            () => NewCommand.RunAsync(console, result.GetRequiredValue(kind), new NewOptions
            {
                Name = result.GetValue(name),
                Output = result.GetValue(output),
                ExtensionId = result.GetValue(extensionId),
                Host = result.GetValue(host),
                DisplayName = result.GetValue(displayName),
                NoTests = result.GetValue(noTests),
                Feed = result.GetValue(feed),
                TemplateHive = result.GetValue(hive),
            }, token)));
        return command;
    }

    private static Command Validate(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var path = PathArgument("path", "An extension.json, a folder that contains one, or a package. Default: the current folder.");
        var host = HostOption("Validate for this host. Default: the first active host in hosts.");
        var json = JsonOption();
        var warningsAsErrors = new Option<bool>("--warnings-as-errors") { Description = "Fail on warnings too." };
        var command = new Command("validate", "Validate a manifest, a project folder or a package.") { path, host, json, warningsAsErrors };
        command.SetAction((result, token) => Guard(console, "validate", result.GetValue(json), passThrough, hasSeparator, allowPassThrough: false,
            () => ValidateCommand.RunAsync(console, result.GetValue(path), result.GetValue(host), result.GetValue(json),
                result.GetValue(warningsAsErrors), token)));
        return command;
    }

    private static Command Pack(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var project = PathArgument("project-dir", "The folder with extension.pack.json and extension.json. Default: the current folder.");
        var output = new Option<string?>("--output", "-o") { Description = "The folder to write the package to. Default: artifacts in the project folder.", HelpName = "dir" };
        var host = HostOption("The host whose package file extension to use. Default: the first active host in hosts.");
        var force = new Option<bool>("--force") { Description = "Replace an existing package." };
        var json = JsonOption();
        var command = new Command("pack", "Build a package from extension.pack.json.") { project, output, host, force, json };
        command.SetAction((result, token) => Guard(console, "pack", result.GetValue(json), passThrough, hasSeparator, allowPassThrough: false,
            () => PackCommand.RunAsync(console, result.GetValue(project), result.GetValue(output), result.GetValue(host), result.GetValue(force),
                result.GetValue(json), token)));
        return command;
    }

    private static Command Verify(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var package = new Argument<string>("package") { Description = "The package file." };
        var host = HostOption("Verify for this host. Default: the first active host in the package's hosts.");
        var json = JsonOption();
        var command = new Command("verify", "Verify a package and print what it contains.") { package, host, json };
        command.SetAction((result, token) => Guard(console, "verify", result.GetValue(json), passThrough, hasSeparator, allowPassThrough: false,
            () => VerifyCommand.RunAsync(console, result.GetRequiredValue(package), result.GetValue(host), result.GetValue(json), token)));
        return command;
    }

    private static Command Test(ToolConsole console, string[] passThrough)
    {
        var project = PathArgument("project-dir", "The project folder. Default: the current folder. Arguments after -- go to dotnet test or npm test.");
        var host = HostOption("Validate for this host. Default: the first active host in hosts.");
        var command = new Command("test", "Validate extension.json, then run the project's tests.") { project, host };
        command.SetAction((result, token) => Guard(console, "test", false, passThrough, true, allowPassThrough: true,
            () => TestCommand.RunAsync(console, result.GetValue(project), result.GetValue(host), passThrough, token)));
        return command;
    }

    private static Command Simulate(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var manifest = new Option<string?>("--manifest") { Description = "The manifest. Default: extension.json in the current folder.", HelpName = "path" };
        var script = new Option<string?>("--script") { Description = "A JSON simulation script to run instead of the prompt.", HelpName = "file" };
        var json = new Option<bool>("--json") { Description = "Write the transcript as one JSON object per line." };
        var command = new Command("simulate", "Run the companion command after -- against a fake host, from a prompt or a script.") { manifest, script, json };
        command.SetAction((result, token) => Guard(console, "simulate", result.GetValue(json), passThrough, hasSeparator, allowPassThrough: true,
            () => RequireCommand(console, passThrough)
                ?? SimulateCommand.RunAsync(console, result.GetValue(manifest), result.GetValue(script), result.GetValue(json), passThrough, token)));
        return command;
    }

    private static Command Run(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var watch = new Option<bool>("--watch") { Description = "Restart when extension.json, the pairing file or the sources change." };
        var command = new Command("run", "Run the companion command after -- and print its status lines.") { watch };
        command.SetAction((result, token) => Guard(console, "run", false, passThrough, hasSeparator, allowPassThrough: true,
            () => RequireCommand(console, passThrough)
                ?? RunCommand.RunAsync(console, passThrough, result.GetValue(watch), TimeSpan.FromSeconds(1), token)));
        return command;
    }

    private static Command Link(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var path = PathArgument("path", "The project folder or its extension.json. Default: the current folder.");
        var host = HostOption("Check this host's pairing. Default: every active host in hosts.");
        var json = JsonOption();
        var command = new Command("link", "Check where the companion finds its pairing, and whether it is valid.") { path, host, json };
        command.SetAction((result, token) => Guard(console, "link", result.GetValue(json), passThrough, hasSeparator, allowPassThrough: false,
            () => LinkCommand.RunAsync(console, result.GetValue(path), result.GetValue(host), result.GetValue(json), token)));
        return command;
    }

    private static Command Schema(ToolConsole console, string[] passThrough, bool hasSeparator)
    {
        var kind = new Option<string>("--kind")
        {
            Description = "Which schema.",
            DefaultValueFactory = _ => "manifest",
        };
        kind.AcceptOnlyFromAmong([.. Schemas.Kinds]);
        var host = HostOption("The host of a manifest or strings schema. Default: the first active host.");
        var output = new Option<string?>("--output", "-o") { Description = "The file to write. Default: standard output.", HelpName = "file" };
        var command = new Command("schema", "Write a bundled JSON Schema, for editors that work offline.") { kind, host, output };
        command.SetAction((result, token) => Guard(console, "schema", false, passThrough, hasSeparator, allowPassThrough: false,
            () => SchemaCommand.RunAsync(console, result.GetRequiredValue(kind), result.GetValue(host), result.GetValue(output), token)));
        return command;
    }

    private static Option<string?> HostOption(string description)
    {
        var option = new Option<string?>("--host") { Description = description, HelpName = "id" };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && !TextRules.IsHostId(value))
            {
                result.AddError("'" + value + "' is not a host id.");
            }
        });
        return option;
    }

    /// <summary>A positional path; refuses option-like tokens, which System.CommandLine would otherwise take as the path.</summary>
    private static Argument<string?> PathArgument(string name, string description)
    {
        var argument = new Argument<string?>(name) { Description = description, Arity = ArgumentArity.ZeroOrOne };
        argument.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { Length: > 1 } value && value[0] == '-')
            {
                result.AddError("Unrecognized command or argument '" + value + "'.");
            }
        });
        return argument;
    }

    /// <summary>Prints the tool's informational version, as the default action would for the entry assembly.</summary>
    private sealed class VersionAction(ToolConsole console) : System.CommandLine.Invocation.SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var assembly = typeof(VersionAction).Assembly;
            console.Out.WriteLine(System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion ?? ToolIdentity.Version);
            return ExitCodes.Success;
        }
    }

    private static Option<bool> JsonOption() => new("--json") { Description = "Write one JSON object to standard output." };

    private static Task<int>? RequireCommand(ToolConsole console, string[] passThrough)
    {
        if (passThrough.Length > 0)
        {
            return null;
        }

        console.Fail("Give the companion command after --, for example: -- dotnet run --project . --");
        return Task.FromResult(ExitCodes.Usage);
    }

    /// <summary>
    /// Runs a command, mapping failures to the exit codes of contract §11.1: unreadable files are 3,
    /// a reader defect is <c>json.internal-error</c> and any other defect <c>tool.internal-error</c>, both 4.
    /// </summary>
    private static async Task<int> Guard(ToolConsole console, string command, bool json, string[] passThrough, bool hasSeparator,
        bool allowPassThrough, Func<Task<int>> run)
    {
        if (hasSeparator && !allowPassThrough)
        {
            console.Fail(command + " takes no arguments after --.");
            return ExitCodes.Usage;
        }

        _ = passThrough;
        try
        {
            console.FaultInjection?.Invoke(command);
            return await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            console.Fail(error.Message);
            return ExitCodes.InputOutput;
        }
        catch (ReaderDefectException defect)
        {
            var report = new DiagnosticReport(console, command, json);
            report.Add(DiagnosticCodes.JsonInternalError, string.Empty, null);
            console.Fail("reader defect: " + defect.InnerException!.GetType().FullName + " (0x"
                + defect.InnerException.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")");
            return report.Write(ExitCodes.Internal);
        }
#pragma warning disable CA1031 // The tool reports every other defect as tool.internal-error.
        catch (Exception error)
#pragma warning restore CA1031
        {
            var report = new DiagnosticReport(console, command, json);
            report.AddToolInternalError();
            console.Fail("internal error: " + error.GetType().FullName + " (0x"
                + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")");
            return report.Write(ExitCodes.Internal);
        }
    }
}
