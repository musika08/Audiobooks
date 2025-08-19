# gui_qt.py — PySide6 GUI for Audiobooks
# Adds:
# - Settings menu with Options… (Intro/Outro + Mastering) and Metadata… (Cover/Tags)
# - Bitrate moved between Output and Export
# - Keeps embedded chapters (Single+M4B), sentence pipeline, etc.

from __future__ import annotations

import sys
import re
import wave
from pathlib import Path
from typing import Dict, Tuple, List, Optional

from PySide6 import QtCore, QtGui, QtWidgets

from .core import (
    __version__,
    convert_epub_to_m4b,
    convert_epub_to_tracks,
    kokoro_synthesize_to_wav,
    gpu_available as _gpu_available,
    fix_paragraph_structure,
    _extract_chapters_text,   # EPUB -> List[(title, body)]
    load_txt_as_chapter,
    load_docx_as_chapter,
    load_pdf_as_chapter,
)

# ===============================
# Voice catalog
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

def _pretty_base_name(voice_id: str) -> str:
    base = voice_id
    for pref in ("af_", "am_", "bf_", "bm_"):
        if base.startswith(pref):
            base = base[len(pref):]
            break
    return base.replace("_", " ").title()

def _display_name(voice_id: str, gender: str, engine: str) -> str:
    icon = "🩷" if gender.upper() == "F" else "🔵"
    return f"{_pretty_base_name(voice_id)} ({engine}) {icon}"

def _sorted_voice_catalog() -> Tuple[List[str], Dict[str, Tuple[str, str, str]]]:
    rows: List[Tuple[str, str, str, str]] = []
    for (vid, g, eng) in KOKORO_VOICES:
        disp = _display_name(vid, g, eng)
        rows.append((g, disp, eng, vid))
    rows.sort(key=lambda r: (0 if r[0] == "F" else 1, r[1].casefold()))
    display_items = [r[1] for r in rows]
    display_to_key = {r[1]: (r[2], r[3], r[0]) for r in rows}
    return display_items, display_to_key

# ------------------- Dialogs -------------------

class MetadataDialog(QtWidgets.QDialog):
    def __init__(self, parent, title: str, artist: str, album: str, cover_path: Optional[str]):
        super().__init__(parent)
        self.setWindowTitle("Metadata")
        self.setModal(True)
        v = QtWidgets.QVBoxLayout(self)
        form = QtWidgets.QFormLayout()
        self.title_edit = QtWidgets.QLineEdit(title)
        self.artist_edit = QtWidgets.QLineEdit(artist)
        self.album_edit = QtWidgets.QLineEdit(album)
        h = QtWidgets.QHBoxLayout()
        self.cover_edit = QtWidgets.QLineEdit(cover_path or "")
        self.cover_btn = QtWidgets.QPushButton("Browse…")
        self.cover_btn.clicked.connect(self._browse_cover)
        h.addWidget(self.cover_edit, 1); h.addWidget(self.cover_btn, 0)
        form.addRow("Book Title", self.title_edit)
        form.addRow("Author (Artist)", self.artist_edit)
        form.addRow("Album", self.album_edit)
        form.addRow("Cover image", h)
        v.addLayout(form)
        btns = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Ok | QtWidgets.QDialogButtonBox.Cancel)
        btns.accepted.connect(self.accept)
        btns.rejected.connect(self.reject)
        v.addWidget(btns)

    def _browse_cover(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(self, "Select cover", filter="Images (*.jpg *.jpeg *.png)")
        if path:
            self.cover_edit.setText(path)

    def values(self):
        return (
            self.title_edit.text().strip(),
            self.artist_edit.text().strip(),
            self.album_edit.text().strip(),
            self.cover_edit.text().strip() or None,
        )

class OptionsDialog(QtWidgets.QDialog):
    def __init__(self, parent, enable_intro: bool, intro_template: str, enable_outro: bool, outro_template: str, enable_mastering: bool):
        super().__init__(parent)
        self.setWindowTitle("Options")
        self.setModal(True)
        v = QtWidgets.QVBoxLayout(self)
        form = QtWidgets.QFormLayout()

        self.chk_intro = QtWidgets.QCheckBox("Enable per‑chapter intro")
        self.chk_intro.setChecked(enable_intro)
        self.intro_edit = QtWidgets.QLineEdit(intro_template or "Previously on {title}…")
        form.addRow(self.chk_intro, self.intro_edit)

        self.chk_outro = QtWidgets.QCheckBox("Enable per‑chapter outro")
        self.chk_outro.setChecked(enable_outro)
        self.outro_edit = QtWidgets.QLineEdit(outro_template or "End of Chapter {n}.")
        form.addRow(self.chk_outro, self.outro_edit)

        self.chk_master = QtWidgets.QCheckBox("Audio mastering (EBU R128 loudness normalize)")
        self.chk_master.setChecked(enable_mastering)
        form.addRow(self.chk_master)

        v.addLayout(form)
        btns = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Ok | QtWidgets.QDialogButtonBox.Cancel)
        btns.accepted.connect(self.accept)
        btns.rejected.connect(self.reject)
        v.addWidget(btns)

    def values(self):
        return (
            bool(self.chk_intro.isChecked()),
            self.intro_edit.text(),
            bool(self.chk_outro.isChecked()),
            self.outro_edit.text(),
            bool(self.chk_master.isChecked()),
        )

