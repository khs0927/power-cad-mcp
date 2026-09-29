# Power CAD MCP / AutoCAD 2027 업그레이드 계약

대상: https://github.com/khs0927/power-cad-mcp
검토 기준 main: `4ac21db718417cbf69e35f66979395e031853623`
작성일: 2026-09-29

## 1. 기존 기반 유지

현재 저장소는 `dotnet/PowerCad.Server` → Named Pipe → `PowerCad.Plugin.A27`의
C# 주력 경로와 Python COM/DXF 보조 경로를 가진다. 조회한 main 트리에는 C++ 구현은 없다.
사용자의 목표는 C#/C++ 기반이므로 C++ ObjectARX 모듈을 필요에 따라 추가하되, 현재 C#을
이미 C++로 구현됐다고 표현하지 않는다. COM으로 주력 실행 경로를 교체하지 않는다.

기존 document lock, transaction, fingerprint, dry-run, rollback을 유지한다.
현재 세션 선택과 문서 선택은 다르다. `cad_select_target`은 AutoCAD 세션을 고르며,
현재 `AcadDocument`는 MdiActiveDocument를 사용한다. 여러 열린 도면의 자동화에서는
다른 활성 문서로 전환되는 경우를 막는 document binding이 우선이다.

## 2. 2027/2027.1 공식 확인 내용

| 항목 | 확인 내용 | 설계 영향 |
|---|---|---|
| Managed 런타임 | .NET 10 | net10.0-windows, AutoCAD 제공 assembly 참조 |
| Binary compatibility | 2025/2026과 호환되지 않아 rebuild 필요 | 2027 전용 빌드/설치 artifact 분리 |
| C++ SDK | ObjectARX 2027, C++20, acdb26 계열 | toolchain/SDK headers/libs 고정 |
| 개발환경 | 공식 SDK 페이지 VS 2026 18.0/VC 14.44.35207 | 설치 환경에서 SDK 가이드와 실제 compiler 확인 |
| API threading | ObjectARX 다중 스레드 접근 미지원 | native 호출 직렬화, 별도 파일 워커 프로세스 검증 |
| Autodesk Assistant | prompt-driven workflows, 2027.1 selection-aware 분석/객체 count | 보조 UI. 공개 대량 수집 API로 가정하지 않음 |
| Geometry Cleanup | 도면 기하 오류 정리 | 관측 단계에서 원본 자동 정리 금지, 별도 복사본 검수 |
| Forma/Connected References | 협업·누락 XREF 연결 개선 | Google Drive 인벤토리의 자동 대체로 보지 않음 |
| Checkout | 다른 사용자가 열린 도면의 일부 객체 변경 제안/검토 | 자체 승인/충돌 검증 유지; 관련 API 공개 여부 별도 확인 |

2027 제품 기능과 API에서 호출 가능한 기능을 구분한다. Assistant/Geometry Cleanup/
Checkout을 MCP에서 자동 호출할 수 있는 공개 API로 확인한 것은 아니다.
Core Console/Side Database는 2027 신기능이 아닌 기존 batch 기법이다.

## 3. P0 — 원본/문서/대상 연결

아래 이름은 **제안 계약**이며 현재 등록된 MCP 도구가 아니다.

### `cad_open_context`

입력: context ticket ID, source ID, captured revision/hash, expected document binding,
layout, requested handles, read_only=true.

서버는 원격에서 온 임의 경로를 그대로 열지 않는다. 인증된 resolver가 캐시에 확보한
원본 해시와 file ID의 매핑을 검증하고 로컬 파일을 결정한다. 확장자/파일 유형/크기 확인,
XREF 및 폰트 등 의존성 상태를 반환한다. 해시 확인 후 다른 파일로 바뀌는 경쟁을 막기 위해
immutable cache entry를 pin하고 파일 핸들의 수명을 관리한다.

### `cad_get_document_identity`

반환: session ID, document ID, canonical source binding, original file hash,
revision, native database fingerprint, units, active layout, modification generation.
디스크 해시만으로 저장하지 않은 현재 도면과 같다고 간주하지 않는다.

### `cad_resolve_context`

입력: expected document identity, source layout, native handle, nested instance path,
optional XREF identity.

반환: 현재 객체 상태와 native fingerprint, bbox, type, mapping status,
missing/ambiguous/unsupported 목록. 변환 DXF handle과 원본 DWG handle의 동일성을
가정하지 않는다. 변환 provenance 또는 원본 재추출로 대응을 확인해야 한다.
ObjectId는 세션/DB 문맥 값이며 영구 식별자로 외부 저장하지 않는다.

