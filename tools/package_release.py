"""Package the existing Release build without launching, restoring, or rebuilding it."""
import hashlib
import json
import subprocess
import sys
import uuid
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from shutil import copy2
from verify_branding import verify as verify_branding

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "BuildProjectLegion/bin/x64/Release/net9.0-windows10.0.26100.0/win-x64"


def sha(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest().upper()


def describe(path, base):
    return {"path": path.relative_to(base).as_posix(), "bytes": path.stat().st_size,
            "sha256": sha(path), "lastWriteUtc": datetime.fromtimestamp(
                path.stat().st_mtime, timezone.utc).isoformat()}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def zip_verified(destination, files, base):
    with zipfile.ZipFile(destination, "x", zipfile.ZIP_DEFLATED) as archive:
        for file in files:
            archive.write(file, file.relative_to(base).as_posix())
    with zipfile.ZipFile(destination) as archive:
        require(archive.testzip() is None, "Archive CRC failure")
        require(len(archive.infolist()) == len(files), "Archive entry count mismatch")
        for file in files:
            require(hashlib.sha256(archive.read(file.relative_to(base).as_posix())).hexdigest().upper() == sha(file),
                    f"Archive payload mismatch: {file}")


def source_files():
    # Explicit source roots: never traverse QA profiles, research, caches or .git.
    roots = [p for p in ROOT.iterdir() if p.is_dir() and p.name.startswith("LenovoLegionToolkit.")]
    roots += [ROOT / "assets", ROOT / "InnoDependencies", ROOT / "tools"]
    skipped = {"bin", "obj", ".git", "__pycache__", "HidSetFeatureTrace"}
    files = [p for p in ROOT.iterdir() if p.is_file() and
             (p.suffix in {".props", ".targets", ".sln", ".slnf", ".bat", ".iss", ".md"}
              or p.name in {"LICENSE", "NOTICE", ".gitignore", ".gitattributes"})]
    for folder in roots:
        files += [p for p in folder.rglob("*") if p.is_file() and not p.is_symlink()
                  and not any(part in skipped for part in p.relative_to(folder).parts)
                  and p.suffix.lower() not in {".pfx", ".p12", ".user", ".env", ".log", ".zip"}]
    return sorted(set(files))


def main():
    global ROOT, BUILD
    require(sys.argv[1:] in ([], ["--self-contained"]), "Use no arguments or --self-contained; each invocation creates a fresh candidate.")
    self_contained = sys.argv[1:] == ["--self-contained"]
    original_root = ROOT
    candidates = ROOT / "BuildProjectLegion/ReleaseCandidates"
    candidate = candidates / (("self-contained-" if self_contained else "presets-ui-") + uuid.uuid4().hex[:8])
    candidate.mkdir(parents=True, exist_ok=False)
    app = candidate / "application"
    inputs = source_files()
    input_hashes = {str(p): sha(p) for p in inputs}
    if self_contained:
        # Separate source/output tree prevents runtime-flavor builds from replacing
        # the validated framework-dependent binaries or the user's installation.
        snapshot = candidate / "source-tree"
        for source in inputs:
            target = snapshot / source.relative_to(ROOT)
            target.parent.mkdir(parents=True, exist_ok=True)
            copy2(source, target)
        ROOT = snapshot
        BUILD = ROOT / "BuildProjectLegion/bin/x64/Release/net9.0-windows10.0.26100.0/win-x64"
        cache = original_root / "BuildProjectLegion/packages"
        restore = ["dotnet", "restore", "LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj", "-p:Platform=x64",
                   "-p:SelfContained=true", "--packages", str(cache), "--source", str(cache), "--disable-parallel", "-m:1", "-nr:false"]
        with (candidate / "restore.log").open("w", encoding="utf-8") as log:
            subprocess.run(restore, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
        inputs = source_files()
    # Refuse an obviously stale no-build publish; never silently build a different application.
    for project in ROOT.glob("LenovoLegionToolkit.*/*.csproj"):
        assembly = BUILD / ("Project Legion.dll" if project.parent.name.endswith(".WPF") else project.stem + ".dll")
        if not assembly.exists():
            continue  # Auxiliary projects are not claimed to be built by this package.
        for source in inputs:
            if (source.is_relative_to(project.parent) or source.name in {"Directory.Build.props", "Directory.Build.targets"}) and source.suffix in {".cs", ".xaml", ".resx", ".csproj", ".props", ".targets"}:
                require(source.stat().st_mtime <= assembly.stat().st_mtime,
                        f"Source newer than built assembly; build the WPF Release project first: {source}")
    command = ["dotnet", "publish", "LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj",
               "-c", "Release", "-p:Platform=x64", "--no-restore", "-p:UseSharedCompilation=false",
               "--self-contained", str(self_contained).lower(), "-m:1", "-nr:false", "-o", str(app)]
    if not self_contained:
        command.append("--no-build")
    with (candidate / "publish.log").open("w", encoding="utf-8") as log:
        subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    payload = sorted(p for p in app.rglob("*") if p.is_file())
    require((app / "Project Legion.exe").exists(), "Executable missing")
    for name in ("Project Legion UpdateHost.exe", "Project Legion UpdateHost.dll", "Project Legion UpdateHost.deps.json", "Project Legion UpdateHost.runtimeconfig.json"):
        require((app / name).is_file(), "Update helper missing: " + name)
    runtime_config = json.loads((app / "Project Legion.runtimeconfig.json").read_text(encoding="utf-8-sig"))["runtimeOptions"]
    host_config = json.loads((app / "Project Legion UpdateHost.runtimeconfig.json").read_text(encoding="utf-8-sig"))["runtimeOptions"]
    if self_contained:
        require("frameworks" not in runtime_config and "framework" not in runtime_config and "frameworks" not in host_config and "framework" not in host_config,
                "Self-contained app/helper still references an installed runtime")
        for name in ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "PresentationFramework.dll"):
            require((app / name).is_file(), "Bundled Desktop Runtime missing: " + name)
    for file in payload:
        original = BUILD / file.relative_to(app)
        require(original.exists() and sha(original) == sha(file), f"Build/publish mismatch: {file}")
        require(not file.name.startswith("ProjectLegion."), f"Diagnostic artifact in application: {file}")
        require(file.suffix != ".json" or file.name.endswith((".deps.json", ".runtimeconfig.json")),
                f"Unexpected configuration in application payload: {file}")
    for name in ("project-legion.svg", "wordmark.svg", "wordmark.png", "project-legion.ico", "sidebar.svg", "titlebar.svg", "monochrome.svg", "README.md"):
        require((app / "Branding" / name).is_file(), f"Missing branding deliverable: {name}")
    verify_branding(app)
    deps = json.loads((app / "Project Legion.deps.json").read_text(encoding="utf-8-sig"))
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    declared = []
    for library, spec in target.items():
        for kind in ("runtime", "native", "resources"):
            for name, metadata in spec.get(kind, {}).items():
                if name.endswith("/_._"):
                    continue
                relative = Path(name).name
                if kind == "resources":
                    relative = metadata["locale"] + "/" + relative
                # Publish prunes Roslyn translations by an existing project target.
                if kind == "resources" and Path(name).name.startswith("Microsoft.CodeAnalysis"):
                    continue
                require((app / relative).is_file(), f"Missing declared {kind} dependency: {library}/{name}")
                declared.append({"library": library, "kind": kind, "path": relative})
        require(not spec.get("runtimeTargets"), f"Unresolved runtime asset selection in {library}")
    for name in ("LICENSE", "NOTICE"):
        copy2(ROOT / name, app / name)
    if self_contained:
        for framework in runtime_config.get("includedFrameworks", []):
            name, version = framework["name"], framework["version"]
            package_root = cache / (name.lower() + ".runtime.win-x64") / version
            notices = [p for p in package_root.iterdir() if p.is_file() and
                       (p.name.upper().startswith("LICENSE") or "THIRD-PARTY-NOTICES" in p.name.upper())]
            require(notices, "Runtime package license missing: " + name)
            for notice in notices:
                destination = app / "RuntimeNotices" / name / notice.name
                destination.parent.mkdir(parents=True, exist_ok=True)
                copy2(notice, destination)
    (app / "RELEASE-NOTES.md").write_text(
        "# Project Legion - Windows x64 preview\n\n"
        "Extract the complete folder before opening Project Legion.exe. "
        + ("This self-contained package bundles its .NET Desktop Runtime. " if self_contained else "This framework-dependent package requires a compatible .NET 9 Windows Desktop Runtime. ") +
        "The production manifest requests "
        "administrator approval for supported hardware integrations.\n\n"
        "The package contains the current Project Legion app, branding assets, LICENSE and NOTICE. "
        "The matching source archive is distributed beside the application archive. This build is unsigned; "
        "Windows may identify the publisher as unknown.\n\n"
        "Open Updater in the sidebar to check versions, choose a release channel and download a verified update ZIP. "
        "Install and restart is available only after an explicit unsigned-package confirmation. It stages a separate installation and previous-version backup; the profile is not replaced. "
        "Startup must be acknowledged before success is recorded. A pending startup is not force-closed. Manual extraction remains available.\n\n"
        "Project Legion is an unofficial independent derivative of Lenovo Legion Toolkit. It is not affiliated "
        "with or endorsed by Lenovo or the original maintainers. It is distributed under GPL-3.0; retain the "
        "corresponding source and license notices when redistributing.\n\n"
        "Use --read-only to inspect the interface while hardware controls and configuration writes are locked. "
        "Normal mode may execute explicit hardware actions and configured automations. Review the displayed "
        "confirmation and Windows UAC prompt before enabling or applying changes.\n", encoding="utf-8")
    require(all(sha(Path(p)) == digest for p, digest in input_hashes.items()), "Source changed during packaging")
    (candidate / "artifact-manifest.json").write_text(json.dumps({
        "publishCommand": command, "buildOutput": str(BUILD),
        "publishedBuildFiles": [describe(p, app) for p in payload], "declaredAssets": declared,
        "sourceFiles": [describe(p, ROOT) for p in inputs],
    }, indent=2), encoding="utf-8")
    binary = candidate / ("ProjectLegion-win-x64-self-contained.zip" if self_contained else "ProjectLegion-win-x64-framework-dependent.zip")
    source = candidate / "ProjectLegion-corresponding-source.zip"
    zip_verified(binary, sorted(p for p in app.rglob("*") if p.is_file()), app)
    zip_verified(source, inputs, ROOT)
    summary = {"status": "Unsigned " + ("self-contained" if self_contained else "framework-dependent") + " preview candidate; use the published validation scope",
               "executable": describe(app / "Project Legion.exe", candidate),
               "assemblies": [describe(app / name, candidate) for name in ("Project Legion.dll", "LenovoLegionToolkit.Lib.dll")],
               "binaryArchive": describe(binary, candidate), "sourceArchive": describe(source, candidate),
               "verifiedBuildFiles": len(payload), "verifiedDeclaredAssets": len(declared),
               "runtimeBundled": self_contained, "softwarePresetUiIntegrated": True,
               "hardwareOperations": 0, "runtimeLaunches": 0}
    (candidate / "release-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(candidate)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
