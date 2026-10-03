from __future__ import annotations

import unittest

from power_cad_mcp.source_binding import (
    CandidateLocator,
    LiveDocumentIdentity,
    LiveEntityIdentity,
    SourceTicket,
    candidate_from_locate,
    verify_source_bound,
)

SHA = "a" * 64
RECEIPT = "b" * 64


def ticket(**kwargs):
    values = {
        "ticket_id": "ticket-1",
        "source_id": "src-1",
        "source_byte_revision_id": "bytes-1",
        "parser_revision_id": "parser-1",
        "source_sha256": SHA,
        "resolved_path": r"C:\PowerCad\cache\A-201.dwg",
        "cache_entry_id": "cache-A201-r7",
        "resolver_receipt_sha256": RECEIPT,
        "resolver_issuer": "sion-source-resolver",
        "trust_domain": "khs0927/aec-source-cache",
        "signature_key_id": "resolver-key-2026-10",
        "receipt_signature_verified": True,
        "immutable_cache": True,
        "issued_at": "2026-10-03T09:00:00+00:00",
        "expires_at": "2026-10-03T11:00:00+00:00",
    }
    values.update(kwargs)
    return SourceTicket(**values)


def candidate(**kwargs):
    values = {
        "element_id": "door-1",
        "discovery_status": "matched",
        "source_id": "src-1",
        "parser_revision_id": "parser-1",
        "layout": "Model",
        "handle": "2F3",
        "instance_path": ("10A", "2F3"),
    }
    values.update(kwargs)
    return CandidateLocator(**values)


def document(**kwargs):
    values = {
        "session_id": "acad-session-1",
        "document_id": "db-1",
        "path": r"c:\powercad\cache\a-201.dwg",
        "file_sha256": SHA,
        "state_digest": "state-42",
        "modification_generation": "generation-42",
        "units": "mm",
        "dirty": False,
    }
    values.update(kwargs)
    return LiveDocumentIdentity(**values)


def entity(**kwargs):
    values = {
        "layout": "Model",
        "handle": "2f3",
        "fingerprint": "entity-fingerprint-1",
        "instance_path": ("10A", "2F3"),
    }
    values.update(kwargs)
    return LiveEntityIdentity(**values)


class SourceBindingTests(unittest.TestCase):
    NOW = "2026-10-03T10:00:00+00:00"

    def test_matched_discovery_alone_does_not_authorize_execution(self):
        c = candidate_from_locate(
            {
                "element_id": "door-1",
                "status": "matched",
                "sheet": "Model",
                "handle": "2F3",
            },
            source_id="src-1",
            parser_revision_id="parser-1",
            instance_path=("10A", "2F3"),
        )
        self.assertEqual(c.discovery_status, "matched")
        report = verify_source_bound(ticket(), c, document(), entity(), now=self.NOW)
        self.assertEqual(report["binding_state"], "SOURCE_BOUND")
        self.assertFalse(report["execution_authorized"])
        self.assertFalse(report["may_execute_mutation"])
        self.assertTrue(report["requires_transaction_revalidation"])
        self.assertTrue(report["requires_single_writer"])
        self.assertTrue(report["requires_execution_receipt"])

    def test_unsigned_or_mutable_ticket_is_rejected_before_binding(self):
        with self.assertRaises(ValueError):
            ticket(receipt_signature_verified=False)
        with self.assertRaises(ValueError):
            ticket(immutable_cache=False)

    def test_expired_ticket_never_becomes_source_bound(self):
        report = verify_source_bound(
            ticket(expires_at="2026-10-03T09:30:00+00:00"),
            candidate(),
            document(),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("source_ticket_expired_or_not_yet_valid", report["reasons"])

    def test_basename_match_is_insufficient_when_full_path_differs(self):
        report = verify_source_bound(
            ticket(),
            candidate(),
            document(path=r"D:\other\A-201.dwg"),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("live_document_path_mismatch", report["reasons"])

    def test_same_handle_in_wrong_source_revision_is_rejected(self):
        report = verify_source_bound(
            ticket(),
            candidate(parser_revision_id="parser-other"),
            document(),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("candidate_parser_revision_mismatch", report["reasons"])

    def test_wrong_live_file_hash_is_rejected(self):
        report = verify_source_bound(
            ticket(),
            candidate(),
            document(file_sha256="c" * 64),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("live_file_hash_mismatch", report["reasons"])

    def test_dirty_document_is_not_source_bound_for_execution_pipeline(self):
        report = verify_source_bound(
            ticket(),
            candidate(),
            document(dirty=True, state_digest="state-dirty", modification_generation="generation-43"),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("live_document_dirty", report["reasons"])

    def test_nested_instance_path_is_required(self):
        report = verify_source_bound(
            ticket(),
            candidate(instance_path=("10A", "2F3")),
            document(),
            entity(instance_path=("OTHER", "2F3")),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("live_instance_path_mismatch", report["reasons"])

    def test_handle_is_case_insensitive_but_layout_is_exact(self):
        ok = verify_source_bound(
            ticket(),
            candidate(handle="2f3"),
            document(),
            entity(handle="2F3"),
            now=self.NOW,
        )
        self.assertEqual(ok["binding_state"], "SOURCE_BOUND")

        bad = verify_source_bound(
            ticket(),
            candidate(layout="A101"),
            document(),
            entity(layout="Model"),
            now=self.NOW,
        )
        self.assertEqual(bad["binding_state"], "CANDIDATE")
        self.assertIn("live_layout_mismatch", bad["reasons"])

    def test_unmatched_pr13_discovery_result_cannot_be_promoted(self):
        report = verify_source_bound(
            ticket(),
            candidate(discovery_status="unverified"),
            document(),
            entity(),
            now=self.NOW,
        )
        self.assertEqual(report["binding_state"], "CANDIDATE")
        self.assertIn("discovery_not_matched", report["reasons"])


if __name__ == "__main__":
    unittest.main()
