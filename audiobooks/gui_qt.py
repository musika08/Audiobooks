# gui_qt.py — PySide6 GUI for Audiobooks (layout-tweaked, 10s preview fix)
from __future__ import annotations

import sys
import wave
from pathlib import Path
from typing import Dict, Tuple, List, Optional

from PySide6 import QtCore, QtGui, QtWidgets

# ---- Import your core helpers (must exist in audiobooks/core.py)
from .core import (
    list_chapter_titles,
    convert_epub_to_m4b,
    convert_epub_to_tracks,
    kokoro_synthesize_to_wav,
    gpu_available as _gpu_available,
)

# ===============================
# Voice catalog (same as before)
# ===============================
KOKORO_VOICES: List[Tuple[str, str, str]] = [
    ("af_heart", "F", "kokoro"),
    ("af_bella", "F", "kokoro"),
    ("af_emily", "F", "kokoro"),
    ("af_fiona", "F", "kokoro"),
    ("af_sarah", "F", "kokoro"),
    ("af_nicole", "F", "kokoro"),
    ("af_nova", "F", "kokoro"),
    ("bf_alice", "F", "kokoro"),
    ("bf_emma", "F", "kokoro"),
    ("bf_isabella", "F", "kokoro"),
    ("bf_lily", "F", "kokoro"),
    ("am_adam", "M", "kokoro"),
    ("am_brian", "M", "kokoro"),
    ("am_chris", "M", "kokoro"),
    ("am_david", "M", "kokoro"),
    ("am_elliot", "M", "kokoro"),
    ("am_echo", "M", "kokoro"),
    ("am_eric", "M", "kokoro"),
    ("am_fenrir", "M", "kokoro"),
    ("am_liam", "M", "kokoro"),
    ("am_michael", "M", "kokoro"),
    ("am_onyx", "M", "kokoro"),
    ("am_puck", "M", "kokoro"),
    ("am_santa", "M", "kokoro"),
    ("bm_daniel", "M", "kokoro"),
    ("bm_fable", "M", "kokoro"),
    ("bm_george", "M", "kokoro"),
    ("bm_lewis", "M", "kokoro"),
]

PIPER_VOICES: List[Tuple[str, str, str]] = [
    ("en_US-amy-medium", "F", "piper"),
    ("en_US-kathleen-low", "F", "piper"),
    ("en_US-joe-medium", "M", "piper"),
    ("en_US-kyle-low", "M", "piper"),
]


def _pretty_base_name(voice_id: str) -> str:
    base = voice_id
    for pref in ("af_", "am_", "bf_", "bm_", "en_US-"):
        if base.startswith(pref):
            base = base[len(pref):]
            break
    return base.replace("_", " ").replace("-", " ").title()


def _display_name(voice_id: str, gender: str, engine: str) -> str:
    icon = "🩷" if gender.upper() == "F" else "🔵"
    return f"{icon} {_pretty_base_name(voice_id)} ({engine})"


def _sorted_voice_catalog() -> Tuple[List[str], Dict[str, Tuple[str, str, str]]]:
    rows: List[Tuple[str, str, str, str]] = []
    for (vid, g, eng) in (KOKORO_VOICES + PIPER_VOICES):
        disp = _display_name(vid, g, eng)
        rows.append((g, disp, eng, vid))
    rows.sort(key=lambda r: (0 if r[0] == "F" else 1, r[1].casefold()))
    display_items = [r[1] for r in rows]
    display_to_key = {r[1]: (r[2], r[3], r[0]) for r in rows}
    return display_items, display_to_key


