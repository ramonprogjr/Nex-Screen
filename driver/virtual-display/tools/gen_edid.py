#!/usr/bin/env python3
"""Generate the NexScreen EDID 1.4 base block (128 bytes) and print it as a C array.

Usage:
    python gen_edid.py            # prints the C initializer for Driver.cpp
    python gen_edid.py --bin out  # also writes the raw 128-byte block to 'out'

The output must match the array in src/Driver.cpp (s_SampleMonitors). Regenerate and paste it after changing any
parameter below.
"""

import argparse
import sys

# --- Parameters -------------------------------------------------------------------------------------------------

MANUFACTURER = "NXS"          # 3-letter PNP id
PRODUCT_CODE = 0x0001
SERIAL_NUMBER = 1             # numeric serial in the header
SERIAL_STRING = "NXS0001"     # descriptor 0xFF
MONITOR_NAME = "NexScreen"    # descriptor 0xFC (max 13 chars)
WEEK = 0                      # 0 = unspecified
YEAR = 2026
WIDTH_MM, HEIGHT_MM = 237, 148

# Preferred timing: 1728x1080@60 CVT reduced blanking, 125.75 MHz. 1728x1080 is the largest 16:10 mode the
# Tab A9+ hardware H.264 decoder accepts (max 1920x1088), so the companion streams it without rescaling.
DTD = dict(
    pixel_clock_khz=125_750,
    h_active=1728, h_blank=160, h_front=48, h_sync=32, h_sync_positive=True,
    v_active=1080, v_blank=31, v_front=3, v_sync=6, v_sync_positive=False,
)

# Range limits (descriptor 0xFD), covering the 30 and 60 Hz modes the driver exposes
V_MIN_HZ, V_MAX_HZ = 30, 60
H_MIN_KHZ, H_MAX_KHZ = 20, 75
MAX_PIXEL_CLOCK_MHZ = 160     # stored in 10 MHz units

# sRGB primaries and D65 white point
CHROMATICITY = dict(rx=0.640, ry=0.330, gx=0.300, gy=0.600, bx=0.150, by=0.060, wx=0.3127, wy=0.3290)


# --- Encoding ---------------------------------------------------------------------------------------------------

def manufacturer_id(code: str) -> bytes:
    assert len(code) == 3 and code.isalpha() and code.isupper()
    a, b, c = (ord(ch) - ord("A") + 1 for ch in code)
    value = (a << 10) | (b << 5) | c
    return value.to_bytes(2, "big")


def chromaticity(c: dict) -> bytes:
    def ten_bits(v: float) -> int:
        return int(round(v * 1024))

    vals = [ten_bits(c[k]) for k in ("rx", "ry", "gx", "gy", "bx", "by", "wx", "wy")]
    lo = [v & 0x3 for v in vals]
    hi = [v >> 2 for v in vals]
    byte25 = (lo[0] << 6) | (lo[1] << 4) | (lo[2] << 2) | lo[3]
    byte26 = (lo[4] << 6) | (lo[5] << 4) | (lo[6] << 2) | lo[7]
    return bytes([byte25, byte26] + hi)


def detailed_timing(d: dict, width_mm: int, height_mm: int) -> bytes:
    clk = d["pixel_clock_khz"] // 10
    ha, hb, hfp, hs = d["h_active"], d["h_blank"], d["h_front"], d["h_sync"]
    va, vb, vfp, vs = d["v_active"], d["v_blank"], d["v_front"], d["v_sync"]

    flags = 0x18  # digital separate sync, non-interlaced
    if d["v_sync_positive"]:
        flags |= 0x04
    if d["h_sync_positive"]:
        flags |= 0x02

    return bytes([
        clk & 0xFF, clk >> 8,
        ha & 0xFF, hb & 0xFF, ((ha >> 8) << 4) | (hb >> 8),
        va & 0xFF, vb & 0xFF, ((va >> 8) << 4) | (vb >> 8),
        hfp & 0xFF, hs & 0xFF,
        ((vfp & 0xF) << 4) | (vs & 0xF),
        ((hfp >> 8) << 6) | ((hs >> 8) << 4) | ((vfp >> 4) << 2) | (vs >> 4),
        width_mm & 0xFF, height_mm & 0xFF, ((width_mm >> 8) << 4) | (height_mm >> 8),
        0, 0,
        flags,
    ])


def text_descriptor(tag: int, text: str) -> bytes:
    assert len(text) <= 13
    body = text.encode("ascii")
    if len(body) < 13:
        body += b"\x0A" + b"\x20" * (12 - len(body))
    return bytes([0, 0, 0, tag, 0]) + body


def range_limits() -> bytes:
    return bytes([
        0, 0, 0, 0xFD, 0,
        V_MIN_HZ, V_MAX_HZ, H_MIN_KHZ, H_MAX_KHZ,
        MAX_PIXEL_CLOCK_MHZ // 10,
        0x01,  # range limits only, no timing formula
        0x0A, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
    ])


def build_edid() -> bytes:
    edid = bytearray()
    edid += b"\x00\xFF\xFF\xFF\xFF\xFF\xFF\x00"
    edid += manufacturer_id(MANUFACTURER)
    edid += PRODUCT_CODE.to_bytes(2, "little")
    edid += SERIAL_NUMBER.to_bytes(4, "little")
    edid += bytes([WEEK, YEAR - 1990])
    edid += b"\x01\x04"                                   # EDID 1.4
    edid += bytes([0xA0])                                 # digital, 8 bpc, interface undefined
    edid += bytes([round(WIDTH_MM / 10), round(HEIGHT_MM / 10)])
    edid += bytes([0x78])                                 # gamma 2.2
    edid += bytes([0x06])                                 # sRGB default, preferred timing is native, RGB 4:4:4
    edid += chromaticity(CHROMATICITY)
    edid += b"\x00\x00\x00"                               # no established timings
    edid += b"\x01\x01" * 8                               # no standard timings
    edid += detailed_timing(DTD, WIDTH_MM, HEIGHT_MM)
    edid += text_descriptor(0xFC, MONITOR_NAME)
    edid += text_descriptor(0xFF, SERIAL_STRING)
    edid += range_limits()
    edid += b"\x00"                                       # no extension blocks
    assert len(edid) == 127, len(edid)
    edid.append((-sum(edid)) & 0xFF)
    assert sum(edid) % 256 == 0
    return bytes(edid)


def as_c_array(data: bytes, per_line: int = 16, indent: str = "            ") -> str:
    lines = []
    for i in range(0, len(data), per_line):
        chunk = ",".join(f"0x{b:02X}" for b in data[i:i + per_line])
        lines.append(indent + chunk)
    return ",\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bin", metavar="PATH", help="write the raw 128-byte EDID to PATH")
    args = parser.parse_args()

    edid = build_edid()
    if args.bin:
        with open(args.bin, "wb") as f:
            f.write(edid)
    print(as_c_array(edid))
    return 0


if __name__ == "__main__":
    sys.exit(main())
