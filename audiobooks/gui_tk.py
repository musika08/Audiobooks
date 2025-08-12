# audiobooks/gui_tk.py — Tkinter GUI for Audiobooks
from __future__ import annotations

import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
from pathlib import Path
from typing import Dict, Tuple, List, Optional, Callable

# ---- Import core helpers ----
from .core import (
    kokoro_available,
    gpu_available,
    convert_epub_to_m4b,     # single-file export (also supports WAV)
    convert_epub_to_tracks,  # per-chapter export (also supports WAV)
    kokoro_synthesize_to_wav,
    list_chapter_titles,
)

try:
    from .core import piper_synthesize_to_wav  # optional (not implemented in this build)
except Exception:
    piper_synthesize_to_wav = None  # type: ignore


# ===============================
# Voice catalog & display helpers
# ===============================

# Format: (voice_id, gender, engine)
# gender: "F" or "M"; engine: "kokoro" or "piper"
KOKORO_VOICES: List[Tuple[str, str, str]] = [
    # --- female (American / British) ---
    ("af_bella",    "F", "kokoro"),
    ("af_emily",    "F", "kokoro"),
    ("af_fiona",    "F", "kokoro"),
    ("af_sarah",    "F", "kokoro"),
    ("af_nicole",   "F", "kokoro"),
    ("af_nova",     "F", "kokoro"),
    ("af_heart",    "F", "kokoro"),
    ("bf_alice",    "F", "kokoro"),  # fixed legacy: no af_alice
    ("bf_emma",     "F", "kokoro"),
    ("bf_isabella", "F", "kokoro"),
    ("bf_lily",     "F", "kokoro"),

    # --- male (American / British) ---
    ("am_adam",     "M", "kokoro"),
    ("am_brian",    "M", "kokoro"),
    ("am_chris",    "M", "kokoro"),
    ("am_david",    "M", "kokoro"),
    ("am_elliot",   "M", "kokoro"),
    ("am_echo",     "M", "kokoro"),
    ("am_eric",     "M", "kokoro"),
    ("am_fenrir",   "M", "kokoro"),
    ("am_liam",     "M", "kokoro"),
    ("am_michael",  "M", "kokoro"),
    ("am_onyx",     "M", "kokoro"),
    ("am_puck",     "M", "kokoro"),
    ("am_santa",    "M", "kokoro"),
    ("bm_daniel",   "M", "kokoro"),
    ("bm_fable",    "M", "kokoro"),
    ("bm_george",   "M", "kokoro"),
    ("bm_lewis",    "M", "kokoro"),
]

