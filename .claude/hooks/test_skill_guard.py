#!/usr/bin/env python3
"""Tests of skill_guard.py's command checks: what counts as a read, and what names a guarded folder.

    python3 -m unittest discover -s .claude/hooks
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import skill_guard  # noqa: E402

ECS = "src/Itera.Engine/Ecs/World.cs"


def guards(command):
    return [g.skill for g in skill_guard.matching_guards("Bash", {"command": command})]


class ReadTests(unittest.TestCase):
    def test_reads(self):
        for command in (
            f"grep -n 'Query\\|World' {ECS} | head -3",
            f'grep "a > b" {ECS}',
            f"cd /home/lucy/dev/Itera && sed -n 10,40p {ECS}",
            f"sed -n '/class World/,/^}}/p' {ECS}",
            f"sed -n -e 5p -e '10,12p' {ECS}",
            f"sed -nE '1~2p;$p' {ECS}",
            f"ls src/Game/Systems 2>&1 | head",
            f"cat {ECS} > /dev/null 2>&1; echo done",
            f"wc -l {ECS}\nhead -5 {ECS}",
            f"git log --oneline -3 -- {ECS}",
            f"git -C /home/lucy/dev/Itera --no-pager show HEAD:{ECS} | head",
            "which dotnet",
        ):
            self.assertTrue(skill_guard.is_read_only(command), command)
            self.assertEqual([], guards(command), command)

    def test_writes(self):
        for command in (
            f"sed -i s/a/b/ {ECS}",
            f"sed -n 'w /tmp/out' {ECS}",
            f"sed -n '1e rm x' {ECS}",
            f"sed 's/a/b/' {ECS}",
            f"cat {ECS}\nrm {ECS}",
            f"cat {ECS} > /tmp/copy",
            f"echo x >> {ECS}",
            f"ls; rm -rf {ECS}",
            f"cat '{ECS}",
            f"awk '{{print}}' {ECS}",
            f"cat $(rm {ECS})",
            f"dotnet build && ls src/Itera.Engine/Ecs",
            f"git -C /tmp checkout -- {ECS}",
        ):
            self.assertFalse(skill_guard.is_read_only(command), command)
            self.assertEqual(["ecs"], guards(command), command)


class FolderTests(unittest.TestCase):
    def test_a_heredoc_that_mentions_a_folder_does_not_name_it(self):
        command = "python3 - <<'EOF'\nprint('src/Itera.Engine/Ecs/ and tools/models/')\nEOF\necho after"
        self.assertEqual([], guards(command))

    def test_the_commands_words_name_a_folder(self):
        self.assertEqual(["models"], guards("python3 tools/models/build.py rifle"))
        self.assertEqual(["models"], guards("python3 - <<'EOF'\nprint(1)\nEOF\npython3 tools/models/build.py"))

    def test_a_command_whose_quotes_do_not_close_counts_by_its_whole_text(self):
        self.assertEqual(["ecs"], guards("python3 -c 'open(\"src/Itera.Engine/Ecs/x\")"))


if __name__ == "__main__":
    unittest.main()
