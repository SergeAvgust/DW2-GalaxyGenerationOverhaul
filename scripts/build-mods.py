#!/usr/bin/env python3
"""Builds Galaxy Generation Overhaul and installs it as a local mod under the game's mods folder.

Usage:
  python scripts/build-mods.py [--path PATH] [--no-build] [--dry-run] [-y|--yes]

  --path       Distant Worlds 2 install path (default: DW2_PATH, then C:/Steam/steamapps/common/Distant Worlds 2 on Windows)
  --no-build   copy what is already in temp/build
  --dry-run    report every step without doing any of them
  -y, --yes    answer yes to every question (create a missing local mod folder)

Builds SpiralArms.csproj in Release into temp/build/, copies GalaxyGenerationOverhaul.dll into <install>/mods/Galaxy Generation Overhaul/.
If that folder is missing the script asks before creating it, with a minimal launcher mod.json made from the repo's mod.json.

Then it keeps the launcher's files in step between this repo and the mod folder, exactly as DW2-XL's scripts/build-mods.py does for its code mods
(it uses that repo's own helpers, so it needs the DW2-XL checkout next to this one, or DW2_XL_REPO pointing at it): description.bbcode,
dw2modlauncher.json and changelog.txt (a copy missing on one side is copied over; when both differ the newer is offered, default yes), and from mod.json
the short description, display name, Workshop id and preview image. The rest of the local mod.json is the launcher's and is never touched.
Backups of anything replaced go under DW2-XL's temp/backups/<run>/.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
XL = Path(os.environ.get("DW2_XL_REPO") or REPO.parent / "DW2-XL")
PROJECT = REPO / "SpiralArms.csproj"
DLL_NAME = "GalaxyGenerationOverhaul.dll"
OUT_DIR = REPO / "temp" / "build"
LOCAL_FOLDER = "Galaxy Generation Overhaul"


def install_path(override: str | None) -> Path:
    if override:
        return Path(override)
    env = os.environ.get("DW2_PATH")
    if env:
        return Path(env)
    if os.name == "nt":
        return Path("C:/Steam/steamapps/common/Distant Worlds 2")
    return Path.home() / ".steam/steam/steamapps/common/Distant Worlds 2"


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def sync_launcher_files(local_dir: Path, dry_run: bool, assume_yes: bool) -> None:
    helpers = XL / "scripts"
    if not (helpers / "dw2" / "descriptions.py").is_file():
        fail(f"the DW2-XL checkout was not found at {XL} (set DW2_XL_REPO); it holds the sync helpers")
    sys.path[:0] = [str(helpers), str(helpers / "common-python")]
    from dw2 import changelog_sync, descriptions  # noqa: PLC0415

    name = local_dir.name
    descriptions.sync_description(REPO / "description.bbcode", local_dir / "description.bbcode", name, dry_run, assume_yes=assume_yes)
    descriptions.sync_short_description(REPO / "mod.json", local_dir / "mod.json", name, dry_run)
    descriptions.sync_display_name(REPO / "mod.json", local_dir / "mod.json", name, dry_run)
    descriptions.sync_preview_image(REPO, local_dir, name, dry_run)
    descriptions.sync_workshop_id(REPO / "mod.json", local_dir / "mod.json", name, dry_run)
    # the launcher writes the author (and language) here when it publishes, so the file is edited on both sides
    descriptions.sync_file(REPO / "dw2modlauncher.json", local_dir / "dw2modlauncher.json", name, dry_run, assume_yes=assume_yes)
    # the hooks and channels this mod declares (read by the launcher; the repo is its only editor)
    descriptions.sync_file(REPO / "dw2modlauncher.hooks.json", local_dir / "dw2modlauncher.hooks.json", name, dry_run, assume_yes=assume_yes)
    # the launcher stamps the Unreleased heading of changelog.txt when it publishes: the stamps are merged into the repo copy, never overwritten
    changelog_sync.reconcile(REPO / "changelog.txt", local_dir / "changelog.txt", name, dry_run, assume_yes=assume_yes)


def create_local_mod(local_dir: Path, dry_run: bool, assume_yes: bool) -> None:
    if dry_run:
        print(f"  [dry-run] {local_dir} is missing; would ask to create it")
        return
    if not assume_yes:
        try:
            answer = input(f"local mod folder missing: {local_dir} - create it? [Y/n] ").strip().lower()
        except EOFError:
            answer = "n"
        if answer not in ("", "y", "yes"):
            fail(f"local mod folder missing: {local_dir} (rerun and answer y)")
    meta = json.loads((REPO / "mod.json").read_text(encoding="utf-8"))
    mod_json = {
        "displayName": meta.get("displayName", LOCAL_FOLDER),
        "shortDescription": meta.get("shortDescription") or meta.get("description", ""),
        "version": meta.get("version", "1.0.0"),
        "bundles": [],
    }
    local_dir.mkdir(parents=True, exist_ok=True)
    (local_dir / "mod.json").write_text(json.dumps(mod_json, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"created {local_dir}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--path", default=None, help="override the DW2 install path")
    parser.add_argument("--build", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("-y", "--yes", action="store_true")
    args = parser.parse_args()

    game = install_path(args.path)
    local_dir = game / "mods" / LOCAL_FOLDER
    dll = OUT_DIR / DLL_NAME
    if not game.is_dir():
        fail(f"no Distant Worlds 2 install at {game} (use --path or set DW2_PATH)")
    if not (local_dir / "mod.json").is_file():
        create_local_mod(local_dir, args.dry_run, args.yes)

    command = ["dotnet", "build", str(PROJECT), "-c", "Release", "-o", str(OUT_DIR), f"-p:GameDir={game}"]
    if args.build:
        print("building Galaxy Generation Overhaul")
        if args.dry_run:
            print(f"  [dry-run] would run {' '.join(command)}")
        elif subprocess.run(command, cwd=REPO).returncode != 0:
            fail("dotnet build failed")
    elif not args.dry_run and not dll.is_file():
        fail(f"{dll} not found; run without --no-build first")

    print(f"installing -> mods/{local_dir.name}/{DLL_NAME}")
    if not args.dry_run:
        shutil.copy2(dll, local_dir / DLL_NAME)
    sync_launcher_files(local_dir, args.dry_run, args.yes)
    print("done")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
