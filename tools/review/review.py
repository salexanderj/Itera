#!/usr/bin/env python3
"""The review page: one local HTML page where every session posts the pictures it wants the user to see.

    tools/review/review.py post TITLE FILE... [--note TEXT] [--caption FILE=TEXT]... [--issue REF]
    tools/review/review.py page            # write the page again
    tools/review/review.py list            # the posts, newest first
    tools/review/review.py path            # the page's path
    tools/review/review.py prune --days N  # remove posts older than N days

A post copies its files (PNG, JPEG, WebP, GIF, MP4, WebM, or a folder of them) into
~/.local/share/itera-review/posts/<time>-<title>/, with what it was about: its title, a note, a caption for each file,
an issue, and the checkout and branch it came from. The page, ~/.local/share/itera-review/index.html, shows every post
newest first, from every checkout, worktree, and game on the engine, so the user opens one file. `post` prints the
page's address with the post's anchor.

Development tool, not game code. Standard library only.
"""

from __future__ import annotations

import argparse
import datetime as dt
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

IMAGES = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
VIDEOS = {".mp4", ".webm"}
#: The posts shown open on the page; older ones are listed folded below them.
OPEN_POSTS = 30

STYLE = """
:root { color-scheme: dark; --bg: #151517; --panel: #1f1f23; --line: #34343a; --text: #e8e8ea; --dim: #9a9aa2;
  --link: #b48cff; }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.5 system-ui, sans-serif; }
header, main { max-width: 1600px; margin: 0 auto; padding: 16px; }
header h1 { margin: 0 0 4px; font-size: 20px; }
.meta { color: var(--dim); margin: 0; font-size: 13px; }
a { color: var(--link); }
section.post { border-top: 1px solid var(--line); padding-top: 12px; margin-top: 20px; }
h2 { font-size: 20px; margin: 0 0 2px; }
.note { background: var(--panel); padding: 10px 12px; border-radius: 6px; margin: 8px 0; white-space: pre-wrap; }
.grid { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(min(100%, 420px), 1fr)); }
figure { margin: 0; background: var(--panel); border-radius: 6px; overflow: hidden; }
figure img, figure video { display: block; width: 100%; height: auto; }
figcaption { padding: 6px 10px; font-size: 13px; color: var(--dim); }
figcaption b { color: var(--text); font-weight: 600; }
details { margin: 12px 0; }
summary { cursor: pointer; color: var(--dim); }
nav { margin-top: 8px; font-size: 13px; }
nav a { margin-right: 12px; }
"""


def review_folder() -> Path:
    """Where the page and the posts are: one folder for every checkout of every game on the engine,
    ~/.local/share/itera-review (XDG_DATA_HOME's in its place; `ITERA_REVIEW` in place of both, for tests)."""
    override = os.environ.get("ITERA_REVIEW")
    if override:
        return Path(override)
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "itera-review"


def slug(text: str) -> str:
    words = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return words[:48].strip("-") or "post"


def git(*arguments: str) -> str:
    try:
        done = subprocess.run(["git", *arguments], capture_output=True, text=True, check=True, cwd=Path.cwd())
    except (OSError, subprocess.CalledProcessError):
        return ""
    return done.stdout.strip()


def checkout() -> dict:
    """The checkout the post comes from: its folder's name, its branch, and its GitHub address if it has one."""
    top = git("rev-parse", "--show-toplevel")
    remote = git("remote", "get-url", "origin")
    match = re.search(r"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?$", remote)
    name = Path(top).name if top else ""
    folder = Path(top)
    if top and folder.parent.name == "worktrees" and folder.parent.parent.name == ".claude":
        name = f"{folder.parent.parent.parent.name} worktree {folder.name}"
    return {
        "checkout": name,
        "path": top,
        "branch": git("rev-parse", "--abbrev-ref", "HEAD"),
        "github": match.group(1) if match else "",
    }


