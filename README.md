# Power CAD MCP

AI 어시스턴트(Claude Desktop, Claude Code 등 MCP 클라이언트)가 **AutoCAD를 직접 조작**하도록 해 주는
[Model Context Protocol](https://modelcontextprotocol.io) 서버입니다.

- **AutoCAD 백엔드 (Windows)** — 이미 실행 중인 AutoCAD 2027(및 2021 이후 버전)에 COM으로 붙어 실시간으로 그립니다.
- **Headless DXF 백엔드 (모든 OS)** — AutoCAD 없이 [ezdxf](https://ezdxf.mozman.at/)로 도면을 만들고 DXF/PNG/PDF/SVG로 저장합니다.
  CI 테스트, 미리보기, 오프라인 작도에 사용됩니다.

두 백엔드는 같은 41개 도구를 제공하므로, 같은 프롬프트가 AutoCAD에서도 DXF에서도 똑같이 동작합니다.

![demo](docs/floor_plan.png)

## 도구 목록

| 분류 | 도구 |
| --- | --- |
| 세션/파일 | `cad_status`, `new_drawing`, `open_drawing`, `save_drawing`, `get_drawing_info`, `export_drawing`, `render_preview` |
| 레이어 | `list_layers`, `create_layer`, `update_layer`, `set_current_layer`, `delete_layer` |
| 작도 | `draw_line`, `draw_polyline`, `draw_rectangle`, `draw_polygon`, `draw_circle`, `draw_arc`, `draw_ellipse`, `draw_point`, `add_text`, `add_mtext`, `add_dimension`, `add_hatch`, **`draw_batch`** |
| 블록 | `list_blocks`, `create_block`, `insert_block` |
| 조회/편집 | `list_entities`, `get_entity`, `delete_entities`, `move_entities`, `copy_entities`, `rotate_entities`, `scale_entities`, `mirror_entities`, `offset_entity`, `set_entity_properties` |
| 화면/기타 | `zoom_extents`, `zoom_window`, `run_command` |

규칙:
- 좌표는 `[x, y]` 또는 `[x, y, z]`(도면 단위), 각도는 **도(degree)**, +X 기준 반시계 방향입니다.
- 생성된 모든 객체는 **handle**(16진 문자열)과 함께 반환되며, 이후 편집은 handle로 합니다.
- 색상은 ACI 번호(0–256) 또는 이름(`red`, `yellow`, `green`, `cyan`, `blue`, `magenta`, `white`, `bylayer` …).
- 많은 객체를 그릴 때는 `draw_batch` 한 번으로 처리하면 훨씬 빠릅니다 (AutoCAD 왕복 횟수 감소).
- 상대 경로는 `POWER_CAD_WORKSPACE`(기본: 현재 폴더) 기준으로 해석되며, 확장자가 없으면 자동으로 붙습니다.

## 설치 (Windows + AutoCAD 2027)

사전 조건: Windows 10/11, AutoCAD 2027 실행 중, Python 3.10+ (없으면 스크립트가 `uv`로 설치).

```powershell
git clone https://github.com/khs0927/power-cad-mcp
cd power-cad-mcp
powershell -ExecutionPolicy Bypass -File scripts\setup_windows.ps1
```

스크립트가 하는 일: `.venv` 생성 → 패키지 설치 → AutoCAD 연결 확인(`--check`) →
`%APPDATA%\Claude\claude_desktop_config.json`에 `power-cad` 서버 등록(기존 파일은 `.bak`으로 백업).
끝나면 Claude Desktop을 재시작하세요.

### 수동 등록

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "power-cad": {
      "command": "uvx",
      "args": ["--from", "git+https://github.com/khs0927/power-cad-mcp", "power-cad-mcp"],
      "env": { "POWER_CAD_BACKEND": "autocad" }
    }
  }
}
```

Claude Code:

```bash
claude mcp add power-cad -e POWER_CAD_BACKEND=autocad -- uvx --from git+https://github.com/khs0927/power-cad-mcp power-cad-mcp
```

### 연결 확인

```powershell
.venv\Scripts\power-cad-mcp.exe --backend autocad --check   # 상태 JSON 출력, 연결 실패 시 exit 1
python scripts\smoke_test_autocad.py                         # 새 도면에 테스트 도형을 그림
```

## 설정 (환경 변수)

| 변수 | 기본값 | 설명 |
| --- | --- | --- |
| `POWER_CAD_BACKEND` | `auto` | `autocad` / `dxf` / `auto`(Windows면 AutoCAD, 아니면 DXF) |
| `POWER_CAD_PROGID` | – | COM ProgID 지정(쉼표 구분). 기본 순서: `AutoCAD.Application`, `.26`(2027), `.25`, `.24` |
| `POWER_CAD_LAUNCH` | `0` | `1`이면 AutoCAD가 꺼져 있을 때 직접 실행 |
| `POWER_CAD_WORKSPACE` | 현재 폴더 | 상대 경로 기준 폴더 |
| `POWER_CAD_DXF_PATH` | – | DXF 백엔드에서 시작 시 열/저장할 파일 |
| `POWER_CAD_ALLOW_COMMANDS` | `1` | `0`이면 `run_command` 비활성화 |
| `POWER_CAD_ALLOW_LISP` | `0` | `1`이면 `run_command`에서 AutoLISP 식 허용(위험 함수는 계속 차단) |

CLI 옵션: `power-cad-mcp [--backend auto|autocad|dxf] [--workspace DIR] [--dxf-path FILE] [--launch]
[--transport stdio|streamable-http|sse --host 127.0.0.1 --port 8765] [--check] [--version]`

## 보안

`run_command`는 AutoCAD 명령줄에 그대로 입력을 보냅니다. 도면 속 텍스트가 프롬프트에 섞여 들어올 수 있으므로
AutoCAD 밖으로 나갈 수 있는 명령(`SHELL`, `START`, `SCRIPT`, `APPLOAD`, `NETLOAD`, `VBARUN`, `QUIT` 등)은 항상 차단되고,
AutoLISP는 기본적으로 꺼져 있습니다. 필요 없다면 `POWER_CAD_ALLOW_COMMANDS=0`으로 완전히 끌 수 있습니다.

## 구조

```
src/power_cad_mcp/
  server.py            MCP 도구 정의 (MCPServer, mcp SDK 2.x)
  backends/base.py     백엔드 인터페이스 (handle 기반, 각도=도)
  backends/com_backend.py   AutoCAD COM: 전용 STA 스레드, busy 재시도(IMessageFilter), 오류 변환
  backends/dxf_backend.py   ezdxf headless 백엔드 + 렌더링
  safety.py            run_command 필터
