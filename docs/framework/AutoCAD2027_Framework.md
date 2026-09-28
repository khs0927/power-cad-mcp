# AutoCAD 2027 제어 프레임워크 (Power CAD / ASTRA)

> 상태: **구현 완료(v0.2.0)** — C# 서버·AutoCAD 2027 플러그인·검증 파이프라인·시뮬레이터·테스트·CI·패키지.
> **아직 하지 않은 것:** 실제 AutoCAD 2027 실도면 시험(사용자 PC에서 `docs/framework/ASTRA_CAD_Operating_Playbook.md` 7장 절차로 수행), Autodesk 공식 MCP 연동.

## 1. 채택 구조

| 역할 | 선택 | 이 저장소에서의 구현 |
| --- | --- | --- |
| 도면 읽기·수정·검증 | **bimwright 기반 C#/.NET 10** | `dotnet/` — `PowerCad.Server`(MCP), `PowerCad.Plugin.A27`(AutoCAD 내부), `PowerCad.Core`(공통) |
| 도면 의미·객체 관계 분석 | **best-cad 기능 재사용, 초기 COM 유지** | 코드를 복사하지 않고 `best-cad-mcp`를 **별도 MCP 서버로 연결**. 기존 Python COM 서버(`src/power_cad_mcp`)는 COM 폴백·DXF 오프라인용으로 유지 |
| 실행 안정성 보완 | **harness의 트랜잭션·상태 확인 구조 참고** | `PowerCad.Core/Harness/ChangeSession.cs` — 지문 사전조건 → 트랜잭션 → 사후조건 → 실패 시 롤백 → diff |
| 공식 MCP | 2027 연결 확인 후 보조 분석에 추가 | 미연동. 설치 PC에서 제공 여부 확인 후 클라이언트 설정에 추가(6장) |
| C++ (ObjectARX) | 실제 병목이 확인될 때 추가 | 미도입. 판단 기준은 7장 |

코드 최소화 원칙: **주력 C# 코드베이스 하나만 보완**하고, 분석기는 프로세스 경계(MCP) 너머로 연결한다.
검증은 매번 도면 전체를 검사하지 않고 **수정 직전 대상 상태**와 **수정 직후 대상의 변경 결과**만 자동 확인한다.

## 2. 구성도

```
MCP 클라이언트 (Claude Desktop / Claude Code)
   │ stdio (MCP)                         │ stdio (MCP, 선택)
   ▼                                     ▼
power-cad-server (C#, .NET 10)      best-cad-mcp (Python, COM)   ← 의미·관계 분석
   │ Named Pipe, NDJSON, 토큰 인증
   │ 발견 파일 %LOCALAPPDATA%\PowerCad\autocad-2027-<PID>.json
   ▼
PowerCad.Plugin.A27 (AutoCAD 2027 내부 .NET 10 DLL)
   │ 숨은 Control.BeginInvoke → AutoCAD 메인 스레드
   │ DocumentLock + Transaction (요청당 1개)
   ▼
AutoCAD 2027 도면 DB
```

- **두 프로세스(bimwright 방식):** 서버는 AutoCAD보다 먼저 떠 있을 수 있고, 플러그인이 죽어도 서버는 살아 있다. 서버는 AutoCAD 참조가 없고 JSON만 중계한다.
- **발견(discovery):** 플러그인이 시작하면 파이프 이름·32바이트 랜덤 토큰·PID를 파일로 쓴다. 서버는 살아 있는 PID만 사용하고 고아 파일은 지운다. 여러 AutoCAD가 떠 있으면 `cad_list_targets` / `cad_select_target`.
- **스레드:** 파이프 수신은 백그라운드 스레드, AutoCAD API 호출은 전부 메인 스레드. 제한시간(55초)을 넘긴 요청은 나중에 늦게 실행되지 않도록 폐기한다.

## 3. 검증된 변경 파이프라인 (harness 참고)

모든 수정 명령(`replace_text`, `move`, `modify_opening`, `create`, `batch`)은 같은 순서를 따른다.

