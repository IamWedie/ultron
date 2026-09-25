"""InCallAudioBridge: Real-time audio bridge between Telegram VoIP (48 kHz mono)
and Gemini Live (16 kHz in / 24 kHz out).

    Uses ntgcalls MediaSource.EXTERNAL with paced external frames for TX.
    RX uses ntgcalls on_frames callback.

Responsibilities:
- Resample Telegram RX (48 kHz) -> Gemini mic queue (16 kHz)
- Resample Gemini output (24 kHz) -> Telegram TX (48 kHz) via external frames
- Mic bypass: route system mic to Telegram instead of Gemini during call
- Barge-in: detect remote speech, drain Gemini queue to prevent overlap
- Voice activity detection on both directions for smart gating
"""

from __future__ import annotations

import asyncio
from typing import Awaitable, Callable, Optional

import numpy as np

# Sample rates
TELEGRAM_SR = 48000   # Telegram VoIP native
GEMINI_IN_SR = 16000  # Gemini Live input
GEMINI_OUT_SR = 24000 # Gemini Live output

# Resampling ratios
RATIO_48_TO_16 = 1/3.0
RATIO_24_TO_48 = 2.0

# Frame sizes (ms)
FRAME_MS = 10
TELEGRAM_FRAME = int(TELEGRAM_SR * FRAME_MS / 1000)   # 480 samples
GEMINI_IN_FRAME = int(GEMINI_IN_SR * FRAME_MS / 1000) # 160 samples
GEMINI_OUT_FRAME = int(GEMINI_OUT_SR * FRAME_MS / 1000) # 240 samples

# VAD thresholds
REMOTE_VAD_THRESHOLD = 0.02  # RMS threshold for remote speech detection
LOCAL_VAD_THRESHOLD = 0.01   # RMS threshold for local speech

# Barge-in
BARGE_IN_QUEUE_DRAIN_MS = 100  # ms of Gemini audio to drain on barge-in


class AudioResampler:
    """High-quality linear resampler using numpy."""
    
    @staticmethod
    def resample_linear(audio: np.ndarray, ratio: float) -> np.ndarray:
        """Fast linear interpolation resampling."""
        if ratio == 1.0:
            return audio
        n_out = int(len(audio) * ratio)
        if n_out == 0:
            return np.array([], dtype=audio.dtype)
        x_old = np.arange(len(audio))
        x_new = np.linspace(0, len(audio) - 1, n_out)
        return np.interp(x_new, x_old, audio).astype(audio.dtype)


