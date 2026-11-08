# 바탕화면 달력 프로그램(M-DeskCal) 설계 및 구현 계획

대상 분석 프로그램: `CalendarTask` (desktopcal.exe v3.32.310.9371, Beijing Xiaowei Cloud Inc.)
작성일: 2026-11-06

---

## 0. 분석 방법과 한계 (먼저 읽을 것)

| 항목 | 내용 |
|---|---|
| 요청 | morluto/rea 를 이용해 desktopcal.exe 를 분석 |
| REA 저장소 | 스크래치 폴더에 clone 완료, README·DISCLAIMER 확인 |
| REA 실행 | 처음에는 `npx rea-agents` 가 권한 분류기에 차단되었으나, 사용자 승인 후 **실행 완료**(desktop 검사·providers·PE imports). 단, 이 PC에 Ghidra/Hopper/IDA 가 없어 **함수 단위 디컴파일은 미수행** (1.4-1 참고) |
| 대체 수단 | REA가 수행하는 "Desktop installation / PE imports / SQLite 스냅샷 / 패키지 리소스" 분석과 동등한 범위를 **로컬 정적 분석**(pefile, zipfile, sqlite3 읽기 전용, 문자열 추출)으로 수행 |
| 하지 않은 것 | 프로그램 실행·디버깅, 디컴파일(Ghidra/Hopper/IDA 미사용), 네트워크 캡처, `calendar.db` 의 **행 내용 열람**(스키마·행 수·설정 키 이름만 확인) |
| 신뢰도 | 아래 "확인됨"은 파일/리소스에서 직접 관찰한 사실, "추정"은 이름·구조로부터의 해석이며 구현 전에 검증 필요 |

REA 의 DISCLAIMER 는 "독점 소프트웨어의 보호된 코드·자산 복제"를 지원하지 않는다고 명시합니다. 따라서 이 계획은
**기능·동작 수준의 클린룸 재구현**을 전제로 하며, 원본의 코드, PNG/MP3 리소스, Lua/XML UI 정의, 언어 파일 문구, 서버 API, 브랜드/로고는
**복제하지 않습니다**(자체 디자인·자체 문구·자체 음원 사용).

---

## 1. 대상 프로그램 분석 결과

### 1.1 구성 (확인됨)

| 파일 | 역할(근거) |
|---|---|
| `desktopcal.exe` (x86, 686KB) | 메인 런처/호스트. 임포트: gdiplus, dkcore, dkui, WININET, SETUPAPI. `RegisterHotKey`, `SHAppBarMessage`, `SetWindowPos`, 뮤텍스 `__MUTEX_DESKTOPCAL_EXE__`(단일 인스턴스), 시작프로그램 레지스트리(`...\CurrentVersion\Run`, `StartupApproved\Run`) |
| `dkcore.dll` | 공통 유틸(문자열/파일/XML/타이머/COM/Base64), libcurl 기반 HTTP, 서버 호스트(account/update/data 계열) |
| `dkui.dll` | **자체 UI 엔진**(GDI+, 레이아웃/렌더/폰트/언어). XML 레이아웃 + Lua 이벤트로 화면 구성 |
| `dkctrl.dll` | 컨트롤 라이브러리. `UpdateLayeredWindow`, `DwmSetWindowAttribute`, 훅 → **투명/반투명(레이어드) 창, 블러 효과** |
| `dkbase.dll` | 기본 로직 + **SQLite 래퍼(CppSQLite3)**, 타임존 레지스트리 접근, `SetParent`(창 부모 변경) |
| `dknotify.dll` | libcurl multi 기반 알림/푸시 수신 |
| `dkdockexplorer*.dll`, `dkdockhost*.exe` | 탐색기(Explorer) 쪽에 "도킹"하는 x86/x64/arm64 헬퍼. 내보내기 `DockExplorer/UndockExplorer/UnsubClassExplorer`, `SetWindowsHookEx`. 데스크톱 레이어에 창을 붙이고 클릭을 받기 위한 장치로 **추정** |
| `app\dkappcal` | **달력 본체**(이벤트 편집, 카운트다운, 검색, 이력, 시간표, 배경화면, 설정). Lua 15개 + XML 12개 + 음원 4개(알림음 1, 완료음 3) |
| `app\dkappnote` | 메모(노트) 모듈. 노트 목록/이력 |
| `app\dkappthirdcal` | 외부 캘린더 동기화: **Google, Outlook, iCloud, CalDAV** (bcrypt/WinSock 사용) |
| `app\dkappaimcp` + `desktopcal-mcp.exe` | **AI MCP 연동**. 이름 있는 파이프 `\\.\pipe\DesktopCalAiMcp-v1`, 토큰 환경변수 `DESKTOPCAL_AIMCP_TOKEN`, 감사 로그, 보류(pending) 저장소. 로그인 계정 필요, 클라우드 설정으로 on/off |
| `Db\calendar.db` | SQLite 단일 DB (아래 1.3) |
| `language\*.xml` (25개 언어) | `IDS_*` 키-값 문자열 테이블(863개 키). 중국 음력/절기/명절 문자열 포함 |
| `config.ini`, `app\app.ini` | 설치 경로/언어/자동실행, 모듈(COM CLSID → DLL) 등록 |
| `dkupdate.exe`, `dkreport.exe`, `uninst.exe` | 자동 업데이트, 오류 리포트, 제거 |

