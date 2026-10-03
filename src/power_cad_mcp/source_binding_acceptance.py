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
TRUST_POLICY_SCHEMA = "power-cad-resolver-trust-policy/1"
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


def _resolver_policy_match(
    handoff: dict[str, Any],
    trust_policy: dict[str, Any] | None,
    reasons: list[str],
) -> bool:
    if trust_policy is None:
        reasons.append("missing_resolver_trust_policy")
        return False
    if not isinstance(trust_policy, dict) or trust_policy.get("schema") != TRUST_POLICY_SCHEMA:
        reasons.append("unsupported_resolver_trust_policy")
        return False

    entries = trust_policy.get("trusted_resolvers")
    if not isinstance(entries, list) or not entries:
        reasons.append("resolver_trust_policy_has_no_entries")
        return False

    resolver_id = _text(handoff.get("resolver_id"))
    issuer = _text(handoff.get("resolver_issuer"))
    domain = _text(handoff.get("trust_domain"))
    key_id = _text(handoff.get("signature_key_id"))

    if handoff.get("receipt_signature_verified") is not True:
        reasons.append("resolver_receipt_signature_not_verified")
    if handoff.get("immutable_cache") is not True:
        reasons.append("resolver_cache_not_immutable")
    for name, value in (
        ("resolver_id", resolver_id),
        ("resolver_issuer", issuer),
        ("trust_domain", domain),
        ("signature_key_id", key_id),
    ):
        if value is None:
            reasons.append(f"missing_resolver_trust_identity:{name}")

    if None in (resolver_id, issuer, domain, key_id):
        return False

    valid_entries = 0
    for row in entries:
        if not isinstance(row, dict):
            continue
        row_resolver = _text(row.get("resolver_id"))
        row_issuer = _text(row.get("resolver_issuer"))
        row_domain = _text(row.get("trust_domain"))
        key_ids = row.get("signature_key_ids")
        if (
            row_resolver is None
            or row_issuer is None
            or row_domain is None
            or not isinstance(key_ids, list)
            or not key_ids
            or any(_text(item) is None for item in key_ids)
        ):
            continue
        valid_entries += 1
        if (
            row_resolver == resolver_id
            and row_issuer == issuer
            and row_domain == domain
            and key_id in key_ids
        ):
            return True

    if valid_entries == 0:
        reasons.append("resolver_trust_policy_has_no_valid_entries")
    else:
        reasons.append("resolver_trust_policy_rejected")
    return False


def evaluate_source_binding_acceptance(
    handoff: dict[str, Any],
    fresh_document: dict[str, Any],
    fresh_target: dict[str, Any],
    trust_policy: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Recheck an Ontology executor handoff against fresh CAD observations.

    READY means only that the bounded handoff still matches fresh read-only
    observations and the executor's current resolver trust policy. It is not an
    execution token.
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
        "resolver_id",
        "cache_entry_id",
        "resolver_issuer",
        "trust_domain",
        "signature_key_id",
    ):
        if _text(handoff.get(name)) is None:
            reasons.append(f"missing_handoff_identity:{name}")

    source_sha = _sha256(handoff.get("source_sha256"))
    file_sha = _sha256(handoff.get("file_sha256"))
    resolved_sha = _sha256(handoff.get("resolved_sha256"))
    receipt_sha = _sha256(handoff.get("resolver_receipt_sha256"))
    if source_sha is None:
        reasons.append("missing_or_invalid_handoff_source_sha256")
    if file_sha is None:
        reasons.append("missing_or_invalid_handoff_file_sha256")
    if resolved_sha is None:
        reasons.append("missing_or_invalid_handoff_resolved_sha256")
    if receipt_sha is None:
        reasons.append("missing_or_invalid_resolver_receipt_sha256")
    if (
        source_sha is not None
        and file_sha is not None
        and resolved_sha is not None
        and len({source_sha, file_sha, resolved_sha}) != 1
    ):
        reasons.append("handoff_source_resolved_file_hash_mismatch")

    resolver_trust_matched = _resolver_policy_match(handoff, trust_policy, reasons)

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
        "schema": "power-cad-source-binding-acceptance/3",
        "status": READY if not reasons else BLOCKED,
        "reasons": reasons,
        "handoff_digest": handoff.get("handoff_digest"),
        "source_id": handoff.get("source_id"),
        "source_byte_revision_id": handoff.get("source_byte_revision_id"),
        "parser_revision_id": handoff.get("parser_revision_id"),
        "candidate_id": handoff.get("candidate_id"),
        "document_id": fresh_document.get("document_id"),
        "file_sha256": fresh_document.get("file_sha256"),
        "resolved_sha256": handoff.get("resolved_sha256"),
        "handle": fresh_target.get("handle"),
        "fingerprint": fresh_target.get("fingerprint"),
        "resolver_id": handoff.get("resolver_id"),
        "resolver_issuer": handoff.get("resolver_issuer"),
        "trust_domain": handoff.get("trust_domain"),
        "signature_key_id": handoff.get("signature_key_id"),
        "resolver_trust_matched": resolver_trust_matched,
        "may_execute_mutation": False,
        "execution_authorized": False,
        "requires_transaction_revalidation": True,
        "requires_single_writer": True,
        "requires_execution_receipt": True,
        "note": (
            "READY means only that the Ontology executor handoff still matches "
            "fresh read-only document bytes/state, target identity and the "
            "executor's current resolver trust policy. Power CAD must re-check "
            "document state, file identity and target fingerprint inside the "
            "single-writer transaction before mutation and then emit "
            "COMMITTED/ROLLED_BACK/REJECTED/INDETERMINATE."
        ),
    }
