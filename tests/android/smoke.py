#!/usr/bin/env python3
"""
Android smoke test (ADR 0066): installs the debug APK on an emulator, starts it, walks the start screen, the restore
wizard and the wallet home, and checks that the app follows the system's light/dark mode while it runs.

It drives the app through the accessibility tree (`uiautomator dump`, which Avalonia exposes: texts, classes, bounds),
so it taps buttons by their label and reads text boxes back instead of guessing coordinates or trusting `input text`
(which drops characters when it types fast).

  python3 tests/android/smoke.py [--apk FILE] [--avd NAME] [--serial ID] [--out DIR] [--keep] [--reset-data] [--langs de,fr]

* With no device attached it creates a throw-away AVD (default `plm-smoke`, an x86_64 Android 14 image with KVM),
  boots it headless, and deletes the AVD when it is done (`--keep` keeps both).
* On a device that was already there (an emulator or a phone) it only installs, starts and checks that the process
  stays up. The full walk-through creates a wallet and changes the system theme, so it needs a device this script
  booted itself, or `--reset-data` (which clears the app's data and then changes the theme setting). It never
  uninstalls anything: a signature mismatch is reported, not "fixed".
* Screenshots and the last UI dump go to --out (default /tmp/plm-android-smoke). Exit code 0 = every check passed.

Needs: the Android SDK (ANDROID_HOME or ~/android-sdk), an APK built with
  dotnet build src/App.Android -c Debug -t:SignAndroidPackage -p:AndroidSdkDirectory=$HOME/android-sdk
Nothing here runs in CI (the Android head is deliberately not built there).
"""
import argparse
import glob
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

PKG = "io.github.davide3011.palladiumwallet"
MNEMONIC = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"

SDK = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or os.path.expanduser("~/android-sdk")
ADB = os.path.join(SDK, "platform-tools", "adb")
EMULATOR = os.path.join(SDK, "emulator", "emulator")
AVDMANAGER = os.path.join(SDK, "cmdline-tools", "latest", "bin", "avdmanager")
# code -> (name on the radio button, Receive tab, Copy button, Settings button, Close button) as in Localization/Loc.cs
LANGS = {"it": ("Italiano", "Ricevi", "Copia", "Impostazioni", "Chiudi"), "en": ("English", "Receive", "Copy", "Settings", "Close"),
         "es": ("Español", "Recibir", "Copiar", "Configuración", "Cerrar"), "fr": ("Français", "Recevoir", "Copier", "Paramètres", "Fermer"),
         "pt": ("Português", "Receber", "Copiar", "Configurações", "Fechar"),
         "de": ("Deutsch", "Empfangen", "Kopieren", "Einstellungen", "Schließen"), "zh": ("中文", "接收", "复制", "设置", "关闭")}
IMAGE = "system-images;android-34;google_apis;x86_64"


class Fail(Exception):
    pass


