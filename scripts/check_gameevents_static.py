#!/usr/bin/env python3
"""Fail the build when a static method is subscribed to a KSP GameEvent.

KSP's EventData<T>.Add (and EventVoid.Add) wraps the delegate in an EvtDelegate
whose constructor reads evt.Target.GetType(). A static method has a null
Target, so Add throws NullReferenceException before the hook is installed and
aborts whatever registration block it sits in. Every later subscription in that
block is silently skipped. Three separate sites have shipped with this
(FlightMilestoneSource, BuffManager, Materialiser). Every GameEvents subscriber
must be an instance method.

This is a text heuristic, not a compiler. Handler names are resolved by
lookup: a bare name in the same file, a qualified name (Foo.Bar.Handler) in the
file that declares `class Bar`. A name that cannot be resolved is a warning,
never a failure, so the check cannot block a legitimate build.

Usage: python3 scripts/check_gameevents_static.py   (from the repo root)
Exit 1 if any static handler is found.
"""

import pathlib
import re
import sys

ROOTS = ["KSPArchipelago", "KSPArchipelago.KSC"]
SKIP_DIRS = {"bin", "obj"}

ADD_RE = re.compile(r"GameEvents(?:\.[A-Za-z_]\w*)+\.Add\s*\(")
ON_EVENT_RE = re.compile(r"\.OnEvent\s*\(\s*([A-Za-z_][\w.]*)\s*\)")
IDENT_RE = re.compile(r"^[A-Za-z_][\w.]*$")
# Tokens that can precede `Name(` on a line without being a return type.
NOT_A_TYPE = {"return", "await", "new", "throw", "else", "case", "in", "is", "as", "yield"}
MODIFIERS = r"(?:public|private|protected|internal|static|override|virtual|async|unsafe|new|extern|sealed)"


def cs_files():
    for root in ROOTS:
        for path in pathlib.Path(root).rglob("*.cs"):
            if SKIP_DIRS.isdisjoint(path.parts):
                yield path


def argument_text(text, open_paren):
    """Return the text between the paren at open_paren and its match."""
    depth = 0
    for i in range(open_paren, len(text)):
        c = text[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return text[open_paren + 1:i]
    return text[open_paren + 1:]


def handler_name(arg):
    """Dotted handler name from an Add(...) argument, or None to skip."""
    arg = arg.strip()
    if "=>" in arg or arg.startswith("delegate"):
        return None  # lambda / anonymous method: Roslyn gives these an instance target
    m = ON_EVENT_RE.search(arg)
    if m:
        return m.group(1)
    if arg.startswith("this."):
        arg = arg[len("this."):]
    if IDENT_RE.match(arg):
        return arg
    return None


def declaration_re(name):
    # modifiers* return-type name(   — the return type must be a real token.
    return re.compile(
        r"^\s*((?:" + MODIFIERS + r"\s+)*)([A-Za-z_][\w<>\[\],.?]*)\s+"
        + re.escape(name) + r"\s*\(",
        re.MULTILINE,
    )


def find_declaration(text, name):
    """Return 'static', 'instance' or None."""
    for m in declaration_re(name).finditer(text):
        if m.group(2) in NOT_A_TYPE:
            continue
        return "static" if "static" in m.group(1).split() else "instance"
    return None


def class_is_static(text, cls):
    return re.search(r"\bstatic\s+(?:partial\s+)?class\s+" + re.escape(cls) + r"\b", text) is not None


def main():
    sources = {path: path.read_text(encoding="utf-8") for path in cs_files()}
    failures = []
    warnings = []
    checked = 0

    for path, text in sources.items():
        for m in ADD_RE.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            name = handler_name(argument_text(text, m.end() - 1))
            if name is None:
                continue
            checked += 1
            parts = name.split(".")
            method = parts[-1]
            qualifier = parts[-2] if len(parts) > 1 else None

            if qualifier is None:
                verdict = find_declaration(text, method)
            else:
                verdict = None
                for other_path, other_text in sources.items():
                    if not re.search(r"\b(?:class|struct)\s+" + re.escape(qualifier) + r"\b", other_text):
                        continue
                    verdict = find_declaration(other_text, method)
                    if verdict == "instance" and class_is_static(other_text, qualifier):
                        verdict = "static"
                    if verdict:
                        break

            where = f"{path}:{line}"
            if verdict == "static":
                failures.append(f"{where}: static handler '{name}' passed to GameEvents.Add")
            elif verdict is None:
                warnings.append(f"{where}: could not resolve handler '{name}' (not checked)")

    for w in warnings:
        print(f"warning: {w}")
    for f in failures:
        print(f"error: {f}")
    print(f"check_gameevents_static: {checked} subscription(s) checked, "
          f"{len(failures)} static, {len(warnings)} unresolved")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