# ------------------- Find/Replace Bar -------------------
class FindReplaceBar(QtWidgets.QWidget):
    find_next = QtCore.Signal(str, bool)
    replace_one = QtCore.Signal(str, str, bool)
    replace_all = QtCore.Signal(str, str, bool)

    def __init__(self, parent: Optional[QtWidgets.QWidget] = None):
        super().__init__(parent)
        self.setVisible(False)
        self.setSizePolicy(QtWidgets.QSizePolicy.Policy.Minimum, QtWidgets.QSizePolicy.Fixed)
        self.setMaximumHeight(30)

        layout = QtWidgets.QHBoxLayout(self)
        layout.setContentsMargins(4, 2, 4, 2)
        layout.setSpacing(6)

        layout.addWidget(QtWidgets.QLabel("Find:"), 0)
        self.find_edit = QtWidgets.QLineEdit()
        self.find_edit.setPlaceholderText("Text")
        self.find_edit.setMaximumWidth(220)
        self.find_edit.returnPressed.connect(self.on_find_next)
        layout.addWidget(self.find_edit, 0)

        layout.addWidget(QtWidgets.QLabel("Replace:"), 0)
        self.replace_edit = QtWidgets.QLineEdit()
        self.replace_edit.setPlaceholderText("With")
        self.replace_edit.setMaximumWidth(220)
        layout.addWidget(self.replace_edit, 0)

        self.chk_all = QtWidgets.QCheckBox("All chapters")
        self.chk_all.setChecked(True)
        layout.addWidget(self.chk_all, 0)

        self.btn_find = QtWidgets.QPushButton("Find Next")
        self.btn_find.setFixedHeight(24)
        self.btn_find.clicked.connect(self.on_find_next)
        layout.addWidget(self.btn_find, 0)

        self.btn_replace = QtWidgets.QPushButton("Replace")
        self.btn_replace.setFixedHeight(24)
        self.btn_replace.clicked.connect(self.on_replace_one)
        layout.addWidget(self.btn_replace, 0)

        self.btn_replace_all = QtWidgets.QPushButton("Replace All")
        self.btn_replace_all.setFixedHeight(24)
        self.btn_replace_all.clicked.connect(self.on_replace_all)
        layout.addWidget(self.btn_replace_all, 0)

        self.btn_close = QtWidgets.QToolButton()
        self.btn_close.setText("✕")
        self.btn_close.setFixedSize(24, 24)
        self.btn_close.clicked.connect(self.hide)
        layout.addWidget(self.btn_close, 0)

    def on_find_next(self):
        self.find_next.emit(self.find_edit.text(), self.chk_all.isChecked())

    def on_replace_one(self):
        self.replace_one.emit(self.find_edit.text(), self.replace_edit.text(), self.chk_all.isChecked())

    def on_replace_all(self):
        self.replace_all.emit(self.find_edit.text(), self.replace_edit.text(), self.chk_all.isChecked())

