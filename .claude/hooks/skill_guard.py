#!/usr/bin/env python3
"""PreToolUse hook: block Vulkan, ECS, ECS-to-GPU, or model work until the matching project skill is loaded in this
session.

Each guard names a skill, the folders it covers, and sets of patterns for code in its area. Guarded work is
a file write under one of the folders, a write to a .cs file whose existing and new text together match
every pattern of one set, or a Bash command whose words name one of the folders (a heredoc's text is not its
words), unless that command only reads (every part of it a reading program such as ls, cat, grep, sed -n, or git
diff, with no redirection into a file and no command substitution: is_read_only). A skill counts as loaded when
the session transcript holds a Skill tool call for it or a typed /<skill> command after the last
/clear. A call from a subagent (the event carries its agent_id) is checked against the subagent's own
transcript, so a subagent must load the skill itself: the main session's loads do not count for it, and
its own loads count. When the transcript cannot be read, the hook allows the call and adds a reminder, so
a hook fault never blocks work.
"""

import json
import os
import re
import shlex
import sys
from typing import NamedTuple


class Guard(NamedTuple):
    skill: str
    topic: str
    folders: tuple  # repo-relative, with a trailing slash
    code: tuple  # pattern sets: a .cs file matches when its text matches every pattern of one set


VULKAN = re.compile(r"Silk\.NET\.Vulkan")
ARCH = re.compile(r"\bArch\.(Core|System|Buffer|LowLevel)\b")
GPU_DATA = re.compile(r"\bItera\.Engine\.Rendering\.GpuData\b")

GUARDS = (
    Guard("vulkan", "Vulkan", ("src/Itera.Engine/Rendering/",), ((VULKAN,),)),
    Guard(
        "ecs",
        "ECS",
        (
            "src/Itera.Engine/Ecs/",
            "src/Itera.Engine/Systems/",
            "src/Itera.Engine/Components/",
            "src/Game/Systems/",
            "src/Game/Components/",
        ),
        ((ARCH,),),
    ),
    Guard(
        "ecs-vulkan",
        "ECS-to-GPU",
        ("src/Itera.Engine/Rendering/GpuData/",),
        ((GPU_DATA,), (ARCH, VULKAN)),
    ),
    Guard("models", "Model", ("tools/models/", "src/Game/ModelRecipes/"), ()),
)
WRITE_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit"}


def normalise(path):
    return path.replace("\\", "/")


def in_folder(guard, path):
    return any(folder in normalise(path) for folder in guard.folders)


def uses_library(guard, tool_input, path):
    """True when a .cs file's existing text and the new text together match every pattern of one set."""
    if not path.endswith(".cs"):
        return False
    texts = [tool_input.get("content"), tool_input.get("new_string")]
    texts += [e.get("new_string") for e in tool_input.get("edits") or [] if isinstance(e, dict)]
    texts = [t for t in texts if isinstance(t, str)]
    try:
        with open(path, encoding="utf-8", errors="ignore") as f:
            texts.append(f.read())
    except OSError:
        pass
    text = "\n".join(texts)
    return any(all(pattern.search(text) for pattern in patterns) for patterns in guard.code)


HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
OPERATORS = {"|", "||", "&&", ";", "&", "\n", "(", ")", ";;", "|&"}


def without_heredocs(command):
    """The command with each heredoc's text left out: its lines are data for a program, not words of the command."""
    lines = command.split("\n")
    kept = []
    end = None
    for line in lines:
        if end is not None:
            if line.strip() == end:
                end = None
            continue
        kept.append(line)
        found = HEREDOC.search(line)
        if found:
            end = found.group(2)
    return "\n".join(kept)


def words_of(command):
    """The command's words and operators, split as the shell splits them around quotes, or None when a quote does not
    close."""
    lexer = shlex.shlex(without_heredocs(command), posix=True, punctuation_chars="();<>|&\n")
    lexer.whitespace = " \t\r"
    lexer.whitespace_split = True
    try:
        return list(lexer)
    except ValueError:
        return None


def names_folder(guard, command):
    # Match without the "src/" prefix so relative paths such as "Itera.Engine/Ecs" also count. Only the command's words
    # count, so a heredoc that mentions a folder does not; when the words cannot be split, the whole text counts.
    words = words_of(command)
    texts = [normalise(word) for word in words] if words is not None else [normalise(command)]
    return any(folder.removeprefix("src/").rstrip("/") in text for folder in guard.folders for text in texts)


