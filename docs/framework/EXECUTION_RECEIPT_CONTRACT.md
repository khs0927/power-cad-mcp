# Execution receipt contract

기준일: 2026-10-03

이 계약은 기존 `cad_plan_execute`와 AutoCAD transaction 구현을 대체하지 않는다.
persisted plan/result를 **실행 근거(receipt)** 로 정규화하는 read-only projection이다.

## 상태

receipt는 최소 네 상태를 사용한다.

- `COMMITTED`: plan state가 Committed이고, non-dry-run committed result가 존재하며,
  result의 `executor=power-cad`, `plan_id`, `document_id`가 persisted plan과 정확히 일치하고,
  source binding이 있으면 result의 source identifiers가 handoff와 모두 일치한다.
- `ROLLED_BACK`: 실패했지만 `rollback_verified=true`라는 명시적 근거가 있다.
- `REJECTED`: `mutation_started=false`가 명시되어 실제 mutation이 시작되지 않았음이 증명된다.
- `INDETERMINATE`: outcome을 증명할 수 없는 모든 경우. Failed에 rollback/no-mutation 근거가
  없거나, persisted state가 Executing/Indeterminate이거나, Committed result가 불완전한 경우다.

단순히 plan state가 Failed라는 이유만으로 ROLLED_BACK으로 승격하지 않는다.
`rollback_verified=true`와 `mutation_started=false`처럼 서로 모순되는 failure evidence도
안전한 상태를 추측하지 않고 `INDETERMINATE`로 둔다.

## 재실행 정책

모든 receipt는 `auto_retry_allowed=false`이다.

특히 `INDETERMINATE`는:

- `requires_manual_reconciliation=true`
- `safe_to_create_replacement_plan=false`

이다. live CAD 상태를 다시 조사하고 결과를 reconcile하기 전에는 같은 plan을 자동 재실행하면 안 된다.

`ROLLED_BACK`과 `REJECTED`는 기존 plan 재실행이 아니라 fresh snapshot을 이용한 새 replacement
plan 생성만 허용 가능한 상태로 표현한다.

## Source binding

plan에 `source_binding`이 있으면 receipt는 다음을 보존한다.

- source_binding_handoff_digest
- source_id
- source_byte_revision_id
- parser_revision_id

COMMITTED result가 이 값을 다르게 echo하면 receipt는 COMMITTED를 유지하지 않고
INDETERMINATE로 downgrade된다.

## Digest

`receipt_digest`는 projector가 만든 evidence payload 전체의 SHA-256이다.
같은 persisted plan은 동일 digest를 만들고 result/source evidence가 바뀌면 digest도 달라진다.

## Canonical boundary

receipt는:

- `canonical_mutation=false`
- `evidence_only=true`

이다.

Ontology가 receipt를 수집하더라도 기존 CAIR를 자동 덮어쓰지 않는다. 별도 evidence ingestion
정책을 거쳐야 한다.

## 현재 한계

현재 PlanTools는 모든 failure에 대해 아직 `mutation_started`와 `rollback_verified`를
명시적으로 저장하지 않는다. 따라서 현 시점에서 증거가 없는 Failed plan은 의도적으로
INDETERMINATE로 projection된다.

후속 통합에서는 transaction 경계가 실제로 rollback을 확인한 경우에만
`rollback_verified=true`를 기록하도록 executor 쪽을 확장해야 한다.
