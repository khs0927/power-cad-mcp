from power_cad_mcp import source_binding_acceptance as sba  # noqa: I001


SHA = "a" * 64
RECEIPT = "b" * 64


def handoff():
    payload = {
        "schema": "aec-executor-handoff/1",
        "binding_state": "SOURCE_BOUND",
        "review_status": "VERIFIED_FOR_REVIEW",
        "document_id": "open-db-1",
        "session_id": "session-1",
        "source_id": "source-1",
        "source_byte_revision_id": "bytes-1",
        "parser_revision_id": "parser-1",
        "source_sha256": SHA,
        "file_sha256": SHA,
        "candidate_id": "door-1",
        "native_path": r"C:\PowerCad\cache\A-201.dwg",
        "state_digest": "state-123",
        "modification_generation": "generation-42",
        "units": "mm",
        "resolver_receipt_sha256": RECEIPT,
        "resolved_sha256": SHA,
        "cache_entry_id": "cache-A201",
        "resolver_issuer": "sion-source-resolver",
        "trust_domain": "khs0927/aec-source-cache",
        "signature_key_id": "resolver-key-2026-10",
        "object_locator": {
            "layout": "Model",
            "handle": "2F3",
            "instance_path": ["10A", "2F3"],
            "fingerprint": "fp-1",
        },
        "execution_authorized": False,
        "may_execute_mutation": False,
        "requires_executor_authorization": True,
    }
    payload["handoff_digest"] = sba._digest(payload)
    return payload


def document():
    return {
        "session_id": "session-1",
        "document_id": "open-db-1",
        "native_path": r"c:/powercad/cache/A-201.DWG",
        "file_sha256": SHA,
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


def evaluate(h=None, d=None, t=None):
    return sba.evaluate_source_binding_acceptance(h or handoff(), d or document(), t or target())


def test_valid_handoff_is_only_ready_for_transaction_revalidation():
    result = evaluate()
    assert result["status"] == sba.READY
    assert result["reasons"] == []
    assert result["execution_authorized"] is False
    assert result["may_execute_mutation"] is False
    assert result["requires_transaction_revalidation"] is True
    assert result["requires_single_writer"] is True
    assert result["requires_execution_receipt"] is True


def test_handoff_must_be_source_bound_review_ready_and_non_authorizing():
    h = handoff()
    h["binding_state"] = "CANDIDATE"
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    assert "handoff_not_source_bound" in evaluate(h=h)["reasons"]

    h = handoff()
    h["review_status"] = "REQUIRES_REVIEW"
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    assert "handoff_not_review_ready" in evaluate(h=h)["reasons"]

    h = handoff()
    h["execution_authorized"] = True
    h["may_execute_mutation"] = True
    h["requires_executor_authorization"] = False
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    result = evaluate(h=h)
    assert "handoff_must_not_pre_authorize_execution" in result["reasons"]
    assert "handoff_must_be_read_only" in result["reasons"]
    assert "handoff_must_require_executor_authorization" in result["reasons"]


def test_handoff_digest_tampering_is_rejected():
    h = handoff()
    h["state_digest"] = "tampered"
    result = evaluate(h=h)
    assert result["status"] == sba.BLOCKED
    assert "handoff_digest_invalid" in result["reasons"]


def test_source_and_file_hashes_must_be_valid_and_equal():
    h = handoff()
    h["source_sha256"] = "c" * 64
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    result = evaluate(h=h)
    assert result["status"] == sba.BLOCKED
    assert "handoff_source_resolved_file_hash_mismatch" in result["reasons"]

    h = handoff()
    h["file_sha256"] = "bad"
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    result = evaluate(h=h)
    assert "missing_or_invalid_handoff_file_sha256" in result["reasons"]


def test_resolved_hash_and_resolver_trust_metadata_are_required():
    h = handoff()
    h["resolved_sha256"] = "c" * 64
    h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
    result = evaluate(h=h)
    assert result["status"] == sba.BLOCKED
    assert "handoff_source_resolved_file_hash_mismatch" in result["reasons"]

    for field in ("resolver_issuer", "trust_domain", "signature_key_id"):
        h = handoff()
        del h[field]
        h["handoff_digest"] = sba._digest({k: v for k, v in h.items() if k != "handoff_digest"})
        result = evaluate(h=h)
        assert result["status"] == sba.BLOCKED
        assert f"missing_handoff_identity:{field}" in result["reasons"]


def test_fresh_file_bytes_must_match_handoff():
    d = document()
    d["file_sha256"] = "c" * 64
    result = evaluate(d=d)
    assert result["status"] == sba.BLOCKED
    assert "fresh_document_mismatch:file_sha256" in result["reasons"]


def test_document_switch_generation_and_state_change_are_rejected():
    d = document()
    d["document_id"] = "open-db-2"
    d["state_digest"] = "state-124"
    d["modification_generation"] = "generation-43"
    result = evaluate(d=d)
    assert result["status"] == sba.BLOCKED
    assert "fresh_document_mismatch:document_id" in result["reasons"]
    assert "fresh_document_mismatch:state_digest" in result["reasons"]
    assert "fresh_document_mismatch:modification_generation" in result["reasons"]


def test_same_basename_in_different_folder_is_rejected():
    d = document()
    d["native_path"] = r"D:\other\A-201.dwg"
    result = evaluate(d=d)
    assert result["status"] == sba.BLOCKED
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
    assert result["status"] == sba.BLOCKED
    assert "fresh_target_mismatch:handle" in result["reasons"]
    assert "fresh_target_mismatch:fingerprint" in result["reasons"]
    assert "fresh_target_mismatch:layout" in result["reasons"]
    assert "fresh_target_mismatch:instance_path" in result["reasons"]


def test_missing_fresh_fields_fail_closed():
    d = document()
    del d["file_sha256"]
    del d["state_digest"]
    del d["modification_generation"]
    t = target()
    del t["instance_path"]
    result = evaluate(d=d, t=t)
    assert result["status"] == sba.BLOCKED
    assert "missing_fresh_document_field:file_sha256" in result["reasons"]
    assert "missing_fresh_document_field:state_digest" in result["reasons"]
    assert "missing_fresh_document_field:modification_generation" in result["reasons"]
    assert "missing_fresh_target_field:instance_path" in result["reasons"]


def test_input_objects_are_not_mutated():
    h, d, t = handoff(), document(), target()
    sba.evaluate_source_binding_acceptance(h, d, t)
    assert h == handoff()
    assert d == document()
    assert t == target()
