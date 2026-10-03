# Source mapping acceptance gate

이 문서는 Ontology의 read-only SOURCE_BOUND 결과를 Power CAD가 소비할 때의
첫 acceptance gate를 정의한다.

이 gate는 **수정 권한을 발급하지 않는다**.

## 입력

1. Ontology가 생성한 `aec-source-live-binding/1`
2. Power CAD가 방금 다시 읽은 document identity
3. Power CAD가 방금 다시 읽은 target state

## 필수 재검증

- binding_state = SOURCE_BOUND
- binding 자체는 execution_authorized=false
- resolver signature attestation 검증 완료
- immutable cache entry
- review_guard = VERIFIED_FOR_REVIEW
- 같은 session_id / document_id
- 같은 full native path
- 같은 state_digest
- 같은 modification_generation
- document_dirty=false
- 같은 units
- 같은 layout
- 같은 handle
- 같은 nested instance_path
- 같은 fingerprint

basename만 같은 다른 파일은 통과하지 않는다.

## 결과

성공해도 반환값은:

```text
READY_FOR_EXECUTOR_REVALIDATION
execution_authorized=false
may_execute_mutation=false
requires_transaction_revalidation=true
```

이다.

실제 Power CAD 수정은 이후 single-writer transaction 내부에서 document binding,
modification generation, target fingerprint를 다시 확인한 뒤 수행해야 한다.

## 현재 PR #13과의 관계

PR #13의 locate/matched는 candidate discovery와 live handle plausibility를 크게 개선하지만,
canonical source revision ticket을 제공하지 않는다. 현재 cad_get_document_identity 역시
document_id/session_id 중심이며 이 acceptance gate가 요구하는 source-bound full identity를
아직 모두 제공한다고 가정하지 않는다.

따라서 이 gate의 목적은 PR #13을 완료로 선언하는 것이 아니라, **무엇이 채워져야
source mapping E2E acceptance가 완료되는지 자동화된 실패 조건으로 고정하는 것**이다.

실행 예:

```bash
python scripts/verify_source_binding_acceptance.py \
  --binding runtime/binding.json \
  --document runtime/fresh-document.json \
  --target runtime/fresh-target.json \
  --out runtime/power-cad-source-acceptance.json
```