PIPER_VOICES: List[Tuple[str, str, str]] = [
    ("en_US-amy-medium",    "F", "piper"),
    ("en_US-kathleen-low",  "F", "piper"),
    ("en_US-joe-medium",    "M", "piper"),
    ("en_US-kyle-low",      "M", "piper"),
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
# Theming helpers (trimmed to avoid Combobox popdown glitches)
# ===============================

THEME_OPTIONS = [
    "Light", "Dark",
    "Clam", "Alt", "Classic",
]

def _apply_theme_styles(app: tk.Tk, theme_name: str):
    style = ttk.Style(app)
    base = {
        "Light": "clam",
        "Dark": "clam",
        "Clam": "clam",
        "Alt": "alt",
        "Classic": "classic",
    }.get(theme_name, "clam")
    style.theme_use(base)

    # Palettes
    if theme_name == "Dark":
        bg = "#1f2125"
        fg = "#e9e9e9"
        acc = "#2a2c31"
        sel = "#2d6cdf"
        field = "#2b2d33"
        trough = "#24262b"
    else:
        bg = "#ffffff"
        fg = "#1a1a1a"
        acc = "#e9e9e9"
        sel = "#2d6cdf"
        field = "#f4f4f4"
        trough = "#e6e6e6"

    app.configure(bg=bg)
    style.configure(".", background=bg, foreground=fg)
    style.configure("TFrame", background=bg)
    style.configure("TLabelframe", background=bg, foreground=fg)
    style.configure("TLabelframe.Label", background=bg, foreground=fg)
    style.configure("TLabel", background=bg, foreground=fg)
    style.configure("TButton", padding=5)
    style.configure("TEntry", fieldbackground=field, foreground=fg)
    # DO NOT over-style TCombobox — keep defaults so dropdown renders reliably
    style.configure("TProgressbar", troughcolor=trough)

    # Non-ttk widgets colors (Listbox/Text)
    app.option_add("*Listbox.background", field)
    app.option_add("*Listbox.foreground", fg)
    app.option_add("*Listbox.selectBackground", sel)
    app.option_add("*Listbox.selectForeground", "#ffffff")
    app.option_add("*Text.background", field)
    app.option_add("*Text.foreground", fg)
    app.option_add("*Text.insertBackground", fg)
    app.option_add("*Text.selectBackground", sel)
    app.option_add("*Text.selectForeground", "#ffffff")

    app.update_idletasks()


# ===============================
# Main App
# ===============================

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        # Apply theme FIRST
        self.theme_var = tk.StringVar(value="Light")
        _apply_theme_styles(self, self.theme_var.get())

        # Title bar text with an ink pen emoji
        self.title("🖋️ Audiobooks")
        self.geometry("960x680")
        self.minsize(840, 580)

        # state fields
        self.selected_engine: str = "kokoro"
        self.selected_voice: str = "af_bella"
        self.speed_var = tk.DoubleVar(value=1.0)
        self.cuda_var = tk.BooleanVar(value=bool(gpu_available()))  # CUDA toggle
        self.format_var = tk.StringVar(value="m4b")   # "m4b" or "wav"
        self.mode_var = tk.StringVar(value="single")  # "single" or "chapters"
        self.epub_path: Optional[Path] = None

        # audio FX and bitrate
        self.normalize_var = tk.BooleanVar(value=False)
        self.noisegate_var = tk.BooleanVar(value=False)
        self.noisegate_db = tk.DoubleVar(value=-40.0)
        self.bitrate_kbps = tk.StringVar(value="64")  # as string for Combobox

        # status/progress
        self.status_var = tk.StringVar(value="")
        self._display_to_key: Dict[str, Tuple[str, str, str]] = {}

        self._build_ui()
        self._update_status("Ready.")

    # ---------- UI ----------

    def _build_ui(self):
        root = ttk.Frame(self, padding=10)
        root.pack(fill="both", expand=True)

        # Header row with theme selector on right
        header = ttk.Frame(root)
        header.pack(fill="x")
        ttk.Label(header, text="Model & Export", font=("Segoe UI", 11, "bold")).pack(side="left")

        self.theme_combo = ttk.Combobox(
            header, state="readonly", width=10,
            values=THEME_OPTIONS, textvariable=self.theme_var
        )
        self.theme_combo.pack(side="right")
        self.theme_combo.bind("<<ComboboxSelected>>", self._on_theme_change)

        # Row 0: EPUB picker
        file_row = ttk.Frame(root)
        file_row.pack(fill="x", pady=(10, 8))

        ttk.Label(file_row, text="EPUB:").pack(side="left")
        self.epub_var = tk.StringVar()
        self.epub_entry = ttk.Entry(file_row, textvariable=self.epub_var, width=76)
        self.epub_entry.pack(side="left", padx=6, fill="x", expand=True)
        ttk.Button(file_row, text="Browse…", command=self._choose_epub).pack(side="left")

        # Row 1: Voice + speed + CUDA + Output + Mode
        vrow = ttk.Frame(root)
        vrow.pack(fill="x", pady=(0, 8))

        # Voices
        ttk.Label(vrow, text="Model:").pack(side="left")
        items, self._display_to_key = _sorted_voice_catalog()
        self.voice_var = tk.StringVar(value=(items[0] if items else ""))
        self.voice_combo = ttk.Combobox(
            vrow,
            textvariable=self.voice_var,
            values=items,
            state="readonly",
            width=38
        )
        self.voice_combo.pack(side="left", padx=(6, 14))
        self.voice_combo.bind("<<ComboboxSelected>>", self._on_voice_change)

        # Speed
        ttk.Label(vrow, text="Speed:").pack(side="left")
        self.speed_scale = ttk.Scale(vrow, from_=0.5, to=2.0, orient="horizontal", variable=self.speed_var)
        self.speed_scale.pack(side="left", padx=6)
        ttk.Entry(vrow, width=6, textvariable=self.speed_var).pack(side="left")

        # Output format
        ttk.Label(vrow, text="Output:").pack(side="left", padx=(12, 0))
        self.format_combo = ttk.Combobox(
            vrow,
            state="readonly",
            width=6,
            values=["m4b", "wav"],
            textvariable=self.format_var
        )
        self.format_combo.pack(side="left", padx=(6, 10))
        self.format_combo.bind("<<ComboboxSelected>>", self._on_format_change)

        # CUDA
        ttk.Checkbutton(
            vrow,
            text="CUDA",
            variable=self.cuda_var,
            state=("normal" if gpu_available() else "disabled")
        ).pack(side="right")

        # Row 2: Mode (Single vs Bulk)
        mrow = ttk.Frame(root)
        mrow.pack(fill="x", pady=(0, 8))

        ttk.Label(mrow, text="Export:").pack(side="left")
        ttk.Radiobutton(mrow, text="Single", value="single", variable=self.mode_var).pack(side="left", padx=(8, 8))
        ttk.Radiobutton(mrow, text="Bulk", value="chapters", variable=self.mode_var).pack(side="left")

        # Row 3: Audio FX & Bitrate
        fx = ttk.LabelFrame(root, text="Audio options")
        fx.pack(fill="x", pady=(0, 8))

        ttk.Checkbutton(fx, text="Normalize", variable=self.normalize_var).pack(side="left", padx=(8, 8))
        ttk.Checkbutton(fx, text="Noise Gate (dB)", variable=self.noisegate_var).pack(side="left")
        self.noise_db_entry = ttk.Entry(fx, width=6, textvariable=self.noisegate_db)
        self.noise_db_entry.pack(side="left", padx=(6, 18))

        ttk.Label(fx, text="Bitrate (M4B):").pack(side="left")
        self.bitrate_combo = ttk.Combobox(
            fx, state="readonly", width=6, textvariable=self.bitrate_kbps,
            values=["32", "48", "64", "96", "128", "192"]
        )
        self.bitrate_combo.pack(side="left", padx=(6, 0))

        # Row 4: Sample text (for Test)
        srow = ttk.LabelFrame(root, text="Quick test text")
        srow.pack(fill="x", pady=(0, 8))
        self.sample_text = tk.Text(srow, height=4)
        self.sample_text.insert("1.0", "Hello from Audiobooks.")
        self.sample_text.pack(fill="x", padx=8, pady=8)

        # Row 5: Chapters box
        crow = ttk.LabelFrame(root, text="Chapters")
        crow.pack(fill="both", expand=True, pady=(0, 8))
        self.ch_list = tk.Listbox(crow, selectmode="extended", height=12)
        self.ch_list.pack(side="left", fill="both", expand=True, padx=(8, 4), pady=8)
        sb = ttk.Scrollbar(crow, orient="vertical", command=self.ch_list.yview)
        sb.pack(side="left", fill="y", pady=8)
        self.ch_list.config(yscrollcommand=sb.set)

        # Row 6: Chapter selection helpers + actions
        brow = ttk.Frame(root)
        brow.pack(fill="x")

        ttk.Button(brow, text="Select All", command=self._chap_select_all).pack(side="left")
        ttk.Button(brow, text="Invert", command=self._chap_invert).pack(side="left", padx=6)
        ttk.Button(brow, text="Test ▶", command=self._on_test).pack(side="right")
        ttk.Button(brow, text="Convert", command=self._on_convert).pack(side="right", padx=6)

        # Row 7: Status + Progress
        sbar = ttk.Frame(root)
        sbar.pack(fill="x", pady=(10, 0))
        self.status_label = ttk.Label(sbar, textvariable=self.status_var, anchor="w")
        self.status_label.pack(side="left", fill="x", expand=True)
        self.pb = ttk.Progressbar(sbar, mode="determinate", length=260)
        self.pb.pack(side="right")

        # Initialize defaults
        self._on_voice_change()
        self._on_format_change()

    # ---------- Events & helpers ----------

    def _on_theme_change(self, event=None):
        name = self.theme_var.get()
        _apply_theme_styles(self, name)

    def _on_format_change(self, event=None):
        # Enable bitrate selector only when m4b is selected
        is_m4b = (self.format_var.get().lower() == "m4b")
        self.bitrate_combo.config(state=("readonly" if is_m4b else "disabled"))

    def _update_status(self, text: str):
        self.status_var.set(text)
        self.update_idletasks()

    def _choose_epub(self):
        path = filedialog.askopenfilename(
            title="Choose EPUB",
            filetypes=[("EPUB files", "*.epub"), ("All files", "*.*")]
        )
        if not path:
            return
        self.epub_path = Path(path)
        self.epub_var.set(path)
        self._populate_chapters_from_epub()

    def _populate_chapters_from_epub(self):
        self.ch_list.delete(0, "end")
        if not self.epub_path:
            return
        try:
            titles = list_chapter_titles(self.epub_path)
            if not titles:
                titles = [f"Chapter {i}" for i in range(1, 21)]
            for t in titles:
                self.ch_list.insert("end", t)
        except Exception as e:
            messagebox.showwarning("Chapters", f"Could not parse chapters:\n{e}")
            for i in range(1, 21):
                self.ch_list.insert("end", f"Chapter {i}")

    def _chap_select_all(self):
        self.ch_list.select_set(0, "end")

    def _chap_invert(self):
        sel = set(self.ch_list.curselection())
        self.ch_list.select_clear(0, "end")
        for i in range(self.ch_list.size()):
            if i not in sel:
                self.ch_list.select_set(i)

    def _on_voice_change(self, event=None):
        disp = self.voice_var.get()
        key = self._display_to_key.get(disp)
        if key:
            engine, voice_id, gender = key
            self.selected_engine = engine
            self.selected_voice = voice_id

    def _run_in_thread(self, fn: Callable, on_done: Optional[Callable] = None):
        def runner():
            try:
                fn()
            except Exception as e:
                self.after(0, lambda: messagebox.showerror("Error", str(e)))
            finally:
                if on_done:
                    self.after(0, on_done)
        t = threading.Thread(target=runner, daemon=True)
        t.start()

    def _on_test(self):
        txt = self.sample_text.get("1.0", "end").strip()
        if not txt:
            messagebox.showwarning("Test", "Please enter some text to synthesize.")
            return

        speed = float(self.speed_var.get())
        use_cuda = bool(self.cuda_var.get())
        do_norm = bool(self.normalize_var.get())
        do_gate = bool(self.noisegate_var.get())
        gate_db = float(self.noisegate_db.get())

        def work():
            self._update_status("Testing voice…")
            self.pb.config(mode="indeterminate")
            self.pb.start(10)
            try:
                if self.selected_engine == "kokoro":
                    out = Path("test_kokoro.wav")
                    secs = kokoro_synthesize_to_wav(
                        txt,
                        self.selected_voice,
                        out,
                        use_gpu=use_cuda,
                        speed=speed,
                        normalize_audio=do_norm,
                        noise_gate=do_gate,
                        noise_gate_db=gate_db,
                    )
                    self.after(0, lambda: messagebox.showinfo("Test (Kokoro)", f"Wrote {out} ({secs:.2f}s)"))
                elif self.selected_engine == "piper":
                    if piper_synthesize_to_wav is None:
                        self.after(0, lambda: messagebox.showerror("Piper", "Piper is not available in this build."))
                        return
                    out = Path("test_piper.wav")
                    secs = piper_synthesize_to_wav(
                        txt,
                        self.selected_voice,
                        out,
                        speed=speed
                    )
                    self.after(0, lambda: messagebox.showinfo("Test (Piper)", f"Wrote {out} ({secs:.2f}s)"))
                else:
                    self.after(0, lambda: messagebox.showerror("Engine", f"Unknown engine: {self.selected_engine}"))
            finally:
                self.pb.stop()
                self.pb.config(mode="determinate", value=0)
                self._update_status("Ready.")

        self._run_in_thread(work)

    def _on_convert(self):
        if not self.epub_path or not self.epub_path.exists():
            messagebox.showwarning("Convert", "Please choose an EPUB first.")
            return

        fmt = (self.format_var.get() or "m4b").lower()   # "m4b" or "wav"
        mode = (self.mode_var.get() or "single").lower()
        selected_indices = list(self.ch_list.curselection())
        speed = float(self.speed_var.get())
        use_cuda = bool(self.cuda_var.get())
        do_norm = bool(self.normalize_var.get())
        do_gate = bool(self.noisegate_var.get())
        gate_db = float(self.noisegate_db.get())
        kbps = int(self.bitrate_kbps.get() or "64")

        if mode == "single":
            # Ask for a destination file
            defext = ".m4b" if fmt == "m4b" else ".wav"
            ftypes = [("Audiobook M4B", "*.m4b")] if fmt == "m4b" else [("WAV audio", "*.wav")]
            out = filedialog.asksaveasfilename(
                title=f"Save as {fmt.upper()}",
                defaultextension=defext,
                filetypes=ftypes
            )
            if not out:
                return

            def work():
                self._update_status("Converting (single)…")
                self.pb.config(mode="indeterminate")
                self.pb.start(10)
                try:
                    convert_epub_to_m4b(
                        self.epub_path,
                        Path(out),
                        engine=self.selected_engine,
                        voice=self.selected_voice,
                        speed=speed,
                        use_gpu=use_cuda,
                        selected_chapter_indices=selected_indices or None,
                        output_format=fmt,
                        normalize_audio=do_norm,
                        noise_gate=do_gate,
                        noise_gate_db=gate_db,
                        bitrate_kbps=kbps,
                    )
                    self.after(0, lambda: messagebox.showinfo("Done", f"Saved: {out}"))
                finally:
                    self.pb.stop()
                    self.pb.config(mode="determinate", value=0)
                    self._update_status("Ready.")

            self._run_in_thread(work)
        else:
            # Per-chapter export into a folder next to the EPUB
            def work():
                self._update_status("Converting (bulk)…")
                self.pb.config(mode="indeterminate")
                self.pb.start(10)
                try:
                    out_dir = convert_epub_to_tracks(
                        self.epub_path,
                        engine=self.selected_engine,
                        voice=self.selected_voice,
                        speed=speed,
                        use_gpu=use_cuda,
                        selected_chapter_indices=selected_indices or None,
                        output_format=fmt,
                        normalize_audio=do_norm,
                        noise_gate=do_gate,
                        noise_gate_db=gate_db,
                        bitrate_kbps=kbps,
                    )
                    self.after(0, lambda: messagebox.showinfo("Done", f"Saved {fmt.upper()} chapters in:\n{out_dir}"))
                finally:
                    self.pb.stop()
                    self.pb.config(mode="determinate", value=0)
                    self._update_status("Ready.")

            self._run_in_thread(work)


# ==============
# Entry points
# ==============

def main_gui():
    app = App()
    app.mainloop()


def main():
    main_gui()


if __name__ == "__main__":
    main()
