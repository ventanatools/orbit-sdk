"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { Outcome } = require("@ventanatools/orbit-extensions");
const { assertManifestValid, createTestSession, createTestInvocation, startTestHost } = require("@ventanatools/orbit-extensions/testing");
const { handler } = require("../index.js");

const manifestPath = path.join(__dirname, "..", "extension.json");
const greet = "example.template-extension/greet";

test("the manifest is valid", () => {
  assertManifestValid(manifestPath);
});

test("Greet answers Done for every greeting", async () => {
  const manifest = assertManifestValid(manifestPath);
  for (const greeting of ["hello", "good-morning"]) {
    // A session as the host starts it: settings checked against extension.json, defaults filled in.
    const recording = createTestSession({ manifest, contributionId: greet, settings: { greeting } });
    assert.equal(await handler.invoke(createTestInvocation(recording), recording.signal), Outcome.Done);
  }
});

test("a real client accepts the session and answers the invocation", async () => {
  // The SDK's companion client against an in-memory host that speaks the real protocol.
  const host = await startTestHost({ manifest: assertManifestValid(manifestPath), handler });
  try {
    const session = await host.startSession(greet);
    assert.equal(session.refusedCode, undefined);
    assert.deepEqual(await host.invoke(session), { kind: "Done" });
    assert.deepEqual(host.faults, []);
  } finally {
    await host.close();
  }
});
