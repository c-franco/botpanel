"""
Auto-injected Chromium defaults for BotPanel containers.
Patches selenium.webdriver.ChromeOptions to always include
low-memory flags and point to the system Chromium binary,
so no bot.py needs to be modified.
"""

try:
    from selenium.webdriver import ChromeOptions as _ChromeOptions
    from selenium.webdriver.chrome.service import Service as _Service

    _orig_options_init = _ChromeOptions.__init__

    def _patched_options_init(self, *args, **kwargs):
        _orig_options_init(self, *args, **kwargs)
        existing = self.arguments if hasattr(self, 'arguments') else []
        flags = [
            '--no-sandbox',
            '--disable-dev-shm-usage',
            '--disable-gpu',
            '--headless=new',
            '--disable-extensions',
            '--disable-background-networking',
            '--disable-sync',
            '--disable-translate',
            '--no-first-run',
            '--mute-audio',
            '--js-flags=--max-old-space-size=128',
        ]
        for flag in flags:
            if flag not in existing:
                self.add_argument(flag)
        # Point to system Chromium binary
        self.binary_location = '/usr/bin/chromium'

    _ChromeOptions.__init__ = _patched_options_init

    # Patch Service so it uses the system chromedriver by default
    _orig_service_init = _Service.__init__

    def _patched_service_init(self, executable_path=None, *args, **kwargs):
        if executable_path is None:
            executable_path = '/usr/bin/chromedriver'
        _orig_service_init(self, executable_path, *args, **kwargs)

    _Service.__init__ = _patched_service_init

except ImportError:
    pass  # selenium not installed in this bot — no-op