# Programs that only read files and print (and git's reading subcommands); a command made only of these may name a
# guarded folder without the skill. Not awk (it can write), nor xargs (it runs any program); sed only as sed_reads
# allows.
READ_PROGRAMS = {
    "ls", "cat", "head", "tail", "wc", "grep", "egrep", "fgrep", "rg", "stat", "file", "tree", "du", "diff", "cut",
    "sort", "uniq", "tr", "nl", "basename", "dirname", "realpath", "echo", "true", "test", "[", "cd", "pwd", "printf",
    "pgrep", "ps", "jq", "which",
}
GIT_READS = {"diff", "log", "show", "status", "blame", "grep", "ls-files", "rev-parse"}
FIND_WRITES = {"-exec", "-execdir", "-delete", "-ok", "-okdir", "-fprint", "-fprint0", "-fprintf", "-fls"}
# A sed script that only prints: addresses (a line, $, /regex/, a range, first~step) with p, P, l, =, or q.
SED_ADDRESS = r"(?:\d+|\$|/(?:[^/\\]|\\.)*/)"
SED_COMMAND = rf"(?:{SED_ADDRESS}(?:\s*,\s*{SED_ADDRESS}|~\d+)?)?\s*!?\s*[pPl=q]"
SED_PRINTS = re.compile(rf"\s*{SED_COMMAND}(?:\s*[;\n]\s*{SED_COMMAND})*\s*;?\s*")
SED_FLAGS = re.compile(r"-[nErsuz]+")


def sed_reads(words):
    """True for sed -n whose scripts only print, with no -i: it writes nothing but its output."""
    scripts, files, quiet = [], [], False
    arguments = iter(words[1:])
    for word in arguments:
        if word in ("-e", "--expression"):
            scripts.append(next(arguments, ""))
        elif word in ("-n", "--quiet", "--silent"):
            quiet = True
        elif SED_FLAGS.fullmatch(word):
            quiet = quiet or "n" in word
        elif word.startswith("-"):
            return False
        else:
            files.append(word)
    if not scripts and files:
        scripts.append(files.pop(0))
    return quiet and bool(scripts) and all(SED_PRINTS.fullmatch(script) for script in scripts)


def is_read_only(command):
    """True when every part of the command (split at pipes, ';', '&', '&&', '||', and new lines, outside quotes) runs
    a program that only reads, with no redirection into a file and no command substitution, which could hide
    anything."""
    if "$(" in command or "`" in command or "<(" in command or ">(" in command:
        return False
    words = words_of(command)
    if words is None:
        return False
    parts, part, following = [], [], None
    for word in words:
        if following:
            # The word after a redirection: only /dev/null or another descriptor (2>&1) writes nothing.
            to_descriptor = following.endswith("&") and word.isdigit()
            if following.startswith((">", "&>")) and word != "/dev/null" and not to_descriptor:
                return False
            following = None
        elif word in OPERATORS:
            parts.append(part)
            part = []
        elif word and word[0] in "<>&" and set(word) <= set("<>&"):
            following = word
            if part and part[-1].isdigit():
                part.pop()  # the descriptor of 2>, not an argument
        else:
            part.append(word)
    parts.append(part)
    for words in parts:
        if not words:
            continue
        program = os.path.basename(words[0])
        if program == "git":
            rest = words[1:]
            while rest[:1] in (["-C"], ["--no-pager"]):
                rest = rest[2:] if rest[0] == "-C" else rest[1:]
            if not rest or rest[0] not in GIT_READS:
                return False
        elif program == "find":
            if any(word in FIND_WRITES for word in words):
                return False
        elif program == "dotnet":
            if words[1:3] != ["csharpier", "check"]:
                return False
        elif program == "sed":
            if not sed_reads(words):
                return False
        elif program not in READ_PROGRAMS:
            return False
    return True


def matching_guards(tool_name, tool_input):
    if tool_name in WRITE_TOOLS:
        path = tool_input.get("file_path") or tool_input.get("notebook_path") or ""
        return [g for g in GUARDS if in_folder(g, path) or uses_library(g, tool_input, path)]
    if tool_name == "Bash":
        command = tool_input.get("command") or ""
        if is_read_only(command):
            return []
        return [g for g in GUARDS if names_folder(g, command)]
    return []


