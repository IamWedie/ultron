"""
Streaming "Ultron" voice processor for the Gemini Live audio out.

Takes the int16 24 kHz mono PCM Gemini produces and reshapes it in real time
into a deep, mechanical voice — the research-doc "Option B" path, since the
Gemini API has no custom voices:

  * Phase-vocoder pitch shift (formant-preserving, duration unchanged),
    default −3.5 semitones for James-Spader weight.
  * Subtle flanger for the machine "metallic shell" shimmer.
  * Low-shelf bass boost for chest resonance + gentle darkening highcut.

Pure numpy, no external DSP libs. Feeds on arbitrary TCP chunk boundaries —
internal overlap-add keeps pitch continuity across every block.
"""

from __future__ import annotations

import numpy as np

SR = 24000          # Gemini Live receive rate (int16 mono)

PW = 4096           # analysis window (≈170 ms @24 kHz)
PH = 1024           # analysis hop (≈43 ms)


def _princarg(a: np.ndarray) -> np.ndarray:
    """Wrap phase to (−π, π]."""
    return (a + np.pi) % (2.0 * np.pi) - np.pi


class UltronVoice:
    def __init__(self, semitones: float = -3.5, chorus: float = 0.3,
                 bass_db: float = 5.0, darken: float = 0.5,
                 enabled: bool = True):
        self.enabled = bool(enabled)
        self.semitones = float(semitones)
        self.chorus = float(chorus)          # 0..1 wet mix of the flanger
        self.bass_db = float(bass_db)
        self.darken = float(darken)          # 0..1 amount of highcut

        self._ratio = float(2.0 ** (self.semitones / 12.0))   # <1 → deeper
        if self._ratio <= 0.0:
            self._ratio = 1.0
        self._hop_out = float(PH) * self._ratio     # phase advance per frame

        self._win = np.hanning(PW).astype(np.float64)
        self._freq = 2.0 * np.pi * np.arange(PW // 2 + 1) / PW
        self._prev_phase = None
        self._out_phase = None

        # spectral EQ (bass shelf + darkening) applied inside the vocoder FFT
        self._eq = np.ones(PW // 2 + 1, dtype=np.float64)
        self._build_eq()

        self._in = np.zeros(0, dtype=np.float64)
        self._synth = np.zeros(PW * 2, dtype=np.float64)
        self._norm = np.zeros(PW * 2, dtype=np.float64)
        self._frames_done = 0
        self._emitted_total = 0
        self._processed = 0                     # output samples emitted so far

        # flanger delay line (delay sweeps ~0.4 → 1.9 ms at 0.35 Hz)
        self._dl = np.zeros(int(8e-3 * SR) + 16, dtype=np.float64)
        self._dl_pos = 0
        self._fl_clock = 0

        # fractional resampler state (stage 2 of the pitch shift)
        self._rs_buf = np.zeros(0, dtype=np.float64)
        self._rs_pos = 0.0

    def _build_eq(self):
        """Precompute the FFT-bin gain curve: bass shelf + dark high cut."""
        bins = np.arange(PW // 2 + 1, dtype=np.float64)
        f = bins * (SR / float(PW))
        A = 10.0 ** (self.bass_db / 20.0)
        shelf = 1.0 + (A - 1.0) / (1.0 + (f / 150.0) ** 2.0)
        dark_fc = 3800.0
        dark = 1.0 - self.darken * (1.0 - 1.0 / (1.0 + (f / dark_fc) ** 2.0))
        self._eq = shelf * dark

    def set(self, enabled=None, semitones=None, chorus=None,
            bass_db=None, darken=None):
        if enabled is not None:
            self.enabled = bool(enabled)
        if semitones is not None:
            self.semitones = float(semitones)
            self._ratio = float(2.0 ** (self.semitones / 12.0))
            if self._ratio <= 0.0:
                self._ratio = 1.0
            self._hop_out = float(PH) * self._ratio
        if chorus is not None:
            self.chorus = float(chorus)
        if bass_db is not None:
            self.bass_db = float(bass_db)
        if darken is not None:
            self.darken = float(darken)
        self._build_eq()

    # ── main entry ──────────────────────────────────────────────────────────
    def process(self, data: bytes | np.ndarray) -> bytes:
        """Feed int16 PCM (mono), get same-length int16 PCM back."""
        if not self.enabled or not len(data):
            return data
        if isinstance(data, (bytes, bytearray, memoryview)):
            if len(data) % 2:
                data = bytes(data[:-1])
        x = np.frombuffer(data, dtype=np.int16).astype(np.float64) / 32768.0
        out = self._resample(self._run(x))
        return (np.clip(out, -0.999, 0.999) * 32767.0).astype(np.int16).tobytes()

    def flush(self) -> bytes:
        """Process leftover buffered audio at a response boundary. The leftover
        is stashed and the input cleared FIRST — passing self._in into _run()
        directly would make _run() append the buffer to itself and repeat the
        chunk (the source of the original 'echo')."""
        if not self.enabled:
            return b""
        tail = np.ascontiguousarray(self._in)
        if not len(tail):
            return b""
        self._in = np.zeros(0)
        stretched = self._run(tail)          # fresh run, no self-doubling
        out = self._resample(stretched)
        out = self._flanger(out)
        return (np.clip(out, -0.999, 0.999) * 32767.0).astype(np.int16).tobytes()

    def flush_drain(self) -> bytes:
        """Response-end drain. Emits the leftover input (including a padded
        final frame so the last ~170 ms is NOT dropped) and drains the
        resampler completely, resetting it for the next response."""
        if not self.enabled:
            return b""
        parts: list = []

        tail = np.ascontiguousarray(self._in)
        self._in = np.zeros(0)
        if len(tail):
            s = self._run(tail)
            if len(s):
                parts.append(s)

        # Padded final frame: process any < PW leftover so the response tail is heard.
        if len(self._in):
            tail2 = np.ascontiguousarray(self._in)
            self._in = np.zeros(0)
            frame = np.concatenate([tail2, np.zeros(PW - len(tail2))]) * self._win
            X = np.fft.rfft(frame)
            mag = np.abs(X)
            phase = np.angle(X)
            if self._prev_phase is not None:
                delta = _princarg(phase - self._prev_phase - self._freq * PH)
            else:
                delta = np.zeros_like(phase)
            self._prev_phase = phase
            if self._out_phase is None:
                self._out_phase = phase
            self._out_phase = self._out_phase + self._freq * self._hop_out + delta
            Y = mag * np.exp(1j * self._out_phase) * self._eq
            voiced = np.fft.irfft(Y, n=PW) * self._win
            i0 = int(self._frames_done * self._hop_out)
            i1 = i0 + PW
            if i1 > len(self._synth):
                pad = i1 - len(self._synth)
                self._synth = np.concatenate([self._synth, np.zeros(pad)])
                self._norm = np.concatenate([self._norm, np.zeros(pad)])
            self._synth[i0:i1] += voiced
            self._norm[i0:i1] += self._win * self._win
            self._frames_done += 1
            rem = self._emit(int(self._frames_done * self._hop_out) - self._emitted_total)
            if len(rem):
                parts.append(rem)

        stretched = np.concatenate(parts) if parts else np.zeros(0)
        out = self._rs_drain(stretched)
        out = self._flanger(out)
        return (np.clip(out, -0.999, 0.999) * 32767.0).astype(np.int16).tobytes()

    def _rs_drain(self, stretched: np.ndarray) -> np.ndarray:
        """Emit the ENTIRE resampled stream (no holdback) and reset the reader."""
        rho = min(max(self._ratio, 0.15), 4.0)
        if len(stretched):
            self._rs_buf = np.concatenate([self._rs_buf, stretched])
        buf, pos = self._rs_buf, self._rs_pos
        out = []
        n = len(buf)
        while pos < n:
            i = int(pos)
            f = pos - i
            i1 = i + 1 if i + 1 < n else i
            out.append(buf[i] * (1.0 - f) + buf[i1] * f)
            pos += rho
        self._rs_buf = np.zeros(0)
        self._rs_pos = 0.0
        return np.asarray(out, dtype=np.float64) if out else np.zeros(0)

    # ── phase vocoder TIME-STRETCH stage (pitch preserved, τ = p) ────────────
    def _run(self, x: np.ndarray) -> np.ndarray:
        """Stretch factor = self._ratio (well under warm < 1 → compress). Frames
        land at hop_out = PH·p, phase advances ω·hop_out → coherence kept."""
        self._in = np.concatenate([self._in, x])
        buf, cursor = self._in, 0
        parts = []

        while len(buf) - cursor >= PW:
            frame = buf[cursor:cursor + PW] * self._win
            X = np.fft.rfft(frame)
            mag = np.abs(X)
            phase = np.angle(X)
            if self._prev_phase is not None:
                delta = _princarg(phase - self._prev_phase - self._freq * PH)
            else:
                delta = np.zeros_like(phase)
            self._prev_phase = phase
            if self._out_phase is None:
                self._out_phase = phase.copy()
            self._out_phase = self._out_phase + self._freq * self._hop_out + delta
            Y = mag * np.exp(1j * self._out_phase) * self._eq
            voiced = np.fft.irfft(Y, n=PW) * self._win

            i0 = int(self._frames_done * self._hop_out)    # stretch grid
            i1 = i0 + PW
            if i1 > len(self._synth):
                pad = i1 - len(self._synth)
                self._synth = np.concatenate([self._synth, np.zeros(pad)])
                self._norm = np.concatenate([self._norm, np.zeros(pad)])
            self._synth[i0:i1] += voiced
            self._norm[i0:i1] += self._win * self._win
            self._frames_done += 1
            cursor += PH

            target = int(self._frames_done * self._hop_out) - PW
            if target > self._emitted_total:
                seg = self._emit(target - self._emitted_total)
                if len(seg):
                    parts.append(seg)

        if cursor:
            self._in = buf[cursor:]
        return np.concatenate(parts) if parts else np.zeros(0)

    # ── fractional-resample stage (restores duration, realises the pitch) ──
    def _resample(self, stretched: np.ndarray) -> np.ndarray:
        """Play back the stretched stream at ratio p: y[j] = stretched[j·p],
        read-pointer slowly re-reads → pitch × p, duration restored."""
        rho = min(max(self._ratio, 0.15), 4.0)
        if abs(rho - 1.0) < 1e-4 or not len(stretched):
            return stretched
        self._rs_buf = np.concatenate([self._rs_buf, stretched])
        buf, pos = self._rs_buf, self._rs_pos
        out = []
        while pos + 4 < len(buf):               # keep tiny lookahead margin
            i = int(pos)
            f = pos - i
            v = buf[i] * (1.0 - f) + buf[i + 1] * f
            out.append(v)
            pos += rho
        if out:
            consumed = int(pos)
            if consumed >= 4096:
                self._rs_buf = buf[consumed:]
                self._rs_pos = pos - consumed
            else:
                self._rs_buf = buf
                self._rs_pos = pos
            return np.asarray(out, dtype=np.float64)
        return np.zeros(0)

    def _emit(self, n: int) -> np.ndarray:
        n = min(n, len(self._synth))
        if n <= 0:
            return np.zeros(0)
        nrm = self._norm[:n].copy()
        nrm[nrm < 1e-4] = 1.0
        out = (self._synth[:n] / nrm).astype(np.float64)
        self._synth[:n] = 0.0
        self._norm[:n] = 0.0
        self._synth = np.concatenate([self._synth[n:], np.zeros(PW)])
        self._norm = np.concatenate([self._norm[n:], np.zeros(PW)])
        self._emitted_total += n
        self._processed += n
        return out

    # ── flanger (metallic shell shimmer) — fully vectorized ─────────────────
    def _flanger(self, x: np.ndarray) -> np.ndarray:
        if self.chorus <= 1e-3 or len(x) == 0:
            return x
        n = len(x)
        t0 = self._fl_clock
        idx_samples = np.arange(n, dtype=np.float64)
        lfo = 0.5 + 0.5 * np.sin(2 * np.pi * 0.35 * (t0 + idx_samples) / SR)
        d = int(0.4e-3 * SR) + (int(1.5e-3 * SR) * lfo).astype(np.intp)
        np.maximum(d, 1, out=d)
        L = len(self._dl)
        read = (self._dl_pos - d) % L
        wet = self._dl[read]
        if n >= L:
            self._dl[:] = x[-L:]
            self._dl_pos = 0
        else:
            pos = self._dl_pos
            take = x[-n:]
            if pos + n <= L:
                self._dl[pos:pos + n] = take
            else:
                k = L - pos
                self._dl[pos:] = take[:k]
                self._dl[:n - k] = take[k:]
            self._dl_pos = (pos + n) % L
        self._fl_clock += n
        self._processed += n
        return x * (1.0 - self.chorus) + wet * self.chorus

    # ── low-shelf bass boost + darkening highcut (spectral, in vocoder FFT) ─


# ── quick self-test when run directly ───────────────────────────────────────
if __name__ == "__main__":
    import random
    n_in = SR // 2
    t = np.arange(n_in) / SR
    tone = (np.sin(2 * np.pi * 220 * t) * 0.7 * 32767).astype(np.int16).tobytes()
    v = UltronVoice(enabled=True)
    random.seed(1)
    out = b""
    i = 0
    while i < len(tone):
        n = random.randint(200, 1200) * 2
        n = min(n, len(tone) - i)
        out += v.process(tone[i:i + n])
        i += n
    arr = np.frombuffer(out, dtype=np.int16)
    a = arr.astype(np.float64)
    zc = int(np.sum((a[:-1] < 0) & (a[1:] >= 0)))
    est = zc * SR / (2 * len(a)) if len(a) else 0.0
    exp = 220.0 * 2.0 ** (-3.5 / 12.0)
    print(f"in={n_in} out={len(arr)}  est_pitch={est:.1f} Hz (expected ~{exp:.1f})")