def files_of(paths: list[str]) -> list[Path]:
    """The files to post: each file given, and each picture or video in a folder given, in name order."""
    found = []
    for given in paths:
        path = Path(given)
        if path.is_dir():
            found += sorted(p for p in path.iterdir() if p.suffix.lower() in IMAGES | VIDEOS)
        elif path.is_file():
            if path.suffix.lower() not in IMAGES | VIDEOS:
                raise SystemExit(f"review: {path} is not a picture or a video ({', '.join(sorted(IMAGES | VIDEOS))})")
            found.append(path)
        else:
            raise SystemExit(f"review: there is no file {path}")
    if not found:
        raise SystemExit("review: nothing to post")
    return found


def post(title: str, paths: list[str], note: str, captions: list[str], issue: str, now: dt.datetime) -> Path:
    """Copies the files into a post of their own, writes the page again, and returns the post's folder."""
    files = files_of(paths)
    by_file = {}
    for caption in captions:
        name, _, text = caption.partition("=")
        if not text:
            raise SystemExit(f"review: a caption is FILE=TEXT, not {caption!r}")
        by_file[Path(name).name] = text
    posts = review_folder() / "posts"
    posts.mkdir(parents=True, exist_ok=True)
    stem = f"{now:%Y%m%d-%H%M%S}-{slug(title)}"
    folder = posts / stem
    number = 2
    while folder.exists():
        folder = posts / f"{stem}-{number}"
        number += 1
    folder.mkdir()
    entries = []
    for path in files:
        name = path.name
        copy = 2
        while (folder / name).exists():
            name = f"{path.stem}-{copy}{path.suffix}"
            copy += 1
        shutil.copy2(path, folder / name)
        entries.append({"file": name, "caption": by_file.get(path.name, ""), "from": str(path.resolve())})
    record = {"title": title, "time": now.isoformat(timespec="seconds"), "note": note, "issue": issue, **checkout()}
    record["files"] = entries
    (folder / "post.json").write_text(json.dumps(record, indent=2) + "\n")
    write_page()
    return folder


def read_posts() -> list[tuple[Path, dict]]:
    """Every post, newest first; a folder whose post.json cannot be read is left out."""
    posts = review_folder() / "posts"
    found = []
    for folder in sorted(posts.iterdir() if posts.exists() else [], reverse=True):
        try:
            found.append((folder, json.loads((folder / "post.json").read_text())))
        except (OSError, ValueError):
            continue
    return found


def issue_link(issue: str, github: str) -> str:
    """An issue reference as a link: `#12` on the post's own repository, `owner/repo#12` on that one."""
    match = re.fullmatch(r"(?:([\w.-]+/[\w.-]+))?#(\d+)", issue.strip())
    if not match or not (match.group(1) or github):
        return html.escape(issue)
    repository = match.group(1) or github
    return f'<a href="https://github.com/{repository}/issues/{match.group(2)}">{html.escape(issue)}</a>'


def render_post(folder: Path, record: dict) -> str:
    anchor = html.escape(folder.name)
    where = " · ".join(part for part in (record.get("checkout"), record.get("branch")) if part)
    meta = [html.escape(record.get("time", "").replace("T", " ")), html.escape(where)]
    if record.get("issue"):
        meta.append(issue_link(record["issue"], record.get("github", "")))
    parts = [f'<section class="post" id="{anchor}">', f'<h2><a href="#{anchor}">{html.escape(record["title"])}</a></h2>']
    parts.append(f'<p class="meta">{" · ".join(item for item in meta if item)}</p>')
    if record.get("note"):
        parts.append(f'<div class="note">{html.escape(record["note"])}</div>')
    parts.append('<div class="grid">')
    for entry in record.get("files", []):
        source = f"posts/{folder.name}/{entry['file']}"
        href = html.escape(source)
        if Path(entry["file"]).suffix.lower() in VIDEOS:
            shown = f'<video src="{href}" controls loop muted playsinline></video>'
        else:
            shown = f'<a href="{href}" target="_blank"><img src="{href}" loading="lazy" alt=""></a>'
        caption = f"<b>{html.escape(entry['file'])}</b>"
        if entry.get("caption"):
            caption += f" — {html.escape(entry['caption'])}"
        parts.append(f"<figure>{shown}<figcaption>{caption}</figcaption></figure>")
    parts.append("</div></section>")
    return "\n".join(parts)


