# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
# PPM(P6) -> PNG converter using only the stdlib (zlib + struct). E2E diagnostics.
import struct, sys, zlib

def read_ppm(path):
    data = open(path, 'rb').read()
    # tokenize header: P6, w, h, maxval (comments with # allowed)
    tokens, i = [], 0
    while len(tokens) < 4:
        while i < len(data) and data[i:i+1].isspace():
            i += 1
        if data[i:i+1] == b'#':
            while i < len(data) and data[i] not in b'\r\n':
                i += 1
            continue
        j = i
        while j < len(data) and not data[j:j+1].isspace():
            j += 1
        tokens.append(data[i:j]); i = j
    i += 1  # single whitespace after maxval
    w, h = int(tokens[1]), int(tokens[2])
    raster = data[i:i + w*h*3]
    if len(raster) < w*h*3:
        raise SystemExit(f"truncated: have {len(raster)} need {w*h*3}")
    return w, h, raster

def png_chunk(tag, payload):
    return (struct.pack('>I', len(payload)) + tag + payload
            + struct.pack('>I', zlib.crc32(tag + payload) & 0xffffffff))

def write_png(w, h, raster, out):
    raw = b''.join(b'\x00' + raster[y*w*3:(y+1)*w*3] for y in range(h))
    png = (b'\x89PNG\r\n\x1a\n'
           + png_chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
           + png_chunk(b'IDAT', zlib.compress(raw, 6))
           + png_chunk(b'IEND', b''))
    open(out, 'wb').write(png)

w, h, r = read_ppm(sys.argv[1])
write_png(w, h, r, sys.argv[2])
print(f"{sys.argv[1]} {w}x{h} -> {sys.argv[2]}")