class InCallAudioBridge:
    """
    Bridges Telegram VoIP (48 kHz) <-> Gemini Live (16k/24k).
    
    Flow:
    Telegram RX (48k) -> on_frames callback -> resample 48->16k -> VAD -> _mic_queue -> Gemini
    Gemini OUT (24k) -> _audio_out_queue -> resample 24->48k -> external frames -> Telegram TX
    
    Mic bypass: system mic -> Telegram (bypasses Gemini entirely during call)
    Barge-in: remote speech detected -> drain _audio_out_queue
    """
    
    def __init__(
        self,
        mic_queue: asyncio.Queue,
        audio_out_queue: asyncio.Queue,
        on_remote_speech: Optional[Callable[[bool], None]] = None,
        vad_model_path: Optional[str] = None,
        send_frame: Optional[Callable[[bytes], Awaitable[None]]] = None,
    ):
        self._mic_queue = mic_queue
        self._audio_out_queue = audio_out_queue
        self._on_remote_speech = on_remote_speech
        self._send_frame = send_frame
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._gemini_tx_queue: asyncio.Queue = asyncio.Queue(maxsize=100)
        self._mic_tx_queue: asyncio.Queue = asyncio.Queue(maxsize=100)
        self._gemini_tx_buffer = bytearray()
        self._mic_tx_buffer = bytearray()
        
        # Audio buffers
        self._rx_buffer = bytearray()      # Telegram RX (48k) -> resample -> mic_queue
        
        # State
        self._running = False
        self._call_active = False
        self._mic_bypassed = False
        
        # VAD for remote speech detection (barge-in trigger)
        self._remote_speech_active = False
        self._remote_vad_frames = 0
        self._remote_silence_frames = 0
        self._VAD_SPEECH_FRAMES = 3   # ~60ms to confirm speech
        self._VAD_SILENCE_FRAMES = 15 # ~300ms to confirm silence end
        
        # Local VAD (for mic gating if needed)
        self._local_vad_frames = 0
        
        # Tasks
        self._rx_task: Optional[asyncio.Task] = None
        self._tx_task: Optional[asyncio.Task] = None
        self._monitor_task: Optional[asyncio.Task] = None
        
        # Stats
        self._stats = {
            "rx_frames": 0, "tx_frames": 0,
            "barge_ins": 0, "bytes_rx": 0, "bytes_tx": 0,
            "dropped_frames": 0
        }
    
    async def start(self) -> None:
        """Start the audio bridge and frame sender."""
        if self._running:
            return
        if self._send_frame is None:
            raise RuntimeError("Telegram frame sender is not configured")
        self._running = True
        self._call_active = True
        self._loop = asyncio.get_running_loop()
        self._rx_task = asyncio.create_task(self._rx_loop())
        self._tx_task = asyncio.create_task(self._tx_loop())
        self._monitor_task = asyncio.create_task(self._monitor_loop())
    
    async def stop(self) -> None:
        """Stop the audio bridge."""
        self._running = False
        self._call_active = False
        
        for task in [self._rx_task, self._tx_task, self._monitor_task]:
            if task and not task.done():
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
        
        self._mic_bypassed = False
        self._gemini_tx_buffer.clear()
        self._mic_tx_buffer.clear()
        for queue in (self._gemini_tx_queue, self._mic_tx_queue):
            while not queue.empty():
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    break
        
        self._rx_task = self._tx_task = self._monitor_task = None
        self._loop = None
    
    def _enqueue_tx_threadsafe(self, queue: asyncio.Queue, data: bytes) -> None:
        loop = self._loop
        if loop is None:
            self._enqueue_tx(queue, data)
            return
        try:
            current = asyncio.get_running_loop()
        except RuntimeError:
            current = None
        if current is loop:
            self._enqueue_tx(queue, data)
        else:
            loop.call_soon_threadsafe(self._enqueue_tx, queue, data)

    def _append_rx_threadsafe(self, pcm_48k: bytes) -> None:
        max_buffer_bytes = TELEGRAM_FRAME * 2 * 10
        if len(self._rx_buffer) + len(pcm_48k) > max_buffer_bytes:
            overflow = len(self._rx_buffer) + len(pcm_48k) - max_buffer_bytes
            del self._rx_buffer[:overflow]
            self._stats["dropped_frames"] += 1
        self._rx_buffer.extend(pcm_48k)
        self._stats["bytes_rx"] += len(pcm_48k)

    def feed_telegram_rx(self, pcm_48k: bytes) -> None:
        if not self._call_active:
            return
        loop = self._loop
        if loop is None:
            return
        try:
            current = asyncio.get_running_loop()
        except RuntimeError:
            current = None
        if current is loop:
            self._append_rx_threadsafe(pcm_48k)
        else:
            loop.call_soon_threadsafe(self._append_rx_threadsafe, pcm_48k)
    
    @property
    def mic_bypassed(self) -> bool:
        return self._mic_bypassed

    def set_mic_bypass(self, enabled: bool) -> None:
        """Enable/disable mic bypass (route system mic to Telegram instead of Gemini)."""
        self._mic_bypassed = bool(enabled)
    
    def feed_local_mic(self, pcm_16k: bytes) -> None:
        """Queue local microphone audio for Telegram at 48 kHz."""
        if not self._call_active or not self._mic_bypassed:
            return
        usable = pcm_16k[:len(pcm_16k) - (len(pcm_16k) % 2)]
        if not usable:
            return
        samples = np.frombuffer(usable, dtype=np.int16).astype(np.float32) / 32768.0
        resampled = AudioResampler.resample_linear(samples, TELEGRAM_SR / GEMINI_IN_SR)
        pcm_48k = (np.clip(resampled, -1.0, 1.0) * 32767).astype(np.int16).tobytes()
        self._enqueue_tx_threadsafe(self._mic_tx_queue, pcm_48k)

    def feed_gemini_audio(self, pcm_24k: bytes) -> None:
        """Queue Gemini output for Telegram at 48 kHz."""
        if not self._call_active:
            return
        usable = pcm_24k[:len(pcm_24k) - (len(pcm_24k) % 2)]
        if not usable:
            return
        samples = np.frombuffer(usable, dtype=np.int16).astype(np.float32) / 32768.0
        resampled = AudioResampler.resample_linear(samples, RATIO_24_TO_48)
        pcm_48k = (np.clip(resampled, -1.0, 1.0) * 32767).astype(np.int16).tobytes()
        self._enqueue_tx_threadsafe(self._gemini_tx_queue, pcm_48k)

    def _enqueue_tx(self, queue: asyncio.Queue, data: bytes) -> None:
        try:
            queue.put_nowait(data)
        except asyncio.QueueFull:
            try:
                queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
            try:
                queue.put_nowait(data)
            except asyncio.QueueFull:
                self._stats["dropped_frames"] += 1
    
    def get_stats(self) -> dict:
        return dict(self._stats)
    
    # ------------------------------------------------------------------------
    # RX Loop: Telegram (48k) -> Resample 48->16k -> VAD -> mic_queue
    # ------------------------------------------------------------------------
    
    async def _rx_loop(self) -> None:
        """Process incoming Telegram audio."""
        while self._running:
            try:
                # Wait for enough data for one 48k frame
                while len(self._rx_buffer) < TELEGRAM_FRAME * 2 and self._running:
                    await asyncio.sleep(0.005)  # 5ms
                
                if not self._running:
                    break
                
                # Extract one frame
                frame_bytes = bytes(self._rx_buffer[:TELEGRAM_FRAME * 2])
                del self._rx_buffer[:TELEGRAM_FRAME * 2]
                
                # Convert to float32 [-1, 1]
                pcm_48k = np.frombuffer(frame_bytes, dtype=np.int16).astype(np.float32) / 32768.0
                
                # Resample 48k -> 16k (ratio 1/3)
                pcm_16k = AudioResampler.resample_linear(pcm_48k, RATIO_48_TO_16)
                
                # Remote VAD for barge-in detection
                rms = np.sqrt(np.mean(pcm_16k ** 2))
                is_speech = rms > REMOTE_VAD_THRESHOLD
                
                if is_speech:
                    self._remote_vad_frames += 1
                    self._remote_silence_frames = 0
                    if self._remote_vad_frames >= self._VAD_SPEECH_FRAMES and not self._remote_speech_active:
                        self._remote_speech_active = True
                        if self._on_remote_speech:
                            self._on_remote_speech(True)
                        await self._handle_barge_in()
                else:
                    self._remote_silence_frames += 1
                    if self._remote_silence_frames >= self._VAD_SILENCE_FRAMES and self._remote_speech_active:
                        self._remote_speech_active = False
                        if self._on_remote_speech:
                            self._on_remote_speech(False)
                    if self._remote_silence_frames >= 1:
                        self._remote_vad_frames = 0
                
                # Convert back to int16 for Gemini queue
                pcm_out = (pcm_16k * 32767).astype(np.int16).tobytes()
                
                if self._mic_queue is not None:
                    try:
                        self._mic_queue.put_nowait(pcm_out)
                        self._stats["rx_frames"] += 1
                    except asyncio.QueueFull:
                        try:
                            self._mic_queue.get_nowait()
                        except asyncio.QueueEmpty:
                            pass
                        try:
                            self._mic_queue.put_nowait(pcm_out)
                        except asyncio.QueueFull:
                            self._stats["dropped_frames"] += 1
                    
            except asyncio.CancelledError:
                break
            except Exception as e:
                print(f"[AudioBridge] RX loop error: {e}")
                await asyncio.sleep(0.01)
    
    # ------------------------------------------------------------------------
    # TX Loop: 48 kHz frames -> ntgcalls external source
    # ------------------------------------------------------------------------
    
    async def _tx_loop(self) -> None:
        """Send paced 48 kHz frames through ntgcalls."""
        frame_bytes = TELEGRAM_FRAME * 2
        loop = asyncio.get_running_loop()
        next_send = loop.time()
        while self._running:
            gemini_get = asyncio.create_task(self._gemini_tx_queue.get())
            mic_get = asyncio.create_task(self._mic_tx_queue.get())
            try:
                done, pending = await asyncio.wait(
                    [gemini_get, mic_get],
                    timeout=FRAME_MS / 1000.0,
                    return_when=asyncio.FIRST_COMPLETED,
                )
            except asyncio.CancelledError:
                gemini_get.cancel()
                mic_get.cancel()
                await asyncio.gather(gemini_get, mic_get, return_exceptions=True)
                raise
            for task in pending:
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
            if not done:
                now = loop.time()
                if next_send <= now and self._send_frame is not None:
                    try:
                        await self._send_frame(bytes(frame_bytes))
                        self._stats["tx_frames"] += 1
                        self._stats["bytes_tx"] += frame_bytes
                        next_send = now + FRAME_MS / 1000.0
                    except asyncio.CancelledError:
                        raise
                    except Exception as e:
                        self._running = False
                        print(f"[AudioBridge] TX silence frame failed: {e}")
                continue
            chunks = []
            if gemini_get in done:
                chunks.append((self._gemini_tx_buffer, gemini_get.result()))
            if mic_get in done:
                chunks.append((self._mic_tx_buffer, mic_get.result()))
            for buffer, chunk in chunks:
                buffer.extend(chunk)
                while len(buffer) >= frame_bytes and self._running:
                    now = loop.time()
                    if next_send < now:
                        next_send = now + FRAME_MS / 1000.0
                    delay = next_send - now
                    if delay > 0:
                        await asyncio.sleep(delay)
                    frame = bytes(buffer[:frame_bytes])
                    del buffer[:frame_bytes]
                    if self._send_frame is None:
                        self._running = False
                        break
                    try:
                        await self._send_frame(frame)
                    except asyncio.CancelledError:
                        raise
                    except Exception as e:
                        self._running = False
                        print(f"[AudioBridge] TX frame send failed: {e}")
                        break
                    self._stats["tx_frames"] += 1
                    self._stats["bytes_tx"] += len(frame)
                    next_send += FRAME_MS / 1000.0
    
    # ------------------------------------------------------------------------
    # Barge-in Handling
    # ------------------------------------------------------------------------
    
    async def _handle_barge_in(self) -> None:
        """Remote speech started - drop queued assistant audio."""
        self._stats["barge_ins"] += 1
        print("[AudioBridge] Barge-in detected! Draining Gemini queue...")
        drained = 0
        drain_bytes = int(GEMINI_OUT_SR * 2 * BARGE_IN_QUEUE_DRAIN_MS / 1000)
        if self._audio_out_queue is not None:
            while not self._audio_out_queue.empty() and drained < drain_bytes:
                try:
                    chunk = self._audio_out_queue.get_nowait()
                    drained += len(chunk)
                except asyncio.QueueEmpty:
                    break
        self._gemini_tx_buffer.clear()
        while not self._gemini_tx_queue.empty():
            try:
                self._gemini_tx_queue.get_nowait()
            except asyncio.QueueEmpty:
                break
        print(f"[AudioBridge] Drained {drained} bytes from Gemini queue")
    
    # ------------------------------------------------------------------------
    # Monitor Loop: Health checks, stats
    # ------------------------------------------------------------------------
    
    async def _monitor_loop(self) -> None:
        """Periodic health logging."""
        while self._running:
            await asyncio.sleep(5.0)
            if self._running:
                print(f"[AudioBridge] Stats: {self._stats}")


# -----------------------------------------------------------------------------
# Standalone test
# -----------------------------------------------------------------------------

async def _test_bridge():
    """Quick self-test of the resampler."""
    print("Testing AudioResampler...")
    
    # Test 48k -> 16k
    t = np.linspace(0, 1, 48000, dtype=np.float32)
    sig = np.sin(2 * np.pi * 440 * t)  # 440Hz tone at 48k
    resampled = AudioResampler.resample_linear(sig, 1/3.0)
    assert len(resampled) == 16000, f"Expected 16000, got {len(resampled)}"
    print(f"  48k->16k: {len(sig)} -> {len(resampled)}")
    
    # Test 24k -> 48k
    sig24 = np.sin(2 * np.pi * 440 * np.linspace(0, 1, 24000, dtype=np.float32))
    resampled2 = AudioResampler.resample_linear(sig24, 2.0)
    assert len(resampled2) == 48000, f"Expected 48000, got {len(resampled2)}"
    print(f"  24k->48k: {len(sig24)} -> {len(resampled2)}")
    
    print("All tests passed!")


if __name__ == "__main__":
    asyncio.run(_test_bridge())