def write_page() -> Path:
    """Writes the page, index.html, from the posts, newest first, and returns its path. Written whole, then moved into
    place, so two sessions posting at once never leave half a page."""
    folder = review_folder()
    folder.mkdir(parents=True, exist_ok=True)
    posts = read_posts()
    shown, older = posts[:OPEN_POSTS], posts[OPEN_POSTS:]
    nav = " ".join(f'<a href="#{html.escape(f.name)}">{html.escape(r["title"])}</a>' for f, r in shown[:12])
    body = [render_post(f, r) for f, r in shown]
    if older:
        listed = "".join(
            f'<li><a href="#{html.escape(f.name)}">{html.escape(r.get("time", "")[:16].replace("T", " "))} '
            f'{html.escape(r["title"])}</a></li>'
            for f, r in older
        )
        body.append(f"<details><summary>{len(older)} older posts</summary><ul>{listed}</ul>")
        body += [render_post(f, r) for f, r in older]
        body.append("</details>")
    page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Review</title><style>{STYLE}</style></head>
<body><header><h1>Review</h1><p class="meta">{len(posts)} posts, newest first. Reload for new ones.</p>
<nav>{nav}</nav></header>
<main>{"".join(body) if body else "<p>No posts yet.</p>"}</main></body></html>
"""
    target = folder / "index.html"
    with tempfile.NamedTemporaryFile("w", dir=folder, delete=False, suffix=".tmp", encoding="utf-8") as handle:
        handle.write(page)
    os.replace(handle.name, target)
    return target


def prune(days: float, now: dt.datetime) -> int:
    """Removes the posts older than `days` and writes the page again; returns how many went."""
    gone = 0
    for folder, record in read_posts():
        try:
            when = dt.datetime.fromisoformat(record["time"])
        except (KeyError, ValueError):
            continue
        if now - when > dt.timedelta(days=days):
            shutil.rmtree(folder)
            gone += 1
    write_page()
    return gone


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    posting = commands.add_parser("post", help="post files to the page")
    posting.add_argument("title")
    posting.add_argument("files", nargs="+")
    posting.add_argument("--note", default="", help="what to look at, and what was decided or is asked")
    posting.add_argument("--caption", action="append", default=[], help="FILE=TEXT, by the file's name")
    posting.add_argument("--issue", default="", help="#N, or OWNER/REPO#N")
    commands.add_parser("page", help="write the page again")
    commands.add_parser("list", help="list the posts")
    commands.add_parser("path", help="print the page's path")
    pruning = commands.add_parser("prune", help="remove old posts")
    pruning.add_argument("--days", type=float, required=True)
    arguments = parser.parse_args(argv)
    now = dt.datetime.now().replace(microsecond=0)
    if arguments.command == "post":
        folder = post(arguments.title, arguments.files, arguments.note, arguments.caption, arguments.issue, now)
        count = len(json.loads((folder / "post.json").read_text())["files"])
        print(f"posted {count} file(s): {(review_folder() / 'index.html').as_uri()}#{folder.name}")
    elif arguments.command == "page":
        print(write_page().as_uri())
    elif arguments.command == "list":
        for folder, record in read_posts():
            print(f"{record.get('time', '')}  {record.get('checkout', '')}  {record['title']}  ({folder.name})")
    elif arguments.command == "path":
        print((review_folder() / "index.html").as_uri())
    else:
        print(f"removed {prune(arguments.days, now)} post(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
