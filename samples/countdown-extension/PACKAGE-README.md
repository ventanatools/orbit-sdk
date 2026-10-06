# Countdown sample package

This local developer preview includes a runtime-dependent built companion in
`payload/companion/` and the independent sample source in `payload/source/`.
It requires Windows and the .NET 10 runtime. Orbit stores these files and starts
nothing. Use only a package from an author you trust.

Enable Countdown sample in Orbit Settings > Extensions and save Copy connection
info to a private file outside this package. From this package folder:

```powershell
dotnet payload/companion/CountdownExtensionSample.dll --manifest extension.json --pairing "C:\private-folder\Countdown.pairing.json"
```

Add Countdown action three times with Start, Pause and Reset choices, plus the
passive Countdown status. Choices are per item; all items share one timer.
Use one minute to try it. Start resumes paused time; Reset prepares its chosen
duration without starting. Completion has no alarm. Restarting this companion
starts Ready at 5:00. Ctrl+C stops it.

After an update, enable again, copy fresh connection info and restart the
companion. Do not share pairing files. Source/build instructions are at
https://dev.ventana.tools/orbit/examples/countdown/ and in the public
https://github.com/ventanatools/orbit-sdk repository. The extracted source can
be built independently with .NET 10 and the local SDK packages from that repo;
repository-relative README links/commands assume a complete checkout.
SDK license terms are in `payload/LICENSE` and `payload/NOTICE`; the sample source
is MIT-0 (`payload/source/LICENSE`).
Store-signed extension support remains unverified; this is a developer example.
