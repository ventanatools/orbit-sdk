// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using CountdownExtensionSample;
using VentanaTools.Orbit.Extensions;

// Finds extension.json and the pairing file, connects to the host app, and runs until Ctrl+C.
return await CompanionApp.RunAsync(args, new CountdownHandler());
