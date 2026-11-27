# Build InfiniFrame and its CEF provider

Run these commands from the root of the framework checkout. The framework source is in `src/`, and the external provider, native companion and reference application are in `providers/cef/`.

## Build for the current machine

```shell
python scripts/build-cef-integration.py --test --output artifacts/cef-build
```

Use Python 3.10 or later, the .NET 10 SDK selected by the root `global.json` (10.0.4xx), and Node.js 24.15 or later with npm on PATH. The native builds also require Git, PowerShell 7 and CMake 3.24 or later. On Windows, install the Visual Studio 2022 or 2026 C++ tools and Windows SDK for the target architecture. On Linux, use GCC or Clang, Ninja and the framework's native development dependencies, including GTK3 and WebKit2GTK 4.1. The framework's [Linux build image](../../docker/linux/Dockerfile) lists the dependency packages.

The command builds the web bridge and the current machine's native framework library, packs the framework release projects, and compiles and packs the .NET 10 provider. It then builds the LocalControlsHost reference application from those packages. The optional `--test` switch runs the provider tests that use a fake companion and the LocalControlsUi tests. Finally, the command restores the pinned CEF binary distribution and compiles the C++ companion and native transport test executable from this checkout.

| Output under the selected directory | Contents |
| --- | --- |
| `feed/` | Framework and managed provider NuGet packages |
| `sample/` | LocalControlsHost and its web assets |
| `cef/runtime/<version>/<rid>/` | CEF runtime, compiled companion and verification manifest |
| `NuGet.config`, `packages/` | Restore configuration and private NuGet package cache for this build |

Each invocation uses a fresh output directory. Omit `--output` to create one automatically under `artifacts/cef-integration/`. On Windows, a short output path helps tools that have path-length limits. The CEF build prints the payload path as `CEF_PAYLOAD_DIRECTORY`. To start the reference application in a desktop session, substitute that path in the following command.

```shell
dotnet artifacts/cef-build/sample/LocalControlsHost.dll --payload "<payload-directory>"
```

## Build and test the managed projects

```shell
python scripts/build-cef-integration.py --managed-only --test
```

This compiles the frontend, all managed framework packages, provider and reference application. It skips native compilation and creates a NativeBridge package without native libraries for managed validation. The desktop run above requires a full build.

The provider package is `Ceffortless`. Its managed assembly and namespace are `Contoso.InfiniFrame.CefHost`. The provider consumes the public `InfiniLore.InfiniFrame.Shared` package. The sample consumes the packaged framework and provider just as an application would. The generated NuGet configuration maps framework and provider package IDs to this run's feed, with nuget.org supplying other packages. A dedicated cache lets successive builds reuse the same version numbers while consuming the newly built packages.

Version defaults live in [Directory.Build.props](Directory.Build.props). Use `--framework-version` and `--provider-version` to label a local build with different prerelease versions, or `--dotnet` to select a specific installed dotnet executable. See `python scripts/build-cef-integration.py --help` for the options.

## Rebuild just the native companion

On Windows x64, run

```shell
python providers/cef/scripts/cef_runtime.py build --rid win-x64 --work artifacts/cef-native
```

On Linux x64, run

```shell
python providers/cef/scripts/cef_runtime.py build --rid linux-x64 --work artifacts/cef-native
```

For ARM64 use `win-arm64` with the matching Visual Studio tools, or `linux-arm64` on a native ARM64 Linux environment. These commands build the companion and CEF wrapper against the CEF project's prebuilt Chromium and `libcef` binaries. The restore checks the downloaded SDK against the [distribution pins](scripts/cef-distributions.json). To reuse a previously restored distribution, add `--cef-root "<distribution-directory>"` to either build entry point. That directory must contain its `cef-distribution.json` record.

The runtime build prints both the payload and transport test paths. Substitute those paths to verify the payload, run the basic native tests and produce a runtime archive for installation independently of the managed NuGet package.

```shell
python providers/cef/scripts/cef_runtime.py verify --payload "<payload-directory>"
python providers/cef/scripts/cef_runtime.py test --suite basic --payload "<payload-directory>" --transport-tests "<transport-test-executable>"
python providers/cef/scripts/cef_runtime.py pack --payload "<payload-directory>" --out artifacts/cef-archives
```
