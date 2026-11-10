"""Export an exact reviewed file list. No directory expansion or local inventory.

Use absolute native paths for --project-root, --destination and --manifest.
The destination's parent must exist; the destination itself must not exist.
Root entrypoints ALWAYS come from scripts/public-templates, never local copies.
Run with python -I -B to keep local imports/bytecode out of the export.
"""
import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys

TEMPLATES = {"build.cmd", "ClipBar.slnx", "README.md", ".gitignore"}
ROOT_FILES = TEMPLATES | {"LICENSE"}
SCRIPT_FILES = {"scripts/package.ps1", "scripts/export-public.py", "scripts/public-manifest.json"}
SCRIPT_FILES |= {"scripts/public-templates/" + name for name in TEMPLATES}
PRIVATE_PARTS = {"data", "docs", "tests", "bin", "obj", ".git", "dist", "dist-lite", "secrets"}
SOURCE_SUFFIXES = {".cs", ".csproj", ".xaml", ".manifest", ".ico"}
SITE_SUFFIXES = {".html", ".css", ".js", ".json", ".svg", ".webp"}
BINARY_SUFFIXES = {".ico", ".webp"}
PERSONAL_PATH = re.compile(
    r"(?:[a-z]:[/\\](?:users|documents and settings)[/\\]|/(?:users|home)/[^/\s]+)", re.I)


def native_absolute(value):
    path = Path(value)
    if (not path.is_absolute() or ".." in path.parts
            or (os.name == "nt" and not re.match(r"^[a-zA-Z]:[\\/]", value))):
        raise ValueError("Use an absolute native path without '..': " + value)
    return path


def no_links(path):
    # lstat every component, including dangling links, junctions and other reparse points.
    for item in [*reversed(path.parents), path]:
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Symlink/reparse point is not allowed: " + str(item))


def validate_entry(name):
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*", name):
        raise ValueError("Manifest entries must be exact relative slash-separated file names")
    parts = name.split("/")
    for part in parts:
        if (part in {".", ".."} or part.endswith(".") or part.casefold() in PRIVATE_PARTS
                or re.fullmatch(r"(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?", part, re.I)):
            raise ValueError("Forbidden manifest path: " + name)
    suffix = Path(name).suffix.lower()
    allowed = (name in ROOT_FILES or name in SCRIPT_FILES
               or (len(parts) >= 3 and parts[0] == "src"
                   and parts[1] in {"ClipBar.App", "ClipBar.Core"} and suffix in SOURCE_SUFFIXES)
               or (len(parts) >= 2 and parts[0] == "site" and suffix in SITE_SUFFIXES))
    if not allowed:
        raise ValueError("Unapproved public path or suffix: " + name)


def load_manifest(path):
    no_links(path)
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if (not isinstance(value, dict) or set(value) != {"schemaVersion", "files"}
            or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or not isinstance(value["files"], list)):
        raise ValueError("Expected schemaVersion 1 and an explicit files array only")
    seen = set()
    for name in value["files"]:
        validate_entry(name)
        if name.casefold() in seen:
            raise ValueError("Duplicate/case-colliding manifest entry: " + name)
        seen.add(name.casefold())
    if not {"build.cmd", "ClipBar.slnx", "README.md", "LICENSE"}.issubset(value["files"]):
        raise ValueError("Manifest must include public build.cmd, ClipBar.slnx, README.md and LICENSE")
    return value["files"]


def prepare(root, destination, manifest):
    no_links(root)
    no_links(destination.parent)
    if not root.is_dir() or not destination.parent.is_dir():
        raise ValueError("Project root and destination parent must be existing directories")
    if os.path.lexists(destination):
        raise ValueError("Destination already exists: " + str(destination))
    if destination.is_relative_to(root) or root.is_relative_to(destination):
        raise ValueError("Project and destination must not overlap")
    prepared = []
    for name in load_manifest(manifest):
        relative_source = "scripts/public-templates/" + name if name in TEMPLATES else name
        source = root / relative_source
        no_links(source)
        if not source.is_file():
            raise ValueError("Manifest entry is not a regular file: " + relative_source)
        payload = source.read_bytes()
        if source.suffix.lower() not in BINARY_SUFFIXES:
            text = payload.decode("utf-8-sig")
            normalized = text.replace("\\\\", "\\").replace("\\", "/").casefold()
            if (PERSONAL_PATH.search(text)
                    or any(str(p).replace("\\", "/").casefold() in normalized
                           for p in (root, destination))):
                raise ValueError("Local/private machine path in public text: " + name)
        prepared.append((name, relative_source, payload))
    return prepared


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", required=True)
    parser.add_argument("--destination", required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        root, destination, manifest = map(native_absolute, (args.project_root, args.destination, args.manifest))
        prepared = prepare(root, destination, manifest)
        for name, source, _ in prepared:
            print(name + " <- " + source)
        if args.dry_run:
            print(f"Dry run: {len(prepared)} files; destination: {destination}")
            return 0
        destination.mkdir()  # Exclusive claim; never merge, overwrite or clean an old tree.
        for name, _, payload in prepared:
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            no_links(target.parent)
            with target.open("xb") as stream:
                stream.write(payload)
        print(f"Exported {len(prepared)} files to {destination}")
        return 0
    except (OSError, ValueError, UnicodeError) as exc:
        print("error: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
