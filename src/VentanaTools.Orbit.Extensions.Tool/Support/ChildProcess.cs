// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Diagnostics;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>A program the tool runs could not be started.</summary>
internal sealed class ProcessStartException(string program, Exception inner) : Exception("The program could not be started.", inner)
{
    public string Program { get; } = program;
}

/// <summary>
/// Runs the programs the tool starts (<c>dotnet</c>, <c>npm</c> and companions) with their output
/// forwarded line by line to the tool's writers, and stops them with their whole process tree.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource _outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ChildProcess(Process process) => _process = process;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// Starts <paramref name="program"/>. Output lines go to <paramref name="output"/> and error lines to
    /// <paramref name="error"/>; standard input is the tool's own unless <paramref name="closeInput"/> is set.
    /// </summary>
    /// <exception cref="ProcessStartException">The program could not be started.</exception>
    public static ChildProcess Start(string program, IEnumerable<string> arguments, string workingDirectory, TextWriter output, TextWriter error,
        IReadOnlyDictionary<string, string?>? environment = null, bool closeInput = false)
    {
        var info = new ProcessStartInfo(ResolveProgram(program))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = closeInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            info.Environment[name] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var child = new ChildProcess(process);
        process.OutputDataReceived += (_, line) => Forward(line.Data, output, child._outputClosed);
        process.ErrorDataReceived += (_, line) => Forward(line.Data, error, child._errorClosed);
        process.Exited += (_, _) => child._exited.TrySetResult();
        try
        {
            process.Start();
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new ProcessStartException(program, failure);
        }

        if (closeInput)
        {
            process.StandardInput.Close();
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    /// <summary>Runs a program to completion and returns its exit code; cancellation stops its process tree.</summary>
    /// <exception cref="ProcessStartException">The program could not be started.</exception>
    public static async Task<int> RunAsync(string program, IEnumerable<string> arguments, string workingDirectory, TextWriter output,
        TextWriter error, CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var child = Start(program, arguments, workingDirectory, output, error, environment);
        try
        {
            return await child.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            child.Stop();
            throw;
        }
    }

    /// <summary>Waits for the process to exit and its output to drain; returns the exit code.</summary>
    public async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        // The Exited event rather than WaitForExitAsync, which also waits for the output to end.
        await _exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Build servers a child started (MSBuild nodes, the compiler server) can keep its output pipes
        // open long after it exits; give the output a moment to drain, then stop waiting for it.
        await Task.WhenAny(Task.WhenAll(_outputClosed.Task, _errorClosed.Task), Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
            .ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>Stops the process and everything it started.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    public void Dispose() => _process.Dispose();

    /// <summary>
    /// The program to start. On Windows, <c>npm</c> is a batch file that <see cref="Process"/> cannot
    /// start by its bare name, so a name without an extension is looked up on <c>PATH</c> with the
    /// extensions in <c>PATHEXT</c>.
    /// </summary>
    public static string ResolveProgram(string program)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(program) || Path.IsPathRooted(program)
            || program.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || program.Contains('/', StringComparison.Ordinal))
        {
            return program;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim('"'), program + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return program;
    }

    private static void Forward(string? line, TextWriter writer, TaskCompletionSource closed)
    {
        if (line is null)
        {
            closed.TrySetResult();
            return;
        }

        lock (writer)
        {
            writer.WriteLine(line);
        }
    }
}
