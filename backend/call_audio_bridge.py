"""InCallAudioBridge: Real-time audio bridge between Telegram VoIP (48 kHz mono)
and Gemini Live (16 kHz in / 24 kHz out).

Uses Windows named pipe with ntgcalls MediaSource.EXTERNAL for TX.
RX uses ntgcalls on_frames callback.

Responsibilities:
- Resample Telegram RX (48 kHz) -> Gemini mic queue (16 kHz)
- Resample Gemini output (24 kHz) -> Telegram TX (48 kHz) via named pipe
- Mic bypass: route system mic to Telegram instead of Gemini during call
- Barge-in: detect remote speech, drain Gemini queue to prevent overlap
- Voice activity detection on both directions for smart gating
"""

from __future__ import annotations

import asyncio
import ctypes
from ctypes import wintypes
import math
import threading
import uuid
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
FRAME_MS = 20
TELEGRAM_FRAME = int(TELEGRAM_SR * FRAME_MS / 1000)   # 960 samples
GEMINI_IN_FRAME = int(GEMINI_IN_SR * FRAME_MS / 1000) # 320 samples
GEMINI_OUT_FRAME = int(GEMINI_OUT_SR * FRAME_MS / 1000) # 480 samples

# VAD thresholds
REMOTE_VAD_THRESHOLD = 0.02  # RMS threshold for remote speech detection
LOCAL_VAD_THRESHOLD = 0.01   # RMS threshold for local speech

# Barge-in
BARGE_IN_QUEUE_DRAIN_MS = 100  # ms of Gemini audio to drain on barge-in

# Windows named pipe constants
PIPE_ACCESS_DUPLEX = 0x00000003
PIPE_TYPE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255


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