구조 요약: **C++/ATL(COM) 모듈 + 자체 UI 엔진(GDI+) + SQLite + libcurl**. 모듈(`app.ini`의 CLSID)을 플러그인처럼 로드합니다.

### 1.2 사용자 기능 (UI 문자열·리소스 구조에서 확인됨)

**A. 바탕화면 달력 표시**
- 바탕화면(배경) 위에 투명 달력 위젯. 제목줄 표시/숨김, 위치·크기 조정 모드("Adjust position and size" → "Complete").
- 표시 모드 5종: 떠다니는 월(Floating), 고정 월, 전체 월(Full), 고정 한 줄, 떠다니는 한 줄.
- 최대 행 수, 셀 간격·스타일·배경색, 그림자, 유리(블러) 효과, 주차 번호, 월 간격 표시.
- 글꼴/글자색/투명도, 공휴일 글자색.
- 주 시작 요일, 연 첫 주 기준, 휴일(쉬는 요일) 지정/표시.
- 음력, 24절기, 명절, 공휴일(지역 선택) 표시.
- 월/날짜 이동, 오늘 이동, 이전/다음 페이지.
- 플로팅 버튼(바탕화면 위 작은 버튼) 표시/숨김, 전역 단축키로 달력 표시/숨김.
- 트레이 아이콘 메뉴: 설정, 플로팅 버튼 토글, 업데이트 확인, 시작하기, 정보, 종료.
- Windows 시작 시 자동 실행.

**B. 일정/할 일**
- 날짜 셀에 직접 텍스트 입력(셀 편집), 서식(글자색·배경색, 최근 색), 자동 번호(없음/점/숫자).
- 완료/미완료 표시, 미완료 항목 30일 자동 연장 옵션, 완료 효과음.
- 이벤트 편집기: 반복 규칙 — 매일/매주/매월/매년/평일/월말/분기말/음력 매년/법정 근무일/에빙하우스 망각곡선/사용자 지정(N일·N주·N개월(일자 기준·요일 기준·마지막 주)·N년), 반복 횟수·무한 반복.
- 알림: 당일/1·2·3·5일 전/1주 전/사용자 지정 시간, 알림음.
- 내용 검색: 기간(전체/1개월/3개월/1년/사용자), 완료 여부, 유형, 글자색 필터.
- 변경 이력 보기·복원, 이력 삭제.
- 콘텐츠 비밀번호(내용 잠금).

