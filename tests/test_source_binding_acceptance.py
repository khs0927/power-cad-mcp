from __future__ import annotations

from copy import deepcopy

from power_cad_mcp.source_binding_acceptance import (
    BLOCKED,
    READY,
    evaluate_source_binding_acceptance,
)


def binding():
    return {
        "schema": "aec-source-live-binding/1",
        "binding_state": "SOURCE_BOUND",
        "source_id": "source-1",
        "source_byte_revision_id": "bytes-1",
        "parser_revision_id": "parser-1",
        "resolver": {
            "resolver_id": "drive-cache-resolver/1",
            "resolver_issuer": "sion-source-resolver",
            "trust_domain": "khs0927/aec-source-cache",
            "signature_key_id": "resolver-key-2026-10",
            "receipt_signature_verified": True,
            "cache_entry_id": "cache-A201",
            "resolver_receipt_sha256": "e" * 64,
            "immutable_cache": True,
            "resolved_path": r"C:\PowerCad\cache\A-201.dwg",
        },
        "live_document": {
            "session_id": "session-1",
            "document_id": "open-db-1",
            "native_path": r"C:\PowerCad\cache\A-201.dwg",
            "state_digest": "state-123",
            "modification_generation": "generation-42",
            "document_dirty": False,
            "units": "mm",
        },
        "live_object": {
            "layout": "Model",
            "handle": "2F3",
            "instance_path": ["10A", "2F3"],
            "fingerprint": "fp-1",
        },
        "review_guard": {
            "status": "VERIFIED_FOR_REVIEW",
            "reasons": [],
            "may_execute_mutation": False,
        },
        "may_execute_mutation": False,
        "execution_authorized": False,
    }


def document():
    return {
        "session_id": "session-1",
        "document_id": "open-db-1",
        "native_path": r"c:/powercad/cache/A-201.DWG",
        "state_digest": "state-123",
        "modification_generation": "generation-42",
        "document_dirty": False,
        "units": "mm",
    }


def target():
    return {
        "layout": "model",
        "handle": "2f3",
        "instance_path": ["10a", "2f3"],
        "fingerprint": "fp-1",
    }


def evaluate(b=None, d=None, t=None):
    return evaluate_source_binding_acceptance(b or binding(), d or document(), t or target())


def test_valid_fresh_observation_is_only_ready_for_transaction_revalidation():
    result = evaluate()
    assert result["status"] == READY
    assert result["reasons"] == []
    assert result["execution_authorized"] is False
    assert result["may_execute_mutation"] is False
    assert result["requires_transaction_revalidation"] is True


def test_candidate_or_review_blocked_binding_is_rejected():
    b = binding()
    b["binding_state"] = "CANDIDATE"
    assert "binding_not_source_bound" in evaluate(b=b)["reasons"]

    b = binding()
    b["review_guard"]["status"] = "REQUIRES_REVIEW"
    assert "binding_not_review_ready" in evaluate(b=b)["reasons"]


def test_document_switch_and_generation_change_are_rejected():
    d = document()
    d["document_id"] = "open-db-2"
    d["modification_generation"] = "generation-43"
    result = evaluate(d=d)
    assert result["status"] == BLOCKED
    assert "fresh_document_mismatch:document_id" in result["reasons"]
    assert "fresh_document_mismatch:modification_generation" in result["reasons"]


def test_same_basename_in_different_folder_is_rejected():
    d = document()
    d["native_path"] = r"D:\other\A-201.dwg"
    result = evaluate(d=d)
    assert result["status"] == BLOCKED
    assert "fresh_document_mismatch:native_path" in result["reasons"]


def test_dirty_or_unknown_document_is_rejected():
    d = document()
    d["document_dirty"] = True
    assert "dirty_or_unknown_current_document" in evaluate(d=d)["reasons"]

    d = document()
    del d["document_dirty"]
    result = evaluate(d=d)
    assert "missing_fresh_document_field:document_dirty" in result["reasons"]
    assert "dirty_or_unknown_current_document" in result["reasons"]


def test_fingerprint_handle_layout_and_nested_instance_are_all_rechecked():
    t = target()
    t.update(
        {
            "handle": "FFF",
            "fingerprint": "changed",
            "layout": "A101",
            "instance_path": ["different"],
        }
    )
    result = evaluate(t=t)
    assert result["status"] == BLOCKED
    assert "fresh_target_mismatch:handle" in result["reasons"]
    assert "fresh_target_mismatch:fingerprint" in result["reasons"]
    assert "fresh_target_mismatch:layout" in result["reasons"]
    assert "fresh_target_mismatch:instance_path" in result["reasons"]


def test_unsigned_or_mutable_resolver_attestation_is_rejected():
    b = binding()
    b["resolver"]["receipt_signature_verified"] = False
    b["resolver"]["immutable_cache"] = False
    result = evaluate(b=b)
    assert "resolver_signature_not_verified" in result["reasons"]
    assert "resolver_cache_not_immutable" in result["reasons"]


def test_missing_fresh_generation_or_target_fields_fail_closed():
    d = document()
    del d["state_digest"]
    del d["modification_generation"]
    t = target()
    del t["instance_path"]
    result = evaluate(d=d, t=t)
    assert result["status"] == BLOCKED
    assert "missing_fresh_document_field:state_digest" in result["reasons"]
    assert "missing_fresh_document_field:modification_generation" in result["reasons"]
    assert "missing_fresh_target_field:instance_path" in result["reasons"]


def test_binding_cannot_smuggle_pre_authorization():
    b = binding()
    b["execution_authorized"] = True
    b["may_execute_mutation"] = True
    result = evaluate(b=b)
    assert result["status"] == BLOCKED
    assert "binding_must_not_pre_authorize_execution" in result["reasons"]
    assert "binding_must_be_read_only" in result["reasons"]
    assert result["execution_authorized"] is False


def test_input_objects_are_not_mutated():
    b, d, t = binding(), document(), target()
    before = deepcopy((b, d, t))
    evaluate_source_binding_acceptance(b, d, t)
    assert (b, d, t) == before
