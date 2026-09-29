"""OpenAPI document helpers shared by the round-trip tools. Standard library only."""

import json
import urllib.parse


METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")
COMPONENT_NAMESPACES = (
    "schemas",
    "responses",
    "parameters",
    "examples",
    "requestBodies",
    "headers",
    "securitySchemes",
    "links",
    "callbacks",
    "pathItems",
)


def load_json(path):
    try:
        with open(path, encoding="utf-8") as source:
            document = json.load(source)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(document, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    return document


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def pointer_token(value):
    return str(value).replace("~", "~0").replace("/", "~1")


def pointer_parts(reference):
    """The unescaped tokens of a local ``#`` / ``#/`` reference, or None.

    The fragment is percent-decoded before it is split (RFC 6901 §6), so ``%2F`` is a
    separator and a literal ``/`` in a name must be written ``~1``. This matches
    Rivet.Tool's ``JsonPointer.FromUriFragment``.
    """
    if reference == "#":
        return []
    if not isinstance(reference, str) or not reference.startswith("#/"):
        return None
    return [
        part.replace("~1", "/").replace("~0", "~")
        for part in urllib.parse.unquote(reference[2:]).split("/")
    ]


def resolve_local_reference(document, reference):
    parts = pointer_parts(reference)
    if parts is None:
        return None
    current = document
    for part in parts:
        if isinstance(current, dict) and part in current:
            current = current[part]
        elif isinstance(current, list) and part.isdigit() and int(part) < len(current):
            current = current[int(part)]
        else:
            return None
    return current
