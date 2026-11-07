# Review page

`review.py` keeps one local HTML page where every session posts the pictures it wants the user to see: screenshots,
view sheets, stress sheets, GIFs, and videos. The user opens one file, `~/.local/share/itera-review/index.html`, for the
work of every checkout, worktree, and game on the engine. A development tool, not game code. Standard library only.

```bash
tools/review/review.py post TITLE FILE... [--note TEXT] [--caption FILE=TEXT]... [--issue REF]
tools/review/review.py page              # write the page again
tools/review/review.py list              # the posts, newest first
tools/review/review.py path              # the page's address
tools/review/review.py prune --days N    # remove posts older than N days
```

## post

- Copies each file into `posts/<time>-<title>/` beside the page. A folder given posts its pictures and videos in name
  order. A file of another kind stops the post (exit 1).
- Records the title, the time, the note, each file's caption, the issue, and the checkout, branch, and GitHub
  repository it was run in (`post.json` in the post's folder). A worktree shows as `REPO worktree NAME`.
- Writes the page again and prints its address with the post's anchor: give the user that link.
- The files are copies, so a later build or run that writes over the originals leaves the post as it was.
- `--note` says what the pictures show and what the user should look at or decide. `--issue` takes `#12` (the
  checkout's own repository) or `OWNER/REPO#12`, and the page links it.

## The page

- Every post, newest first. The newest 30 show open; the older ones are listed folded below them.
- Each post shows its title (a link to its anchor), when and where it was made, its issue, its note, and its files in a
  grid with their captions. A picture opens full size in a new tab, and a video plays in place.
- The page is written whole and then moved into place, so two sessions posting at once never leave half a page. It
  does not reload itself.

## Tests

`python3 tools/review/test_review.py`. `ITERA_REVIEW` sets the folder in place of the user's, as the tests do.
