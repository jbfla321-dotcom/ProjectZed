# Cuts the raw Sonniss files (C:/Users/josht/SoundLibrary) into game one-shots under Assets/Audio and writes
# Assets/Audio/CREDITS.md. Re-runnable: each event's files are rebuilt from scratch.
# Each set: (folder, event name, stereo?, [(source needle, mode, args)]).
#   whole - the whole file from its first transient, up to `len` seconds
#   slice - cut at detected hits (min `gap` s apart), `take` of them spread across the file, each up to `len` s
#   crack - like slice, but keeps the brightest hits (supersonic cracks rather than ricochet whines)
#   at    - cut at the given `times` (seconds into the file), each up to `len` s
#   loop  - a seamless loop: up to `len` s from `start`, its end crossfaded into its start (`xfade` s), no trimming
#   lead  - (option) start each cut this many seconds before its loudest point
#   skip  - (option) start each cut this many seconds after its hit (tails without the shot's attack)
#   level - (option) match loudness: first 100 ms to this RMS (dBFS), peaks soft-clipped, instead of peak-normalising
#   fout  - (option) fade-out length (s)
import glob, json, os, sys
import numpy as np, soundfile as sf
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from audio_common import load, onsets, to_mono

LIB = r"C:/Users/josht/SoundLibrary"
OUT = r"C:/Users/josht/My project/Assets/Audio"
SR = 48000

# Close shots are split into a short core (every shot; cut before the Nagant's slap echo at ~0.4 s and
# loudness-matched so no take jumps out in full auto) and a tail that plays once when a burst ends.
RIFLE_CORE = [
        ("NAGANT 1m close", "slice", dict(gap=3.0, take=5, len=0.3, level=-10, fout=0.08)),
        ("M1903A3, Firing", "slice", dict(gap=2.0, take=3, len=0.3, level=-10, fout=0.08)),
    ]
RIFLE_TAIL = [
        ("M1903A3, Firing", "slice", dict(gap=2.0, take=3, len=1.8, skip=0.22)),
    ]