tests/
  fake_acad.py         AutoCAD COM 객체 모델 모사 → COM 백엔드를 Linux CI에서도 검증
  test_live_autocad.py 실제 AutoCAD 대상 테스트 (옵트인)
```

COM 객체는 스레드(아파트먼트)에 묶여 있고 MCP 런타임은 동기 도구를 임의의 워커 스레드에서 실행하므로,
모든 AutoCAD 호출은 하나의 전용 STA 스레드로 모아서 실행합니다. AutoCAD가 바쁠 때(`RPC_E_CALL_REJECTED`)는
COM 메시지 필터가 자동으로 재시도하며, 도형을 만드는 호출은 중복 생성을 막기 위해 통째로 재실행하지 않습니다.

## 개발

```bash
uv venv && uv pip install -e ".[dev]"
pytest                     # 59개 테스트 + 실기 AutoCAD 테스트 1개(옵트인) (DXF end-to-end, 가짜 AutoCAD COM, stdio 프로세스, 유닛)
ruff check . && ruff format --check .
python -m build            # dist/*.whl, dist/*.tar.gz
python examples/demo_floor_plan.py            # headless 데모 → examples/out/
python examples/demo_floor_plan.py --backend autocad   # 실행 중인 AutoCAD에 그리기
```

실제 AutoCAD 테스트 (Windows):

```powershell
$env:POWER_CAD_LIVE_TESTS = "1"; pytest -m autocad -v
```

## 문제 해결

- **`Could not attach to a running AutoCAD`** — AutoCAD가 실행 중이고 도면 하나가 열려 있는지 확인하세요.
  AutoCAD와 MCP 서버는 **같은 사용자, 같은 권한 수준**이어야 합니다(한쪽만 "관리자 권한으로 실행"이면 COM 연결 실패).
- **`AutoCAD stayed busy`** — 명령 실행 중이거나 대화상자가 열려 있습니다. AutoCAD에서 `Esc`를 누르고 다시 시도하세요.
- **PDF 내보내기** — `DWG To PDF.pc3` 플로터를 사용해 도면 범위를 용지에 맞춰 출력합니다.
- **DXF로 내보내기(AutoCAD)** — AutoCAD의 SaveAs를 사용하므로, 원래 DWG가 있다면 다시 원래 파일로 저장해 활성 문서를 되돌립니다.

## License

MIT