**C. 부가 기능**
- 카운트다운(디데이): 기념일/생일(나이)/명절/시험/사용자, 바탕화면 카드 표시 또는 달력 하단 표시, 카드 투명도.
- 시간표(수업 시간표): 학기·주차·하루 교시 수·오전/오후 시작·수업/쉬는 시간 자동 생성, 주말 숨김, 공유 링크.
- 메모(노트) 모듈: 목록/이력/관리.
- 배경 화면 스킨: 사진 순환(5분~24시간), 바탕화면 캡처(실시간), 단색; 채우기/맞춤/늘이기/타일/가운데.
- 가져오기/내보내기: 자체 DB 파일, 텍스트, Excel(목록/달력 시트), 타사 "Wallpaper Calendar 3" 가져오기. 내보내기 기간(최근 6개월/전체/사용자).
- 인쇄, 프록시 설정(HTTP), 다국어(25개), 자동 업데이트.

**D. 계정·클라우드(범위 밖 권장)**
- 로그인/동기화/VIP 업그레이드/결제, 구독(Subs) 공유 일정, 서버 push 알림, 광고성 추천 영역.

**E. 외부 캘린더 동기화**: Google / Outlook / iCloud / CalDAV (이벤트 테이블 별도, `dirty/deleted/etag` 로 양방향 동기화 구조).

**F. AI MCP**: 로컬 파이프 + 토큰으로 외부 AI 에이전트가 일정 조회/수정, 보류 승인 흐름과 감사 로그.

### 1.3 데이터 모델 (calendar.db 스키마 — 확인됨 / 의미는 추정)

| 테이블 | 용도(추정) | 핵심 컬럼 |
|---|---|---|
| `item_table` | **날짜 셀의 내용**(본 사용자 DB에서 138행으로 가장 많음) | `it_unique_id`(날짜 키로 추정), `it_content`, `it_bgcolor`, `it_history`, `it_appinfo`, `it_cdate/mdate`, `it_stime/mtime`(타임스탬프), `group_id` |
| `event_table` | 반복/알림이 있는 이벤트 | `ev_unique_id`, `ev_content`, `ev_type`(1,2 관찰), `ev_start_date/end_date`, `ev_status`, `ev_recurrence`, `ev_reminder`, `ev_subs`, `ev_info` |
| `subs_table` | 구독 이벤트(서버 공유) | event_table + 구독 정보 |
| `note_table`, `note_history_table` | 메모와 이력 | `nt_title`, `nt_content`, `nt_status`, `nt_history` |
| `project_table` | 프로젝트(사실상 단일 기본값) | `pj_name` |
| `setting_table` | 키-값 설정 | `st_name`, `st_nval`, `st_sval`; 키는 `board_*` 접두사(예: `board_disp_month`, `board_show_festival`, `board_show_24solarterms`, `board_item_gap`, `board_item_line_spacing`, `board_max_columns`, `board_enable_tone`…) 77개 |
| `picture_table`, `capture_table` | 배경 사진/캡처 파일 목록(경로 MD5 + 내용 MD5) | |
| `google/outlook/icloud/caldav_cal_event_table` | 외부 캘린더 로컬 캐시 | `*_etag`, `*_dirty`, `*_deleted`, `*_rrule`, `*_start/end_raw/day/tz`, `*_all_day`, `*_reminder_minutes`, `*_sync_time` |

공통: 다계정·다프로젝트를 위한 `u_id`, `pj_id`, `u_mid`, `group_id` 컬럼. 동기화를 위한 `*_stime/mtime`.

### 1.4 외부 통신 (확인됨 — 우리 구현에서는 사용하지 않음)
`api-account*.xdiarys.com`, `api-update*.xdiarys.com`, `data*.xdiarys.com` (계정/업데이트/데이터). 이 서버 API는 호환 대상이 아니며 **사용하지 않습니다**.

