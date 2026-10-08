// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// The one place for the bridge's port, path and labels. The UXP panel and the Node companion
// both load this file, so they can never disagree.
"use strict";

/** The loopback port of the companion's WebSocket bridge. */
var BRIDGE_PORT = 38475;
/** The bridge's WebSocket path. */
var BRIDGE_PATH = "/photoshop-bridge";
/** The address the companion listens on. Photoshop's permission matcher needs "localhost" in the panel's URL. */
var LISTEN_HOST = "127.0.0.1";
/** The URL the bridge file carries, and the URL the panel connects to. */
var BRIDGE_URL = "ws://" + LISTEN_HOST + ":" + BRIDGE_PORT + BRIDGE_PATH;
var PANEL_URL = "ws://localhost:" + BRIDGE_PORT + BRIDGE_PATH;
/** The bridge protocol version in hello and in the bridge file. */
var BRIDGE_VERSION = 3;
/** The first line of the bridge's proof transcript. */
var HANDSHAKE_LABEL = "Example.Photoshop.Bridge.v3";
/** The domain separator of the companion's per-launch bridge key. */
var KEY_LABEL = "Example.Photoshop.Bridge.Key.v3";
/** The extension's contributions. */
var TOGGLE_ID = "example.photoshop/toggle-layer";
var STATUS_ID = "example.photoshop/layer-status";
/** How long a face stays current; the companion renews it while the session runs. */
var FACE_SECONDS = 30;

module.exports = {
    BRIDGE_PORT: BRIDGE_PORT,
    BRIDGE_PATH: BRIDGE_PATH,
    LISTEN_HOST: LISTEN_HOST,
    BRIDGE_URL: BRIDGE_URL,
    PANEL_URL: PANEL_URL,
    BRIDGE_VERSION: BRIDGE_VERSION,
    HANDSHAKE_LABEL: HANDSHAKE_LABEL,
    KEY_LABEL: KEY_LABEL,
    TOGGLE_ID: TOGGLE_ID,
    STATUS_ID: STATUS_ID,
    FACE_SECONDS: FACE_SECONDS,
};