# ------------------- Main Window -------------------
class MainWindow(QtWidgets.QMainWindow):
    MAX_RECENTS = 10

    def __init__(self):
        super().__init__()
        self.setWindowTitle(f"Audiobooks v{__version__}")
        self.setMinimumSize(1200, 760)

        # Core state
        self.selected_engine: str = "kokoro"
        self.selected_voice: str = "af_heart"
        self.use_gpu: bool = bool(_gpu_available())
        self.output_format: str = "wav"
        self.export_mode: str = "single"
        self.speed: float = 1.0

        self.include_titles: bool = True
        self.title_pause_seconds: float = 0.5
        self.sentence_pause_seconds: float = 0.2
        self.enable_mastering: bool = False
        self.bitrate_kbps: int = 64

        # Intro/outro (now in Options dialog)
        self.enable_intro: bool = False
        self.enable_outro: bool = False
        self.intro_template: str = "Previously on {title}…"
        self.outro_template: str = "End of Chapter {n}."

        # Metadata (now in Metadata dialog)
        self.cover_image: Optional[Path] = None
        self.meta_title: str = ""
        self.meta_artist: str = ""
        self.meta_album: str = ""

        # State
        self._display_to_key: Dict[str, Tuple[str, str, str]] = {}
        self.current_file: Optional[Path] = None
        self._chapters: List[Tuple[str, str]] = []
        self._dirty: bool = False
        self._save_path: Optional[Path] = None
        self._suppress_item_changed = False

        # Central UI
        central = QtWidgets.QWidget(self)
        self.setCentralWidget(central)
        self.vbox = QtWidgets.QVBoxLayout(central)
        self.vbox.setContentsMargins(10, 10, 10, 10)
        self.vbox.setSpacing(8)

        self._build_menubar()
        self._build_model_speed_cuda_row()
        self._build_splitter_panel()
        self._build_actions_row()
        self._build_status_row()

        self._update_title_dirty()
        self.apply_theme("Fusion Dark")
        self._populate_chapters()

    # ---------- Menu bar ----------
    def _build_menubar(self):
        mb = self.menuBar()

        file_menu = mb.addMenu("&File")
        file_menu.addAction(QtGui.QAction("Open…", self, shortcut="Ctrl+O", triggered=self.on_open_file_dialog))
        self.open_recent_menu = file_menu.addMenu("Open &Recent")
        self._rebuild_recent_menu()
        file_menu.addSeparator()
        file_menu.addAction(QtGui.QAction("Save", self, shortcut="Ctrl+S", triggered=self.on_save))
        file_menu.addAction(QtGui.QAction("Save As…", self, shortcut="Ctrl+Shift+S", triggered=self.on_save_as))
        file_menu.addSeparator()
        file_menu.addAction(QtGui.QAction("Exit", self, shortcut="Alt+F4", triggered=self.close))

        edit_menu = mb.addMenu("&Edit")
        edit_menu.addAction(QtGui.QAction("Undo", self, shortcut="Ctrl+Z", triggered=lambda: self.editor.undo()))
        edit_menu.addAction(QtGui.QAction("Redo", self, shortcut="Ctrl+Y", triggered=lambda: self.editor.redo()))
        edit_menu.addSeparator()
        edit_menu.addAction(QtGui.QAction("Cut", self, shortcut="Ctrl+X", triggered=lambda: self.editor.cut()))
        edit_menu.addAction(QtGui.QAction("Copy", self, shortcut="Ctrl+C", triggered=lambda: self.editor.copy()))
        edit_menu.addAction(QtGui.QAction("Paste", self, shortcut="Ctrl+V", triggered=lambda: self.editor.paste()))
        edit_menu.addSeparator()
        edit_menu.addAction(QtGui.QAction("Find…", self, shortcut="Ctrl+F", triggered=self.show_find_bar))
        edit_menu.addAction(QtGui.QAction("Replace…", self, shortcut="Ctrl+H", triggered=self.show_find_bar_replace_mode))
        edit_menu.addAction(QtGui.QAction("Replace All", self, triggered=lambda: self.find_bar.on_replace_all()))
        edit_menu.addSeparator()
        edit_menu.addAction(QtGui.QAction("Fix Paragraphs (Selected)", self, shortcut="Ctrl+Shift+F", triggered=self.on_fix_paragraphs_selected))

        # NEW: Settings menu
        settings_menu = mb.addMenu("&Settings")
        settings_menu.addAction(QtGui.QAction("Options…", self, triggered=self.on_options_dialog))
        settings_menu.addAction(QtGui.QAction("Metadata…", self, triggered=self.on_metadata_dialog))

        themes_menu = mb.addMenu("&Themes")
        for t in ["Fusion Dark", "Fusion Light", "Windows", "WindowsVista", "Fusion"]:
            act = QtGui.QAction(t, self)
            act.triggered.connect(lambda _, name=t: self.apply_theme(name))
            themes_menu.addAction(act)

        about = mb.addMenu("&Help")
        about.addAction(QtGui.QAction("About", self, triggered=self.on_about))

    def on_about(self):
        QtWidgets.QMessageBox.information(self, "About", f"Audiobooks v{__version__}\nKokoro text-to-speech GUI\n© 2025")

    def on_options_dialog(self):
        dlg = OptionsDialog(
            self,
            enable_intro=self.enable_intro,
            intro_template=self.intro_template,
            enable_outro=self.enable_outro,
            outro_template=self.outro_template,
            enable_mastering=self.enable_mastering,
        )
        if dlg.exec() == QtWidgets.QDialog.Accepted:
            self.enable_intro, self.intro_template, self.enable_outro, self.outro_template, self.enable_mastering = dlg.values()

    def on_metadata_dialog(self):
        dlg = MetadataDialog(
            self,
            title=(self.meta_title or (self.current_file.stem if self.current_file else "")),
            artist=self.meta_artist,
            album=(self.meta_album or self.meta_title or (self.current_file.stem if self.current_file else "")),
            cover_path=str(self.cover_image) if self.cover_image else None,
        )
        if dlg.exec() == QtWidgets.QDialog.Accepted:
            title, artist, album, cover = dlg.values()
            self.meta_title = title
            self.meta_artist = artist
            self.meta_album = album
            self.cover_image = Path(cover) if cover else None

    def show_find_bar(self):
        self.find_bar.setVisible(True)
        self.find_bar.raise_()
        self.find_bar.find_edit.setFocus()

    def show_find_bar_replace_mode(self):
        self.find_bar.setVisible(True)
        self.find_bar.raise_()
        self.find_bar.replace_edit.setFocus()

    def _recent_files(self) -> List[str]:
        s = QtCore.QSettings("AudiobooksApp", "AudiobooksGUI")
        return s.value("recent_files", [], type=list)

    def _add_recent_file(self, path: str):
        s = QtCore.QSettings("AudiobooksApp", "AudiobooksGUI")
        recents = [p for p in self._recent_files() if Path(p).exists()]
        if path in recents:
            recents.remove(path)
        recents.insert(0, path)
        while len(recents) > self.MAX_RECENTS:
            recents.pop()
        s.setValue("recent_files", recents)
        self._rebuild_recent_menu()

    def _rebuild_recent_menu(self):
        if not hasattr(self, "open_recent_menu"):
            return
        self.open_recent_menu.clear()
        recents = self._recent_files()
        if not recents:
            dummy = QtGui.QAction("(No recent files)", self)
            dummy.setEnabled(False)
            self.open_recent_menu.addAction(dummy)
            return
        for p in recents:
            act = QtGui.QAction(p, self)
            act.triggered.connect(lambda _, path=p: self._open_file(Path(path)))
            self.open_recent_menu.addAction(act)

    # ---------- Top controls (model/speed/CUDA) ----------
    def _build_model_speed_cuda_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(8)

        row.addWidget(QtWidgets.QLabel("Model"), 0)
        items, self._display_to_key = _sorted_voice_catalog()
        self.model_combo = QtWidgets.QComboBox()
        self.model_combo.addItems(items)
        for i, text in enumerate(items):
            if "Heart" in text:
                self.model_combo.setCurrentIndex(i)
                break
        self.model_combo.currentTextChanged.connect(self.on_model_changed)
        row.addWidget(self.model_combo, 0)

        row.addSpacing(18)
        row.addWidget(QtWidgets.QLabel("Speed"), 0)

        self.speed_slider = QtWidgets.QSlider(QtCore.Qt.Orientation.Horizontal)
        self.speed_slider.setMinimum(50)
        self.speed_slider.setMaximum(200)
        self.speed_slider.setValue(100)
        self.speed_slider.setFixedWidth(220)
        self.speed_slider.valueChanged.connect(self.on_speed_changed)
        row.addWidget(self.speed_slider, 0)

        self.speed_edit = QtWidgets.QLineEdit("1.00")
        self.speed_edit.setFixedWidth(48)
        self.speed_edit.setAlignment(QtCore.Qt.AlignmentFlag.AlignCenter)
        self.speed_edit.editingFinished.connect(self.on_speed_edited)
        row.addWidget(self.speed_edit, 0)

        row.addSpacing(18)
        self.speak_titles_check = QtWidgets.QCheckBox("Speak titles")
        self.speak_titles_check.setChecked(True)
        self.speak_titles_check.toggled.connect(lambda v: setattr(self, "include_titles", bool(v)))
        row.addWidget(self.speak_titles_check, 0)

        row.addWidget(QtWidgets.QLabel("Title pause (s)"), 0)
        self.pause_spin = QtWidgets.QDoubleSpinBox()
        self.pause_spin.setDecimals(1)
        self.pause_spin.setRange(0.0, 3.0)
        self.pause_spin.setSingleStep(0.1)
        self.pause_spin.setValue(0.5)
        self.pause_spin.setFixedWidth(80)
        self.pause_spin.valueChanged.connect(lambda v: setattr(self, "title_pause_seconds", float(v)))
        row.addWidget(self.pause_spin, 0)

        row.addWidget(QtWidgets.QLabel("Sentence pause (s)"), 0)
        self.sent_pause = QtWidgets.QDoubleSpinBox()
        self.sent_pause.setDecimals(1)
        self.sent_pause.setRange(0.0, 1.0)
        self.sent_pause.setSingleStep(0.1)
        self.sent_pause.setValue(0.2)
        self.sent_pause.setFixedWidth(80)
        self.sent_pause.valueChanged.connect(lambda v: setattr(self, "sentence_pause_seconds", float(v)))
        row.addWidget(self.sent_pause, 0)

        row.addStretch(1)

        self.gpu_check = QtWidgets.QCheckBox("CUDA")
        self.gpu_check.setChecked(bool(_gpu_available()))
        self.gpu_check.toggled.connect(lambda v: setattr(self, "use_gpu", bool(v)))
        row.addWidget(self.gpu_check, 0)

        self.vbox.addLayout(row)

    def on_model_changed(self, display_name: str):
        if display_name in self._display_to_key:
            eng, vid, _ = self._display_to_key[display_name]
            self.selected_engine = eng
            self.selected_voice = vid
        else:
            self.selected_engine = "kokoro"
            self.selected_voice = "af_heart"

    def on_speed_changed(self, value: int):
        self.speed = value / 100.0
        self.speed_edit.setText(f"{self.speed:.2f}")

    def on_speed_edited(self):
        try:
            val = float(self.speed_edit.text())
        except ValueError:
            val = 1.0
        val = max(0.5, min(2.0, val))
        self.speed = val
        self.speed_slider.setValue(int(val * 100))
        self.speed_edit.setText(f"{val:.2f}")

    # ---------- Splitter (left list + right editor) ----------
    def _build_splitter_panel(self):
        group = QtWidgets.QGroupBox("Workspace")
        gl = QtWidgets.QVBoxLayout(group)
        gl.setContentsMargins(8, 8, 8, 8)
        gl.setSpacing(6)

        # Find/Replace (compact)
        self.find_bar = FindReplaceBar()
        self.find_bar.find_next.connect(self._editor_find_next)
        self.find_bar.replace_one.connect(self._editor_replace_one)
        self.find_bar.replace_all.connect(self._editor_replace_all)
        gl.addWidget(self.find_bar)

        splitter = QtWidgets.QSplitter(QtCore.Qt.Orientation.Horizontal)
        splitter.setChildrenCollapsible(False)

        # Left: list + side controls
        left = QtWidgets.QWidget()
        left_h = QtWidgets.QHBoxLayout(left)
        left_h.setContentsMargins(0, 0, 0, 0)
        left_h.setSpacing(6)

        self.chapter_list = QtWidgets.QListWidget()
        self.chapter_list.setSelectionMode(QtWidgets.QAbstractItemView.SelectionMode.ExtendedSelection)
        self.chapter_list.setMinimumWidth(300)
        self.chapter_list.setEditTriggers(QtWidgets.QAbstractItemView.EditTrigger.DoubleClicked)
        self.chapter_list.currentRowChanged.connect(self._on_current_row_changed)
        self.chapter_list.itemChanged.connect(self._on_chapter_name_changed)
        left_h.addWidget(self.chapter_list, 1)

        side = QtWidgets.QWidget()
        side_v = QtWidgets.QVBoxLayout(side)
        side_v.setContentsMargins(0, 0, 0, 0)
        side_v.setSpacing(6)

        def mk(text, slot, tip=""):
            b = QtWidgets.QPushButton(text, clicked=slot)
            b.setFixedWidth(120)
            b.setFixedHeight(28)
            if tip:
                b.setToolTip(tip)
            return b

        side_v.addWidget(mk("Insert", self.on_insert_chapter, "Insert a new chapter after current"))
        side_v.addWidget(mk("Delete", self.on_delete_chapters, "Delete selected chapters"))
        side_v.addSpacing(10)
        side_v.addWidget(mk("Select All", self.on_select_all, "Select all chapters"))
        side_v.addWidget(mk("Select None", self.on_select_none, "Clear selection"))
        side_v.addWidget(mk("Invert", self.on_select_invert, "Invert selection"))
        side_v.addStretch(1)

        left_h.addWidget(side, 0)
        splitter.addWidget(left)

        # Right: editor (Title + blank + Body)
        right = QtWidgets.QWidget()
        right_v = QtWidgets.QVBoxLayout(right)
        right_v.setContentsMargins(0, 0, 0, 0)
        right_v.setSpacing(6)

        self.editor_title = QtWidgets.QLabel("Editor")
        right_v.addWidget(self.editor_title, 0)

        self.editor = QtWidgets.QPlainTextEdit()
        self.editor.setLineWrapMode(QtWidgets.QPlainTextEdit.LineWrapMode.WidgetWidth)
        mono = QtGui.QFontDatabase.systemFont(QtGui.QFontDatabase.FixedFont)
        self.editor.setFont(mono)
        self.editor.textChanged.connect(self._mark_dirty)
        right_v.addWidget(self.editor, 1)

        splitter.addWidget(right)
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)

        gl.addWidget(splitter)
        self.vbox.addWidget(group)

    # ---------- Actions row ----------
    def _build_actions_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(10)

        row.addWidget(QtWidgets.QLabel("Output"), 0)
        self.output_combo = QtWidgets.QComboBox()
        self.output_combo.addItems(["m4b", "wav"])
        self.output_combo.setCurrentText("wav")
        self.output_combo.currentTextChanged.connect(self.on_output_changed)
        row.addWidget(self.output_combo, 0)

        # NEW: Bitrate placed between Output and Export
        row.addSpacing(12)
        row.addWidget(QtWidgets.QLabel("Bitrate (kbps)"), 0)
        self.bitrate_combo = QtWidgets.QComboBox()
        self.bitrate_combo.addItems(["48", "64", "96", "128"])
        self.bitrate_combo.setCurrentText("64")
        self.bitrate_combo.currentTextChanged.connect(lambda t: setattr(self, "bitrate_kbps", int(t)))
        row.addWidget(self.bitrate_combo, 0)

        row.addSpacing(18)
        row.addWidget(QtWidgets.QLabel("Export"), 0)
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

    def on_output_changed(self, text: str):
        self.output_format = text.lower()

    def on_export_changed(self, text: str):
        self.export_mode = "single" if text.lower() == "single" else "chapters"

    # ---------- Status (thin) ----------
    def _build_status_row(self):
        row = QtWidgets.QHBoxLayout()
        row.setContentsMargins(2, 0, 2, 0)
        row.setSpacing(4)
        self.status_label = QtWidgets.QLabel("Ready")
        f = self.status_label.font()
        f.setPointSizeF(max(7.0, f.pointSizeF() - 2.0))
        self.status_label.setFont(f)
        self.status_label.setSizePolicy(QtWidgets.QSizePolicy.Ignored, QtWidgets.QSizePolicy.Minimum)
        row.addWidget(self.status_label, 1)
        row.addStretch(0)
        container = QtWidgets.QWidget()
        container.setLayout(row)
        container.setMaximumHeight(16)
        self.vbox.addWidget(container)

    # ---------- Load & Save ----------
    def on_open_file_dialog(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(
            self, "Open file", filter="Supported (*.epub *.txt *.docx *.pdf);;All files (*.*)"
        )
        if not path:
            return
        self._open_file(Path(path))

    def on_save(self):
        if self._save_path:
            self._write_all_chapters_to(self._save_path)
        else:
            self.on_save_as()

    def on_save_as(self):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(
            self, "Save Edited Text", filter="Text/Markdown (*.txt *.md);;All files (*.*)"
        )
        if not path:
            return
        self._save_path = Path(path)
        self._write_all_chapters_to(self._save_path)

    def _open_file(self, path: Path):
        if self._dirty and not self._confirm_discard_changes():
            return
        self.current_file = Path(path)
        self._save_path = None
        self._load_chapters_from_file(self.current_file)
        self.meta_title = self.current_file.stem
        self._add_recent_file(str(path))
        self._rebuild_recent_menu()
        self.status_label.setText(f"Opened: {path.name}")

    def _load_chapters_from_file(self, path: Path):
        self._chapters = []
        suffix = path.suffix.lower()
        try:
            if suffix == ".epub":
                arr = _extract_chapters_text(path)
                cleaned = []
                for t, b in arr:
                    cleaned.append((t, self._strip_leading_heading_from_body(b, t)))
                self._chapters = cleaned
            elif suffix == ".txt":
                self._chapters = [load_txt_as_chapter(path)]
            elif suffix == ".docx":
                self._chapters = [load_docx_as_chapter(path)]
            elif suffix == ".pdf":
                self._chapters = [load_pdf_as_chapter(path)]
            else:
                self._chapters = [load_txt_as_chapter(path)]
        except Exception as e:
            QtWidgets.QMessageBox.critical(self, "Open error", str(e))
            self._chapters = []
        self._populate_chapters()

    def _populate_chapters(self):
        self._suppress_item_changed = True
        try:
            self.chapter_list.clear()
            if not self._chapters:
                self.editor.clear()
                self.editor_title.setText("Editor")
                return
            for t, _ in self._chapters:
                item = QtWidgets.QListWidgetItem(t)
                item.setFlags(item.flags() | QtCore.Qt.ItemFlag.ItemIsEditable)
                self.chapter_list.addItem(item)
            if self.chapter_list.count() > 0:
                self.chapter_list.setCurrentRow(0, QtCore.QItemSelectionModel.ClearAndSelect)
        finally:
            self._suppress_item_changed = False

    def _confirm_discard_changes(self) -> bool:
        if not self._dirty:
            return True
        m = QtWidgets.QMessageBox(self)
        m.setIcon(QtWidgets.QMessageBox.Icon.Warning)
        m.setWindowTitle("Unsaved changes")
        m.setText("You have unsaved changes. Discard them?")
        m.setStandardButtons(QtWidgets.QMessageBox.StandardButton.Discard | QtWidgets.QMessageBox.StandardButton.Cancel)
        return m.exec() == QtWidgets.QMessageBox.StandardButton.Discard

    def _write_all_chapters_to(self, path: Path):
        try:
            with open(path, "w", encoding="utf-8") as f:
                for i, (title, body) in enumerate(self._chapters):
                    if i > 0:
                        f.write("\n\n")
                    f.write(title.strip())
                    if body.strip():
                        f.write("\n\n" + body.strip())
            self._dirty = False
            self._update_title_dirty()
            self.status_label.setText(f"Saved: {path.name}")
        except Exception as e:
            QtWidgets.QMessageBox.critical(self, "Save error", str(e))

    # ---------- Title/Body helpers ----------
    _CHAPTER_HEADING_RE = re.compile(
        r'^\s*(?:chapter|CHAPTER)\s+([0-9]+|[ivxlcdmIVXLCDM]+)\s*[:\-–]?\s*(.*)$'
    )

    def _strip_leading_heading_from_body(self, body: str, title: str) -> str:
        s = (body or "").replace("\r\n", "\n").replace("\r", "\n")
        lines = s.split("\n")
        first_idx = next((i for i, ln in enumerate(lines) if ln.strip()), None)
        if first_idx is None:
            return s.strip("\n")
        first = lines[first_idx].strip()

        if first.casefold() == (title or "").strip().casefold():
            i = first_idx + 1
            while i < len(lines) and not lines[i].strip():
                i += 1
            return "\n".join(lines[i:]).strip("\n")

        m = self._CHAPTER_HEADING_RE.match(first)
        if m:
            i = first_idx + 1
            while i < len(lines) and not lines[i].strip():
                i += 1
            return "\n".join(lines[i:]).strip("\n")

        return s.strip("\n")

    def _compose_editor_text(self, title: str, body: str) -> str:
        title = (title or "").strip()
        body = (body or "").strip("\n")
        if body:
            return f"{title}\n\n{body}"
        return title

    def _parse_editor_text(self, text: str, fallback_title: str) -> Tuple[str, str]:
        s = (text or "").replace("\r\n", "\n").replace("\r", "\n")
        lines = s.split("\n")
        first_idx = next((i for i, ln in enumerate(lines) if ln.strip()), None)
        if first_idx is None:
            return fallback_title, ""
        title = lines[first_idx].strip()
        body_start = first_idx + 1
        while body_start < len(lines) and lines[body_start].strip() == "":
            body_start += 1
        body = "\n".join(lines[body_start:]).rstrip("\n")
        return (title or fallback_title, body)

    # ---------- Chapter list / editor sync ----------
    def _on_current_row_changed(self, row: int):
        if row < 0 or row >= len(self._chapters):
            self.editor.clear()
            self.editor_title.setText("Editor")
            return
        title, body = self._chapters[row]
        try:
            self.editor.textChanged.disconnect(self._mark_dirty)
        except Exception:
            pass
        self.editor.setPlainText(self._compose_editor_text(title, body))
        self.editor_title.setText("Editor")
        self.editor.document().setModified(False)
        self.editor.textChanged.connect(self._mark_dirty)

    def _on_chapter_name_changed(self, item: QtWidgets.QListWidgetItem):
        if self._suppress_item_changed:
            return
        idx = self.chapter_list.row(item)
        if 0 <= idx < len(self._chapters):
            _, body = self._chapters[idx]
            new_title = item.text()
            self._chapters[idx] = (new_title, self._strip_leading_heading_from_body(body, new_title))
            if idx == self.chapter_list.currentRow():
                try:
                    self.editor.textChanged.disconnect(self._mark_dirty)
                except Exception:
                    pass
                self.editor.setPlainText(self._compose_editor_text(new_title, self._chapters[idx][1]))
                self.editor.textChanged.connect(self._mark_dirty)
            self._dirty = True
            self._update_title_dirty()

    def _mark_dirty(self):
        row = self.chapter_list.currentRow()
        if 0 <= row < len(self._chapters):
            fallback = self._chapters[row][0] or f"Chapter {row+1}"
            title, body = self._parse_editor_text(self.editor.toPlainText(), fallback)
            body = self._strip_leading_heading_from_body(body, title)
            self._chapters[row] = (title, body)
            self._suppress_item_changed = True
            try:
                it = self.chapter_list.item(row)
                if it and it.text() != title:
                    it.setText(title)
            finally:
                self._suppress_item_changed = False
        self._dirty = True
        self._update_title_dirty()

    # ---------- Find/Replace (across all chapters toggle) ----------
    def _selected_rows(self) -> List[int]:
        rows = sorted({i.row() for i in self.chapter_list.selectedIndexes()})
        if not rows:
            cur = self.chapter_list.currentRow()
            if cur >= 0:
                rows = [cur]
        return rows

    def _chapters_scope(self, all_chapters: bool) -> List[int]:
        return list(range(len(self._chapters))) if all_chapters else self._selected_rows()

    def _editor_find_next(self, needle: str, all_chapters: bool):
        if not needle:
            return
        rows = self._chapters_scope(all_chapters)
        cur = self.chapter_list.currentRow() if not all_chapters else (rows[0] if rows else -1)

        if 0 <= cur < len(self._chapters):
            cursor = self.editor.textCursor()
            pos = cursor.selectionEnd()
            text = self.editor.toPlainText()
            idx = text.find(needle, pos)
            if idx == -1:
                idx = text.find(needle, 0)
            if idx != -1:
                cursor.setPosition(idx)
                cursor.setPosition(idx + len(needle), QtGui.QTextCursor.MoveMode.KeepAnchor)
                self.editor.setTextCursor(cursor)
                self.editor.ensureCursorVisible()
                return

        for r in rows:
            if r == cur:
                continue
            t, body = self._chapters[r]
            full = self._compose_editor_text(t, body)
            idx = full.find(needle)
            if idx != -1:
                if self.chapter_list.currentRow() != r:
                    self.chapter_list.setCurrentRow(r, QtCore.QItemSelectionModel.ClearAndSelect)
                    QtWidgets.QApplication.processEvents()
                cursor = self.editor.textCursor()
                cursor.setPosition(idx)
                cursor.setPosition(idx + len(needle), QtGui.QTextCursor.MoveMode.KeepAnchor)
                self.editor.setTextCursor(cursor)
                self.editor.ensureCursorVisible()
                return
        QtWidgets.QApplication.beep()

    def _editor_replace_one(self, needle: str, repl: str, all_chapters: bool):
        if not needle:
            return
        cur = self.editor.textCursor()
        if cur.hasSelection() and cur.selectedText() == needle:
            cur.insertText(repl)
            self._mark_dirty()
        else:
            self._editor_find_next(needle, all_chapters)
            cur = self.editor.textCursor()
            if cur.hasSelection() and cur.selectedText() == needle:
                cur.insertText(repl)
                self._mark_dirty()

    def _editor_replace_all(self, needle: str, repl: str, all_chapters: bool):
        if not needle:
            return
        rows = self._chapters_scope(all_chapters)
        changed = 0
        for r in rows:
            title, body = self._chapters[r]
            full = self._compose_editor_text(title, body)
            new_full = full.replace(needle, repl)
            if new_full != full:
                new_title, new_body = self._parse_editor_text(new_full, title or f"Chapter {r+1}")
                new_body = self._strip_leading_heading_from_body(new_body, new_title)
                self._chapters[r] = (new_title, new_body)
                changed += 1
                if r == self.chapter_list.currentRow():
                    self.editor.blockSignals(True)
                    try:
                        self.editor.setPlainText(self._compose_editor_text(new_title, new_body))
                    finally:
                        self.editor.blockSignals(False)
                self._suppress_item_changed = True
                try:
                    it = self.chapter_list.item(r)
                    if it and it.text() != new_title:
                        it.setText(new_title)
                finally:
                    self._suppress_item_changed = False
        if changed:
            self._dirty = True
            self._update_title_dirty()
            self.status_label.setText(f"Replaced in {changed} chapter(s)")
        else:
            self.status_label.setText("No matches to replace")

    # ---------- Paragraph fixer ----------
    def on_fix_paragraphs_selected(self):
        rows = self._selected_rows()
        if not rows:
            QtWidgets.QMessageBox.information(self, "Fix Paragraphs", "Select one or more chapters first.")
            return
        count = 0
        for r in rows:
            title, body = self._chapters[r]
            fixed = fix_paragraph_structure(body)
            fixed = self._strip_leading_heading_from_body(fixed, title)
            if fixed != body:
                self._chapters[r] = (title, fixed)
                count += 1
                if r == self.chapter_list.currentRow():
                    self.editor.blockSignals(True)
                    try:
                        self.editor.setPlainText(self._compose_editor_text(title, fixed))
                    finally:
                        self.editor.blockSignals(False)
        if count:
            self._dirty = True
            self._update_title_dirty()
        self.status_label.setText(f"Fixed paragraphs in {len(rows)} chapter(s)")

    # ---------- Preview ----------
    def _auto_open(self, target: Path):
        try:
            import subprocess
            if sys.platform.startswith("win"):
                subprocess.Popen(["cmd", "/c", "start", "", str(target)], shell=True)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", str(target)])
            else:
                subprocess.Popen(["xdg-open", str(target)])
        except Exception:
            pass

    def on_preview_clicked(self):
        if not self.current_file:
            QtWidgets.QMessageBox.warning(self, "Preview", "Please open a file first.")
            return
        row = self.chapter_list.currentRow()
        if row < 0:
            QtWidgets.QMessageBox.information(self, "Preview", "Select a chapter first.")
            return
        title, body = self._chapters[row]
        if not (title.strip() or body.strip()):
            QtWidgets.QMessageBox.information(self, "Preview", "Selected item has no text.")
            return

        preview_text = body[:2000] if body else title
        tmp = Path("._preview_tmp.wav")
        out = Path("preview_10s.wav")
        try:
            kokoro_synthesize_to_wav(
                preview_text, self.selected_voice, tmp, use_gpu=self.use_gpu, speed=self.speed
            )
            with wave.open(str(tmp), "rb") as src:
                fr = src.getframerate()
                nch = src.getnchannels()
                sw = src.getsampwidth()
                frames = src.readframes(int(10 * fr))
            with wave.open(str(out), "wb") as dst:
                dst.setnchannels(nch)
                dst.setsampwidth(sw)
                dst.setframerate(fr)
                dst.writeframes(frames)
            tmp.unlink(missing_ok=True)
            self.status_label.setText(f"Preview: wrote {out.name} (10s)")
            self._auto_open(out)
        except Exception as e:
            tmp.unlink(missing_ok=True)
            QtWidgets.QMessageBox.critical(self, "Preview error", str(e))

    # ---------- Convert ----------
    def on_convert_clicked(self):
        if not self.current_file:
            QtWidgets.QMessageBox.warning(self, "Convert", "Please open a file first.")
            return

        sel_rows = [i.row() for i in self.chapter_list.selectedIndexes()]
        chapters_to_use = [self._chapters[i] for i in sel_rows] if sel_rows else self._chapters

        self.status_label.setText("Converting…")
        try:
            if self.export_mode == "single":
                filt = "Audiobook M4B (*.m4b)" if self.output_format == "m4b" else "WAV audio (*.wav)"
                target, _ = QtWidgets.QFileDialog.getSaveFileName(
                    self, f"Save as {self.output_format.upper()}", filter=filt
                )
                if not target:
                    return
                embed_chapters = (self.output_format == "m4b")
                convert_epub_to_m4b(
                    self.current_file,
                    Path(target),
                    engine=self.selected_engine,
                    voice=self.selected_voice,
                    speed=self.speed,
                    use_gpu=self.use_gpu,
                    selected_chapter_indices=None,
                    output_format=self.output_format,
                    chapters_override=chapters_to_use,
                    include_titles=self.include_titles,
                    title_pause_seconds=self.title_pause_seconds,
                    sentence_pause_seconds=self.sentence_pause_seconds,
                    bitrate_kbps=self.bitrate_kbps,
                    enable_mastering=self.enable_mastering,
                    enable_intro=self.enable_intro,
                    enable_outro=self.enable_outro,
                    intro_template=self.intro_template,
                    outro_template=self.outro_template,
                    cover_image=self.cover_image,
                    meta_title=(self.meta_title or self.current_file.stem),
                    meta_artist=self.meta_artist,
                    meta_album=(self.meta_album or self.meta_title or self.current_file.stem),
                    embed_chapters=embed_chapters,
                )
                self.status_label.setText(f"Saved: {Path(target).name}")
            else:
                out_dir = convert_epub_to_tracks(
                    self.current_file,
                    engine=self.selected_engine,
                    voice=self.selected_voice,
                    speed=self.speed,
                    use_gpu=self.use_gpu,
                    selected_chapter_indices=None,
                    output_format=self.output_format,
                    chapters_override=chapters_to_use,
                    include_titles=self.include_titles,
                    title_pause_seconds=self.title_pause_seconds,
                    sentence_pause_seconds=self.sentence_pause_seconds,
                    bitrate_kbps=self.bitrate_kbps,
                    enable_mastering=self.enable_mastering,
                    enable_intro=self.enable_intro,
                    enable_outro=self.enable_outro,
                    intro_template=self.intro_template,
                    outro_template=self.outro_template,
                )
                self.status_label.setText(f"Saved {self.output_format.upper()} tracks to: {out_dir}")
        except Exception as e:
            QtWidgets.QMessageBox.critical(self, "Conversion error", str(e))
            self.status_label.setText("Error")

    # ---------- Selection helpers ----------
    def on_insert_chapter(self):
        row = self.chapter_list.currentRow()
        insert_at = max(0, row + 1) if row >= 0 else self.chapter_list.count()
        default_name = "New Section"
        self._chapters.insert(insert_at, (default_name, ""))
        self._suppress_item_changed = True
        try:
            item = QtWidgets.QListWidgetItem(default_name)
            item.setFlags(item.flags() | QtCore.Qt.ItemFlag.ItemIsEditable)
            self.chapter_list.insertItem(insert_at, item)
            self.chapter_list.setCurrentRow(insert_at, QtCore.QItemSelectionModel.ClearAndSelect)
        finally:
            self._suppress_item_changed = False
        self._dirty = True
        self._update_title_dirty()

    def on_delete_chapters(self):
        rows = sorted({i.row() for i in self.chapter_list.selectedIndexes()}, reverse=True)
        if not rows:
            return
        m = QtWidgets.QMessageBox(self)
        m.setIcon(QtWidgets.QMessageBox.Icon.Warning)
        m.setWindowTitle("Delete")
        m.setText(f"Delete {len(rows)} item(s)?")
        m.setStandardButtons(QtWidgets.QMessageBox.StandardButton.Yes | QtWidgets.QMessageBox.StandardButton.Cancel)
        if m.exec() != QtWidgets.QMessageBox.StandardButton.Yes:
            return
        for r in rows:
            if 0 <= r < len(self._chapters):
                del self._chapters[r]
                self.chapter_list.takeItem(r)
        if self.chapter_list.count() == 0:
            self.editor.clear()
            self.editor_title.setText("Editor")
        else:
            next_row = min(max(0, (rows[-1] - 1)), self.chapter_list.count() - 1)
            self.chapter_list.setCurrentRow(next_row, QtCore.QItemSelectionModel.ClearAndSelect)
        self._dirty = True
        self._update_title_dirty()

    def on_select_all(self):
        self.chapter_list.selectAll()
        if self.chapter_list.currentRow() == -1 and self.chapter_list.count() > 0:
            self.chapter_list.setCurrentRow(0)

    def on_select_none(self):
        self.chapter_list.clearSelection()

    def on_select_invert(self):
        sel = {i.row() for i in self.chapter_list.selectedIndexes()}
        self.chapter_list.clearSelection()
        for i in range(self.chapter_list.count()):
            if i not in sel:
                self.chapter_list.item(i).setSelected(True)
        if self.chapter_list.currentRow() == -1:
            sel2 = self.chapter_list.selectedIndexes()
            if sel2:
                self.chapter_list.setCurrentRow(sel2[0].row())

    # ---------- Themes ----------
    def apply_theme(self, name: str):
        app = QtWidgets.QApplication.instance()
        if not app:
            return
        if name == "Fusion Dark":
            app.setStyle("Fusion")
            dark = QtGui.QPalette()
            dark.setColor(QtGui.QPalette.ColorRole.Window, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.ColorRole.WindowText, QtCore.Qt.GlobalColor.white)
            dark.setColor(QtGui.QPalette.ColorRole.Base, QtGui.QColor(30, 30, 30))
            dark.setColor(QtGui.QPalette.ColorRole.AlternateBase, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.ColorRole.Text, QtCore.Qt.GlobalColor.white)
            dark.setColor(QtGui.QPalette.ColorRole.Button, QtGui.QColor(45, 45, 45))
            dark.setColor(QtGui.QPalette.ColorRole.ButtonText, QtCore.Qt.GlobalColor.white)
            dark.setColor(QtGui.QPalette.ColorRole.Highlight, QtGui.QColor(64, 128, 255))
            dark.setColor(QtGui.QPalette.ColorRole.HighlightedText, QtCore.Qt.GlobalColor.black)
            app.setPalette(dark)
        else:
            app.setStyle("Fusion")
            app.setPalette(app.style().standardPalette())

    def _update_title_dirty(self):
        base = f"Audiobooks v{__version__}"
        if self.current_file:
            base += f" — {self.current_file.name}"
        if self._save_path:
            base += f" [{self._save_path.name}]"
        if self._dirty:
            base += " *"
        self.setWindowTitle(base)

# -------------- Entry points --------------
def main_gui():
    QtCore.QCoreApplication.setOrganizationName("AudiobooksApp")
    QtCore.QCoreApplication.setApplicationName("AudiobooksGUI")
    app = QtWidgets.QApplication(sys.argv)
    mw = MainWindow()
    mw.show()
    sys.exit(app.exec())

def main():
    main_gui()

if __name__ == "__main__":
    main()
