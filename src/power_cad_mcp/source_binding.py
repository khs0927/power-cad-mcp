"""Strict read-only source binding for Ontology candidates.

This module sits *after* ontology.locate()/MatchElementAsync discovery. A
"matched" discovery result is not execution authority. SOURCE_BOUND requires
an independently verified source ticket plus exact live document/object
identity. Execution authorization remains a later executor concern.
"""

from __future__ import annotations

import ntpath
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any


_HEX = set("0123456789abcdef")


def _nonempty(value: str, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{name} must be non-empty")
    return value.strip()


def _sha256(value: str, name: str) -> str:
    if not isinstance(value, str) or len(value) != 64 or any(ch not in _HEX for ch in value):
        raise ValueError(f"{name} must be a lowercase SHA-256 digest")
    return value


def _time(value: str, name: str) -> datetime:
    try:
        dt = datetime.fromisoformat(_nonempty(value, name).replace("Z", "+00:00"))
    except ValueError as exc:
        raise ValueError(f"{name} must be ISO-8601") from exc
    if dt.tzinfo is None:
        raise ValueError(f"{name} must include timezone")
    return dt.astimezone(timezone.utc)


def normalize_path(value: str) -> str:
    return ntpath.normcase(ntpath.normpath(_nonempty(value, "path")))


@dataclass(frozen=True)
class SourceTicket:
    ticket_id: str
    source_id: str
    source_byte_revision_id: str
    parser_revision_id: str
    source_sha256: str
    resolved_path: str
    cache_entry_id: str
    resolver_receipt_sha256: str
    resolver_issuer: str
    trust_domain: str
    signature_key_id: str
    receipt_signature_verified: bool
    immutable_cache: bool
    issued_at: str
    expires_at: str

    def __post_init__(self):
        for name in (
            "ticket_id",
            "source_id",
            "source_byte_revision_id",
            "parser_revision_id",
            "resolved_path",
            "cache_entry_id",
            "resolver_issuer",
            "trust_domain",
            "signature_key_id",
        ):
            _nonempty(getattr(self, name), f"SourceTicket.{name}")
        _sha256(self.source_sha256, "SourceTicket.source_sha256")
        _sha256(self.resolver_receipt_sha256, "SourceTicket.resolver_receipt_sha256")
        normalize_path(self.resolved_path)
        issued = _time(self.issued_at, "SourceTicket.issued_at")
        expires = _time(self.expires_at, "SourceTicket.expires_at")
        if expires <= issued:
            raise ValueError("SourceTicket.expires_at must be later than issued_at")
        if self.receipt_signature_verified is not True:
            raise ValueError("SourceTicket requires verified resolver receipt signature")
        if self.immutable_cache is not True:
            raise ValueError("SourceTicket requires immutable cache binding")

    def current(self, now: str) -> bool:
        when = _time(now, "now")
        return _time(self.issued_at, "SourceTicket.issued_at") <= when <= _time(
            self.expires_at, "SourceTicket.expires_at"
        )


@dataclass(frozen=True)
class CandidateLocator:
    element_id: str
    discovery_status: str
    source_id: str
    parser_revision_id: str
    layout: str
    handle: str
    instance_path: tuple[str, ...] = ()

    def __post_init__(self):
        for name in ("element_id", "discovery_status", "source_id", "parser_revision_id", "layout", "handle"):
            _nonempty(getattr(self, name), f"CandidateLocator.{name}")


@dataclass(frozen=True)
class LiveDocumentIdentity:
    session_id: str
    document_id: str
    path: str
    file_sha256: str
    state_digest: str
    modification_generation: str
    units: str
    dirty: bool

    def __post_init__(self):
        for name in (
            "session_id",
            "document_id",
            "path",
            "state_digest",
            "modification_generation",
            "units",
        ):
            _nonempty(getattr(self, name), f"LiveDocumentIdentity.{name}")
        _sha256(self.file_sha256, "LiveDocumentIdentity.file_sha256")
        normalize_path(self.path)


@dataclass(frozen=True)
class LiveEntityIdentity:
    layout: str
    handle: str
    fingerprint: str
    instance_path: tuple[str, ...] = ()

    def __post_init__(self):
        for name in ("layout", "handle", "fingerprint"):
            _nonempty(getattr(self, name), f"LiveEntityIdentity.{name}")


def verify_source_bound(
    ticket: SourceTicket,
    candidate: CandidateLocator,
    document: LiveDocumentIdentity,
    entity: LiveEntityIdentity,
    *,
    now: str,
) -> dict[str, Any]:
    """Return CANDIDATE or SOURCE_BOUND, never execution authority."""
    reasons: list[str] = []

    if not ticket.current(now):
        reasons.append("source_ticket_expired_or_not_yet_valid")
    if candidate.discovery_status != "matched":
        reasons.append("discovery_not_matched")
    if candidate.source_id != ticket.source_id:
        reasons.append("candidate_source_id_mismatch")
    if candidate.parser_revision_id != ticket.parser_revision_id:
        reasons.append("candidate_parser_revision_mismatch")
    if document.file_sha256 != ticket.source_sha256:
        reasons.append("live_file_hash_mismatch")
    if normalize_path(document.path) != normalize_path(ticket.resolved_path):
        reasons.append("live_document_path_mismatch")
    if document.dirty:
        reasons.append("live_document_dirty")
    if candidate.layout != entity.layout:
        reasons.append("live_layout_mismatch")
    if candidate.handle.upper() != entity.handle.upper():
        reasons.append("live_handle_mismatch")
    if tuple(candidate.instance_path) != tuple(entity.instance_path):
        reasons.append("live_instance_path_mismatch")

    source_bound = not reasons
    return {
        "schema": "power-cad-source-bound/1",
        "binding_state": "SOURCE_BOUND" if source_bound else "CANDIDATE",
        "element_id": candidate.element_id,
        "ticket_id": ticket.ticket_id,
        "source_id": ticket.source_id,
        "source_byte_revision_id": ticket.source_byte_revision_id,
        "parser_revision_id": ticket.parser_revision_id,
        "live_document": {
            "session_id": document.session_id,
            "document_id": document.document_id,
            "path": document.path,
            "state_digest": document.state_digest,
            "modification_generation": document.modification_generation,
            "units": document.units,
            "dirty": document.dirty,
        },
        "live_entity": {
            "layout": entity.layout,
            "handle": entity.handle,
            "instance_path": list(entity.instance_path),
            "fingerprint": entity.fingerprint,
        },
        "resolver": {
            "issuer": ticket.resolver_issuer,
            "trust_domain": ticket.trust_domain,
            "signature_key_id": ticket.signature_key_id,
            "receipt_sha256": ticket.resolver_receipt_sha256,
            "signature_verified": ticket.receipt_signature_verified,
            "cache_entry_id": ticket.cache_entry_id,
            "immutable_cache": ticket.immutable_cache,
        },
        "reasons": sorted(set(reasons)),
        "may_execute_mutation": False,
        "execution_authorized": False,
        "requires_transaction_revalidation": True,
        "requires_single_writer": True,
        "requires_execution_receipt": True,
        "note": (
            "SOURCE_BOUND proves read-only source/live identity only. Before mutation, "
            "the executor must re-read document state and entity fingerprint inside its "
            "single-writer transaction and return an execution receipt."
        ),
    }


def candidate_from_locate(
    locate_result: dict[str, Any],
    *,
    source_id: str,
    parser_revision_id: str,
    instance_path: tuple[str, ...] = (),
) -> CandidateLocator:
    """Adapt a PR #13 locate result without upgrading its trust level."""
    if not isinstance(locate_result, dict):
        raise ValueError("locate_result must be an object")
    return CandidateLocator(
        element_id=_nonempty(str(locate_result.get("element_id") or ""), "locate element_id"),
        discovery_status=_nonempty(str(locate_result.get("status") or ""), "locate status"),
        source_id=_nonempty(source_id, "source_id"),
        parser_revision_id=_nonempty(parser_revision_id, "parser_revision_id"),
        layout=_nonempty(str(locate_result.get("sheet") or "Model"), "locate layout"),
        handle=_nonempty(str(locate_result.get("handle") or ""), "locate handle"),
        instance_path=tuple(instance_path),
    )