SETS = [
    # ---- Weapons ----
    # First person (stereo, heard by the shooter) and the same shots in mono for the 3D close layer.
    ("Weapons", "Rifle_FP", True, RIFLE_CORE),
    ("Weapons", "Rifle_Near", False, RIFLE_CORE),
    ("Weapons", "Rifle_FP_Tail", True, RIFLE_TAIL),
    ("Weapons", "Rifle_Near_Tail", False, RIFLE_TAIL),
    ("Weapons", "Rifle_Mid", False, [
        ("SOURCE Single Shots 30m", "slice", dict(gap=3.0, take=4, len=3.0)),
        ("NAGANT 50m distant", "slice", dict(gap=3.0, take=4, len=3.0)),
    ]),
    ("Weapons", "Rifle_Far", False, [
        ("NAGANT mixed long distance", "whole", dict(len=4.0)),
        ("Lynx 50 - FIRING", "slice", dict(gap=5.0, take=3, len=4.0)),
        ("M1911A1, Firing, t2, 200m", "slice", dict(gap=3.0, take=4, len=3.0)),
    ]),
    ("Weapons", "Bullet_Crack", False, [
        ("warfare_t3_mg_whizzes", "crack", dict(gap=0.25, take=7, len=0.6)),
        ("bullet_flyby_fast_05", "whole", dict(len=0.6)),
    ]),
    ("Weapons", "Dry_Fire", False, [
        ("M1911A1, Handling, Dry Fire", "slice", dict(gap=0.8, take=2, len=0.5)),
        ("1928A1, Handling, Dry Fire", "slice", dict(gap=0.8, take=2, len=0.5)),
    ]),
    ("Weapons", "Mag_Out", False, [
        ("P38, Handling, Removing Magazine", "slice", dict(gap=1.2, take=2, len=1.2)),
        ("Desert Eagle - VAR SFX", "slice", dict(gap=0.8, take=4, len=0.8, parity=0)),
    ]),
    ("Weapons", "Mag_In", False, [
        ("Magazine Insert Slow", "slice", dict(gap=1.5, take=4, len=1.6, lead=0.12)),
        ("Desert Eagle - VAR SFX", "slice", dict(gap=0.8, take=4, len=0.8, parity=1)),
    ]),
    ("Weapons", "Charge", False, [
        ("MP 40, Handling, Cocking", "slice", dict(gap=1.5, take=2, len=1.0)),
    ]),
    ("Weapons", "Impact_Dirt", False, [
        ("PM_BBI_Bullet_Impact_Dirt_3", "slice", dict(gap=0.3, take=2, len=0.8)),
        ("Bullet Impact 16", "whole", dict(len=0.4)),
        ("RL_bodyfall_Dirt", "whole", dict(len=0.5, pitch=1.5)),
    ]),
    ("Weapons", "Impact_Rock", False, [
        ("Bullet rock Impact 4", "slice", dict(gap=0.25, take=2, len=0.6)),
        ("bullet_impact_concrete_brick_01", "whole", dict(len=0.4)),
        ("PM_RI_Source_53", "slice", dict(gap=0.2, take=3, len=0.5)),
        ("PM_RI_Source_92", "slice", dict(gap=0.3, take=2, len=0.6)),
    ]),
    ("Weapons", "Impact_Wood", False, [
        ("Impacts Hard - Short Wobbly", "whole", dict(len=0.6)),
        ("Impacts Soft - Short, Crack", "whole", dict(len=0.45)),
        ("WOODImpt_Impact Wood 23", "whole", dict(len=0.8)),
    ]),
    ("Weapons", "Impact_Metal", False, [
        ("bullet_impact_metal_heavy_08", "whole", dict(len=0.8)),
        ("MECHMisc_Ricochet Hits 01", "slice", dict(gap=0.25, take=6, len=0.8)),
    ]),
    ("Weapons", "Impact_Flesh", False, [
        ("PM_BBI_Bullet_Impact_Hit_Body_Flesh_25", "whole", dict(len=0.7)),
        ("bullet_impact_body_thump_02", "whole", dict(len=0.45)),
    ]),
    # ---- Footsteps ----
    ("Footsteps", "Step_Dirt", False, [
        ("PM_SDNG_Single_Step_Footstep_19", "whole", dict(len=0.6)),
        ("PM_SDNG_Single_Step_Footstep_46", "whole", dict(len=0.6)),
        ("PM_SDNG_Stereo_Walk_Seamless_Loop_1", "slice", dict(gap=0.4, take=6, len=0.45)),
    ]),
    ("Footsteps", "Step_Forest", False, [
        ("PM_SDGS_113", "whole", dict(len=0.6)),
        ("PM_SDGS_14 ", "whole", dict(len=0.6)),
        ("PM_SDGS_186", "whole", dict(len=0.45)),
        ("grass 3 single step 3", "whole", dict(len=0.6)),
        ("169_Foley_Footsteps_Grass", "slice", dict(gap=0.25, take=4, len=0.3)),
    ]),
    ("Footsteps", "Step_Gravel", False, [
        ("S23_SFX_Footsteps_Gravel", "slice", dict(gap=0.4, take=8, len=0.5)),
    ]),
    ("Footsteps", "Step_Wood", False, [
        ("wooden floor 1 loop", "slice", dict(gap=0.4, take=4, len=0.45)),
        ("1879 - Footsteps - Wooden Stairs", "slice", dict(gap=0.5, take=4, len=0.5)),
    ]),
    ("Footsteps", "Step_Rock", False, [
        ("015_Foley_Footsteps_Asphalt", "slice", dict(gap=0.25, take=8, len=0.3)),
    ]),
    ("Footsteps", "Step_Water", False, [
        ("0014_Footsteps_water_puddle", "slice", dict(gap=0.25, take=6, len=0.7)),
        ("mud 1 loop", "slice", dict(gap=0.5, take=2, len=0.6)),
    ]),
    ("Footsteps", "Land", False, [
        ("289_Foley_Footsteps_Rocks_Sneaker_Jump_Land", "slice", dict(gap=0.8, take=5, len=0.7)),
        ("RL_bodyfall_Dirt", "whole", dict(len=0.8)),
        ("RL_bodyfall_Concrete_Generic_Feet", "whole", dict(len=0.8)),
    ]),
    # ---- World (houses) ----
    ("World", "Door_Open", False, [
        ("DOORCreak_Wooden Door, Opening and Closing 09", "at", dict(times=[0.2], len=1.9)),
        ("80s wooden door - 80s city apartment door", "at", dict(times=[12.3, 22.3], len=1.2)),
    ]),
    ("World", "Door_Close", False, [
        ("80s wooden door - 80s city apartment door", "at", dict(times=[17.48, 34.72, 40.72], len=1.0)),
        ("DOORCreak_Wooden Door, Door Slams", "at", dict(times=[2.22, 8.48], len=0.7)),
    ]),
    ("World", "Glass_Break", False, [
        ("Window,Small,Break,Medium Impact,Large Amount", "whole", dict(len=2.5)),
        ("Glass,Shards,Smash,Medium Impact,Lots of Large", "whole", dict(len=2.0)),
        ("SH101_Glass_Break_Pane_702T", "whole", dict(len=2.5)),
        ("Glass,Plate Glass,Thick,Break,Topple", "whole", dict(len=2.5)),
        ("glass_impact_break_002", "whole", dict(len=2.0)),
    ]),
    ("Ambience", "Rain_Outdoor_Loop", True, [
        ("RAINVege_InsJ_Ambience_Rain_Moderate_02_LOOP", "loop", dict(start=0.0, len=60.0, xfade=2.0)),
    ]),
    ("Ambience", "Rain_Light_Loop", True, [
        ("rain_shower of rain_steady rain_forest_leaves", "loop", dict(start=1.0, len=20.0, xfade=1.5)),
    ]),
    ("Ambience", "Rain_Indoor_Loop", True, [
        ("RAINInt_Heavy Rain on Window,  Constant", "loop", dict(start=5.0, len=45.0, xfade=2.0)),
    ]),
    ("World", "Thunder", False, [
        ("Thunder_Very-Close_Rain_08", "whole", dict(len=9.0, fout=1.5)),
        ("thunder_clap_04", "whole", dict(len=12.0, fout=2.0)),
        ("ambient thunder clap distant with rain mono", "slice", dict(gap=8.0, take=2, len=10.0, fout=2.0)),
        ("THUN_InsJ_Thunder_Very-Close_Rain_03", "whole", dict(len=12.0, fout=2.0)),
    ]),
    ("Footsteps", "Gear_Rustle", False, [
        ("FOLYClth_ClothMovement24", "slice", dict(gap=0.3, take=3, len=0.6)),
        ("FOLYClth_ClothMovement29", "whole", dict(len=0.6)),
        ("HERO - CK - ARMOR Leather Walk", "slice", dict(gap=0.5, take=4, len=0.5)),
    ]),
]