1. **Capture(사전조건)** — 대상 엔티티를 한 번 읽어 지문(fingerprint)을 계산한다.
   - 에이전트가 분석 때 받은 `expect_fingerprint`(텍스트는 `expect_text`)와 다르면 `STALE_TARGET`으로 거부.
   - 잠긴 레이어면 `LOCKED_LAYER`로 거부(임의로 잠금 해제하지 않음).
2. **Apply** — 하나의 AutoCAD 트랜잭션 안에서 변경.
3. **Verify(사후조건)** — 변경된 대상만 다시 읽어 명령별 조건을 확인한다.
   - 텍스트: 저장된 내용 == 요청 내용 (글꼴·인코딩으로 깨지면 감지)
   - 이동: 기준점(시작점/중심/삽입점/첫 정점)이 정확히 변위만큼 이동
   - 문·개구부: 폭, 위치, 회전, 반전 부호, 속성값이 요청과 일치 (동적 블록이 값 목록으로 스냅한 경우도 감지)
   - 생성: 종류·레이어·핵심 형상이 요청과 일치
4. **Commit 또는 Rollback** — 하나라도 실패하면 예외 → 트랜잭션 중단 → 도면 무변경, `VERIFY_FAILED`.
5. **Report** — 엔티티별 `before_fingerprint`, `after_fingerprint`, 변경된 속성의 before/after.

`dry_run: true`는 1~4를 모두 수행한 뒤 **항상 롤백**하므로, 실제와 같은 조건에서 미리보기를 얻는다.
`batch`(최대 20단계)는 전 단계를 **한 트랜잭션**으로 묶어 전부 성공하거나 전부 취소된다.

## 4. 데이터 계약

### 4.1 파이프 메시지 (NDJSON, 1 MiB 제한)
```json
{"id":"7","token":"<discovery token>","command":"modify_opening","params":{"handle":"2A8","width":1000}}
{"id":"7","ok":true,"result":{ ... }}
{"id":"7","ok":false,"error":{"code":"STALE_TARGET","message":"...","hint":"..."}}
```

### 4.2 엔티티 상태
```json
{"handle":"2A8","type":"INSERT","layer":"A-DOOR","fingerprint":"0c63d47813a52826",
 "name":"DOOR_SINGLE","position":[6000,1000,0],"rotation":90,"scale":[1,1,1],
 "dynamic":{"Width":900},"attributes":{"DOOR_NO":"D1"},"width":900,"bbox":[[..],[..]]}
```
- 지문 = `{type, layer, props}`를 키 정렬·소수 6자리 반올림한 JSON의 SHA-256 앞 16자리.
- `width`(개구부 유효 폭) = 동적 매개변수(Width, Distance, 폭, 너비, 문폭 …) 값, 없으면 블록 정의 폭 × |X 축척|.

### 4.3 변경 결과
```json
{"dry_run":false,"committed":true,"checks_passed":2,
 "changes":[{"handle":"2A8","type":"INSERT","before_fingerprint":"…","after_fingerprint":"…",
             "changed":{"width":{"before":900,"after":1000},"dynamic":{…}}}],
 "created":[]}
```

### 4.4 오류 코드
`INVALID_PARAMS`, `UNKNOWN_COMMAND`, `NOT_FOUND`, `STALE_TARGET`, `LOCKED_LAYER`, `UNSUPPORTED`, `VERIFY_FAILED`,
`TOO_MANY_MATCHES`, `NO_DOCUMENT`, `CAD_BUSY`, `TIMEOUT`, `UNAUTHORIZED`, `NOT_CONNECTED`, `READ_ONLY`, `INTERNAL` — 모두 `hint`(다음 행동)를 동반한다.

## 5. MCP 도구 (power-cad-server)

| 도구 | 종류 | 설명 |
| --- | --- | --- |
| `cad_status` | 읽기 | 연결·도면 상태 |
| `cad_list_targets` / `cad_select_target` | 읽기/설정 | 실행 중인 AutoCAD 세션 선택 |
| `cad_query` | 읽기 | 종류·레이어·핸들·텍스트·정규식·블록명(와일드카드)·영역 필터, 최대 1000개 |
| `cad_get` | 읽기 | 핸들로 현재 상태·지문 |
| `cad_replace_text` | 수정 | 대상 지정(기대 텍스트/지문 고정) 또는 찾아 바꾸기(`max_changes` 상한) |
| `cad_move` | 수정 | 변위 또는 from→to, 기준점 검증 |
| `cad_modify_opening` | 수정 | 문·창·개구부 블록: 폭, 위치/벽 방향 슬라이드, 회전, 좌우·안팎 반전, 속성 |
| `cad_create` | 생성 | 선·폴리선·원·호·텍스트·MTEXT·블록 삽입 |
| `cad_batch` | 수정 | 최대 20단계 원자적 실행 |

