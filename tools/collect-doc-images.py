"""사용자 매뉴얼(README)용 스크린샷을 docs/images 로 모은다.

1) 스크린샷 생성:
   DeskCal.App.exe --snapshot=<폴더> --db=<임시 db 파일> --seed --docshots
   (화면에 아무것도 띄우지 않고 오프스크린으로 PNG 를 그린다. 임시 db 에만 쓰므로 내 데이터는 건드리지 않는다.)
2) 이 스크립트로 필요한 파일만 골라 이름을 붙이고, 설정 창은 아래쪽 빈 공간을 잘라 낸다:
   python tools/collect-doc-images.py <스냅샷 폴더>     (Pillow 필요: pip install pillow)
"""
import shutil
import sys
from pathlib import Path

from PIL import Image

src = Path(sys.argv[1])
out = Path(__file__).resolve().parent.parent / "docs" / "images"
out.mkdir(parents=True, exist_ok=True)

COPY = {
    "board-main.png": "m7-board-with-countdowns.png",
    "mode-fixed.png": "1-FixedMonth.png",
    "mode-floating.png": "0-FloatingMonth.png",
    "mode-full.png": "2-FullMonth.png",
    "mode-line.png": "3-SingleLine.png",
    "mode-line-floating.png": "4-SingleLineFloating.png",
    "gear-menu.png": "doc-gear-menu.png",
    "tray-menu.png": "doc-tray-menu.png",
    "edit-mode.png": "doc-edit-mode.png",
    "custom-look.png": "doc-custom-look.png",
    "editor.png": "opt-editor.png",
    "board-marks.png": "opt-board-marks.png",
    "opt-colors.png": "opt-colors.png",
    "opt-repeat.png": "opt-repeat.png",
    "opt-reminder.png": "opt-reminder.png",
    "opt-countdown.png": "opt-countdown.png",
    "event-editor.png": "m5-editor-edit.png",
    "reminder-popup.png": "m5-reminder-popup.png",
    "search.png": "m7-search.png",
    "countdown-cards.png": "m7-cards.png",
    "notes.png": "m9-notes.png",
    "timetable-widget.png": "m9-timetable-widget.png",
    "timetable-editor.png": "m9-timetable-editor.png",
    "locked.png": "m9-locked.png",
    "print.png": "m9-print.png",
    "sync.png": "doc-sync.png",
    "mcp-approvals.png": "doc-mcp-approvals.png",
    "extract.png": "doc-extract.png",
    "quick-input.png": "doc-quick-input.png",
    "briefing.png": "doc-briefing.png",
    "review-plan.png": "doc-review-plan.png",
    "rollover.png": "doc-rollover.png",
}
for dst, name in COPY.items():
    f = src / name
    if not f.exists():
        print("없음:", name)
        continue
    shutil.copyfile(f, out / dst)

SETTINGS = ["calendar", "cell", "font", "memo", "advanced", "ai", "about"]
BG = (0xEE, 0xF1, 0xF6)
for i, key in enumerate(SETTINGS):
    f = src / f"doc-settings-{i}.png"
    if not f.exists():
        print("없음:", f.name)
        continue
    im = Image.open(f).convert("RGB")
    w, h = im.size
    # 오른쪽(페이지) 영역에서 배경색이 아닌 마지막 줄을 찾는다
    last = 0
    px = im.load()
    for y in range(h - 1, -1, -1):
        if any(px[x, y] != BG for x in range(210, w - 20, 7)):
            last = y
            break
    bottom = min(h, last + 24)
    link = im.crop((0, h - 40, 200, h))          # 왼쪽 아래 "기본 설정으로 사용" 링크
    page = im.crop((0, 0, w, bottom))
    page.paste(link, (0, bottom - 40))
    page.save(out / f"settings-{key}.png", optimize=True)

print("완료:", out)
