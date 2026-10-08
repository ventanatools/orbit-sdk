// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Tool;

// Ctrl+C cancels the running command, which stops any program it started and exits 0.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    cancellation.Cancel();
};

return await ToolApplication.RunAsync(args, ToolConsole.System(), cancellation.Token).ConfigureAwait(false);
