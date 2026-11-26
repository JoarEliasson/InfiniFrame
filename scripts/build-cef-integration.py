#!/usr/bin/env python3
"""Build the framework, CEF provider and reference host from one source checkout."""

import argparse
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
CEF = ROOT / "providers" / "cef"
RELEASE = ROOT / "InfiniFrame.GitHubActions.Release.slnf"
PROVIDER = CEF / "src/Contoso.InfiniFrame.CefHost/Contoso.InfiniFrame.CefHost.csproj"
HOST = CEF / "samples/LocalControlsHost/LocalControlsHost.csproj"


def create_config(work, feed):
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local")
    for pattern in ("InfiniLore.InfiniFrame", "InfiniLore.InfiniFrame.*", "Contoso.InfiniFrame.CefHost"):
        ET.SubElement(local, "package", pattern=pattern)
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    path = work / "NuGet.config"
    ET.indent(config)
    ET.ElementTree(config).write(path, encoding="utf-8", xml_declaration=True)
    return path


def host_rid():
    systems = {"Windows": "win", "Linux": "linux"}
    arches = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}
    if platform.system() not in systems or platform.machine().lower() not in arches:
        raise ValueError("The native CEF companion requires Windows or Linux on x64 or ARM64.")
    return f"{systems[platform.system()]}-{arches[platform.machine().lower()]}"


def build(args):
    rid = None if args.managed_only else host_rid()
    versions = ET.parse(CEF / "Directory.Build.props")
    framework = args.framework_version or versions.findtext(".//InfiniFrameVersion")
    provider = args.provider_version or versions.findtext(".//ContosoCefHostVersion")

    if args.output:
        work = args.output.resolve()
        work.mkdir(parents=True)
    else:
        parent = ROOT / "artifacts/cef-integration"
        parent.mkdir(parents=True, exist_ok=True)
        work = Path(tempfile.mkdtemp(prefix="run-", dir=parent))
    feed = work / "feed"
    feed.mkdir()
    config = create_config(work, feed)
    print(f"Build directory: {work}", flush=True)
    env = dict(os.environ, NUGET_PACKAGES=str(work / "packages"),
               NUGET_HTTP_CACHE_PATH=str(work / "http-cache"), NUGET_FALLBACK_PACKAGES="",
               CEFHOST_TESTS_PYTHON=sys.executable)

    def run(*argv):
        print("+", " ".join(str(value) for value in argv), flush=True)
        subprocess.run(argv, cwd=ROOT, env=env, check=True)

    def dotnet(command, *argv):
        run(args.dotnet, command, *argv, f"-p:RestoreConfigFile={config}",
            "-p:DisableImplicitNuGetFallbackFolder=true")

    framework_props = [f"-p:Version={framework}", "-p:GeneratePackageOnBuild=false",
                       "-p:InfiniFrameEnableTestExports=false", "-p:RunFrontendBuild=false",
                       "-p:InfiniFrameSkipNativeBuild=true"]
    provider_props = [f"-p:InfiniFrameVersion={framework}", f"-p:InfiniFrameSharedVersion={framework}",
                      f"-p:ContosoCefHostVersion={provider}"]
    if args.managed_only:
        # The packaging targets also enumerate pre-existing native outputs in the checkout.
        framework_props.append(f"-p:NativeOutputRoot={work / 'empty-native'}")
    dotnet("restore", RELEASE, "--force", *framework_props)
    js = ROOT / "src/InfiniFrame.Js"
    run("node", ROOT / "scripts/js-buildfrontend.mjs", js, js / "obj/frontend-build.stamp",
        js / "wwwroot/InfiniFrame.js", js / "wwwroot/InfiniFrame.dev.js")
    if rid:
        run("pwsh", "-NoProfile", "-File", ROOT / "src/InfiniFrame.NativeBridge/native-build.ps1",
            "Release", rid.split("-")[1], "false")
    dotnet("build", RELEASE, "-c", "Release", "--no-restore", *framework_props)
    dotnet("pack", RELEASE, "-c", "Release", "--no-build", "-o", feed, *framework_props)
    dotnet("restore", PROVIDER, "--force", *provider_props)
    dotnet("pack", PROVIDER, "-c", "Release", "--no-restore", "-o", feed, *provider_props)
    dotnet("restore", HOST, "--force", *provider_props)
    dotnet("build", HOST, "-c", "Release", "--no-restore", "-o", work / "sample", *provider_props)
    if args.test:
        tests = CEF / "tests"
        for name in ("Contoso.InfiniFrame.CefHost.Tests", "LocalControlsUi.Tests"):
            project = tests / name / f"{name}.csproj"
            dotnet("restore", project, "--force", *provider_props)
            command = [args.dotnet, "run", "--project", project, "-c", "Release", "--no-restore",
                       *provider_props]
            if name == "Contoso.InfiniFrame.CefHost.Tests":
                command.extend(["--", "--treenode-filter", "/*/*/*/*[Category=Fake]"])
            run(*command)
    if rid:
        command = [sys.executable, CEF / "scripts/cef_runtime.py", "build", "--rid", rid,
                   "--work", work / "cef"]
        if args.cef_root:
            command.extend(["--cef-root", args.cef_root.resolve()])
        run(*command)
    print(f"Package feed: {feed}\nReference host: {work / 'sample'}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="new build directory (default: artifacts/cef-integration/run-*)")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable (default: on PATH)")
    parser.add_argument("--managed-only", action="store_true", help="skip both native builds for managed validation")
    parser.add_argument("--test", action="store_true", help="run the fake-companion provider tests and UI library tests")
    parser.add_argument("--cef-root", type=Path, help="reuse a verified upstream CEF SDK distribution")
    parser.add_argument("--framework-version", help="override the framework prerelease package version")
    parser.add_argument("--provider-version", help="override the provider prerelease package version")
    args = parser.parse_args()
    if args.managed_only and args.cef_root:
        parser.error("--cef-root requires the native build")
    try:
        build(args)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))


if __name__ == "__main__":
    main()