`--simulate`: 샘플 평면(벽·실명·동적 문·일반 창·잠긴 레이어)으로 AutoCAD 없이 전 기능 시험.
`--read-only`: 모든 수정 거부.

## 6. 재사용 범위와 추가 코드

| 출처 | 재사용한 것 | 방식 |
| --- | --- | --- |
| bimwright/rvt-mcp | 2-프로세스 구조, 발견 파일+토큰, NDJSON 파이프, 요청당 트랜잭션, 배치 롤백, 1 MiB 제한, 오류 경로 마스킹, 점진 공개 개념 | 설계 패턴 재구현(코드 복사 없음). Revit ExternalEvent → AutoCAD 숨은 Control로 대체 |
| xuanquangIT/autocad-mechanical-harness | 사전 상태 확인(stale revision 거부), 원자적 커밋, 독립 readback, AutoCAD 2027/.NET 10(R26) 타깃 | 개념 재구현. 전체 도면 리비전 대신 **대상 단위 지문**으로 축소 |
| LokmenoWer/best-cad-mcp | 의미 그래프·치수 결합·제약 검증 등 분석 기능 | **그대로 별도 MCP로 연결**(실행: `uvx --from best-cad-mcp cad-mcp`) |
| Autodesk AutoCAD.NET 26.0.0 | AutoCAD 2027 관리 API 참조 어셈블리 | NuGet 참조만, 재배포하지 않음(`ExcludeAssets=runtime`) |

추가로 작성한 코드: `PowerCad.Core`(프로토콜, 지문, 검증 파이프라인, 명령 처리기, 시뮬레이터, 파이프·발견), `PowerCad.Plugin.A27`(AutoCAD 어댑터), `PowerCad.Server`(MCP 도구), 테스트 25개.

공식 MCP 연동 절차(설치 PC에서): AutoCAD 2027에 Autodesk 제공 MCP/AI 연동 기능이 있는지 확인 → 있으면 읽기 전용 보조 분석 서버로 클라이언트 설정에 추가 → 수정은 계속 `power-cad` 경로로만 수행.

## 7. 도입 순서

1. **시뮬레이터** — `power-cad-server --simulate`로 도구·절차 숙지. (완료, 테스트로 보증)
2. **플러그인 설치** — `scripts\install_autocad_plugin.ps1` → AutoCAD 재시작 → `POWERCAD_STATUS`.
3. **읽기 전용 시험** — `POWER_CAD_READ_ONLY=1`로 실제 도면 조회·지문 확인.
4. **복사본 도면 수정 시험** — 문자 변경 → 객체 이동 → 문 폭/위치, 각각 `dry_run` 후 적용, `UNDO`로 되돌려지는지 확인.
5. **분석기 연결** — `best-cad-mcp`를 두 번째 MCP 서버로 추가해 개구부·벽 관계 분석에 사용.
6. **공식 MCP 확인** — 제공 시 보조 분석으로 추가.
7. **C++ 판단** — 1만 개 이상 엔티티 조회가 5초를 넘거나 .NET으로 접근 불가한 API가 필요할 때만 ObjectARX 모듈 추가.

## 8. 알려진 한계

- 개구부 수정은 블록 참조(INSERT)만 대상. 벽 선을 자르거나 잇는 작업(벽 개구부 재생성)은 하지 않는다.
- MTEXT는 서식 코드를 포함한 원문(Contents)을 바꾼다.
- 속성 참조는 위치·회전 변경 시 블록과 함께 변환되지만, 축척 변경 시 속성 글자 크기는 AutoCAD 규칙을 따른다.
- 서명되지 않은 DLL이므로 `SECURELOAD` 경고가 뜰 수 있다. 신뢰 경로 추가는 사용자가 직접 결정한다.
