"""
Auto-injected Chrome defaults for BotPanel containers.
Patches selenium.webdriver.ChromeOptions to always include
low-memory flags, so no bot.py needs to be modified.
"""
import os

try:
    from selenium.webdriver import ChromeOptions as _ChromeOptions

    _orig_init = _ChromeOptions.__init__

    def _patched_init(self, *args, **kwargs):
        _orig_init(self, *args, **kwargs)
        # Only add if not already present
        existing = getattr(self, '_arguments', []) or []
        flags = [
            '--no-sandbox',
            '--disable-dev-shm-usage',
            '--disable-gpu',
            '--headless=new',
            '--disable-extensions',
            '--disable-plugins',
            '--disable-background-networking',
            '--disable-default-apps',
            '--disable-sync',
            '--disable-translate',
            '--metrics-recording-only',
            '--mute-audio',
            '--no-first-run',
            '--safebrowsing-disable-auto-update',
            '--js-flags=--max-old-space-size=128',
            '--memory-pressure-off',
            '--single-process',
        ]
        for flag in flags:
            if flag not in existing:
                self.add_argument(flag)

    _ChromeOptions.__init__ = _patched_init
except ImportError:
    pass  # selenium not installed in this bot — no-op
