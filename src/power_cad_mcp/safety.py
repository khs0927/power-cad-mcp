"""Guard rails for the raw ``run_command`` tool.

AutoCAD's command line can reach the operating system (SHELL, START, AutoLISP ``startapp``...).
Because drawing content (text, attributes, file names) can end up in model prompts, commands that
escape AutoCAD are refused by default. AutoLISP is opt-in via POWER_CAD_ALLOW_LISP=1.
"""

from __future__ import annotations

import re

from .errors import CadError

BLOCKED_COMMANDS = {
    "SHELL",
    "SH",
    "START",
    "EXEC",
    "RUN",
    "SCRIPT",
    "SCR",
    "APPLOAD",
    "NETLOAD",
    "ARX",
    "VBARUN",
    "VBALOAD",
    "VBAIDE",
    "VBASTMT",
    "ACTUSERMESSAGE",
    "BROWSER",
    "SECURELOAD",
    "TRUSTEDPATHS",
    "QUIT",
    "EXIT",
    "CLOSEALL",
    "LOADCUIX",
    "CUILOAD",
}
LISP_DANGER = re.compile(
    r"\b(startapp|command-s|vl-cmdf|vlax-create-object|vlax-get-or-create-object|vl-file-delete|"
    r"vl-file-rename|vl-registry-write|open|load|arxload|dos_[a-z]+|setenv)\b",
    re.IGNORECASE,
)


def check_command(command: str, *, allow_commands: bool, allow_lisp: bool) -> str:
    if not allow_commands:
        raise CadError("Raw commands are disabled (POWER_CAD_ALLOW_COMMANDS=0).")
    command = command.strip("\r\n")
    if not command.strip():
        raise CadError("Command is empty.")
    stripped = command.lstrip()
    if stripped.startswith("(") or "(" in stripped and re.search(r"\(\s*[a-z_\-:]+", stripped, re.I):
        if not allow_lisp:
            raise CadError("AutoLISP expressions are disabled; set POWER_CAD_ALLOW_LISP=1 to allow them.")
        if LISP_DANGER.search(stripped):
            raise CadError(
                "This AutoLISP expression calls a function that can reach outside AutoCAD; refused."
            )
    if stripped.startswith("!"):
        raise CadError("'!' LISP variable evaluation is not allowed.")
    # Every token that starts a command (first token, and tokens after an empty-enter) is checked.
    for token in re.split(r"[\s\n]+", stripped):
        name = token.lstrip("_.-'").upper()
        if name in BLOCKED_COMMANDS:
            raise CadError(f"The command {name} is blocked because it can act outside the drawing.")
    return command
