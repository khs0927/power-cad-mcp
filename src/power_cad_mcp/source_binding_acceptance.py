"""Consumer-side acceptance gate for Ontology source-live binding reports.

This module is read-only. Passing this gate still does not authorize a CAD
mutation. The executor must revalidate inside its single-writer transaction.
"""

from __future__ import annotations

import ntpath
from typing import Any

SCHEMA = "aec-source-live-binding/1"
READY = "READY_FOR_EXECUTOR_REVALIDATION"
BLOCKED = "BLOCKED"


def _path(value: Any) -> str | None:
    if not isinstance(value, str) or not value.strip():
        return None
    return ntpath.normcase(ntpath.normpath(value.strip()))


def _text(value: Any) -> str | None:
    return value.strip() if isinstance(value, str) and value.strip() else None


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


def evaluate_source_binding_acceptance(
    binding: dict[str, Any],
    fresh_document: dict[str, Any],
    fresh_target: dict[str, Any],
) -> dict[str, Any]:
    """Recheck a SOURCE_BOUND report against fresh executor observations.

    The result is only readiness for *another* executor-side transaction
    revalidation. It is never an execution token.
    """
    reasons: list[str] = []

    if binding.get("schema") != SCHEMA:
        reasons.append("unsupported_binding_schema")
    if binding.get("binding_state") != "SOURCE_BOUND":
        reasons.append("binding_not_source_bound")
    if binding.get("execution_authorized") is not False:
        reasons.append("binding_must_not_pre_authorize_execution")
    if binding.get("may_execute_mutation") is not False:
        reasons.append("binding_must_be_read_only")

    for name in ("source_id", "source_byte_revision_id", "parser_revision_id"):
        if _text(binding.get(name)) is None:
            reasons.append(f"missing_binding_identity:{name}")

    resolver = binding.get("resolver")
    if not isinstance(resolver, dict):
        reasons.append("missing_resolver_attestation")
        resolver = {}
    if resolver.get("receipt_signature_verified") is not True:
        reasons.append("resolver_signature_not_verified")
    if resolver.get("immutable_cache") is not True:
        reasons.append("resolver_cache_not_immutable")
    for name in (
        "resolver_id",
        "resolver_issuer",
        "trust_domain",
        "signature_key_id",
        "cache_entry_id",
        "resolver_receipt_sha256",
        "resolved_path",
    ):
        if _text(resolver.get(name)) is None:
            reasons.append(f"missing_resolver_field:{name}")

    review = binding.get("review_guard")
    if not isinstance(review, dict) or review.get("status") != "VERIFIED_FOR_REVIEW":
        reasons.append("binding_not_review_ready")

    bound_doc = binding.get("live_document")
    if not isinstance(bound_doc, dict):
        reasons.append("missing_bound_document")
        bound_doc = {}
    bound_target = binding.get("live_object")
    if not isinstance(bound_target, dict):
        reasons.append("missing_bound_target")
        bound_target = {}

    required_doc = (
        "session_id",
        "document_id",
        "native_path",
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
        if _text(fresh_document.get(name)) != _text(bound_doc.get(name)):
            reasons.append(f"fresh_document_mismatch:{name}")

    if _path(fresh_document.get("native_path")) != _path(bound_doc.get("native_path")):
        reasons.append("fresh_document_mismatch:native_path")
    if _path(bound_doc.get("native_path")) != _path(resolver.get("resolved_path")):
        reasons.append("bound_document_resolver_path_mismatch")

    required_target = ("handle", "fingerprint", "layout", "instance_path")
    for name in required_target:
        if name not in fresh_target:
            reasons.append(f"missing_fresh_target_field:{name}")

    if _handle(fresh_target.get("handle")) != _handle(bound_target.get("handle")):
        reasons.append("fresh_target_mismatch:handle")
    if _text(fresh_target.get("fingerprint")) != _text(bound_target.get("fingerprint")):
        reasons.append("fresh_target_mismatch:fingerprint")
    if _layout(fresh_target.get("layout")) != _layout(bound_target.get("layout")):
        reasons.append("fresh_target_mismatch:layout")
    if _instance_path(fresh_target.get("instance_path")) != _instance_path(bound_target.get("instance_path")):
        reasons.append("fresh_target_mismatch:instance_path")

    reasons = sorted(set(reasons))
    return {
        "schema": "power-cad-source-binding-acceptance/1",
        "status": READY if not reasons else BLOCKED,
        "reasons": reasons,
        "source_id": binding.get("source_id"),
        "source_byte_revision_id": binding.get("source_byte_revision_id"),
        "parser_revision_id": binding.get("parser_revision_id"),
        "document_id": fresh_document.get("document_id"),
        "handle": fresh_target.get("handle"),
        "fingerprint": fresh_target.get("fingerprint"),
        "may_execute_mutation": False,
        "execution_authorized": False,
        "requires_transaction_revalidation": True,
        "note": (
            "READY means only that the previously SOURCE_BOUND identity still matches "
            "fresh read-only observations. Power CAD must re-check the bound document, "
            "generation and target fingerprint inside the single-writer transaction."
        ),
    }
