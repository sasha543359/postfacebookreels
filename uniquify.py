# -*- coding: utf-8 -*-
"""
uniquify.py — make a re-uploaded TikTok clip read as a distinct, clean FB-Reels file.

FACEBOOK REELS SPEC (enforced here, per Meta's Reels Publishing API docs):
  container mp4 / H.264 / yuv420p 4:2:0 / progressive / fixed fps
  9:16, 1080x1920 (minimum accepted: 540x960)
  24-60 fps                -> we emit 30
  3-90 seconds             -> longer clips are TRIMMED to 90s (see MAX_DURATION)
  audio AAC-LC, 48 kHz, stereo, >=128 kbps
Two of those differ from the Instagram build this script came from: Instagram
was happy with 44.1 kHz mono, Facebook asks for 48 kHz stereo. Hence
"-ar 48000 -ac 2" below -- without it Facebook still usually accepts the file,
but it re-encodes the audio itself and some clips come out with the sound
noticeably off.

The 90-second trim is not cosmetic either. A 91-second clip is rejected with
error 1363128, the video never gets marked as published, and the program retries
it every single cycle forever -- burning the 30-posts-per-day API quota on a file
that can never go out. Trimming makes it publishable.

WHAT THIS DOES (and what it does NOT):
  It defeats the CHEAP detectors: exact byte/MD5 hash, metadata correlation,
  cross-account audio-hash. Ordered by detection layer:
    Layer 1  rework audio per-account         -> pitch/EQ, or replace/mix a bed
    Layer 2  shift the picture                -> 2-3% zoom + micro-rotate, color, grain
    Layer 3  kill byte/metadata signature     -> re-encode, strip metadata, randomize params

  The zoom is deliberately SMALL. The Instagram build cut 5% off every edge and
  then zoomed (12-15%, "to move a perceptual hash"), and on Facebook that ate
  the picture: the Facebook app fills a tall phone screen with a 9:16 clip by
  trimming about 5% off each side on its own. Stacked, the two pushed meme text
  off the screen -- the caption line "Cuando encontraste snacks" lost its first
  and last letters (reported from a live page, 25.09.2026; measured 1.22x
  magnification against TikTok = 1.11 ours x 1.10 Facebook's). The edge
  trim was there to cut the TikTok watermark, but yt-dlp downloads the
  unwatermarked copy, so there was nothing to cut.

  It does NOT beat Instagram's account-level "aggregator/unoriginal" classifier
  (the thing behind a flat 300-400 view ceiling). That is decided at ACCOUNT level
  over a rolling 30-day window; cosmetic edits do not clear it. To clear it, add
  MATERIAL transformation (a new joke via on-screen text / voiceover / recut) and
  keep each account's 30-day mix majority transformed. Use --caption to bake an
  original on-screen line. See docs/STRATEGY.md. Uniquify = hygiene, not magic.

Usage:
  python uniquify.py in.mp4 out.mp4 --profile acc1
  python uniquify.py in.mp4 out.mp4 --profile acc1 --mode reframe --caption "когда понял"
  python uniquify.py in.mp4 out.mp4 --profile acc2 --audio replace --audio-file bed.mp3
Requires ffmpeg + ffprobe in PATH.
"""
import argparse, hashlib, json, math, os, random, subprocess, sys

# Per-account signature. Everything stays subtle, zoom included: 2-3% keeps a
# clip's edge text on screen after Facebook's own ~5%-per-side phone crop (see
# the note above). Don't push it back toward 10%+.
PROFILES = {
    "default": dict(speed=1.00, hue=0,  sat=1.00, bright=0.00, contrast=1.00, pitch=1.00, zoom=1.025),
    "acc1":    dict(speed=1.03, hue=4,  sat=1.05, bright=0.02, contrast=1.03, pitch=1.02, zoom=1.03),
    "acc2":    dict(speed=0.97, hue=-5, sat=0.97, bright=-0.02,contrast=1.02, pitch=0.98, zoom=1.02),
    "acc3":    dict(speed=1.02, hue=7,  sat=1.03, bright=0.01, contrast=0.98, pitch=1.03, zoom=1.03),
    "acc4":    dict(speed=0.98, hue=-3, sat=1.02, bright=-0.01,contrast=1.04, pitch=0.97, zoom=1.025),
    "acc5":    dict(speed=1.01, hue=-7, sat=1.04, bright=0.015,contrast=1.01, pitch=1.01, zoom=1.03),
    "acc6":    dict(speed=0.99, hue=6,  sat=0.98, bright=-0.015,contrast=1.03,pitch=0.99, zoom=1.02),
    "acc7":    dict(speed=1.04, hue=-2, sat=1.06, bright=0.025,contrast=0.99, pitch=1.04, zoom=1.03),
    "acc8":    dict(speed=0.96, hue=9,  sat=0.96, bright=-0.025,contrast=1.05,pitch=0.96, zoom=1.025),
    "acc9":    dict(speed=1.02, hue=-9, sat=1.03, bright=0.01, contrast=1.02, pitch=1.03, zoom=1.03),
    "acc10":   dict(speed=0.98, hue=3,  sat=1.01, bright=-0.01,contrast=0.98, pitch=0.98, zoom=1.02),
}
W, H = 1080, 1920

