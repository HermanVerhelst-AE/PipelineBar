# Zet een vierkante PNG om naar een Windows-.ico met PNG-lagen. Gebruik: python3 MakeIco.py in.png uit.ico
# De PNG komt van ../macOS/MakeIcon.swift.
# Snijdt de macOS-marge (100 px rond 824 px inhoud) weg en schaalt met sips.
import os, struct, subprocess, sys, tempfile

src, out = sys.argv[1], sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
tmp = tempfile.mkdtemp()
tile = os.path.join(tmp, "tile.png")
subprocess.run(["sips", "-c", "824", "824", src, "--out", tile], check=True, capture_output=True)

images = []
for s in sizes:
    p = os.path.join(tmp, f"{s}.png")
    subprocess.run(["sips", "-z", str(s), str(s), tile, "--out", p], check=True, capture_output=True)
    images.append((s, open(p, "rb").read()))

header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for s, data in images:
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
    blobs += data
open(out, "wb").write(header + entries + blobs)
