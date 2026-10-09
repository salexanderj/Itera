#!/usr/bin/env python3
"""PreToolUse hook: block Vulkan, ECS, or ECS-to-GPU work until the matching project skill is loaded in this session.

Each guard names a skill, the folders it covers, and sets of patterns for code in its area. Guarded work is
a file write under one of the folders, a write to a .cs file whose existing and new text together match
every pattern of one set, or a Bash command that names one of the folders. A skill counts as loaded when
the session transcript holds a Skill tool call for it or a typed /<skill> command after the last
/clear. A call from a subagent (the event carries its agent_id) is checked against the subagent's own
transcript, so a subagent must load the skill itself: the main session's loads do not count for it, and
its own loads count. When the transcript cannot be read, the hook allows the call and adds a reminder, so
a hook fault never blocks work.
"""

import json
import os
import re
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


def names_folder(guard, command):
    # Match without the "src/" prefix so relative paths such as "Itera.Engine/Ecs" also count.
    command = normalise(command)
    return any(folder.removeprefix("src/").rstrip("/") in command for folder in guard.folders)


def matching_guards(tool_name, tool_input):
    if tool_name in WRITE_TOOLS:
        path = tool_input.get("file_path") or tool_input.get("notebook_path") or ""
        return [g for g in GUARDS if in_folder(g, path) or uses_library(g, tool_input, path)]
    if tool_name == "Bash":
        command = tool_input.get("command") or ""
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
