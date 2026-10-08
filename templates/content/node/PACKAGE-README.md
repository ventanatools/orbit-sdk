# Template Extension Name

Writes a greeting to the companion's console when you pick its Greet item.

## Before you install

This package contains a Node.js companion in `payload/companion/`. Installing
the package runs nothing. Install it only if you trust its author.

## Use it

1. Install the package in the host app and turn the extension on.
2. In the host app, choose **Save connection info**. It saves the companion's
   credential in your user profile.
3. In the installed extension's `payload/companion/` folder, run
   `npm install --omit=dev`, then `node index.js`. It needs Node.js 22 or later.
4. Add a **Greet** item, choose a greeting, and pick it.

Press Ctrl+C in the companion's window to stop it.
