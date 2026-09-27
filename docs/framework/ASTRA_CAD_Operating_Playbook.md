# ASTRA CAD 운영 지침 — AI가 AutoCAD를 제어할 때의 절차

AI 에이전트(Claude 등)가 `power-cad` MCP 서버로 실제 도면을 다룰 때 지키는 순서다.
서버의 `instructions`에도 같은 요약이 들어 있다.

## 1. 도구 발견
1. `tools/list`로 사용 가능한 도구를 확인한다. 수정 도구가 없거나 `READ_ONLY`가 나오면 읽기 전용 모드다.
2. `cad_status` → `backend`(autocad/simulator), `document`, `entity_count` 확인.
   - `NOT_CONNECTED`: AutoCAD 2027 실행·도면 열기·`POWERCAD_STATUS` 확인을 사용자에게 요청.
   - AutoCAD가 여러 개면 `cad_list_targets` → 사용자에게 어느 도면인지 묻고 `cad_select_target`.

## 2. 대상 분석
1. **좁게 조회**한다: `cad_query`에 `types`, `layers`, `block_name`(예: `DOOR*`), `text_contains`, `within` 중 두 개 이상.
2. `truncated: true`면 필터를 더 좁힌다(상한을 올려 전체를 덤프하지 않는다).
3. 대상마다 **handle + fingerprint**를 기록한다. 이름·좌표로 다시 찾지 않는다.
4. 관계가 필요한 판단(이 문이 어느 벽·실에 속하는가 등)은 best-cad 분석기(별도 MCP)나 `within` 영역 조회로 근거를 확보한다.
5. 후보가 둘 이상이면 사용자에게 핸들·위치·텍스트를 보여 주고 선택받는다.

## 3. 수정
| 사례 | 도구 | 필수 고정값 |
| --- | --- | --- |
| 문자 변경 | `cad_replace_text` (targets 모드 권장) | `expect_text`, `expect_fingerprint` |
| 일괄 문자 치환 | `cad_replace_text` (find 모드) | `layers`로 범위 제한, `max_changes` 기본 50 유지 |
| 객체 이동 | `cad_move` | 각 target의 `expect_fingerprint` |
| 문·개구부 수정 | `cad_modify_opening` | `expect_fingerprint` |
| 여러 단계가 함께 성공해야 할 때 | `cad_batch` (≤20단계) | 각 단계의 고정값 |

규칙
- 대상이 2개 이상이거나 사용자가 결과를 확인해야 하는 수정은 먼저 `dry_run: true` → diff를 보여 주고 승인 후 적용.
- 잠긴 레이어 해제, `max_changes` 상향, 원본 도면 저장은 **반드시 사용자 확인** 후에만.
- 문 폭은 도면 단위로 준다(mm 도면에서 900 = 0.9 m). 단위가 불명확하면 `cad_status`의 `units`로 확인.

## 4. 결과 확인
- 성공 응답의 `checks_passed`와 `changes[].changed`의 before/after를 확인한다.
- 기대와 다른 속성이 바뀌었으면(예: 폭 대신 축척 변경) 사용자에게 알린다.

## 5. 오류 복구
| 코드 | 의미 | 다음 행동 |
| --- | --- | --- |
| `STALE_TARGET` | 분석 후 도면이 바뀜 | `cad_get`으로 재확인 → 여전히 맞는 대상이면 새 지문으로 재시도 |
| `VERIFY_FAILED` | 사후 검증 실패, 자동 롤백됨 | 원인(동적 블록 값 목록, 글꼴, 주석축척)을 `cad_get`으로 확인 후 요청 조정 |
| `LOCKED_LAYER` | 잠긴 레이어 | 사용자에게 해제 여부 질문. 임의 해제 금지 |
| `TOO_MANY_MATCHES` | 치환 대상 과다 | 레이어/대상 지정으로 범위 축소 |
| `NOT_FOUND` | 핸들 없음(삭제·다른 도면) | 다시 조회 |
| `CAD_BUSY` / `TIMEOUT` | 명령 실행 중·대화상자 | 사용자에게 Esc 요청 → **재조회 후** 재시도(수정을 맹목적으로 반복하지 않음) |
| `NOT_CONNECTED` / `UNAUTHORIZED` | 플러그인 재시작 등 | `cad_status`로 재연결 |
| `UNSUPPORTED` | 대상 유형 불일치 | 올바른 유형(TEXT/MTEXT, INSERT) 대상을 다시 찾기 |

실수로 적용된 변경은 AutoCAD에서 `UNDO`(Ctrl+Z)로 되돌릴 수 있다. 요청 하나는 되돌리기 한 단계에 해당한다.

## 6. 결과 보고
사용자에게 다음을 짧게 보고한다.
- 무엇을 바꿨는가(핸들, 종류, 레이어), before → after 핵심 값
- 검증 결과(`checks_passed`, dry_run 여부)
- 바꾸지 않은 것과 그 이유(잠긴 레이어, 후보 모호 등)
- 저장 여부(이 서버는 **저장하지 않는다** — 저장은 사용자가 AutoCAD에서)

## 7. 실도면 인수 시험 (사용자 PC, 최초 1회)
1. 도면 **복사본**을 연다.
2. `POWER_CAD_READ_ONLY=1`로 서버 실행 → `cad_status`, `cad_query`(문 블록, 실명 텍스트) 결과가 도면과 일치하는지 확인.
3. 읽기 전용 해제 후 아래 세 사례를 각각 `dry_run` → 적용 → 화면 확인 → `UNDO`로 원복 확인:
   - 실명 텍스트 하나 변경
   - 가구/기호 블록 하나 이동
   - 문 블록 폭 변경(동적 블록이면 Width 매개변수, 아니면 축척) + 벽 방향 슬라이드
4. 일부러 AutoCAD에서 대상을 옮긴 뒤 이전 지문으로 수정을 요청해 `STALE_TARGET`이 나오는지 확인.
5. 잠긴 레이어의 객체 수정 요청이 `LOCKED_LAYER`로 거부되는지 확인.
