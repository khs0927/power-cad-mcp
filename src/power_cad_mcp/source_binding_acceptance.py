"""Consumer-side acceptance gate for Ontology executor handoffs.

This module is read-only. Passing this gate still does not authorize a CAD
mutation. The executor must revalidate inside its single-writer transaction and
emit an execution receipt.
"""

from __future__ import annotations

import hashlib
import json
import ntpath
from typing import Any

SCHEMA = "aec-executor-handoff/1"
READY = "READY_FOR_EXECUTOR_REVALIDATION"
BLOCKED = "BLOCKED"
_HEX = set("0123456789abcdef")


def _path(value: Any) -> str | None:
    if not isinstance(value, str) or not value.strip():
        return None
    return ntpath.normcase(ntpath.normpath(value.strip()))


def _text(value: Any) -> str | None:
    return value.strip() if isinstance(value, str) and value.strip() else None


def _sha256(value: Any) -> str | None:
    text = _text(value)
    if text is None or len(text) != 64 or any(ch not in _HEX for ch in text):
        return None
    return text


def _handle(value: Any) -> str | None:
    text = _text(value)
    return text.upper() if text else None


def _layout(value: Any) -> str | None:
    text = _text(value)
    return text.casefold() if text else None


def _instance_path(value: Any) -> tuple[str, ...] | None:
    if not isinstance(value, list):
        return None
    out: list[str] = []
    for item in value:
        text = _text(item)
        if text is None:
            return None
        out.append(text.upper())
    return tuple(out)


def _digest(value: Any) -> str:
    return hashlib.sha256(
        json.dumps(
            value,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
            allow_nan=False,
        ).encode("utf-8")
    ).hexdigest()


def _verify_handoff_digest(handoff: dict[str, Any]) -> bool:
    declared = _sha256(handoff.get("handoff_digest"))
    if declared is None:
        return False
    payload = dict(handoff)
    payload.pop("handoff_digest", None)
    return declared == _digest(payload)


def evaluate_source_binding_acceptance(
    handoff: dict[str, Any],
    fresh_document: dict[str, Any],
    fresh_target: dict[str, Any],
) -> dict[str, Any]:
    """Recheck an Ontology executor handoff against fresh CAD observations.

    READY means only that the bounded handoff still matches fresh read-only
    observations. It is not an execution token.
    """
    reasons: list[str] = []

    if handoff.get("schema") != SCHEMA:
        reasons.append("unsupported_handoff_schema")
    if handoff.get("binding_state") != "SOURCE_BOUND":
        reasons.append("handoff_not_source_bound")
    if handoff.get("review_status") != "VERIFIED_FOR_REVIEW":
        reasons.append("handoff_not_review_ready")
    if handoff.get("execution_authorized") is not False:
        reasons.append("handoff_must_not_pre_authorize_execution")
    if handoff.get("may_execute_mutation") is not False:
        reasons.append("handoff_must_be_read_only")
    if handoff.get("requires_executor_authorization") is not True:
        reasons.append("handoff_must_require_executor_authorization")
    if not _verify_handoff_digest(handoff):
        reasons.append("handoff_digest_invalid")

    for name in (
        "source_id",
        "source_byte_revision_id",
        "parser_revision_id",
        "candidate_id",
        "document_id",
        "session_id",
        "native_path",
        "state_digest",
        "modification_generation",
        "units",
        "cache_entry_id",
    ):
        if _text(handoff.get(name)) is None:
            reasons.append(f"missing_handoff_identity:{name}")

    source_sha = _sha256(handoff.get("source_sha256"))
    file_sha = _sha256(handoff.get("file_sha256"))
    receipt_sha = _sha256(handoff.get("resolver_receipt_sha256"))
    if source_sha is None:
        reasons.append("missing_or_invalid_handoff_source_sha256")
    if file_sha is None:
        reasons.append("missing_or_invalid_handoff_file_sha256")
    if receipt_sha is None:
        reasons.append("missing_or_invalid_resolver_receipt_sha256")
    if source_sha is not None and file_sha is not None and source_sha != file_sha:
        reasons.append("handoff_source_file_hash_mismatch")

    locator = handoff.get("object_locator")
    if not isinstance(locator, dict):
        reasons.append("missing_handoff_object_locator")
        locator = {}
    for name in ("handle", "fingerprint", "layout", "instance_path"):
        if name not in locator:
            reasons.append(f"missing_handoff_locator_field:{name}")

    required_doc = (
        "session_id",
        "document_id",
        "native_path",
        "file_sha256",
        "state_digest",
        "modification_generation",
        "document_dirty",
        "units",
    )
    for name in required_doc:
        if name not in fresh_document:
            reasons.append(f"missing_fresh_document_field:{name}")

    if fresh_document.get("document_dirty") is not False:
        reasons.append("dirty_or_unknown_current_document")

    for name in ("session_id", "document_id", "state_digest", "modification_generation", "units"):
        if _text(fresh_document.get(name)) != _text(handoff.get(name)):
            reasons.append(f"fresh_document_mismatch:{name}")

    if _path(fresh_document.get("native_path")) != _path(handoff.get("native_path")):
        reasons.append("fresh_document_mismatch:native_path")

    fresh_file_sha = _sha256(fresh_document.get("file_sha256"))
    if fresh_file_sha is None:
        reasons.append("missing_or_invalid_fresh_file_sha256")
    elif file_sha is not None and fresh_file_sha != file_sha:
        reasons.append("fresh_document_mismatch:file_sha256")

    required_target = ("handle", "fingerprint", "layout", "instance_path")
    for name in required_target:
        if name not in fresh_target:
            reasons.append(f"missing_fresh_target_field:{name}")

    if _handle(fresh_target.get("handle")) != _handle(locator.get("handle")):
        reasons.append("fresh_target_mismatch:handle")
    if _text(fresh_target.get("fingerprint")) != _text(locator.get("fingerprint")):
        reasons.append("fresh_target_mismatch:fingerprint")
    if _layout(fresh_target.get("layout")) != _layout(locator.get("layout")):
        reasons.append("fresh_target_mismatch:layout")
    if _instance_path(fresh_target.get("instance_path")) != _instance_path(locator.get("instance_path")):
        reasons.append("fresh_target_mismatch:instance_path")

    reasons = sorted(set(reasons))
    return {
        "schema": "power-cad-source-binding-acceptance/2",
        "status": READY if not reasons else BLOCKED,
        "reasons": reasons,
        "handoff_digest": handoff.get("handoff_digest"),
        "source_id": handoff.get("source_id"),
        "source_byte_revision_id": handoff.get("source_byte_revision_id"),
        "parser_revision_id": handoff.get("parser_revision_id"),
        "candidate_id": handoff.get("candidate_id"),
        "document_id": fresh_document.get("document_id"),
        "file_sha256": fresh_document.get("file_sha256"),
        "handle": fresh_target.get("handle"),
        "fingerprint": fresh_target.get("fingerprint"),
        "may_execute_mutation": False,
        "execution_authorized": False,
        "requires_transaction_revalidation": True,
        "requires_single_writer": True,
        "requires_execution_receipt": True,
        "note": (
            "READY means only that the Ontology executor handoff still matches "
            "fresh read-only document bytes/state and target identity. Power CAD "
            "must re-check document state, file identity and target fingerprint "
            "inside the single-writer transaction before mutation and then emit "
            "COMMITTED/ROLLED_BACK/REJECTED/INDETERMINATE."
        ),
    }
