#!/usr/bin/env python
"""JSON round-trip driver of the WWCP S2 interoperability tests.

Reads one request per line from stdin, ``{"id": <any>, "message": <S2 JSON>}``, parses the
message with s2-python (``S2Parser.parse_as_any_message``, i.e. the pydantic model of the
message type with all of its validators), re-serialises it with ``to_json`` and answers
``{"id": <same>, "ok": true, "message": <re-serialised>, "type": "<python class>"}`` or
``{"id": <same>, "ok": false, "error": "<reason>"}``.
"""

import json
import sys

import s2python
from s2python.s2_parser import S2Parser
from s2python.s2_validation_error import S2ValidationError


def describe(error: BaseException) -> str:
    text = f"{type(error).__name__}: {error}"
    cause = error.__cause__
    while cause is not None:
        text += f" <- {type(cause).__name__}: {cause}"
        cause = cause.__cause__
    return text


def main() -> int:
    print(json.dumps({"event": "ready", "s2python": s2python.__version__}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        if line == "__exit__":
            break

        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            message = S2Parser.parse_as_any_message(request["message"])
            response = {
                "id": request_id,
                "ok": True,
                "message": json.loads(message.to_json()),
                "type": type(message).__name__,
            }
        except (S2ValidationError, ValueError, KeyError, TypeError) as error:
            response = {"id": request_id, "ok": False, "error": describe(error)}

        print(json.dumps(response), flush=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
