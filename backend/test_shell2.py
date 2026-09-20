import ntgcalls
import tempfile
import os

# Create a simple audio generator script
gen_script = r'''
import sys
import time
import math

SAMPLE_RATE = 48000
DURATION = 5  # seconds
FREQ = 440  # Hz

# Generate 440Hz sine wave
samples = int(SAMPLE_RATE * DURATION)
for i in range(samples):
    t = i / SAMPLE_RATE
    val = int(0.3 * 32767 * math.sin(2 * math.pi * 440 * t))
    # Write as 16-bit little-endian
    sys.stdout.buffer.write(val.to_bytes(2, 'little', signed=True))
    # Flush every 20ms (960 samples)
    if i % 960 == 0:
        sys.stdout.flush()
        time.sleep(0.02)
'''

# Write the generator script
with tempfile.NamedTemporaryFile(mode='w', suffix='.py', delete=False) as f:
    f.write(gen_script)
    gen_path = f.name

try:
    # Test SHELL source with the generator
    ad = ntgcalls.AudioDescription(
        ntgcalls.MediaSource.SHELL,
        48000, 1,
        f'python "{gen_path}"'
    )
    print('SHELL AudioDescription with generator created successfully')
finally:
    os.unlink(gen_path)