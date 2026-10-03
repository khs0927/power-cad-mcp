# 도면 전수 분석 절차 — 빠짐없이 분석하고 자산화하기

목표: 도면(예: `건축,구조1.dwg`)의 **모든 객체가 어느 시트·레이어·블록에 속하는지 숫자로 확인**한 뒤, 그 위에서 형태를 분석하고 ZIUM 가이드와 자산으로 옮긴다.
화면으로 훑거나 `cad_query`만 쓰면 놓치는 것(도곽 밖 객체, 블록 내부, 지시선, 레이아웃, 빈·잠긴 레이어)이 생기므로, **전수 인벤토리를 먼저 만들고 모든 칸을 하나씩 닫는다.**

## 0. 준비 (사용자 PC)
1. 원본을 건드리지 않도록 **복사본**을 연다.
2. `DXFOUT` → 같은 폴더에 `건축,구조1.dxf` (버전 2018). ODA File Converter로 바꿔도 된다.
   - ezdxf(오픈소스, MIT)는 DXF를 읽는다. ODA File Converter(무료)가 설치돼 있으면 DWG를 바로 넣어도 된다.

## 1. 전수 인벤토리 (자동)
```bash
python -m power_cad_mcp.census 건축,구조1.dxf --standard docs/standards/floor_plan_standard.json --out census/건축구조1
```
- Claude에서는 MCP 도구 `drawing_census {path, standard}`로 같은 일을 한다(DXF, ODA File Converter가 있으면 DWG도 직접).
- 결과: `census.md`(사람용 요약), `census.json`(전체 데이터: 모든 문자, 레이어, 블록, 해치).
- **첫 줄이 `COMPLETE`가 아니면 멈춘다.** 파일 속 그래픽 객체 수와 방문한 수가 같아야 한다(종료코드 1 = 누락).

인벤토리가 나누는 칸:
| 칸 | 뜻 |
| --- | --- |
| `model:S01…` | 모델 공간, 도곽(`ZIUM_sheet_architect`) 안. 시트마다 배율·A3 축척(배율×200) |
| `model:outside_sheets` | 도곽 밖 객체 — 작업 흔적, 숨은 상세, 원본 참조. **반드시 확인** |
| `model:no_extents` | 범위가 없는 객체(빈 블록, 이상 형상) |
| `layout:<이름>` | 종이 공간 레이아웃 |
| `block:<이름>` | 블록 정의 안(중첩 포함). 삽입 수 0이면 미사용 블록 |

## 1-1. Ontology와 대조 (선택, 읽기 전용)
도면이 Ontology(건물 데이터 저장소)에도 들어가 있으면 두 인벤토리가 같은 파일을 말하는지 확인한다. `POWERCAD_ONTOLOGY_URL`이 설정돼 있어야 한다.

- MCP 도구 `ontology_census_check {path, standard}` — census를 새로 돌려 대조. 1절에서 만든 결과를 쓰려면 `{path, census_json:"census/건축구조1/census.json"}`. 여러 프로젝트에 같은 파일이 있으면 `project_id`로 고른다.
- 파일을 쓰지 않고 도면도 바꾸지 않는다.

| 결과 항목 | 뜻 | 할 일 |
| --- | --- | --- |
| `ingested: false` | Ontology에 이 파일(파일명 기준, .dwg = .dxf)이 없다 | 대조할 것 없음. 필요하면 Ontology 쪽에서 수집 |
| `handles.only_in_ontology` | Ontology 요소의 핸들이 파일에 없다 | 수집 후 도면이 바뀌었거나 다른 리비전. `warnings`의 수정 시각 확인 후 재수집 |
| `handles.only_in_census_*` | Ontology가 요소로 만들지 않은 객체(치수·일반 선·해치 등) | 결함 아님. 문·창이 여기 많으면 분류 누락 의심 |
| `layers[].status` | Ontology `Layer` 행의 객체 수(레이아웃/블록 정의)와 census 비교: `equal` / `differs` / `not_in_ontology` | `differs`는 수집 후 편집 또는 파서 차이 — 핸들 결과와 같이 본다 |
| `classes[]` | 클래스별 Ontology 요소 수 옆에 같은 레이어의 census 객체 수, ZIUM 표준 레이어(DOOR·WIN/WINBAR·WAL1~3·STAIR·실명)의 객체 수 | **나란히 놓은 숫자일 뿐 일치 판정이 아니다**(문 하나 = LINE+ARC 2개일 수 있음). 정확한 대조는 `handles` |
| `not_comparable` | 맞대어 볼 수 없는 것(핸들 없는 Ontology 행, 블록 내부·ATTRIB, ZIUM에서 기둥·구조벽이 섞인 COL 등)과 이유 | 억지로 맞추지 않는다 |

- census.json이 `entity_index` 추가 전 버전이면 핸들·레이어 대조는 `not_comparable`로 나온다 → 1절을 다시 돌린다.
- 도면 레이어가 ZIUM 표준에 편입되지 않았으면(`unmapped_layers`) 클래스의 ZIUM 칸은 "no layer of this drawing maps to …"로 나온다. 편입표를 채운 뒤 다시 대조한다.

## 2. 칸 닫기 체크리스트
각 항목을 `census.md`에서 확인하고 결과를 분석 노트에 적는다. 모든 항목이 "확인함"이어야 끝난다.

