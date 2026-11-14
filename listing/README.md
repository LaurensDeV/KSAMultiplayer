# listing/

What players read on [Borea](https://ksamodding.github.io/Borea/). The repository's own
`README.md` is for developers and stays in the repository -- the archive a player installs holds the
assembly and `mod.toml` and nothing else -- so this folder is the store page.

| File | Where it goes |
| --- | --- |
| `abstract.txt` | Borea's one-line summary in lists and search |
| `description.md` | the long description, as CommonMark |
| `borea.toml` | the authored document, copied to `listings/KSAMultiplayer.toml` in [KSAModding/content-index](https://github.com/KSAModding/content-index) |
| `forum-post.md` | the Ahwoo thread the listing hangs on: title and body, to paste |

`borea.toml` carries its own copies of the abstract and the description, because that is the file
content-index reads. The two plain files beside it are what to edit and paste from; keep them in
step by hand.

## Publishing

The thread comes first. Borea's schema requires `links.forums` for a mod -- it ties the listing to
an Ahwoo account, settles disputes over an id, and is the takedown tripwire -- so a listing without
one cannot merge. Post `forum-post.md` under **Kitten Space Agency -> Mod Releases** with the
**Gameplay** prefix, then put the thread's URL in `links.forums`.

Then either use [Borea's listing page](https://ksamodding.github.io/content-index/), which writes
the document section by section and opens the pull request for you, or by hand:

```bash
cp listing/borea.toml <content-index>/listings/KSAMultiplayer.toml
```

and open a pull request on content-index. Ownership verifies through the GitHub repository named
under `[releases]`, so a listing whose releases are this repository's merges itself.

Check it before opening anything, from a checkout of content-index:

```bash
pip install -r tools/requirements.txt && python3 tools/check_schema.py
```

## Images

`icon.png` is the listing's tile: four kittens on EVA with three players' names over them, cut
square from a screenshot of a shared world. It is not in `borea.toml` yet, and until it is the
listing shows Borea's default tile: its record goes under `[images.icon]` as a `url`, `sha256`,
`width`, `height` and `size`, and the URL has to name a commit that holds the file.

The schema asks for a square icon of 256-2048 px and at most 256 KiB, and allows up to 16
description images, each at most 2048 px and 1 MiB, PNG or JPEG for anything photographic. Those
are referenced from the description as `ksa-image:<id>` and recorded in `[images]` the same way.

Each image stays on this repository and its URL is **pinned to a commit**, because Borea's checks
fetch every image and fail the listing when the bytes no longer match the recorded hash. Replacing
an image therefore means re-pinning its URL and sending the new record.
