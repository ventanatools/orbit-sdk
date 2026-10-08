using VentanaTools.Orbit.Extensions;
//#if (!extensionIdValid)
#error The extension id is not a third-party extension id: publisher.name in lowercase letters, digits and single hyphens. Correct "id" in extension.json and the contribution ids, then delete this line.
//#endif

// Finds extension.json and the pairing file, connects to the host, and runs until Ctrl+C.
return await CompanionApp.RunAsync(args, new TallyHandler());
