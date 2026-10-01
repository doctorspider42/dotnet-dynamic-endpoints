#!/usr/bin/env python3
"""Keeps CHANGELOG.md (https://keepachangelog.com) in sync with releases.

  changelog.py notes                    -> prints the [Unreleased] section body (empty when there is nothing to release)
  changelog.py release <version> <date> -> moves [Unreleased] entries under a new version heading and updates the links
"""
import re
import sys
from pathlib import Path

PATH = Path(__file__).resolve().parents[2] / "CHANGELOG.md"
REPO = "https://github.com/doctorspider42/dotnet-dynamic-endpoints"
UNRELEASED = re.compile(r"^## \[Unreleased\][^\n]*\n(?P<body>.*?)(?=^## \[|^\[[^\]]+\]: |\Z)", re.S | re.M)


def unreleased(text: str) -> str:
    match = UNRELEASED.search(text)
    return match.group("body").strip() if match else ""


def release(text: str, version: str, date: str) -> str:
    body = unreleased(text)
    if not body:
        return text

    match = UNRELEASED.search(text)
    text = text[: match.start()] + f"## [Unreleased]\n\n## [{version}] - {date}\n\n{body}\n\n" + text[match.end():]

    # Link references: [Unreleased] compares against the new tag, the new version against the previous one.
    previous = re.search(r"^## \[(\d+\.\d+\.\d+)\]", text[text.index(f"## [{version}]") + 1:], re.M)
    version_link = (f"[{version}]: {REPO}/compare/v{previous.group(1)}...v{version}" if previous
                    else f"[{version}]: {REPO}/releases/tag/v{version}")
    text = re.sub(r"^\[Unreleased\]: .*$", f"[Unreleased]: {REPO}/compare/v{version}...HEAD", text, flags=re.M)
    text = re.sub(r"^(\[Unreleased\]: .*)$", rf"\1\n{version_link}", text, count=1, flags=re.M)
    return text


def main() -> None:
    text = PATH.read_text(encoding="utf-8")
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "notes":
        print(unreleased(text))
    elif command == "release" and len(sys.argv) == 4:
        PATH.write_text(release(text, sys.argv[2], sys.argv[3]), encoding="utf-8", newline="\n")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