### `cad_focus_context`

검증된 후보만 임시 강조/줌. PaperSpace viewport 변환과 WCS를 구분한다.
별도 레이어나 DWG 객체를 생성하지 않고 일시 표시를 사용한다.

## 4. P1 — 빠른 읽기와 대형 도면 처리

- `cad_extract_snapshot`: 읽기 전용 묶음 추출. 레이어/블록/문자/기하를 한 번의 세션에서
  bounded page로 반환; raw count/processed count/unsupported count/truncated 필수.
- `cad_query_page`: stable cursor와 snapshot generation. 요청 사이 도면 변경 시 cursor 만료.
- geometry detail: metadata → bbox → full geometry로 단계별 요청.
- block definition을 한 번만 직렬화하고 instance transform만 반복 저장한다.
- 대량 JSON은 파일/압축 artifact로 내보내고 MCP에는 summary와 참조만 전달한다.
- 객체 수정 이벤트는 dirty marker/outbox로만 기록하고 매 이벤트마다 전체 도면을 재스캔하지 않는다.
- 현재 객체 DTO를 추출한 후 일반 CPU 작업을 별도 워커로 병렬화할 수 있다.
  AutoCAD ObjectId/DBObject를 워커 스레드로 넘기지 않는다.
- `cad_status`의 전체 entity count는 대형 도면에서 O(N)일 수 있다. 캐시 또는 선택 조회로 분리한다.
- 기존 수정 도구의 document binding/fingerprint는 변경 직전 transaction 안에서 확인한다.

## 5. P2 — C++ 도입과 네이티브 배치

C++는 native geometry extraction, custom object 접근, 측정상 반복 호출 병목이 클 때 도입한다.
C#과 C++가 각각 별도 MCP/수정 상태를 관리하지 않도록 같은 context contract를 사용한다.
단일 C# gateway가 native 모듈을 호출하고 결과를 공통 DTO로 반환하도록 한다.

Core Console용 extractor는 UI Editor/화면/COM 의존을 분리해야 한다. 현재 플러그인이
그대로 Core Console에서 동작한다고 가정하지 않는다. UI 호스트와 headless 호스트별 시험을 둔다.
Side database는 원본을 read-only로 읽고 save/audit/fix를 호출하지 않는다.
개체 enabler가 없는 proxy는 누락·불확실 상태로 내보낸다.

## 6. 읽기/수정 경계와 수용 시험

- 컨텍스트 검색은 수정 승인 아님. 새 guard의 VERIFIED_FOR_REVIEW도 수정 권한을 부여하지 않는다.
- 편집 전에 fresh fingerprint/문서 generation을 다시 확인하고 기존 transaction harness 사용.
- A.dwg와 B.dwg에 같은 handle, 같은 이름의 서로 다른 Drive 파일, 저장 전 수정 도면,
  rev 교체, 외부 참조 중첩, 동적 문, ByBlock 색상, mm/inch, UCS/OCS, PaperSpace를 시험.
- cold open 시간 / warm context 조회 시간 / raw extraction throughput / serialization size /
  원본 재선택 정확도를 따로 측정. C++ 전환 효과를 계측 전 보장하지 않는다.

## 7. 이번 변경의 실제 범위

Ontology 확장에 candidate guard와 공통 DTO가 구현됐다. 이 문서는 Power CAD의 단계별
업그레이드 사양이다. 새 C# 도구, C++ module, native extractor는 아직 구현/빌드/실행되지 않았다.
사용자 PC/AutoCAD 2027에서의 live test가 필요하며 현재 도구가 이미 추가됐다고 보고하지 않는다.

근거:
https://blog.autodesk.io/autocad-2027-sdk-what-every-plugin-developer-needs-to-know/
https://aps.autodesk.com/developer/overview/objectarx-autocad-sdk
https://help.autodesk.com/cloudhelp/2027/ENU/AutoCAD-WhatsNew/files/GUID-D52A51CA-BDA1-4A78-9E67-87B340C55490.htm

## 연동 프레임워크

Ontology 확장: https://github.com/khs0927/Ontology/tree/master/extensions/drawing_context

이 문서는 업그레이드 사양이며 현재 Power CAD 도구나 바이너리를 변경하지 않습니다.