class NamedPipeWriter:
    """Windows named pipe writer for ntgcalls EXTERNAL source."""
    
    def __init__(self, pipe_name: str):
        self.pipe_name = pipe_name
        self.pipe_handle = None
        self._connected = False
        self._lock = threading.Lock()
        
        # Windows API
        self.kernel32 = ctypes.windll.kernel32
        self.PIPE_ACCESS_DUPLEX = 0x00000003
        self.PIPE_TYPE_BYTE = 0x00000000
        self.PIPE_WAIT = 0x00000000
    
    def create_and_wait(self) -> bool:
        """Create named pipe and wait for ntgcalls to connect."""
        with self._lock:
            if self.pipe_handle is not None:
                return True
            
            self.pipe_handle = self.kernel32.CreateNamedPipeW(
                self.pipe_name,
                self.PIPE_ACCESS_DUPLEX,
                0,  # PIPE_TYPE_BYTE | PIPE_WAIT
                1,  # max instances
                65536,  # out buffer
                65536,  # in buffer
                0,  # default timeout
                None  # security attributes
            )
            
            if self.pipe_handle == -1:
                err = ctypes.GetLastError()
                print(f"[NamedPipe] CreateNamedPipe failed: {err}")
                return False
            
            print(f"[NamedPipe] Created pipe {self.pipe_name}, waiting for connection...")
            
            # Wait for connection in background thread
            def wait_and_connect():
                result = self.kernel32.ConnectNamedPipe(self.pipe_handle, None)
                err = ctypes.GetLastError()
                with self._lock:
                    self._connected = (result != 0 or err == 535)  # 535 = ERROR_PIPE_CONNECTED
                    if self._connected:
                        print(f"[NamedPipe] Client connected!")
            
            threading.Thread(target=wait_and_connect, daemon=True).start()
            return True
    
    def write(self, data: bytes) -> bool:
        """Write audio data to the pipe."""
        with self._lock:
            if self.pipe_handle is None or self.pipe_handle == -1 or not self._connected:
                return False
            
            written = wintypes.DWORD()
            result = self.kernel32.WriteFile(
                self.pipe_handle, data, len(data), ctypes.byref(written), None
            )
            return result != 0
    
    def close(self) -> None:
        with self._lock:
            if self.pipe_handle is not None and self.pipe_handle != -1:
                self.kernel32.CloseHandle(self.pipe_handle)
                self.pipe_handle = None
                self._connected = False


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
    Gemini OUT (24k) -> _audio_out_queue -> resample 24->48k -> named pipe -> Telegram TX
    
    Mic bypass: system mic -> Telegram (bypasses Gemini entirely during call)
    Barge-in: remote speech detected -> drain _audio_out_queue
    """
    
    def __init__(
        self,
        mic_queue: asyncio.Queue,           # Gemini input queue (16k int16 PCM)
        audio_out_queue: asyncio.Queue,     # Gemini output queue (24k int16 PCM)
        on_remote_speech: Optional[Callable[[bool], None]] = None,
        vad_model_path: Optional[str] = None,
    ):
        self._mic_queue = mic_queue
        self._audio_out_queue = audio_out_queue
        self._on_remote_speech = on_remote_speech
        
        # Audio buffers
        self._rx_buffer = bytearray()      # Telegram RX (48k) -> resample -> mic_queue
        
        # Named pipe for Telegram TX
        self._pipe_name = fr"\\.\pipe\ultron_audio_{uuid.uuid4().hex[:8]}"
        self._pipe_writer: Optional[NamedPipeWriter] = None
        
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
            "barge_ins": 0, "bytes_rx": 0, "bytes_tx": 0
        }
    
    def get_pipe_name(self) -> str:
        """Get the named pipe name for ntgcalls EXTERNAL source."""
        return self._pipe_name
    
    async def start(self) -> None:
        """Start the audio bridge and named pipe."""
        if self._running:
            return
        self._running = True
        self._call_active = True
        
        # Create named pipe
        self._pipe_writer = NamedPipeWriter(self._pipe_name)
        if not self._pipe_writer.create_and_wait():
            raise RuntimeError("Failed to create named pipe")
        
        # Wait a bit for connection
        await asyncio.sleep(0.5)
        
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
        
        if self._pipe_writer:
            self._pipe_writer.close()
            self._pipe_writer = None
        
        self._rx_task = self._tx_task = self._monitor_task = None
    
    def feed_telegram_rx(self, pcm_48k: bytes) -> None:
        """Called from ntgcalls on_frames callback (PLAYBACK mode).
        Receives 48kHz mono int16 PCM from Telegram."""
        if not self._call_active:
            return
        self._rx_buffer.extend(pcm_48k)
        self._stats["bytes_rx"] += len(pcm_48k)
    
    def set_mic_bypass(self, enabled: bool) -> None:
        """Enable/disable mic bypass (route system mic to Telegram instead of Gemini)."""
        self._mic_bypassed = enabled
    
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
                
                # Push to Gemini mic queue
                try:
                    self._mic_queue.put_nowait(pcm_out)
                    self._stats["rx_frames"] += 1
                except asyncio.QueueFull:
                    pass
                    
            except asyncio.CancelledError:
                break
            except Exception as e:
                print(f"[AudioBridge] RX loop error: {e}")
                await asyncio.sleep(0.01)
    
    # ------------------------------------------------------------------------
    # TX Loop: Gemini OUT (24k) -> Resample 24->48k -> named pipe -> Telegram
    # ------------------------------------------------------------------------
    
    async def _tx_loop(self) -> None:
        """Process outgoing Gemini audio to Telegram via named pipe."""
        while self._running:
            try:
                # Get audio from Gemini output queue
                pcm_24k = await asyncio.wait_for(
                    self._audio_out_queue.get(),
                    timeout=0.1
                )
                
                if not self._call_active:
                    continue
                
                # Convert to float32
                pcm_24k_f = np.frombuffer(pcm_24k, dtype=np.int16).astype(np.float32) / 32768.0
                
                # Resample 24k -> 48k (ratio 2.0)
                pcm_48k = AudioResampler.resample_linear(pcm_24k_f, RATIO_24_TO_48)
                
                # Local VAD (optional: could gate outgoing)
                rms = np.sqrt(np.mean(pcm_48k ** 2))
                is_local_speech = rms > LOCAL_VAD_THRESHOLD
                if is_local_speech:
                    self._local_vad_frames += 1
                else:
                    self._local_vad_frames = 0
                
                # Convert to int16
                pcm_out = (pcm_48k * 32767).astype(np.int16).tobytes()
                
                # Write to named pipe
                if self._pipe_writer and self._pipe_writer.write(pcm_out):
                    self._stats["tx_frames"] += 1
                    self._stats["bytes_tx"] += len(pcm_out)
                else:
                    print("[AudioBridge] Pipe write failed (not connected?)")
                    
            except asyncio.TimeoutError:
                continue
            except asyncio.CancelledError:
                break
            except Exception as e:
                print(f"[AudioBridge] TX loop error: {e}")
                await asyncio.sleep(0.01)
    
    # ------------------------------------------------------------------------
    # Barge-in Handling
    # ------------------------------------------------------------------------
    
    async def _handle_barge_in(self) -> None:
        """Remote speech started - drain Gemini output queue to prevent overlap."""
        self._stats["barge_ins"] += 1
        print(f"[AudioBridge] Barge-in detected! Draining Gemini queue...")
        
        # Drain the audio output queue (Gemini's response)
        drained = 0
        drain_bytes = int(GEMINI_OUT_SR * 2 * BARGE_IN_QUEUE_DRAIN_MS / 1000)
        while not self._audio_out_queue.empty() and drained < drain_bytes:
            try:
                chunk = self._audio_out_queue.get_nowait()
                drained += len(chunk)
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