# ===============================
# Main Window
# ===============================
class MainWindow(QtWidgets.QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Audiobooks")
        self.setMinimumSize(980, 640)

        # Try to load provided icon (if present)
        icon_path = Path(__file__).resolve().parent.parent / "audiobook_icon.ico"
        if icon_path.exists():
            self.setWindowIcon(QtGui.QIcon(str(icon_path)))

        # ---- Core state
        self.selected_engine: str = "kokoro"
        self.selected_voice: str = "af_heart"  # default Heart
        self.use_gpu: bool = bool(_gpu_available())
        self.output_format: str = "wav"        # default WAV
        self.export_mode: str = "single"
        self.speed: float = 1.0

        self._display_to_key: Dict[str, Tuple[str, str, str]] = {}
        self.current_file: Optional[Path] = None

        # Central widget
        central = QtWidgets.QWidget(self)
        self.setCentralWidget(central)
        self.vbox = QtWidgets.QVBoxLayout(central)
        self.vbox.setContentsMargins(10, 10, 10, 10)
        self.vbox.setSpacing(10)

        # ====== Top bar: Theme menu on menu bar ======
        self._build_menu()

        # ====== Row 1: File picker ======
        self._build_file_row()

        # ====== Row 2: Model / Speed / CUDA (same row) ======
        self._build_model_speed_cuda_row()

        # ====== Row 3: Quick Test (label + Test button aligned) ======
        self._build_quick_test_header()

        # ====== Row 4: Quick Test text box (4–5 lines with scrollbar) ======
        self._build_quick_test_box()

        # ====== Row 5: Chapters panel (half more space) ======
        self._build_chapters_panel()

        # ====== Row 6: Export options and buttons ======
        self._build_actions_row()

        # ====== Row 7: Status + Progress ======
        self._build_status_progress()

        # Set default selections
        self._populate_chapters()

    # ---------- Menu (Theme) ----------
    def _build_menu(self):
        mb = self.menuBar()
        theme_menu = mb.addMenu("Theme")
        self.themes = [
            "Fusion Dark",
            "Fusion Light",
            "Windows",
            "WindowsVista",
            "Fusion",
        ]
        for t in self.themes:
            act = QtGui.QAction(t, self)
            act.triggered.connect(lambda _, name=t: self.apply_theme(name))
            theme_menu.addAction(act)
        self.apply_theme("Fusion Dark")  # default dark

    def apply_theme(self, name: str):
        app = QtWidgets.QApplication.instance()
        if not app:
            return
        if name == "Fusion Dark":
            app.setStyle("Fusion")
            dark = QtGui.QPalette()
            dark.setColor(QtGui.QPalette.Window, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.WindowText, QtCore.Qt.white)
            dark.setColor(QtGui.QPalette.Base, QtGui.QColor(30, 30, 30))
            dark.setColor(QtGui.QPalette.AlternateBase, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.Text, QtCore.Qt.white)
            dark.setColor(QtGui.QPalette.Button, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.ButtonText, QtCore.Qt.white)
            dark.setColor(QtGui.QPalette.Highlight, QtGui.QColor(64, 128, 255))
            dark.setColor(QtGui.QPalette.HighlightedText, QtCore.Qt.black)
            app.setPalette(dark)
        elif name == "Fusion Light":
            app.setStyle("Fusion")
            app.setPalette(app.style().standardPalette())
        elif name in ("Windows", "WindowsVista", "Fusion"):
            app.setStyle(name)
            app.setPalette(app.style().standardPalette())

    # ---------- Row 1: File picker ----------
    def _build_file_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(8)

        lbl = QtWidgets.QLabel("File:")
        row.addWidget(lbl)

        self.file_edit = QtWidgets.QLineEdit()
        self.file_edit.setPlaceholderText("Choose an EPUB / TXT / DOCX / PDF...")
        row.addWidget(self.file_edit, 1)

        browse = QtWidgets.QPushButton("Browse…")
        browse.clicked.connect(self.on_browse_file)
        row.addWidget(browse)

        self.vbox.addLayout(row)

    # ---------- Row 2: Model / Speed / CUDA ----------
    def _build_model_speed_cuda_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(6)

        model_lbl = QtWidgets.QLabel("Model")
        row.addWidget(model_lbl, 0)

        items, self._display_to_key = _sorted_voice_catalog()
        self.model_combo = QtWidgets.QComboBox()
        self.model_combo.addItems(items)
        for i, text in enumerate(items):
            if "Heart" in text:
                self.model_combo.setCurrentIndex(i)
                break
        self.model_combo.currentTextChanged.connect(self.on_model_changed)
        row.addWidget(self.model_combo, 0)

        # small spacer (~3 spaces)
        row.addSpacing(24)

        speed_lbl = QtWidgets.QLabel("Speed")
        row.addWidget(speed_lbl, 0)

        self.speed_slider = QtWidgets.QSlider(QtCore.Qt.Horizontal)
        self.speed_slider.setMinimum(50)   # 0.50
        self.speed_slider.setMaximum(200)  # 2.00
        self.speed_slider.setValue(100)    # 1.00
        self.speed_slider.setFixedWidth(220)
        self.speed_slider.valueChanged.connect(self.on_speed_changed)
        row.addWidget(self.speed_slider, 0)

        self.speed_edit = QtWidgets.QLineEdit("1.00")
        self.speed_edit.setFixedWidth(48)  # ~4 chars
        self.speed_edit.setAlignment(QtCore.Qt.AlignCenter)
        self.speed_edit.editingFinished.connect(self.on_speed_edited)
        row.addWidget(self.speed_edit, 0)

        row.addStretch(1)

        self.gpu_check = QtWidgets.QCheckBox("CUDA")
        self.gpu_check.setChecked(bool(_gpu_available()))
        self.gpu_check.toggled.connect(lambda v: setattr(self, "use_gpu", bool(v)))
        row.addWidget(self.gpu_check, 0)

        self.vbox.addLayout(row)

    # ---------- Row 3: Quick Test header ----------
    def _build_quick_test_header(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(6)

        lbl = QtWidgets.QLabel("Quick Test")
        row.addWidget(lbl, 0)

        row.addStretch(1)

        self.test_btn = QtWidgets.QPushButton("Test")
        self.test_btn.clicked.connect(self.on_test_clicked)
        row.addWidget(self.test_btn, 0)

        self.vbox.addLayout(row)

    # ---------- Row 4: Quick Test text box ----------
    def _build_quick_test_box(self):
        self.test_text = QtWidgets.QPlainTextEdit()
        self.test_text.setPlaceholderText("Enter text to test synthesis…")
        self.test_text.setLineWrapMode(QtWidgets.QPlainTextEdit.WidgetWidth)
        self.test_text.setFixedHeight(110)  # ~4–5 lines visible with scrollbar
        poem = (
            "I am the fear that breaths in your bone,\n"
            "A whisper in the winds alone.\n"
            "A king without a throne, unknown,\n"
            "I haunt the hearts of those who’ve grown.\n\n"
            "I am eternal, bound by none,\n"
            "The taker of what’s never done.\n"
            "Fear my name, for I am near—\n"
            "The storm, the end, the final fear.\n\n"
            "by: Musika, the author of Audiobooks"
        )
        self.test_text.setPlainText(poem)
        self.vbox.addWidget(self.test_text)

    # ---------- Row 5: Chapters panel ----------
    def _build_chapters_panel(self):
        group = QtWidgets.QGroupBox("Chapters")
        gl = QtWidgets.QVBoxLayout(group)
        gl.setContentsMargins(8, 8, 8, 8)
        gl.setSpacing(6)

        body = QtWidgets.QHBoxLayout()
        body.setSpacing(8)

        self.chapter_list = QtWidgets.QListWidget()
        self.chapter_list.setSelectionMode(QtWidgets.QAbstractItemView.ExtendedSelection)
        self.chapter_list.setMinimumHeight(260)
        body.addWidget(self.chapter_list, 1)

        side = QtWidgets.QVBoxLayout()
        btn_all = QtWidgets.QPushButton("Select All")
        btn_all.clicked.connect(self.on_select_all)
        side.addWidget(btn_all)
        btn_inv = QtWidgets.QPushButton("Invert")
        btn_inv.clicked.connect(self.on_select_invert)
        side.addWidget(btn_inv)
        side.addStretch(1)
        body.addLayout(side, 0)

        gl.addLayout(body)
        self.vbox.addWidget(group)

    # ---------- Row 6: Export & Buttons ----------
    def _build_actions_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(10)

        out_lbl = QtWidgets.QLabel("Output")
        row.addWidget(out_lbl, 0)

        self.output_combo = QtWidgets.QComboBox()
        self.output_combo.addItems(["m4b", "wav"])
        self.output_combo.setCurrentText("wav")
        self.output_combo.currentTextChanged.connect(self.on_output_changed)
        row.addWidget(self.output_combo, 0)

        row.addSpacing(18)

        exp_lbl = QtWidgets.QLabel("Export")
        row.addWidget(exp_lbl, 0)

        self.export_combo = QtWidgets.QComboBox()
        self.export_combo.addItems(["Single", "Bulk"])
        self.export_combo.setCurrentText("Single")
        self.export_combo.currentTextChanged.connect(self.on_export_changed)
        row.addWidget(self.export_combo, 0)

        row.addStretch(1)

        self.preview_btn = QtWidgets.QPushButton("Preview (10s)")
        self.preview_btn.clicked.connect(self.on_preview_clicked)
        row.addWidget(self.preview_btn, 0)

        self.convert_btn = QtWidgets.QPushButton("Convert")
        self.convert_btn.clicked.connect(self.on_convert_clicked)
        row.addWidget(self.convert_btn, 0)

        self.vbox.addLayout(row)

    # ---------- Row 7: Status + Progress ----------
    def _build_status_progress(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(8)

        self.status_label = QtWidgets.QLabel("Ready")
        row.addWidget(self.status_label, 0)

        row.addStretch(1)

        self.progress = QtWidgets.QProgressBar()
        self.progress.setMinimum(0)
        self.progress.setMaximum(100)
        self.progress.setValue(0)
        self.progress.setTextVisible(True)
        row.addWidget(self.progress, 3)

        self.vbox.addLayout(row)

    # ---------- Events & Helpers ----------
    def on_browse_file(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(
            self, "Choose file",
            filter="Supported (*.epub *.txt *.docx *.pdf);;All files (*.*)"
        )
        if not path:
            return
        self.file_edit.setText(path)
        self.current_file = Path(path)
        self._populate_chapters()

    def _populate_chapters(self):
        self.chapter_list.clear()
        if not self.current_file or not self.current_file.exists():
            return
        try:
            titles = list_chapter_titles(self.current_file)
            if not titles:
                titles = [f"Chapter {i}" for i in range(1, 21)]
            for t in titles:
                self.chapter_list.addItem(t)
        except Exception as e:
            QtWidgets.QMessageBox.warning(self, "Chapters", f"Could not parse chapters:\n{e}")
            for i in range(1, 21):
                self.chapter_list.addItem(f"Chapter {i}")

    def on_model_changed(self, text: str):
        key = self._display_to_key.get(text)
        if key:
            eng, vid, _g = key
            self.selected_engine = eng
            self.selected_voice = vid

    def on_speed_changed(self, val: int):
        self.speed = round(val / 100.0, 2)
        self.speed_edit.setText(f"{self.speed:.2f}")

    def on_speed_edited(self):
        try:
            v = float(self.speed_edit.text().strip())
        except Exception:
            v = 1.0
        v = max(0.5, min(2.0, v))
        self.speed = v
        self.speed_slider.setValue(int(v * 100))
        self.speed_edit.setText(f"{v:.2f}")

    def on_output_changed(self, text: str):
        self.output_format = text.lower()

    def on_export_changed(self, text: str):
        self.export_mode = "single" if text.lower() == "single" else "chapters"

    def on_select_all(self):
        self.chapter_list.selectAll()

    def on_select_invert(self):
        sel = set([i.row() for i in self.chapter_list.selectedIndexes() ])
        self.chapter_list.clearSelection()
        for i in range(self.chapter_list.count()):
            if i not in sel:
                self.chapter_list.item(i).setSelected(True)

    # --- Audio helpers ---
    def _auto_play_wav(self, wav_path: Path):
        try:
            if sys.platform.startswith("win"):
                import subprocess
                subprocess.Popen(["cmd", "/c", "start", "", str(wav_path)], shell=True)
            elif sys.platform == "darwin":
                import subprocess
                subprocess.Popen(["open", str(wav_path)])
            else:
                import subprocess
                subprocess.Popen(["xdg-open", str(wav_path)])
        except Exception:
            pass

    def on_test_clicked(self):
        txt = self.test_text.toPlainText().strip()
        if not txt:
            QtWidgets.QMessageBox.warning(self, "Test", "Please enter text to synthesize.")
            return
        out = Path("test_kokoro.wav")
        try:
            secs = kokoro_synthesize_to_wav(
                txt, self.selected_voice, out,
                use_gpu=self.use_gpu, speed=self.speed
            )
            self.status_label.setText(f"Test: wrote {out.name} ({secs:.2f}s)")
            self._auto_play_wav(out)
        except Exception as e:
            QtWidgets.QMessageBox.critical(self, "Synthesis error", str(e))

    def _chapter_indices(self) -> List[int]:
        return [i.row() for i in self.chapter_list.selectedIndexes()]

    def on_preview_clicked(self):
        """
        Synthesize a short preview and save EXACTLY 10 seconds to preview_10s.wav.
        We synthesize to a temporary wav, trim to 10s, then delete the temp.
        """
        if not self.current_file:
            QtWidgets.QMessageBox.warning(self, "Preview", "Please choose a file first.")
            return

        sel = self._chapter_indices()
        if not sel:
            QtWidgets.QMessageBox.information(self, "Preview", "Select at least one chapter.")
            return

        # Use internal chapter extractor to get raw text
        from .core import _extract_chapters_text  # available in your core
        chapters = _extract_chapters_text(self.current_file)
        idx = sel[0]
        if idx < 0 or idx >= len(chapters):
            QtWidgets.QMessageBox.warning(self, "Preview", "Invalid chapter selection.")
            return

        title, text = chapters[idx]
        if not text.strip():
            QtWidgets.QMessageBox.information(self, "Preview", "Selected chapter has no text.")
            return

        # Give the TTS enough text so we can trim to 10s; but not too huge
        preview_text = text[:2000]

        tmp = Path("._preview_tmp.wav")
        out = Path("preview_10s.wav")

        try:
            # 1) Synthesize to a temporary wav
            kokoro_synthesize_to_wav(
                preview_text, self.selected_voice, tmp,
                use_gpu=self.use_gpu, speed=self.speed
            )

            # 2) Trim to 10 seconds → out
            with wave.open(str(tmp), "rb") as src:
                fr = src.getframerate()
                nch = src.getnchannels()
                sw = src.getsampwidth()
                max_frames = int(10 * fr)
                frames = src.readframes(max_frames)

            with wave.open(str(out), "wb") as dst:
                dst.setnchannels(nch)
                dst.setsampwidth(sw)
                dst.setframerate(fr)
                dst.writeframes(frames)

            # remove temp
            try:
                tmp.unlink(missing_ok=True)
            except Exception:
                pass

            self.status_label.setText(f"Preview: wrote {out.name} (10s)")
            self._auto_play_wav(out)

        except Exception as e:
            # try to remove temp on failure, too
            try:
                tmp.unlink(missing_ok=True)
            except Exception:
                pass
            QtWidgets.QMessageBox.critical(self, "Preview error", str(e))

    def on_convert_clicked(self):
        if not self.current_file:
            QtWidgets.QMessageBox.warning(self, "Convert", "Please choose a file first.")
            return

        sel = self._chapter_indices()
        self.progress.setValue(0)
        self.status_label.setText("Converting…")

        try:
            if self.export_mode == "single":
                filt = "Audiobook M4B (*.m4b)" if self.output_format == "m4b" else "WAV audio (*.wav)"
                target, _ = QtWidgets.QFileDialog.getSaveFileName(
                    self, f"Save as {self.output_format.upper()}",
                    filter=filt
                )
                if not target:
                    return
                convert_epub_to_m4b(
                    self.current_file,
                    Path(target),
                    engine=self.selected_engine,
                    voice=self.selected_voice,
                    speed=self.speed,
                    use_gpu=self.use_gpu,
                    selected_chapter_indices=sel or None,
                    output_format=self.output_format,
                )
                self.status_label.setText(f"Saved: {Path(target).name}")
                self.progress.setValue(100)
            else:
                out_dir = convert_epub_to_tracks(
                    self.current_file,
                    engine=self.selected_engine,
                    voice=self.selected_voice,
                    speed=self.speed,
                    use_gpu=self.use_gpu,
                    selected_chapter_indices=sel or None,
                    output_format=self.output_format,
                )
                self.status_label.setText(f"Saved {self.output_format.upper()} tracks to: {out_dir}")
                self.progress.setValue(100)
        except Exception as e:
            QtWidgets.QMessageBox.critical(self, "Conversion error", str(e))
            self.status_label.setText("Error")


# --------------
# Entry points
# --------------
def main_gui():
    app = QtWidgets.QApplication(sys.argv)
    # Default theme: Fusion Dark
    mw = MainWindow()
    mw.show()
    sys.exit(app.exec())


def main():
    main_gui()


if __name__ == "__main__":
    main()
