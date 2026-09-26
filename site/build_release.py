"""Assemble the static site with verified metadata from the latest GitHub release."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import urllib.request
from pathlib import Path

REPOSITORY = "franklai-rise/StageManager"
API_URL = f"https://api.github.com/repos/{REPOSITORY}/releases/latest"
SITE_DIR = Path(__file__).resolve().parent


def get_latest_release() -> dict:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "StageManager-Lai-Pages-Build",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(API_URL, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        release = json.load(response)

    version = release.get("tag_name", "")
    if not re.fullmatch(r"v\d+\.\d+\.\d+", version):
        raise ValueError(f"Unexpected latest release tag: {version!r}")
    if release.get("draft") or release.get("prerelease"):
        raise ValueError("Latest release is not a stable public release")

    expected_name = f"Stage_Manager_Lai_{version}.exe"
    matching = [asset for asset in release.get("assets", []) if asset.get("name") == expected_name]
    if len(matching) != 1:
        raise ValueError(f"Expected exactly one {expected_name} release asset")
    asset = matching[0]
    digest = asset.get("digest", "")
    if not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest):
        raise ValueError("Release asset is missing its SHA-256 digest")
    if not isinstance(asset.get("size"), int) or asset["size"] <= 0:
        raise ValueError("Release asset size is invalid")

    release_url = f"https://github.com/{REPOSITORY}/releases/tag/{version}"
    asset_url = f"https://github.com/{REPOSITORY}/releases/download/{version}/{expected_name}"
    if release.get("html_url") != release_url or asset.get("browser_download_url") != asset_url:
        raise ValueError("Release URLs do not match the expected repository and version")

    return {
        "version": version,
        "published_at": release.get("published_at"),
        "release_url": release_url,
        "asset": {
            "name": expected_name,
            "url": asset_url,
            "size_bytes": asset["size"],
            "sha256": digest.split(":", 1)[1].lower(),
        },
    }


def build(destination: Path) -> None:
    release = get_latest_release()
    if destination.resolve() == SITE_DIR or SITE_DIR in destination.resolve().parents:
        raise ValueError("Build output must be outside the source site")
    shutil.copytree(
        SITE_DIR,
        destination,
        dirs_exist_ok=True,
        ignore=shutil.ignore_patterns("build_release.py", "README.md", "__pycache__", "*.pyc"),
    )
    (destination / "data" / "release.json").write_text(
        json.dumps(release, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Prepared {destination} for {release['version']} ({release['asset']['name']})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="Directory for published website files")
    arguments = parser.parse_args()
    try:
        build(arguments.output)
    except Exception as error:
        print(f"Site build failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
