# Source-bound acceptance

기준 브랜치: Power CAD PR #13 위 stacked acceptance.

현재 `ontology_locate`의 `matched`는 discovery 결과다. 같은 파일처럼 보이고,
handle이 존재하며 class/layer/block signal이 맞다는 뜻이지, Drive/source revision과
현재 열린 DWG의 bytes가 같은 원본이라는 증명은 아니다.

따라서 이 단계는 다음 상태만 인정한다.

- CANDIDATE: discovery 결과 또는 source ticket/live identity 중 하나라도 불충분
- SOURCE_BOUND: signed source ticket과 live document/entity identity가 모두 일치

이 모듈은 EXECUTION_AUTHORIZED를 만들지 않는다.

## SOURCE_BOUND 필수 근거

Source ticket:

- source_id
- source byte revision id
- parser revision id
- source SHA-256
- exact resolved path
- immutable cache entry
- resolver receipt SHA-256
- resolver issuer / trust domain / signature key id
- receipt signature verified
- issued_at / expires_at

Live document:

- session id
- document id
- exact path
- file SHA-256
- state digest
- modification generation
- units
- dirty=false

Live object:

- layout
- handle
- nested instance path
- fingerprint

PR #13 discovery result가 `matched`여도 source ticket이 없으면 SOURCE_BOUND로
승격해서는 안 된다.

## 실행 경계

SOURCE_BOUND 이후에도 다음은 executor가 mutation transaction 직전에 다시 해야 한다.

1. bound document가 아직 active/bound document인지 확인
2. state digest / modification generation 재확인
3. entity fingerprint 재확인
4. 사용자/정책 승인 확인
5. document별 single writer 확보
6. transaction 내부에서 마지막 재검증
7. postcondition 검증
8. COMMITTED / ROLLED_BACK / REJECTED / INDETERMINATE receipt 반환

통신 단절로 결과를 모르면 INDETERMINATE이며 자동 재실행하지 않는다.

## 현재 목적

이 단계의 목적은 실제 AutoCAD mutation 기능 추가가 아니다.
PR #13/#15의 discovery/source-path 보강과 별개로, 이후 edit path가 반드시 통과해야
하는 source identity acceptance를 독립 계약으로 고정하는 것이다.
