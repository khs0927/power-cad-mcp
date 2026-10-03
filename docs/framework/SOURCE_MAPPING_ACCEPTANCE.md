# Source mapping executor-handoff acceptance

이 gate는 Ontology가 만든 `aec-executor-handoff/1`을 Power CAD가 소비할 때의
read-only acceptance를 정의한다.

Power CAD는 SOURCE_BOUND를 새로 만들지 않는다. Ontology가 source identity를 검증한 뒤
`build_executor_handoff()`로 만든 bounded handoff만 소비한다.

## 입력

1. Ontology `aec-executor-handoff/1`
2. Power CAD가 방금 다시 읽은 document identity
3. Power CAD가 방금 다시 읽은 target identity

## handoff 검증

- schema = `aec-executor-handoff/1`
- binding_state = SOURCE_BOUND
- review_status = VERIFIED_FOR_REVIEW
- execution_authorized=false
- may_execute_mutation=false
- requires_executor_authorization=true
- handoff_digest 재계산 일치
- source_sha256 / file_sha256 / resolver receipt SHA-256 형식 유효
- source_sha256 = handoff file_sha256

handoff가 변조되면 digest mismatch로 차단한다.

## fresh document 재검증

Power CAD가 현재 host에서 다시 읽은 값과 handoff를 비교한다.

- 같은 session_id
- 같은 document_id
- 같은 full native path
- 같은 file SHA-256
- 같은 state_digest
- 같은 modification_generation
- document_dirty=false
- 같은 units

같은 basename만 가진 다른 파일이나, 같은 path에 다른 bytes가 있는 경우는 통과하지 않는다.

## fresh target 재검증

- 같은 layout
- 같은 handle
- 같은 nested instance_path
- 같은 fingerprint

## 결과

성공해도 반환값은:

```text
READY_FOR_EXECUTOR_REVALIDATION
execution_authorized=false
may_execute_mutation=false
requires_transaction_revalidation=true
requires_single_writer=true
requires_execution_receipt=true
```

이다.

실제 수정 직전에는 Power CAD transaction 내부에서 document state/file identity/target
fingerprint를 다시 읽고 승인 및 single-writer 조건을 확인해야 한다.

실행 후 receipt는 최소 다음을 구분해야 한다.

- COMMITTED
- ROLLED_BACK
- REJECTED
- INDETERMINATE

INDETERMINATE는 자동 재실행하지 않는다.

## PR #13 / #15와의 관계

PR #13의 locate/matched는 discovery다. 실행 authority가 아니다.

PR #15의 source path conflict guard는 discovery false-positive를 줄이지만 canonical
source revision identity를 대신하지 않는다.

이 acceptance gate는 두 PR을 완료로 선언하는 코드가 아니라, Ontology source binding과
Power CAD executor 사이의 필수 handoff 조건을 fail-closed로 고정한다.

실행 예:

```bash
python scripts/verify_source_binding_acceptance.py \
  --handoff runtime/executor-handoff.json \
  --document runtime/fresh-document.json \
  --target runtime/fresh-target.json \
  --out runtime/power-cad-source-acceptance.json
```
