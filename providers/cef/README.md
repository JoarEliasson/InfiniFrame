# CEF host provider for InfiniFrame

`Contoso.InfiniFrame.CefHost` connects InfiniFrame applications to Chromium Embedded Framework through the public host contract. The managed package contains the adapter, and the native companion runs in a separate process. Applications select an installed runtime with `CefHostOptions.PayloadDirectory`. The adapter checks the runtime manifest against the host protocol, platform, CEF version and API.

Start with [Build from source](BUILD.md) to build the framework and provider from this checkout. The [samples](samples/README.md) include a Blazor Hybrid application and a browser-free host contract check.

## Native runtime

The companion uses protocol 3 and CEF API 15600. Official CEF binary distributions for Windows and Linux, x64 and ARM64, are pinned by size and checksum in [cef-distributions.json](scripts/cef-distributions.json). Rebuilding compiles the companion and CEF wrapper against the binary distribution for the selected platform. It requires Python 3.10+, Git, CMake 3.24+ and a matching C++ toolchain. Run the Linux builds on the target architecture.

Run these commands from the repository root. Replace `win-x64` with the target RID. The first build downloads the pinned archive. For an existing verified distribution, pass `--cef-root <directory>` to `build`. Use a fresh work directory for each rebuild.

```sh
python providers/cef/scripts/cef_runtime.py build --rid win-x64 --work artifacts/cef-build
python providers/cef/scripts/cef_runtime.py verify --rid win-x64 --payload artifacts/cef-build/runtime/156.0.8-p3.7/win-x64
python providers/cef/scripts/cef_runtime.py pack --payload artifacts/cef-build/runtime/156.0.8-p3.7/win-x64 --out artifacts/cef-dist
```

The build prints the runtime directory and transport-test executable. Each receipt records the integrated repository commit and a digest of the companion sources and build scripts. The packed archive includes the payload and its manifest. A SHA-256 checksum, a manifest copy and a CycloneDX SBOM sit alongside the archive. Use `install --archive <archive> --root <destination> --rid <rid>` with the same tool to verify and install the runtime into its versioned directory.

Run the offline runtime-tool tests from the repository root:

```sh
python -B -m unittest discover -s providers/cef/tests -p test_cef_runtime.py
```

For live tests, use `cef_runtime.py test --payload <directory>`. See [native tests and build details](native/README.md) for desktop requirements and [the provider license](LICENSE) for the imported code. The CEF distribution includes its own notices.
