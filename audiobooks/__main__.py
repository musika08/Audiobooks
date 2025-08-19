# audiobooks/__main__.py
from __future__ import annotations

import sys
import subprocess
import importlib
import os

REQUIRED_PKGS = [
    'PySide6>=6.6,<6.8',   # Qt runtime for the GUI
]

OPTIONAL_PKGS = [
    # Only needed if you open DOCX/PDF from the GUI's File->Open
    'python-docx>=1.0.0',
    'pypdf>=4.2.0',
]

def _ensure_package(spec: str) -> None:
    """
    Ensure a pip package is installed into *this* Python interpreter.
    Accepts version specifiers (e.g., 'PySide6>=6.6,<6.8').
    """
    try:
        name = spec.split('==')[0].split('>=')[0].split('<')[0]
        importlib.import_module(name.replace('-', '_'))
        return
    except Exception:
        pass

    # Install using the running interpreter
    print(f"[setup] Installing {spec} ...")
    try:
        subprocess.check_call([sys.executable, '-m', 'pip', 'install', spec])
    except subprocess.CalledProcessError as e:
        print(f"[setup] Failed to install {spec}: {e}", file=sys.stderr)
        raise

def _bootstrap():
    # Make sure pip itself is available & up-to-date enough
    try:
        import pip  # noqa
    except Exception:
        print("[setup] pip missing; attempting to bootstrap ensurepip ...")
        import ensurepip  # noqa
        ensurepip.bootstrap(upgrade=True)

    # Install required packages
    for spec in REQUIRED_PKGS:
        _ensure_package(spec)

    # Best-effort install optional deps (don’t hard-fail)
    for spec in OPTIONAL_PKGS:
        try:
            _ensure_package(spec)
        except Exception as e:
            print(f"[setup] Optional package skipped ({spec}): {e}", file=sys.stderr)

def main():
    # Make sure Qt exists before importing the GUI
    try:
        import PySide6  # noqa: F401
    except Exception:
        _bootstrap()

    # Now import and run the GUI
    from .gui_qt import main_gui
    main_gui()

if __name__ == "__main__":
    main()