- [ ] **시트**: 시트마다 타이틀란 도면명 확인(`texts`에서 해당 `model:Sxx` 칸의 큰 문자). 번호가 아니라 **도면명**으로 기록한다.
- [ ] **도곽 밖**: `outside_sheets`의 객체가 무엇인지 확인하고, 버릴지 자산화할지 정한다.
- [ ] **레이어**: `unmapped_layers`마다 용도를 보고 ZIUM 편입표에 추가한다. 잠금(L)·동결(F)·꺼짐(X) 레이어도 내용을 본다.
- [ ] **블록**: 삽입 수 순으로 모두 본다. 익명(`A$C…`, `*U…`) 블록은 이름을 붙여 자산 후보로 둔다. XREF가 있으면 원본 파일도 받는다.
- [ ] **사각지대**: `LEADER`/`MULTILEADER`(power-cad 조회에 안 잡힘), 프록시, OLE, 이미지, WIPEOUT, 표(ACAD_TABLE)를 하나씩 본다. 지시선 문구는 `texts`에 있다.
- [ ] **해치**: 패턴·축척·각도·레이어 조합을 가이드 "해치 표준"과 대조한다.
- [ ] **치수 스타일**: 스타일별 개수. 시트 축척과 달라도 결함이 아니다(가이드 규칙).
- [ ] **문자**: 전체 문자에서 재료 지시선(`T120 …`), 실명, 레벨(`S.L`, `F.L`, `EL`) 형식을 뽑아 가이드 표기 규칙과 대조한다.
- [ ] **글꼴**: `text_styles`의 글꼴이 다른 PC에 없으면 자산화할 때 같이 챙긴다.

## 3. 형태 분석 (시트별)
시트마다 가이드의 해당 장(배치도·입면·단면·평면) 순서대로 본다. 수치는 `cad_query`/`cad_get`으로 실측하고, 화면은 `cad_snapshot`으로 확인한다.
외부 분석 자료는 참고만 하고 반드시 인벤토리와 실측으로 확인한다.

## 4. 자산화
1. 가이드 "자산으로 저장할 목록"과 인벤토리의 블록 목록을 대조한다. 새로 발견한 공통 블록은 목록에 추가한다.
2. PC에서 `cad_export_block {name, description, tags}` → `문서\PowerCad\blocks`. 익명 블록은 알아볼 수 있는 이름(`ZIUM_…`)으로 저장한다.
3. 레이어 편입, 해치 설정, 표기 규칙처럼 블록이 아닌 것은 `floor_plan_standard.json`과 `ZIUM_GUIDE.md`에 반영한다.
4. 인벤토리(`census.json`)를 도면과 함께 보관해 다음 분석의 기준선으로 쓴다. 도면이 바뀌면 다시 돌려 차이를 본다.

## 5. 인벤토리 보관·다른 PC에서 불러오기
census 결과는 실제 프로젝트 문자를 담고 있어 **저장소(공개)에 올리지 않고** 구글 드라이브에 둔다. 저장소의 `census/`는 .gitignore 대상이다.

- 보관 위치: `<드라이브>:\내 드라이브\PowerCad\census\<도면>_census\` (Google Drive for desktop, `내 드라이브`). 아래 예시는 `G:`로 쓰지만 PC마다 문자가 다를 수 있다.
  - 예: `건축,구조1_census\`

    | 파일 | 만드는 방법 |
    | --- | --- |
    | `census.md`, `census.json` | 1절 명령(또는 `drawing_census`)이 자동으로 만든다 |
    | `CHECKLIST.md` | 2절 칸 닫기 체크리스트를 복사해 항목마다 확인 결과를 손으로 적는다 |
    | `test_sheet_T201_handles.json` | 시험 시트를 그린 뒤 생성된 객체 핸들을 저장한 것(작도 결과에서 손으로 저장) |

- 새 census를 만들면 같은 폴더 이름으로 드라이브에 복사한다. 덮어쓰기 전에 이전 것을 날짜를 붙여 남긴다:
  ```powershell
  $d = "G:\내 드라이브\PowerCad\census\건축,구조1_census"
  if (Test-Path $d) { Rename-Item $d ("{0}_{1}" -f (Split-Path $d -Leaf), (Get-Date -Format yyyyMMdd)) }
  Copy-Item -Recurse census\건축구조1 $d
  ```

다른 PC에서:
1. Google Drive for desktop을 설치하고 같은 계정으로 로그인한다. 탐색기에서 `내 드라이브`의 실제 드라이브 문자를 확인한다.
2. 저장소를 클론한 뒤 드라이브 폴더를 저장소 `census\`로 연결한다(복사하지 않고 링크하면 항상 최신). 저장소 루트에서:
   ```powershell
   $drive = "G:\내 드라이브\PowerCad\census"   # 1에서 확인한 문자로 바꾼다
   if (Test-Path census) { Rename-Item census census_local }   # 1절을 이미 돌린 PC면 기존 폴더를 비켜 둔다
   New-Item -ItemType Junction -Path census -Target $drive
   ```
   - 정션이 만들어지지 않으면(가상 드라이브를 대상으로 거부하는 경우) 심볼릭 링크를 쓴다. 관리자 권한 또는 개발자 모드가 필요하다:
     `cmd /c mklink /D census "G:\내 드라이브\PowerCad\census"`
   - 둘 다 안 되면 폴더를 `census\`로 복사해도 된다(읽기 전용 참고용, 최신 여부는 직접 챙긴다).
3. 확인: `census\건축,구조1_census\census.md` 머리의 `완전성: 통과`(누락 0)인지 본다. 도면(DWG)이 바뀌었으면 1절 명령으로 다시 돌려 차이를 본다(새 결과는 링크된 `census\` 밖의 폴더로 내고, 위 백업 절차로 드라이브에 올린다).
4. 블록 자산(`문서\PowerCad\blocks`)도 다른 PC에서 쓰려면 같은 방식으로 드라이브 `PowerCad\blocks`에 두고 `문서\PowerCad\blocks`를 그 폴더로 연결한다(기존 폴더가 있으면 먼저 비켜 둔다).