AGENT_ID = re.compile(r"^[A-Za-z0-9_-]+$")


def find_session_transcript(event):
    path = event.get("transcript_path")
    if path and os.path.isfile(path):
        return path
    session_id, cwd = event.get("session_id"), event.get("cwd")
    if session_id and cwd:
        project = re.sub(r"[^A-Za-z0-9]", "-", cwd)
        derived = os.path.join(os.path.expanduser("~"), ".claude", "projects", project, session_id + ".jsonl")
        if os.path.isfile(derived):
            return derived
    return None


def find_transcript(event):
    """The transcript to check: the main session's, or for a call from a subagent, the subagent's own.

    In a subagent's event, transcript_path is the main session's transcript (<session>.jsonl); the subagent's own is
    <session>/subagents/agent-<agent_id>.jsonl beside it. None when it cannot be found.
    """
    session = find_session_transcript(event)
    agent_id = event.get("agent_id")
    if not agent_id:
        return session
    if not isinstance(agent_id, str) or not AGENT_ID.match(agent_id) or not session:
        return None
    name = f"agent-{agent_id}.jsonl"
    if os.path.basename(session) == name:
        return session
    own = os.path.join(os.path.splitext(session)[0], "subagents", name)
    return own if os.path.isfile(own) else None


def message_content(entry):
    message = entry.get("message")
    if not isinstance(message, dict):
        return []
    content = message.get("content")
    if isinstance(content, str):
        return [{"type": "text", "text": content}]
    return content if isinstance(content, list) else []


def loaded_skills(transcript, skills):
    """Returns the subset of skills loaded since the last /clear."""
    loaded = set()
    quoted = [f'"{skill}"' for skill in skills]
    with open(transcript, encoding="utf-8", errors="ignore") as f:
        for line in f:
            # Cheap filter before parsing: only lines that can change the answer.
            if "<command-name>/" not in line and not any(q in line for q in quoted):
                continue
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            for block in message_content(entry):
                if not isinstance(block, dict):
                    continue
                if block.get("type") == "tool_use" and block.get("name") == "Skill":
                    skill = (block.get("input") or {}).get("skill")
                    if skill in skills:
                        loaded.add(skill)
                text = block.get("text") if block.get("type") == "text" else None
                if isinstance(text, str):
                    if "<command-name>/clear</command-name>" in text:
                        loaded.clear()
                    for skill in skills:
                        if f"<command-name>/{skill}</command-name>" in text:
                            loaded.add(skill)
    return loaded


def deny_reason(missing):
    topics = " and ".join(g.topic for g in missing)
    names = " and ".join(g.skill for g in missing)
    folders = ", ".join(f".claude/skills/{g.skill}" for g in missing)
    typed = " and ".join(f"/{g.skill}" for g in missing)
    if len(missing) == 1:
        requires = f"the project's {names} skill ({folders})"
        load = f'Call the Skill tool with skill "{missing[0].skill}" once in this session'
    else:
        quoted = " and ".join(f'"{g.skill}"' for g in missing)
        requires = f"the project's {names} skills ({folders})"
        load = f"Call the Skill tool once for each of {quoted} in this session"
    return (
        f"{topics} work in Itera requires {requires}. {load}, follow it, then retry this call. "
        f"The user can also type {typed}."
    )


def respond(output):
    json.dump({"hookSpecificOutput": {"hookEventName": "PreToolUse", **output}}, sys.stdout)


def main():
    event = json.load(sys.stdin)
    guards = matching_guards(event.get("tool_name", ""), event.get("tool_input") or {})
    if not guards:
        return

    transcript = find_transcript(event)
    try:
        loaded = loaded_skills(transcript, {g.skill for g in guards}) if transcript else None
    except OSError:
        loaded = None

    if loaded is None:
        names = ", ".join(g.skill for g in guards)
        respond(
            {
                "additionalContext": "skill_guard could not read the session transcript, so it did not check "
                f"whether the {names} skill is loaded. Load it with the Skill tool before this work if it is "
                "not already loaded."
            }
        )
        return

    missing = [g for g in guards if g.skill not in loaded]
    if missing:
        respond({"permissionDecision": "deny", "permissionDecisionReason": deny_reason(missing)})


if __name__ == "__main__":
    main()
