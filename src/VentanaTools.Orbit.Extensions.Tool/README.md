# VentanaTools.Orbit.Extensions.Tool

This standalone .NET 10 tool packs an explicit staging directory or verifies
an existing `.orbitextension`. It uses only the `VentanaTools.Orbit.Extensions` library.

Stage exactly `extension.json`, `package.json`, `README.md`, and optional
`payload/` companion/source files. Root names and case are exact. Do not point
it at a checkout; copy only intended files, excluding credentials and runtime
data. Links/reparse points, including ancestors, are rejected.

```powershell
dotnet run --project src/VentanaTools.Orbit.Extensions.Tool/VentanaTools.Orbit.Extensions.Tool.csproj -c Release -- pack "C:\extension-stage" "C:\extension-output\example.orbitextension"
dotnet run --project src/VentanaTools.Orbit.Extensions.Tool/VentanaTools.Orbit.Extensions.Tool.csproj -c Release -- verify "C:\extension-output\example.orbitextension"
```

The output directory must exist outside staging, and the output file must not
exist. Keep staging unchanged during packing. The shared reader validates
paths, attributes, size/count limits, compatibility, README and ZIP integrity.
The completed archive is validated before a flushed temporary file moves into
place without overwriting an existing package. Verify never extracts files.

Sorted ordinal names, fixed 1980 timestamps and normalized attributes produce
reproducible packages for identical bytes with the same tool/runtime. Deflate
changes between runtimes can change compressed bytes.

Exit 0 prints `extension-package.packed` or `extension-package.verified`.
Exit 1 prints `extension-package.failed`; exit 2 prints usage. Errors expose
no raw exceptions, paths or file content. See the
[distribution guide](../../docs/extension-distribution.md)
for the package format and Orbit's separate review/install/update workflow.