### 1.4-1 REA 실제 실행 결과 (2026-11-06 추가)
- 실행한 명령: `inspect-desktop-application`(디렉터리 151개 항목, 48개 파일 프로브 — 완료), `providers`, `inspect-pe-imports`(dkbase.dll). 모두 정상 종료. 앞선 수작업 PE 분석과 일치.
- **네이티브 디스어셈블 provider 없음**: 이 PC에는 Ghidra/Hopper/IDA/Kuna/objdump/Rizin 이 없어 함수 단위 디컴파일은 불가(JDK 21 은 있음). Ghidra 설치(수백 MB 다운로드)는 별도 승인이 필요하여 진행하지 않음.
- 추가 정적 증거(문자열·Lua 로직 분석):
  - `dkdockexplorer(.x64).dll` 에 `CDockExplorerWindow::OnTimer SubclassWindow SHELLDLL_DefView`, `ProgMan`, `SysListView32`, `Shell_TrayWnd`, 창 클래스 `CalendarTask_ShellDLL`, `Common::IsWallpaperEngineRunning`, 배경화면 스타일(`TileWallpaper`/`WallpaperStyle`) 레지스트리 값이 있음 → **원본은 Explorer에 DLL을 주입(`SetWindowsHookEx`)해 바탕화면 `SHELLDLL_DefView` 를 서브클래싱**하고, 타이머로 재확인하며, Wallpaper Engine 실행 여부를 감지함. 달력 창 자체의 부모 지정은 `dkui.dll`(`ProgMan`/`WorkerW`/`SHELLDLL_DefView` 탐색)이 담당.
  - 이는 **외부 프로세스 DLL 주입 방식이라 백신 오탐·호환성 위험이 큼** → 본 프로젝트는 주입 없이 WorkerW 부모 지정 + 편집 모드 방식을 채택(2.4 유지).
  - 전역 단축키는 3종: 달력 표시/숨김, 콘텐츠(메모) 표시/숨김, 편집 완료. 디스플레이 변경(`OnDisplayChange`) 시 재배치.
  - 이벤트 유형: `EEventType_Event / Reminder / Countdown`, 반복 종류: `Null, Day, Week, Month, Year, WorkDay, MonthLastDay, Season, LegalWorkDay, Ebbinghaus, CustomDay/Week/Month/Year`, 주기 기준: `EFreq_Solar / EFreq_Lunar`(양력/음력). 반복 이벤트 삭제/수정은 **이번 건만 / 이번 이후 전체 / 전체** 3종(`DeleteRecurrenceCurrent/Future/DeleteRecurrence`). 카운트다운 카드는 바탕화면 도킹 창(Dock Countdown Window)으로 별도 표시.
  - 배경화면 서버 목록(`/api/v5/wallpaper/list`)은 온라인 의존 기능 → 제외.

### 1.5 미확인(추정) 항목 — 구현 전 검증 필요
1. ~~데스크톱에 창을 붙이는 정확한 방식~~ → **1.4-1 로 대부분 해소**(Explorer 주입 + SHELLDLL_DefView 서브클래싱). 정확한 호출 순서는 디스어셈블(Ghidra) 또는 Spy++/WinSpy 관찰이 필요.
2. `ev_recurrence/ev_reminder/ev_info`, `it_content` 의 **직렬화 형식**(XML/JSON/자체 포맷). → 샘플 행 구조 확인 필요(개인 데이터이므로 사용자 동의 하에 테스트 DB 생성 후 확인 권장). 이벤트 유형 값은 DB 관찰값 1, 2 (Reminder/Countdown 등으로 추정, 열거형 숫자값 미확정).
3. "에빙하우스 곡선" 간격 값, 30일 자동 연장 동작의 정확한 규칙.
4. 단축키 기본값, 플로팅 버튼 동작, 위치·크기 조정 UX의 세부.

---

## 2. 설계

### 2.1 목표 / 범위