# Micro-rotation ceiling. The rotated frame has to cover the whole output, or
# its corners show up black -- the old graph did exactly that. How much a tilt
# can get away with depends on the zoom (see fit_rotation): at 1.02 the limit
# is about 0.48 degrees, at 1.03 about 0.8.
MAX_ROTATION_DEG = 0.5

# Facebook Reels: 3-90 seconds. Anything longer is trimmed rather than skipped.
MAX_DURATION = 90.0
MIN_DURATION = 3.0
# Trim target sits half a second under the ceiling: ffmpeg lands on the nearest
# frame/packet boundary rather than the exact mark, and overshooting 90.0 by a
# few hundredths is enough for Facebook to reject the clip (error 1363128).
TRIM_TARGET = 89.5


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        sys.stderr.write((p.stderr or "")[-2500:] + "\n")
        raise SystemExit(f"ffmpeg failed ({p.returncode})")
    return p


def probe(path):
    p = subprocess.run(["ffprobe", "-v", "error", "-print_format", "json",
                        "-show_format", "-show_streams", path],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    d = json.loads(p.stdout or "{}")
    v = next((s for s in d.get("streams", []) if s.get("codec_type") == "video"), {})
    a = next((s for s in d.get("streams", []) if s.get("codec_type") == "audio"), None)
    return dict(dur=float(d.get("format", {}).get("duration", 0) or 0),
                w=int(v.get("width", 0) or 0), h=int(v.get("height", 0) or 0),
                has_audio=a is not None,
                # Needed by the pitch trick below, which works by lying about the
                # sample rate. It has to lie relative to the REAL rate: TikTok
                # serves both 44.1 and 48 kHz, and a hardcoded 44100 against a
                # 48 kHz source plays the audio 8% slow -- video and sound drift
                # apart over the clip, which reads as a bad re-upload.
                ar=int((a or {}).get("sample_rate", 0) or 0) or 44100)


def esc(s):
    return s.replace("\\", "\\\\").replace(":", "\\:").replace("'", "’").replace("%", "\\%")


def jit(base, amt, rng):
    return base + rng.uniform(-amt, amt)


def even(x):
    return int(round(x / 2)) * 2


def fit_rotation(zw, zh, deg, margin=3):
    """Largest tilt, up to `deg`, at which a zw x zh frame still covers the whole
    W x H output once rotated -- so no black corner ever reaches the picture.
    `margin` pixels of slack absorb the soft edge rotate's interpolation leaves."""
    def covers(d):
        a = math.radians(abs(d))
        return (W / 2 * math.cos(a) + H / 2 * math.sin(a) <= zw / 2 - margin and
                W / 2 * math.sin(a) + H / 2 * math.cos(a) <= zh / 2 - margin)
    deg = math.trunc(deg * 100) / 100   # toward zero: rounding up could cross the limit
    while abs(deg) > 0.01 and not covers(deg):
        deg = math.trunc(deg * 90) / 100
    return deg if covers(deg) else 0.0


def zoom_rotate(zoom, rot):
    """Filter chain: zoom the W x H frame by `zoom`, tilt it by `rot` degrees
    inside that bigger canvas, cut the centre W x H back out. The corners that
    rotate leaves black lie outside the cut (fit_rotation guarantees it) -- the
    old chain rotated at 1:1 and let them into the corners of the picture."""
    zw, zh = even(W * zoom), even(H * zoom)
    chain = f"scale={zw}:{zh}"
    if rot:
        chain += f",rotate={rot}*PI/180:ow=iw:oh=ih:c=black"
    return chain + f",crop={W}:{H}"


def video_graph(prof, mode, caption, rng, font=""):
    """Return (graph_body, speed, chosen) where graph consumes [0:v] and yields [v]."""
    speed    = round(jit(prof["speed"], 0.012, rng), 3)
    hue      = round(jit(prof["hue"], 2, rng), 1)
    sat      = round(jit(prof["sat"], 0.02, rng), 3)
    bright   = round(jit(prof["bright"], 0.012, rng), 3)
    contrast = round(jit(prof["contrast"], 0.015, rng), 3)
    zoom     = round(jit(prof["zoom"], 0.004, rng), 3)
    rot      = fit_rotation(even(W * zoom), even(H * zoom),
                            rng.uniform(-MAX_ROTATION_DEG, MAX_ROTATION_DEG))

    # Speed and frame rate go FIRST, not last. The output is 30 fps either way,
    # but TikTok serves plenty of 60 fps clips, and with fps=30 at the end
    # every filter below chewed through twice the frames only for half of them
    # to be dropped. On the 1-vCPU droplet that decided whether a clip made the
    # timeout (16.7 s at 1080p60: 252 s against a 180 s limit, 18.09.2026).
    head = f"setpts={round(1/speed,4)}*PTS,fps=30"

    tail = (f"eq=brightness={bright}:contrast={contrast}:saturation={sat},hue=h={hue},"
            f"noise=alls=4:allf=t+u")
    if caption:
        ff = f"fontfile='{esc(font)}':" if font else ""
        tail += (f",drawtext={ff}text='{esc(caption)}':fontcolor=white:fontsize=64:borderw=4:"
                 f"bordercolor=black@0.9:x=(w-text_w)/2:y=h*0.11:line_spacing=8")
    # Square pixels, stated outright. The scale steps round to even sizes and
    # carry the rounding into the sample aspect ratio; a clip flagged as
    # 1079:1080 is a clip some player somewhere stretches.
    tail += ",setsar=1"

    fill = f"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H}"
    if mode == "reframe":
        # zoom-crop the core, composite onto a blurred scaled copy -> fresh border pixels
        body = (f"[0:v]{head},{fill},split=2[bg][fg];"
                f"[bg]scale={W}:{H},boxblur=28:2,eq=brightness=-0.06[bgb];"
                f"[fg]crop=iw*0.80:ih*0.80:iw*0.10:ih*0.10,scale=iw*0.86:-2[fgs];"
                f"[bgb][fgs]overlay=(W-w)/2:(H-h)/2,{zoom_rotate(zoom, rot)},{tail}[v]")
    else:  # crop: the whole frame, a hair bigger and tilted (see PROFILES)
        body = f"[0:v]{head},{fill},{zoom_rotate(zoom, rot)},{tail}[v]"
    chosen = dict(speed=speed, hue=hue, sat=sat, bright=bright, contrast=contrast,
                  zoom=zoom, rot=rot, mode=mode, caption=bool(caption))
    return body, speed, chosen


def audio_graph(prof, meta, speed, audio, has_bed, rng):
    """Return (graph_body_or_None, produces_audio_bool). Consumes [0:a]/[1:a], yields [a]."""
    pitch = round(jit(prof["pitch"], 0.012, rng), 3)
    src_audio = meta["has_audio"]
    src_ar = meta["ar"]
    OUT_AR = 48000                       # Facebook Reels spec
    if audio == "strip" or (not src_audio and audio in ("keep", "mix") and not has_bed):
        return None, False
    if audio == "keep" and src_audio:
        eqg = round(rng.uniform(-1.5, 1.5), 2)
        # asetrate shifts pitch by re-declaring the rate; atempo=1/pitch puts the
        # SPEED back where it was, leaving only the pitch change. The base must be
        # the source's own rate or the two no longer cancel out.
        return (f"[0:a]asetrate={src_ar}*{pitch},atempo={round(1/pitch,4)},atempo={speed},"
                f"aresample={OUT_AR},equalizer=f=1000:t=q:w=1:g={eqg}[a]"), True
    if audio == "replace" and has_bed:
        return f"[1:a]atempo={speed},aresample={OUT_AR}[a]", True
    if audio == "mix" and has_bed:
        if src_audio:
            return (f"[0:a]atempo={speed}[a0];[1:a]volume=0.35,atempo={speed}[a1];"
                    f"[a0][a1]amix=inputs=2:duration=first,aresample={OUT_AR}[a]"), True
        return f"[1:a]atempo={speed},aresample={OUT_AR}[a]", True
    if src_audio:  # fallback: at least tempo-match kept audio
        return f"[0:a]atempo={speed},aresample={OUT_AR}[a]", True
    return None, False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("dst")
    ap.add_argument("--profile", default="default")
    ap.add_argument("--mode", choices=["crop", "reframe"], default="crop")
    ap.add_argument("--caption", default="")
    ap.add_argument("--font", default=r"C:/Windows/Fonts/arialbd.ttf",
                    help="ttf with Cyrillic glyphs (Linux: /usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf)")
    ap.add_argument("--audio", choices=["keep", "strip", "replace", "mix"], default="keep")
    ap.add_argument("--audio-file", default="")
    ap.add_argument("--seed", type=int, default=None)
    args = ap.parse_args()

    prof = PROFILES.get(args.profile, PROFILES["default"])
    if args.seed is None:
        args.seed = int(hashlib.sha1((os.path.basename(args.src) + args.profile).encode()).hexdigest()[:8], 16)
    rng = random.Random(args.seed)
    meta = probe(args.src)
    has_bed = bool(args.audio_file) and os.path.exists(args.audio_file)

    font = args.font if (args.font and os.path.exists(args.font)) else ""
    vbody, speed, chosen = video_graph(prof, args.mode, args.caption, rng, font)
    abody, has_audio = audio_graph(prof, meta, speed, args.audio, has_bed, rng)

    crf = rng.choice([19, 20, 21, 22, 23])
    preset = rng.choice(["fast", "medium"])   # server-friendly; still good compression
    gop = rng.choice([48, 60, 72])

    # Duration after the speed change -- setpts/atempo stretch or squeeze the clip,
    # so the cap has to be checked against the OUTPUT length, not the source.
    out_dur = meta["dur"] / speed if speed else meta["dur"]
    trim_to = TRIM_TARGET if out_dur > MAX_DURATION else None

    graph = vbody + ((";" + abody) if abody else "")
    cmd = ["ffmpeg", "-y", "-i", args.src]
    if args.audio in ("replace", "mix") and has_bed:
        cmd += ["-stream_loop", "-1", "-i", args.audio_file]
    cmd += ["-filter_complex", graph, "-map", "[v]"]
    cmd += (["-map", "[a]"] if has_audio else ["-an"])
    cmd += ["-c:v", "libx264", "-preset", preset, "-crf", str(crf),
            "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-g", str(gop), "-x264-params", f"keyint={gop}:min-keyint={gop//2}:scenecut=40",
            "-movflags", "+faststart", "-map_metadata", "-1", "-metadata", "encoder=Lavf"]
    if has_audio:
        # 48 kHz stereo: what the Facebook Reels spec asks for.
        cmd += ["-c:a", "aac", "-profile:a", "aac_low",
                "-b:a", f"{rng.choice([128,160,192])}k", "-ar", "48000", "-ac", "2"]
    if args.audio in ("replace", "mix") and has_bed:
        cmd += ["-shortest"]
    if trim_to:
        cmd += ["-t", str(trim_to)]
    cmd += [args.dst]

    run(cmd)
    out = probe(args.dst)

    # Check the RESULT against the Facebook Reels spec, not the intent. ffmpeg can
    # succeed and still hand back something Facebook will refuse, and finding that
    # out here costs nothing -- finding it out from error 1363128 costs a slot in
    # the 30-posts-per-day quota.
    problems = []
    if (out["w"], out["h"]) != (W, H):
        problems.append(f"geometry {out['w']}x{out['h']}, expected {W}x{H}")
    if out["dur"] < MIN_DURATION:
        problems.append(f"duration {out['dur']:.2f}s is under the {MIN_DURATION}s Reels minimum")
    if out["dur"] > MAX_DURATION + 0.5:
        problems.append(f"duration {out['dur']:.2f}s is over the {MAX_DURATION}s Reels maximum")

    print(json.dumps({"dst": args.dst, "profile": args.profile, "seed": args.seed,
                      "encode": {"crf": crf, "preset": preset, "gop": gop},
                      "params": chosen,
                      "trimmed_to": trim_to,
                      "in": {"w": meta["w"], "h": meta["h"], "dur": round(meta["dur"], 2),
                             "audio": meta["has_audio"], "ar": meta["ar"]},
                      "out": {"w": out["w"], "h": out["h"], "dur": round(out["dur"], 2),
                              "audio": out["has_audio"], "ar": out["ar"]},
                      "problems": problems},
                     ensure_ascii=False, indent=2))
    print("OK" if not problems else "WARN: " + "; ".join(problems))


if __name__ == "__main__":
    main()
