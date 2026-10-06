"use strict";

const { runCompanion, Outcome } = require("@ventanatools/orbit-extensions");
//#if (!extensionIdValid)
throw new Error("The extension id is not a third-party extension id: publisher.name in lowercase letters, digits and single hyphens. Correct \"id\" in extension.json and the contribution ids, then delete this line.");
//#endif

/** The Greet action: writes the chosen greeting to the companion's console. */
const handler = {
  async invoke(invocation) {
    // Settings hold one of the choice values declared in extension.json.
    const greeting = invocation.session.settings.greeting === "good-morning" ? "Good morning" : "Hello";
    console.log(`${greeting}, world`);
    return Outcome.Done;
  },
};

module.exports = { handler };

// Finds extension.json and the pairing file, connects to the host, and runs until Ctrl+C.
if (require.main === module) {
  runCompanion(process.argv.slice(2), handler).then((code) => process.exit(code));
}