class Device:
    def __init__(self, serial=None):
        self.serial = serial
        self.top = 0  # y of the Avalonia root view: the accessibility bounds are relative to it

    def adb(self, *args, check=True, binary=False, timeout=60):
        cmd = [ADB] + (["-s", self.serial] if self.serial else []) + list(args)
        r = subprocess.run(cmd, capture_output=True, timeout=timeout)
        if check and r.returncode != 0:
            raise Fail(f"adb {' '.join(args)} failed: {r.stderr.decode(errors='replace').strip()}")
        return r.stdout if binary else r.stdout.decode(errors="replace").replace("\r", "")

    def sh(self, command, **kw):
        return self.adb("shell", command, **kw)

    # ---- accessibility tree ----
    def dump(self):
        self.sh("uiautomator dump /sdcard/plm-ui.xml", check=False)
        xml = self.sh("cat /sdcard/plm-ui.xml", check=False)
        nodes, top = parse_dump(xml)
        if top is not None:
            self.top = top
        return nodes

    def find(self, text, cls=None, nodes=None):
        nodes = nodes if nodes is not None else self.dump()
        exact = [n for n in nodes if n["text"] == text and (cls is None or n["cls"].endswith(cls))]
        if exact:
            return exact[0]
        loose = [n for n in nodes if text.lower() in n["text"].lower() and (cls is None or n["cls"].endswith(cls))]
        return loose[0] if loose else None

    def wait_for(self, text, cls=None, timeout=30):
        end = time.time() + timeout
        while time.time() < end:
            n = self.find(text, cls)
            if n:
                return n
            time.sleep(1)
        raise Fail(f"'{text}' did not appear within {timeout}s")

    def tap_node(self, n):
        x1, y1, x2, y2 = n["box"]
        self.sh(f"input tap {(x1 + x2) // 2} {(y1 + y2) // 2 + self.top}")

    def tap_any(self, texts, timeout=20):
        """Taps the first label that is on screen: the same button has a different label in each language."""
        end = time.time() + timeout
        while time.time() < end:
            nodes = self.dump()
            for t in texts:
                n = self.find(t, "Button", nodes)
                if n:
                    self.tap_node(n)
                    time.sleep(1.0)
                    return n
            time.sleep(1)
        raise Fail(f"none of {texts} appeared within {timeout}s")

    def tap(self, text, cls=None, timeout=20):
        n = self.wait_for(text, cls, timeout)
        self.tap_node(n)
        time.sleep(1.0)
        return n

    def keyboard_shown(self):
        return "mInputShown=true" in self.sh("dumpsys input_method", check=False)

    def hide_keyboard(self):
        if self.keyboard_shown():
            self.sh("input keyevent 4")  # back closes the keyboard, not the app, while it is shown
            time.sleep(0.8)

    def type_into_first_textbox(self, text):
        """Types word by word and reads the box back: `input text` loses characters when it is fast."""
        for attempt, pause in enumerate((0.25, 0.6, 1.2)):
            box = next((n for n in self.dump() if n["cls"].endswith("TextBox")), None)
            if not box:
                raise Fail("no text box on the screen")
            self.tap_node(box)
            time.sleep(0.8)
            self.sh("input keyevent KEYCODE_MOVE_END")
            self.sh("input keyevent --longpress " + " ".join(["KEYCODE_DEL"] * 60), check=False)  # clear what is there
            for i, word in enumerate(text.split(" ")):
                self.sh(f"input text {word}{'%s' if i < len(text.split(' ')) - 1 else ''}")
                time.sleep(pause)
            box = next((n for n in self.dump() if n["cls"].endswith("TextBox")), None)
            if box and box["text"] == text:
                return
        raise Fail("the text did not arrive intact after three attempts")

    # ---- screen ----
    def screenshot(self, path):
        with open(path, "wb") as f:
            f.write(self.adb("exec-out", "screencap", "-p", binary=True))

    def pixel(self, x, y):
        return pixel_from_raw(self.adb("exec-out", "screencap", binary=True), x, y)

    def screen_size(self):
        m = re.search(r"(\d+)x(\d+)", self.sh("wm size"))
        return int(m.group(1)), int(m.group(2))


def parse_dump(xml):
    """Parses a `uiautomator dump`: (nodes, y of the Avalonia root view or None). Not a dump -> ([], None)."""
    if not xml.startswith("<?xml"):
        return [], None
    top = None
    nodes = []
    for n in ET.fromstring(xml).iter("node"):
        b = re.findall(r"\d+", n.get("bounds", ""))
        if len(b) != 4:
            continue
        x1, y1, x2, y2 = map(int, b)
        if n.get("resource-id", "").endswith(":id/action_bar_root"):
            top = y1
        nodes.append({"text": n.get("text", ""), "cls": n.get("class", ""), "desc": n.get("content-desc", ""),
                      "checked": n.get("checked") == "true", "box": (x1, y1, x2, y2)})
    return nodes, top


def pixel_from_raw(raw, x, y):
    """RGB of a pixel in `screencap` raw output: width, height, format, [16-byte header from Android 10], RGBA rows."""
    w, h, _fmt = (int.from_bytes(raw[i:i + 4], "little") for i in (0, 4, 8))
    header = len(raw) - w * h * 4  # 12 bytes before Android 10, 16 after
    o = header + (y * w + x) * 4
    return raw[o], raw[o + 1], raw[o + 2]


def overflowing(nodes, width):
    """Nodes that stick out of the screen sideways: text that is too long for its box shows up like this."""
    return [n for n in nodes if n["box"][2] > width + 1 or n["box"][0] < -1]


def luma(rgb):
    return 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]