- **목표**: Windows 11/10 에서 바탕화면(아이콘 뒤)에 투명 달력을 띄워 날짜별 메모·할 일·반복 일정·알림을 관리하는 개인용 프로그램.
- **MVP 포함**: A(표시) + B(일정/할 일/반복/알림/검색/이력) + 카운트다운 + 가져오기/내보내기(JSON/CSV/ICS) + 트레이/단축키/자동 실행 + 한국어/영어.
- **2차**: 메모 모듈, 시간표, 배경 스킨(사진 순환/캡처), Excel 내보내기, 인쇄, 콘텐츠 잠금.
- **3차**: Google/Outlook/iCloud/CalDAV 동기화(표준 프로토콜), 로컬 MCP 서버.
- **제외**: 자체 계정/VIP/결제/광고/구독 공유 서버, 원본 서버 연동, 원본 데이터 포맷의 바이트 호환(가져오기 도구는 선택 사항).
- **한국 사용자 특화**: 음력(한국 음양력), 24절기, 한국 공휴일·대체공휴일, 한국어 기본.

### 2.2 기술 스택 비교와 권장

| 안 | 장점 | 단점 |
|---|---|---|
| **A. C# .NET 8 + WPF** (권장) | 투명 창/블러/DPI 대응 쉬움, `KoreanLunisolarCalendar` 내장, SQLite·타이머·토스트 알림 라이브러리 풍부, 빠른 개발 | 런타임 배포 크기(자체 포함 빌드 시 ~70MB, 트리밍 시 감소), WorkerW 부모 지정은 P/Invoke 필요 |
| B. C++ Win32 + Direct2D | 원본과 유사, 작고 빠름 | 개발 비용 큼, UI 레이아웃 직접 구현 |
| C. Tauri/Electron | 웹 UI 디자인 용이 | 바탕화면 임베딩·클릭 투과 처리 번거로움, 메모리 사용 |

→ **A안 권장**. UI는 MVVM(CommunityToolkit.Mvvm), 저장소는 `Microsoft.Data.Sqlite` (+ 필요 시 EF 없이 Dapper 정도), 알림은 `Microsoft.Toolkit.Uwp.Notifications`/Windows App SDK 토스트.

### 2.3 아키텍처

```
M-DeskCal.sln
├─ src/
│  ├─ DeskCal.App            (WPF 진입점, 트레이, 단일 인스턴스, 자동 실행, 전역 단축키)
│  ├─ DeskCal.Desktop        (바탕화면 호스팅: WorkerW 부모 지정, 클릭 처리, DPI/멀티모니터)
│  ├─ DeskCal.UI             (달력 보드, 셀 편집기, 이벤트 편집기, 설정, 검색, 카운트다운 카드)
│  ├─ DeskCal.Core           (도메인: Item/Event/Recurrence/Reminder/Countdown/Note/Timetable)
│  ├─ DeskCal.Calendars      (음력·절기·공휴일 제공자: Korean, 확장 가능)
│  ├─ DeskCal.Data           (SQLite 저장소, 마이그레이션, 이력, 가져오기/내보내기)
│  ├─ DeskCal.Notify         (알림 스케줄러, 토스트, 사운드)
│  ├─ DeskCal.Sync           (3차: 캘린더 프로바이더 CalDAV/Google/Graph)
│  └─ DeskCal.Mcp            (3차: 로컬 MCP 서버)
└─ tests/ (Core, Data, Calendars 단위 테스트, UI 스모크)
```

원본의 "모듈(COM 플러그인)" 구조는 복제하지 않고, 프로젝트 단위 모듈 + 인터페이스(`ICalendarProvider`, `IReminderSink`)로 단순화합니다.

### 2.4 바탕화면 호스팅 (핵심 기술)

1. **WorkerW 부모 지정 방식**: `Progman` 에 메시지 `0x052C` 전송 → `SHELLDLL_DefView` 를 가진 `WorkerW` 아래(또는 Win11 24H2 이후 구조에 맞게 `Progman` 자식)에 WPF 창 `SetParent`. 아이콘 뒤·배경 앞에 표시됨.
   - Windows 11 24H2/25H2(현재 10.0.26200)에서는 WorkerW 계층이 달라졌으므로 **버전별 탐색 로직과 폴백**(`HWND_BOTTOM` 최하위 창 + `WS_EX_TOOLWINDOW`) 구현.
