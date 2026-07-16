#!/usr/bin/env python3
"""
dav-cleanup.py — Find and delete duplicate DAV items (files/dirs ending in (2), (3), etc.)

Usage:
    ./dav-cleanup.py --url http://host:port --api-key KEY --duplicate 2
    ./dav-cleanup.py --url http://host:port --api-key KEY --duplicate 2 --yes
    ./dav-cleanup.py --url http://host:port --api-key KEY --all
"""

import argparse
import json
import re
import sys
import urllib.request
import urllib.parse
import urllib.error


def search(base_url: str, api_key: str, query: str) -> list:
    url = f"{base_url}/api/search-webdav"
    data = urllib.parse.urlencode({"query": query, "directory": ""}).encode()
    req = urllib.request.Request(url, data=data, method="POST")
    req.add_header("x-api-key", api_key)
    req.add_header("Content-Type", "application/x-www-form-urlencoded")
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read()).get("results", [])
    except urllib.error.HTTPError as e:
        print(f"Search failed: HTTP {e.code} {e.reason}", file=sys.stderr)
        sys.exit(1)


def delete_item(base_url: str, api_key: str, dav_item_id: str) -> bool:
    url = f"{base_url}/api/dav-items/{dav_item_id}"
    req = urllib.request.Request(url, method="DELETE")
    req.add_header("x-api-key", api_key)
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read()).get("status", False)
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code} {e.reason}")
        return False


def find_duplicates(base_url: str, api_key: str, suffix: int) -> list:
    pattern = re.compile(rf"\({suffix}\)(\.[^.]+)?$", re.IGNORECASE)
    results = search(base_url, api_key, f"({suffix})")
    return [r for r in results if r.get("davItemId") and pattern.search(r["name"])]


def print_matches(matches: list, suffix: int):
    print(f"\nFound {len(matches)} item(s) ending in ({suffix}):\n")
    for item in matches:
        kind = "DIR " if item.get("isDirectory") else "FILE"
        size = f"  [{item['size']:,} bytes]" if item.get("size") else ""
        print(f"  [{kind}] {item['path']}{size}")


def confirm_and_delete(base_url: str, api_key: str, matches: list, yes: bool) -> tuple:
    if not yes:
        answer = input(f"\nDelete {len(matches)} item(s)? [y/N] ").strip().lower()
        if answer != "y":
            return 0, 0, True  # aborted

    print()
    deleted = 0
    failed = 0
    for item in matches:
        print(f"  Deleting {item['path']}...", end=" ", flush=True)
        if delete_item(base_url, api_key, item["davItemId"]):
            print("OK")
            deleted += 1
        else:
            print("FAILED")
            failed += 1
    return deleted, failed, False


def main():
    parser = argparse.ArgumentParser(
        description="Find and delete duplicate DAV items with (N) suffix",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Examples:
  # Dry-run: show items ending in (4)
  ./dav-cleanup.py --url http://192.168.1.1:8181 --api-key KEY --duplicate 4

  # Delete items ending in (2) without confirmation
  ./dav-cleanup.py --url http://192.168.1.1:8181 --api-key KEY --duplicate 2 --yes

  # Scan for all duplicates (2) through (9)
  ./dav-cleanup.py --url http://192.168.1.1:8181 --api-key KEY --all
""",
    )
    parser.add_argument("--url", required=True, help="nzbdav2 base URL (e.g. http://192.168.1.1:8181)")
    parser.add_argument("--api-key", required=True, help="FRONTEND_BACKEND_API_KEY value")
    parser.add_argument("--duplicate", type=int, metavar="N", help="Target duplicate suffix number (e.g. 2, 3, 4)")
    parser.add_argument("--all", action="store_true", help="Scan for all duplicate suffixes (2) through (9)")
    parser.add_argument("--yes", "-y", action="store_true", help="Skip confirmation and delete immediately")
    args = parser.parse_args()

    if not args.duplicate and not args.all:
        parser.error("Specify --duplicate N or --all")

    base_url = args.url.rstrip("/")
    suffixes = range(2, 10) if args.all else [args.duplicate]

    total_deleted = 0
    total_failed = 0

    for suffix in suffixes:
        print(f"Searching for items ending in ({suffix})...", end=" ", flush=True)
        matches = find_duplicates(base_url, args.api_key, suffix)

        if not matches:
            print("none found.")
            continue

        print(f"{len(matches)} found.")
        print_matches(matches, suffix)

        deleted, failed, aborted = confirm_and_delete(base_url, args.api_key, matches, args.yes)
        if aborted:
            print("Aborted.")
            break

        total_deleted += deleted
        total_failed += failed

    if total_deleted or total_failed:
        print(f"\nTotal — Deleted: {total_deleted}, Failed: {total_failed}")


if __name__ == "__main__":
    main()