def ensure_device(args):
    """Returns (device, booted_by_us, emulator_process)."""
    out = subprocess.run([ADB, "devices"], capture_output=True, text=True).stdout
    attached = [l.split()[0] for l in out.splitlines()[1:] if l.strip().endswith("device")]
    if args.serial:
        return Device(args.serial), False, None
    if len(attached) == 1:
        return Device(attached[0]), False, None
    if len(attached) > 1:
        raise Fail(f"several devices attached ({', '.join(attached)}): pick one with --serial")

    avds = subprocess.run([EMULATOR, "-list-avds"], capture_output=True, text=True).stdout.split()
    created = args.avd not in avds
    if created:
        print(f"creating the AVD {args.avd} ({IMAGE})")
        subprocess.run([AVDMANAGER, "create", "avd", "-n", args.avd, "-k", IMAGE, "-d", "pixel"],
                       input="no\n", text=True, check=True, capture_output=True)
    print(f"booting {args.avd} headless…")
    proc = subprocess.Popen([EMULATOR, "-avd", args.avd, "-no-window", "-gpu", "swiftshader_indirect", "-no-snapshot",
                             "-no-audio", "-no-boot-anim"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    dev = Device()
    end = time.time() + 360
    while time.time() < end:
        r = subprocess.run([ADB, "shell", "getprop", "sys.boot_completed"], capture_output=True, text=True)
        if r.stdout.strip() == "1":
            time.sleep(3)
            return dev, True, (proc, created)
        time.sleep(3)
    proc.kill()
    raise Fail("the emulator did not boot within 6 minutes")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apk")
    ap.add_argument("--avd", default="plm-smoke")
    ap.add_argument("--serial")
    ap.add_argument("--out", default="/tmp/plm-android-smoke")
    ap.add_argument("--keep", action="store_true", help="leave the emulator running and keep a created AVD")
    ap.add_argument("--langs", default="", help="comma-separated language codes (de,fr,zh…) to switch to after the walk-through, "
                    "checking Receive and Settings for text that overflows the screen; use with a full walk-through")
    ap.add_argument("--reset-data", action="store_true", help="on an existing device: clear the app's data and run the full walk-through")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    apk = args.apk
    if not apk:
        found = sorted(glob.glob(os.path.join(os.path.dirname(__file__), "..", "..", "src", "App.Android", "bin", "Debug",
                                              "net10.0-android", "*-Signed.apk")), key=os.path.getmtime)
        if not found:
            print("no APK: build it first (see the header of this file)")
            return 2
        apk = found[-1]

    failures = []
    dev = None
    emu = None
    created_avd = False
    original_night = None

    def check(name, fn):
        try:
            fn()
            print(f"  ok    {name}")
        except Fail as e:
            print(f"  FAIL  {name}: {e}")
            failures.append(name)
            raise

    try:
        dev, booted, emu_info = ensure_device(args)
        if emu_info:
            emu, created_avd = emu_info
        full = booted or args.reset_data
        print(f"device: {dev.serial or 'default'}; apk: {os.path.basename(apk)}; "
              f"{'full walk-through' if full else 'start-up check only (use --reset-data for the full one)'}")

        def install():
            r = dev.adb("install", "-r", apk, check=False, timeout=300)
            if "Success" not in r:
                if "UPDATE_INCOMPATIBLE" in r:
                    raise Fail("a build signed with another key is installed; this script never uninstalls (use a throw-away AVD)")
                raise Fail(r.strip()[-200:])
        check("the APK installs", install)

        if full:
            dev.sh(f"pm clear {PKG}", check=False)
        dev.sh("logcat -c", check=False)
        dev.sh(f"monkey -p {PKG} -c android.intent.category.LAUNCHER 1", check=False)

        def start():
            dev.wait_for("Palladium Wallet", timeout=60)
            time.sleep(8)
            if not dev.sh(f"pidof {PKG}").strip():
                raise Fail("the process is gone")
            crash = [l for l in dev.sh("logcat -d", check=False).splitlines() if "FATAL EXCEPTION" in l and PKG in l]
            if crash:
                raise Fail("fatal exception in the log: " + crash[0][:160])
        check("the app starts and stays up (no crash in 8 s)", start)
        dev.screenshot(os.path.join(args.out, "01-start.png"))

        def start_screen():
            if not dev.find("Create a new wallet", "Button"):
                raise Fail("the start screen has no 'Create a new wallet' button")
        check("the start screen shows its actions", start_screen)

        if not full:
            return 0 if not failures else 1

        # ---- restore wizard ----
        def wizard():
            dev.tap("Restore from seed", "Button")
            dev.wait_for("Step 1 of 4")
            dev.type_into_first_textbox(MNEMONIC)
            dev.hide_keyboard()
            dev.screenshot(os.path.join(args.out, "02-words.png"))
            for step in (2, 3, 4):
                dev.tap("Next", "Button")
                dev.wait_for(f"Step {step} of 4", timeout=15)
        check("the restore wizard walks four steps with the step counter", wizard)

        def create():
            enc = dev.find("Encrypt the wallet file", "CheckBox")
            if enc and enc["checked"]:
                dev.tap_node(enc)
                time.sleep(1)
            dev.tap("Create wallet", "Button")
            dev.wait_for("Balance", timeout=40)
        check("an unencrypted wallet is created and the home appears", create)
        dev.screenshot(os.path.join(args.out, "03-home.png"))

        def receive():
            dev.tap("Receive")
            # The button's content is a panel with two labels, so its label is the TextBlock inside it.
            copy = dev.wait_for("Copy", timeout=15)
            _w, h = dev.screen_size()
            bottom = copy["box"][3] + dev.top
            if bottom > h - 120:
                raise Fail(f"the Copy button ends at {bottom}px of {h}px: it is under the status bar")
        check("Receive shows the QR and the Copy button above the status bar", receive)
        dev.screenshot(os.path.join(args.out, "04-receive.png"))

        def settings():
            dev.tap("Settings", "Button")
            dev.wait_for("Solid surfaces", timeout=15)
            dev.screenshot(os.path.join(args.out, "05-settings.png"))
            dev.tap("Close", "Button")
        check("Settings opens with the theme and solid-surfaces options, and closes", settings)

        # ---- other languages: longer strings must not push anything off the screen ----
        for code in [c for c in args.langs.split(",") if c]:
            if code not in LANGS:
                raise Fail(f"--langs: unknown language '{code}' (known: {', '.join(LANGS)})")
            name, receive, copy, _settings, close = LANGS[code]

            def language(code=code, name=name, receive=receive, copy=copy, close=close):
                width, height = dev.screen_size()
                for attempt in (1, 2):  # a tap right after a tab change is sometimes lost
                    dev.tap_any([v[3] for v in LANGS.values()])
                    try:
                        dev.wait_for(name, "RadioButton", timeout=6)
                        break
                    except Fail:
                        if attempt == 2:
                            raise
                dev.tap(name, "RadioButton", timeout=15)
                dev.screenshot(os.path.join(args.out, f"07-settings-{code}.png"))
                bad = overflowing(dev.dump(), width)
                if bad:
                    raise Fail(f"Settings sticks out of the screen: {[n['text'] for n in bad][:3]}")
                dev.tap(close, "Button")  # by label: the Copy button of Receive is also in the tree, behind the overlay
                end = time.time() + 10
                while dev.find(name, "RadioButton") and time.time() < end:
                    time.sleep(0.5)
                if dev.find(name, "RadioButton"):
                    raise Fail("the Settings overlay did not close")
                dev.tap(receive)
                c = dev.wait_for(copy, timeout=15)
                if c["box"][3] + dev.top > height - 120:
                    raise Fail("the Copy button is under the status bar")
                bad = overflowing(dev.dump(), width)
                if bad:
                    raise Fail(f"Receive sticks out of the screen: {[n['text'] for n in bad][:3]}")
                dev.screenshot(os.path.join(args.out, f"08-receive-{code}.png"))
            check(f"{name}: Settings and Receive fit the screen", language)

        # ---- follows the system's light/dark mode while running ----
        original_night = dev.sh("cmd uimode night", check=False).strip().split(":")[-1].strip() or "auto"

        def theme():
            readings = {}
            for mode in ("no", "yes", "no"):
                dev.sh(f"cmd uimode night {mode}")
                time.sleep(3)
                readings.setdefault(mode, []).append(luma(dev.pixel(30, 300)))
            if not (readings["no"][0] > 150 and readings["yes"][0] < 90 and readings["no"][1] > 150):
                raise Fail(f"the window did not follow the system mode: light={readings['no']} dark={readings['yes']}")
            if not dev.sh(f"pidof {PKG}").strip():
                raise Fail("the app died when the mode changed")
        check("the System theme follows the OS light/dark switch while the app runs", theme)
        dev.sh("cmd uimode night yes")
        time.sleep(3)
        dev.screenshot(os.path.join(args.out, "06-dark.png"))
    except Fail as e:
        print(f"stopped: {e}")
        if dev:
            try:
                dev.screenshot(os.path.join(args.out, "failure.png"))
                with open(os.path.join(args.out, "failure-ui.txt"), "w") as f:
                    f.write("\n".join(f"{n['cls']:30} {n['text']!r:40} {n['box']}" for n in dev.dump()))
            except Exception:
                pass
        failures.append("stopped early")
    finally:
        if dev and original_night is not None:
            dev.sh(f"cmd uimode night {original_night}", check=False)
        if emu is not None and not args.keep:
            subprocess.run([ADB, "emu", "kill"], capture_output=True)
            try:
                emu.wait(timeout=30)
            except Exception:
                emu.kill()
            if created_avd:
                subprocess.run([AVDMANAGER, "delete", "avd", "-n", args.avd], capture_output=True)

    print("android smoke:", "all checks passed" if not failures else f"FAILED ({', '.join(failures)})")
    print(f"screenshots: {args.out}")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