def fade(x, sr, fin=0.002, fout=0.03):
    n_in, n_out = int(fin * sr), min(len(x) // 2, int(fout * sr))
    x[:n_in] *= np.linspace(0, 1, n_in)[:, None]
    x[-n_out:] *= (np.linspace(1, 0, n_out) ** 2)[:, None]
    return x


def trim_tail(x, sr, db=-50):
    env = np.abs(to_mono(x))
    above = np.nonzero(env > env.max() * 10 ** (db / 20))[0]
    return x[: above[-1] + int(0.01 * sr)] if len(above) else x


def finish(x, sr, stereo, pitch=1.0, level=None, fout=0.03, fin=0.002):
    if pitch != 1.0:
        from scipy.signal import resample
        x = resample(x, int(len(x) / pitch), axis=0).astype(np.float32)
    x = to_mono(x)[:, None] if not stereo else (np.repeat(x, 2, axis=1) if x.shape[1] == 1 else x)
    x = fade(trim_tail(x, sr), sr, fin, fout)
    if level is not None:
        rms = np.sqrt((x[: int(0.1 * sr)] ** 2).mean()) + 1e-9
        x = x * (10 ** (level / 20) / rms)
        return np.tanh(x / 0.89) * 0.89  # soft-clip the peaks
    return x / (np.abs(x).max() + 1e-9) * 10 ** (-1 / 20)  # peak -1 dBFS; levels are set in the SoundEvents


def brightness(seg, sr):
    n = min(len(seg), int(0.05 * sr))
    spec = np.abs(np.fft.rfft(to_mono(seg)[:n]))
    f = np.fft.rfftfreq(n, 1 / sr)
    return (spec * f).sum() / (spec.sum() + 1e-9)


def spread(items, take):
    if len(items) <= take:
        return items
    idx = np.linspace(0, len(items) - 1, take).round().astype(int)
    return [items[i] for i in idx]


manifest = json.load(open(os.path.join(LIB, "manifest.json"), encoding="utf-8"))


def find(needle):
    hits = [m for m in manifest if needle in m["path"]]
    assert hits, needle
    return hits[0]


credits = {}
for folder, event, stereo, sources in SETS:
    d = os.path.join(OUT, folder)
    os.makedirs(d, exist_ok=True)
    for old in glob.glob(os.path.join(d, event + "_[0-9][0-9].wav")):
        os.remove(old)
    n = 0
    for needle, mode, a in sources:
        m = find(needle)
        data, sr = load(m["local"], SR)
        L = int(a["len"] * sr)
        if mode == "loop":
            seg = data[int(a["start"] * sr):int(a["start"] * sr) + L].copy()
            x = int(a["xfade"] * sr)
            if x > 0 and len(seg) > 2 * x:   # fold the tail over the head so the loop point is seamless
                ramp = np.linspace(0, 1, x)[:, None]
                seg[:x] = seg[:x] * ramp + seg[-x:] * (1 - ramp)
                seg = seg[:-x]
            n += 1
            name = f"{event}_{n:02d}.wav"
            if not stereo: seg = to_mono(seg)[:, None]
            seg = seg / (np.abs(seg).max() + 1e-9) * 10 ** (-1 / 20)
            sf.write(os.path.join(d, name), seg, sr, subtype="PCM_16")
            credits.setdefault(m["path"], (m["year"], []))[1].append(f"{folder}/{name}")
            continue
        if mode == "at":
            segs = [data[max(0, int(t * sr) - int(0.01 * sr)):int(t * sr) + L] for t in a["times"]]
        elif mode == "whole":
            mono = to_mono(data)
            start = max(0, int(np.argmax(np.abs(mono) > np.abs(mono).max() * 0.05)) - int(0.003 * sr))
            segs = [data[start:start + L]]
        else:
            hits = onsets(to_mono(data), sr, min_gap=a["gap"])
            if "parity" in a:
                hits = hits[a["parity"]::2]
            segs = []
            for i, h in enumerate(hits):
                end = min(h + L, hits[i + 1] - int(0.01 * sr) if i + 1 < len(hits) else len(data))
                s = max(0, h - int(0.003 * sr))
                if end - s > 0.05 * sr:
                    segs.append(data[s:end])
            if "skip" in a:
                segs = [sg[int(a["skip"] * sr):] for sg in segs]
            if "lead" in a:  # start just before the loudest moment (the click), not at the first handling noise
                segs = [sg[max(0, int(np.argmax(np.abs(to_mono(sg)))) - int(a["lead"] * sr)):][: int(0.8 * sr)] for sg in segs]
            if mode == "crack":
                segs = sorted(segs, key=lambda s: -brightness(s, sr))[: a["take"]]
            else:
                segs = spread(segs, a["take"])
        for seg in segs:
            n += 1
            name = f"{event}_{n:02d}.wav"
            out = finish(seg.copy(), sr, stereo, a.get("pitch", 1.0), a.get("level"), a.get("fout", 0.03), 0.04 if "skip" in a else 0.002)
            sf.write(os.path.join(d, name), out, sr, subtype="PCM_16")
            credits.setdefault(m["path"], (m["year"], []))[1].append(f"{folder}/{name}")
    print(f"{folder}/{event}: {n}")

with open(os.path.join(OUT, "CREDITS.md"), "w", encoding="utf-8") as f:
    f.write("# Audio credits\n\n"
            "All sounds are cut from the **Sonniss GDC Game Audio Bundle**, the free yearly bundle from "
            "https://sonniss.com/gameaudiogdc. License: royalty-free, commercial use allowed, no attribution required, "
            "unlimited projects, not for AI/ML training (https://sonniss.com/gdc-bundle-license/).\n\n"
            "Downloaded 2026-10-21 from the Internet Archive mirrors of the official bundles "
            "(`archive.org/details/sonniss-gdc-<year>-game-audio-bundle-normalized`; file sizes match the official zips), "
            "because sonniss.com's own download server and Drive mirrors were unavailable. "
            "Raw files are kept outside the project in `C:/Users/josht/SoundLibrary`. "
            "Cut, trimmed, faded and peak-normalised by `Audio/Scripts/slice.py`.\n\n"
            "| Game files | GDC bundle | Library | Source file |\n|---|---|---|---|\n")
    for path, (year, outs) in sorted(credits.items(), key=lambda kv: kv[1][1][0]):
        lib, file = path.split("/")[0], path.split("/")[-1]
        f.write(f"| {', '.join(outs)} | {year} | {lib} | {file} |\n")
print("credits written")
