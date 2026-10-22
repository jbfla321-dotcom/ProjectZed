import json, sys, numpy as np, soundfile as sf
sys.path.insert(0, r"C:/Users/josht/My project/Audio/Scripts")
from audio_common import *
for m in json.load(open(r"C:/Users/josht/SoundLibrary/manifest.json", encoding="utf-8")):
    info = sf.info(m["local"])
    data, sr = load(m["local"])
    mono = to_mono(data)
    h = onsets(mono, sr)
    print(f"{info.duration:7.2f}s {info.samplerate} ch{info.channels} hits={len(h):3d} {[round(x/sr,2) for x in h[:12]]} | {m['path'].split('/')[-1][:70]}".encode('ascii','replace').decode())
