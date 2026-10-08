# Template Extension Name

A widget that counts how often you pick it and shows the count on its face.

## Before you install

This package contains a companion program in `payload/companion/`. Installing
the package runs nothing. Install it only if you trust its author.

## Use it

1. Install the package in the host app and turn the extension on.
2. In the host app, choose **Save connection info**. It saves the companion's
   credential in your user profile.
3. Start the companion: run `payload/companion/TemplateExtension.exe` from the
   installed extension's folder. It is an x64 program and needs the x64 .NET 10
   Runtime, also on a Windows on Arm PC, where the Arm64 runtime alone does not
   run it.
4. Add a **Tally** item. Its face shows the count; pick it to add one.

Press Ctrl+C in the companion's window to stop it.