2. **상호작용 문제**: 바탕화면에 붙인 창은 마우스 입력이 Explorer 로 가므로 → 달력 영역만 `WS_EX_TRANSPARENT` 해제 + 저수준 마우스 훅 또는 `Progman` 클릭 전달로 해결. 원본은 별도 Explorer 도킹 DLL을 사용(추정)하므로, 초기에는 **"편집 모드"에서 일반 최상위 창처럼 동작**(위치·크기 조정/셀 입력)하고 평상시는 바탕화면 레이어로 되돌리는 방식으로 위험을 줄임.
3. **외형**: `AllowsTransparency` + 사용자 배경색/투명도, 유리 효과는 `DwmSetWindowAttribute`(Acrylic/Mica backdrop) 또는 바탕화면 캡처 블러, 그림자, 둥근 모서리. 글꼴/색은 설정으로.
4. **표시 모드 5종**: 레이아웃 엔진이 (기준월, 행 수, 한 줄 여부, 고정/부유)를 받아 셀 그리드를 계산. "부유"는 오늘을 중심으로 스크롤.
5. **멀티모니터/DPI**: Per-Monitor V2, 모니터 변경 시 위치 복원.
6. **전체화면 앱·가상 데스크톱·작업 표시줄**: 가시성 이벤트 처리, `SHAppBarMessage` 로 작업 영역 계산.

### 2.5 데이터 설계 (자체 스키마, 원본과 별개)

```sql
-- 날짜 셀 메모/할 일 (item)
items(id TEXT PK, day TEXT /*yyyy-MM-dd*/, content TEXT /*리치텍스트 JSON*/, bg_color TEXT, sort INT,
      done INT, created_at INT, updated_at INT, deleted INT)
-- 반복/알림 이벤트
events(id TEXT PK, title TEXT, body TEXT, start_utc TEXT, end_utc TEXT, all_day INT, tz TEXT,
       rrule TEXT /*RFC5545 RRULE + 확장*/, ext_rule TEXT /*평일/월말/음력/에빙하우스*/,
       status INT, color TEXT, created_at INT, updated_at INT, deleted INT)
reminders(id, event_id/item_id, offset_minutes INT, custom_time TEXT, sound TEXT, fired_at INT)
countdowns(id, title, date, type, show_card INT, show_bottom INT, lunar INT)
notes(id, title, content, color, status, created_at, updated_at)
history(id, entity, entity_id, content, at INT)   -- 변경 이력/복원
settings(key TEXT PK, value TEXT)                  -- board.* 키 이름은 자체 정의
pictures(id, path, hash, kind)                     -- 배경 사진
timetables(id, name, start_date, weeks, config JSON) + timetable_slots(...)
sync_accounts(...), sync_events(... etag, dirty, deleted, remote_id ...)
```

- 날짜는 `yyyy-MM-dd` 문자열 + UTC 타임스탬프 분리(원본의 `*_day`, `*_raw` 패턴과 유사). 마이그레이션은 `PRAGMA user_version`.
- 변경 시 이력 자동 기록, 소프트 삭제, 주기적 백업(DB 파일 복사).
- 비밀번호 잠금: 콘텐츠를 사용자 비밀번호 유래 키(Argon2/PBKDF2)로 AES-GCM 암호화.

### 2.6 도메인 로직

