"""Tests for review.py: python3 tools/review/test_review.py"""

import datetime as dt
import json
import os
import tempfile
import unittest
from pathlib import Path

import review

# The smallest PNG: one transparent pixel.
PNG = bytes.fromhex(
    "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d4944415478da63f8ffff3f0005fe02fe"
    "a7356e530000000049454e44ae426082"
)
NOW = dt.datetime(2026, 11, 6, 20, 30, 0)


class ReviewTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        os.environ["ITERA_REVIEW"] = str(self.root / "Review")
        self.pictures = self.root / "pictures"
        self.pictures.mkdir()
        for name in ("run.png", "dead.png"):
            (self.pictures / name).write_bytes(PNG)

    def tearDown(self):
        del os.environ["ITERA_REVIEW"]
        self.folder.cleanup()

    def test_a_post_copies_its_files_and_the_page_shows_them_with_their_captions_and_issue(self):
        folder = review.post(
            "Crowd canid <trot>",
            [str(self.pictures / "run.png"), str(self.pictures / "dead.png")],
            "Look at the paws.",
            ["run.png=trotting"],
            "#51",
            NOW,
        )
        self.assertEqual(folder.name, "20261106-203000-crowd-canid-trot")
        self.assertEqual((folder / "run.png").read_bytes(), PNG)
        record = json.loads((folder / "post.json").read_text())
        self.assertEqual([entry["file"] for entry in record["files"]], ["run.png", "dead.png"])
        page = (self.root / "Review" / "index.html").read_text()
        self.assertIn("Crowd canid &lt;trot&gt;", page)
        self.assertIn('src="posts/20261106-203000-crowd-canid-trot/run.png"', page)
        self.assertIn("— trotting", page)
        self.assertIn("Look at the paws.", page)
        self.assertIn('id="20261106-203000-crowd-canid-trot"', page)

    def test_a_folder_posts_its_pictures_and_a_file_of_another_kind_is_refused(self):
        folder = review.post("Sheets", [str(self.pictures)], "", [], "", NOW)
        self.assertEqual(sorted(p.name for p in folder.glob("*.png")), ["dead.png", "run.png"])
        (self.pictures / "notes.txt").write_text("no")
        with self.assertRaises(SystemExit):
            review.post("Notes", [str(self.pictures / "notes.txt")], "", [], "", NOW)

    def test_posts_show_newest_first_and_two_at_one_time_keep_folders_of_their_own(self):
        first = review.post("Same", [str(self.pictures / "run.png")], "", [], "", NOW)
        second = review.post("Same", [str(self.pictures / "run.png")], "", [], "", NOW)
        later = review.post("Later", [str(self.pictures / "run.png")], "", [], "", NOW + dt.timedelta(minutes=5))
        self.assertNotEqual(first, second)
        page = (self.root / "Review" / "index.html").read_text()
        self.assertLess(page.index(f'id="{later.name}"'), page.index(f'id="{first.name}"'))

    def test_an_issue_links_to_its_repository(self):
        self.assertEqual(
            review.issue_link("#51", "salexanderj/ZorfTD"),
            '<a href="https://github.com/salexanderj/ZorfTD/issues/51">#51</a>',
        )
        self.assertIn("salexanderj/Itera/issues/54", review.issue_link("salexanderj/Itera#54", "salexanderj/ZorfTD"))
        self.assertEqual(review.issue_link("soon", "salexanderj/ZorfTD"), "soon")

    def test_prune_removes_only_posts_older_than_its_days(self):
        old = review.post("Old", [str(self.pictures / "run.png")], "", [], "", NOW - dt.timedelta(days=10))
        new = review.post("New", [str(self.pictures / "run.png")], "", [], "", NOW)
        self.assertEqual(review.prune(7, NOW), 1)
        self.assertFalse(old.exists())
        self.assertTrue(new.exists())


if __name__ == "__main__":
    unittest.main()
