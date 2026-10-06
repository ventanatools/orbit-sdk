"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { Outcome } = require("@ventanatools/orbit-extensions");
const { assertManifestValid } = require("@ventanatools/orbit-extensions/testing");
const { handler } = require("../index.js");

test("the manifest is valid", () => {
  assertManifestValid(path.join(__dirname, "..", "extension.json"));
});

test("Greet answers Done for every greeting", async () => {
  for (const greeting of ["hello", "good-morning"]) {
    const outcome = await handler.invoke({ requestId: "test", session: { settings: { greeting } } }, new AbortController().signal);
    assert.equal(outcome, Outcome.Done);
  }
});
