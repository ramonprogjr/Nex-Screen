"""Checks that the EDID in src/Driver.cpp matches tools/gen_edid.py and is a valid EDID 1.4 base block.

Run: python -m unittest discover driver/virtual-display/tests
"""

import pathlib
import re
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import gen_edid  # noqa: E402


def edid_from_driver() -> bytes:
    source = (ROOT / "src" / "Driver.cpp").read_text(encoding="utf-8")
    match = re.search(r"do not edit by hand\.\s*\{\s*\{(.*?)\}", source, re.S)
    if not match:
        raise AssertionError("EDID array not found in Driver.cpp")
    return bytes(int(h, 16) for h in re.findall(r"0x([0-9A-Fa-f]{2})", match.group(1)))


class EdidTest(unittest.TestCase):
    def setUp(self):
        self.edid = gen_edid.build_edid()

    def test_driver_matches_generator(self):
        self.assertEqual(edid_from_driver(), self.edid)

    def test_header_and_checksum(self):
        self.assertEqual(len(self.edid), 128)
        self.assertEqual(self.edid[:8], b"\x00\xFF\xFF\xFF\xFF\xFF\xFF\x00")
        self.assertEqual(sum(self.edid) % 256, 0)

    def test_identity(self):
        value = int.from_bytes(self.edid[8:10], "big")
        pnp = "".join(chr(((value >> shift) & 0x1F) + ord("A") - 1) for shift in (10, 5, 0))
        self.assertEqual(pnp, "NXS")
        self.assertIn(b"\x00\x00\x00\xFC\x00NexScreen\n", self.edid)
        self.assertIn(b"\x00\x00\x00\xFF\x00NXS0001\n", self.edid)

    def test_preferred_timing_is_1920x1200_60(self):
        dtd = self.edid[54:72]
        clock_hz = int.from_bytes(dtd[0:2], "little") * 10_000
        h_active = dtd[2] | ((dtd[4] >> 4) << 8)
        h_blank = dtd[3] | ((dtd[4] & 0xF) << 8)
        v_active = dtd[5] | ((dtd[7] >> 4) << 8)
        v_blank = dtd[6] | ((dtd[7] & 0xF) << 8)
        self.assertEqual((h_active, v_active), (1920, 1200))
        refresh = clock_hz / ((h_active + h_blank) * (v_active + v_blank))
        self.assertAlmostEqual(refresh, 60, delta=0.1)


if __name__ == "__main__":
    unittest.main()
