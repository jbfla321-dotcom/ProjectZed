# Shared helpers for slicing the raw library into game one-shots.
import numpy as np, soundfile as sf
from scipy.signal import resample_poly

def load(path, sr_out=48000):
    data, sr = sf.read(path, always_2d=True, dtype="float32")
    if sr != sr_out:
        from math import gcd
        g = gcd(sr, sr_out)
        data = resample_poly(data, sr_out // g, sr // g, axis=0).astype(np.float32)
    return data, sr_out

def envelope(mono, sr, win_ms=5):
    w = max(1, int(sr * win_ms / 1000))
    n = len(mono) // w
    return np.sqrt((mono[: n * w].reshape(n, w) ** 2).mean(axis=1) + 1e-12), w

def onsets(mono, sr, rel_db=-18, min_gap=0.25, rise_db=12):
    """Sample indices of hits: frames that jump rise_db over the recent floor and sit within rel_db of the loudest."""
    env, w = envelope(mono, sr)
    db = 20 * np.log10(env)
    peak = db.max()
    hits, last = [], -1e9
    floor_len = int(0.05 * sr / w)
    for i in range(1, len(db)):
        if db[i] < peak + rel_db: continue
        floor = db[max(0, i - floor_len):i].min()
        if db[i] - floor >= rise_db and (i - last) * w / sr >= min_gap:
            # step back to where it starts rising
            j = i
            while j > 0 and db[j - 1] < db[j] - 1 and db[j - 1] > floor + 3: j -= 1
            hits.append(j * w); last = i
    return hits

def to_mono(data):
    return data.mean(axis=1) if data.shape[1] > 1 else data[:, 0]
