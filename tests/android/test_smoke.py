"""Tests of the parts of smoke.py that need no device: python3 -m unittest discover -s tests/android (ADR 0066)."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import smoke

# Shape of a real `uiautomator dump` of the app (Avalonia's accessibility nodes under the activity's action_bar_root).
DUMP = """<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
<hierarchy rotation="0">
  <node class="android.widget.FrameLayout" bounds="[0,0][1080,2400]" resource-id="">
    <node class="android.widget.FrameLayout" bounds="[0,63][1080,2337]" resource-id="android:id/action_bar_root">
      <node class="Avalonia.Android.Automation.AvaloniaButton" text="Create a new wallet" bounds="[48,500][1032,640]"/>
      <node class="Avalonia.Android.Automation.AvaloniaCheckBox" text="Encrypt the wallet file" checked="true" bounds="[48,700][900,760]"/>
      <node class="Avalonia.Android.Automation.AvaloniaTextBox" text="abandon about" bounds="[48,800][1032,900]"/>
      <node class="android.view.View" text="no box" bounds="garbage"/>
    </node>
  </node>
</hierarchy>"""


class ParseDump(unittest.TestCase):
    def test_reads_text_class_checked_and_box(self):
        nodes, top = smoke.parse_dump(DUMP)
        self.assertEqual(63, top)
        btn = next(n for n in nodes if n["text"] == "Create a new wallet")
        self.assertEqual((48, 500, 1032, 640), btn["box"])
        self.assertTrue(btn["cls"].endswith("Button"))
        self.assertTrue(next(n for n in nodes if n["cls"].endswith("CheckBox"))["checked"])

    def test_skips_nodes_without_bounds(self):
        nodes, _ = smoke.parse_dump(DUMP)
        self.assertFalse(any(n["text"] == "no box" for n in nodes))

    def test_not_a_dump_is_empty(self):
        self.assertEqual(([], None), smoke.parse_dump("ERROR: null root node returned by UiTestAutomationBridge."))


class FindNodes(unittest.TestCase):
    def setUp(self):
        self.nodes, _ = smoke.parse_dump(DUMP)

    def test_exact_beats_loose_and_class_filters(self):
        d = smoke.Device("x")
        self.assertEqual("Create a new wallet", d.find("Create a new wallet", "Button", self.nodes)["text"])
        self.assertIsNone(d.find("Create a new wallet", "CheckBox", self.nodes))
        self.assertEqual("Encrypt the wallet file", d.find("encrypt", None, self.nodes)["text"])
        self.assertIsNone(d.find("missing", None, self.nodes))


class Pixels(unittest.TestCase):
    def raw(self, header, w=2, h=2):
        px = bytes([10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255])
        head = w.to_bytes(4, "little") + h.to_bytes(4, "little") + (1).to_bytes(4, "little")
        return head + bytes(header - 12) + px

    def test_both_header_sizes(self):  # 12 bytes before Android 10, 16 after
        for header in (12, 16):
            self.assertEqual((70, 80, 90), smoke.pixel_from_raw(self.raw(header), 0, 1))
            self.assertEqual((40, 50, 60), smoke.pixel_from_raw(self.raw(header), 1, 0))

    def test_luma_separates_light_from_dark(self):
        self.assertGreater(smoke.luma((245, 247, 252)), 150)
        self.assertLess(smoke.luma((14, 22, 42)), 90)


class Layout(unittest.TestCase):
    def test_overflow_is_found(self):
        nodes = [{"text": "ok", "box": (0, 0, 1080, 10)}, {"text": "wide", "box": (20, 0, 1300, 10)},
                 {"text": "left", "box": (-40, 0, 100, 10)}]
        self.assertEqual(["wide", "left"], [n["text"] for n in smoke.overflowing(nodes, 1080)])

    def test_every_language_has_labels_like_the_app(self):
        self.assertEqual({"it", "en", "es", "fr", "pt", "de", "zh"}, set(smoke.LANGS))
        self.assertTrue(all(len(v) == 5 and all(v) for v in smoke.LANGS.values()))


if __name__ == "__main__":
    unittest.main()