- **반복 규칙**: RFC 5545 RRULE 을 기반(라이브러리: `Ical.Net`)으로 하고, 비표준(평일, 월말, 분기말, 법정 근무일, 음력 매년, 에빙하우스 간격 [예: +1,2,4,7,15,30일 — 값은 자체 정의])은 `IRecurrenceExpander` 플러그인으로 구현. 셀 범위 질의: `Expand(range)`.
- **음력/절기/공휴일**: `KoreanLunisolarCalendar`(1900–2050) 로 음력 변환·윤달, 24절기는 천문 계산(또는 테이블), 한국 공휴일은 연도별 JSON 테이블(설/추석 연휴, 대체공휴일, 임시공휴일 포함)을 업데이트 가능한 로컬 파일로 관리. 일본/중국 등은 제공자 확장.
- **알림 스케줄러**: 다음 발화 시각 계산 → 단일 `Timer` 재설정, 절전 복귀/시간 변경(`SystemEvents`) 후 재계산, 누락 알림 보정(앱 비실행 중 지난 알림 처리). 토스트 + 알림음(자체 음원) + 완료 효과음.
- **자동 연장**: "미완료 → 30일간 매일 다음 날로 이월" 옵션은 가상 표시(렌더링 시 계산) 또는 일일 배치로 구현 — 정확한 원본 규칙 확인 후 확정.
- **검색**: SQLite FTS5(내용 인덱스) + 기간/완료/유형/글자색 필터.

### 2.7 설정·기타

- 설정 키는 자체 네임스페이스(`board.*` 형태 참고, 복제하지 않고 필요한 항목만 정의), 기본값 복원 버튼.
- 다국어: `.resx` 또는 JSON(ko/en 우선). 원본 `language/*.xml` 문구는 사용하지 않고 직접 작성.
- 업데이트: MVP는 수동(GitHub Releases 링크), 이후 MSIX/Velopack 자동 업데이트.
- 설치: MSIX 또는 Inno Setup, 자동 실행은 `HKCU\...\Run` 또는 StartupTask.
- 로그/진단: 로컬 파일 로그(Serilog), 원격 전송 없음.
- 개인정보: 모든 데이터는 `%AppData%\DeskCal` 로컬 저장. 외부 통신은 사용자가 켠 동기화에 한함.

### 2.8 3차 확장 설계 요약

- **외부 캘린더**: `ICalendarProvider` (Pull/Push, etag 기반 충돌 해결, `dirty/deleted` 플래그). CalDAV(iCloud 포함), Google Calendar API(OAuth PKCE), Microsoft Graph(OAuth). 토큰은 Windows DPAPI/Credential Locker 저장.
- **로컬 MCP 서버**: stdio 또는 named pipe, 액세스 토큰, **쓰기 작업은 보류→사용자 승인 후 반영**, 감사 로그. (원본의 보류 저장소/감사 로그 개념을 기능 수준에서 참고)

---

## 3. 구현 계획

| 단계 | 기간(1인 기준) | 산출물 | 완료 기준 |
|---|---|---|---|
| **M0. 착수·검증** | 3~5일 | 솔루션 골격, 기술 검증 프로토타입 2개: (1) WorkerW/Progman 부모 지정으로 Win11 26200 에서 투명 창 표시, (2) 편집 모드 전환 | 바탕화면 아이콘 뒤에 반투명 사각형이 표시되고, 편집 모드에서 클릭 입력 가능 |
| **M1. 코어·데이터** | 1주 | `Core`/`Data`(스키마, 마이그레이션, 이력), 설정 저장소, 단위 테스트 | CRUD·이력 복원·마이그레이션 테스트 통과 |
| **M2. 달력 보드 렌더링** | 1.5주 | 5가지 표시 모드, 주 시작/휴일/주차, 글꼴·색·투명도·그림자·유리, 오늘 이동/월 이동, DPI·멀티모니터 | 모드별 스냅샷 테스트, 2개 모니터에서 위치 복원 |
| **M3. 셀 편집·할 일** | 1주 | 셀 인라인 편집, 서식·색, 자동 번호, 완료 토글, 효과음, 컨텍스트 메뉴 | 입력→저장→재시작 후 복원 |
| **M4. 음력·절기·공휴일** | 1주 | 한국 음력/절기/공휴일 제공자, 표시 옵션, 공휴일 데이터 JSON | 2020–2030 공휴일·음력 변환 테스트(공식 달력 대조) |
| **M5. 이벤트·반복·알림** | 2주 | 이벤트 편집기, RRULE+확장 반복, 알림 스케줄러, 토스트/사운드, 절전 복귀 보정 | 반복 규칙 테스트 스위트(윤달/월말/DST), 알림 시각 오차 < 1초 |
| **M6. 트레이·단축키·자동 실행** | 3~4일 | 트레이 메뉴, 플로팅 버튼, 전역 단축키, 단일 인스턴스, 시작 프로그램 | 재부팅 후 자동 실행, 단축키로 표시/숨김 |
| **M7. 검색·이력·카운트다운** | 1주 | FTS 검색+필터, 이력 보기/복원, 디데이 카드(바탕화면/하단) | 필터 조합 테스트, 카드 표시 |
| **M8. 가져오기/내보내기** | 1주 | JSON(백업), CSV, ICS 가져오기/내보내기, 기간 선택, (선택) 원본 DB 가져오기 도구 | 왕복(roundtrip) 테스트 |
| **베타 릴리스** | | 설치 파일, 사용자 가이드(ko) | 7일간 상시 실행 안정성(메모리/CPU) 확인 |
| **M9. 2차 기능** | 3~4주 | 메모, 시간표, 배경 스킨(순환/캡처), Excel 내보내기(ClosedXML), 인쇄, 콘텐츠 잠금 | 기능별 수용 테스트 |
| **M10. 3차 기능** | 4~6주 | CalDAV/Google/Graph 동기화, 로컬 MCP 서버 | 양방향 동기화 충돌 시나리오 테스트 |

총 MVP(M0~M8): 약 9~10주, 전체: 약 16~20주 (1인 개발 기준 추정).

### 3.1 테스트 전략
- 단위: 반복 규칙, 음력/공휴일, 알림 스케줄, 마이그레이션, 가져오기/내보내기.
- UI: 레이아웃 스냅샷(모드·DPI별), 편집 흐름 스모크.
- 시스템: Win10/Win11, 가상 데스크톱, 해상도·배율 변경, 절전/복귀, 전체화면 앱, Explorer 재시작(`TaskbarCreated` 수신 후 재부착).
- 성능 목표: 유휴 CPU < 1%, 메모리 < 150MB, 시작 < 2초.

### 3.2 주요 리스크와 대응

| 리스크 | 대응 |
|---|---|
| Win11 24H2+ 에서 WorkerW 구조 변경, Explorer 재시작 시 창 소실 | M0 에서 우선 검증, 폴백(최하위 최상위 창)과 `TaskbarCreated` 재부착 |
| 바탕화면 레이어에서 마우스 입력 불가 | 편집 모드 분리, 필요 시 저수준 훅 또는 투명 입력 오버레이 |
| 한국 공휴일 데이터 갱신 | 로컬 JSON + 업데이트 가능한 소스, 임시공휴일 수동 추가 UI |
| 저작권·상표 | 클린룸 구현, 자체 UI/음원/아이콘/문구, 원본 명칭·로고 미사용 |
| 원본 데이터 호환 | 직렬화 형식 미확인 → M8 에서 선택 기능으로, 사용자 동의 하 샘플 분석 후 결정 |

### 3.3 추가 분석 제안
1. (완료) `rea inspect-desktop-application` / `inspect-pe-imports` 실행 및 수작업 결과와 교차 검증.
2. (미실행, 승인 필요) Ghidra 설치(JDK 21 은 이미 있음) 후 `rea analyze`/`search`/`xrefs` 로 `dkdockexplorer.dll` 의 서브클래싱·타이머 경로와 `dkui.dll` 의 부모 지정 경로 확인.
3. (미실행) `rea inspect-sqlite-database --table item_table --row-limit N` 은 개인 데이터가 포함되므로, **테스트용 DB를 새로 만들어** 직렬화 형식 분석.
4. M0 보강: 설계 2.4 의 "주입 없는 방식"이 Explorer 서브클래싱 방식 대비 기능 손실(클릭 처리, Wallpaper Engine 공존)이 있는지 프로토타입으로 비교.
