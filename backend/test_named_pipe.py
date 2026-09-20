"""Test named pipe approach for EXTERNAL source"""
import ctypes
from ctypes import wintypes
import threading
import time

# Windows API constants
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000

kernel32 = ctypes.windll.kernel32

pipe_name = r'\\.\pipe\ultron_audio_bridge_test'

# Server side: create pipe and wait for connection
def server():
    print('[SERVER] Creating pipe...')
    pipe = kernel32.CreateNamedPipeW(
        pipe_name,
        0x00000003,  # PIPE_ACCESS_DUPLEX
        0,  # PIPE_TYPE_BYTE | PIPE_WAIT
        1,  # max instances
        65536, 65536, 0, None
    )
    
    if pipe == -1:
        print('[SERVER] CreateNamedPipe failed:', ctypes.GetLastError())
        return
    
    print('[SERVER] Pipe created, waiting for connection...')
    result = kernel32.ConnectNamedPipe(pipe, None)
    err = ctypes.GetLastError()
    print(f'[SERVER] ConnectNamedPipe: {result}, error: {err}')
    
    if result or err == 535:  # ERROR_PIPE_CONNECTED
        print('[SERVER] Client connected!')
        
        # Write some data
        for i in range(10):
            data = b'x' * 960 * 2  # 960 samples * 2 bytes
            written = wintypes.DWORD()
            kernel32.WriteFile(pipe, data, len(data), ctypes.byref(written), None)
            print(f'[SERVER] Wrote {written.value} bytes')
            time.sleep(0.02)
        
        kernel32.CloseHandle(pipe)
        print('[SERVER] Done')

# Client side: connect to pipe and read
def client():
    time.sleep(0.5)  # Wait for server to create pipe
    print('[CLIENT] Connecting to pipe...')
    pipe = kernel32.CreateFileW(
        pipe_name,
        GENERIC_READ,
        0, None, OPEN_EXISTING, 0, None
    )
    
    if pipe == -1:
        print('[CLIENT] CreateFile failed:', ctypes.GetLastError())
        return
    
    print('[CLIENT] Connected!')
    
    # Read some data
    for i in range(10):
        buf = ctypes.create_string_buffer(960 * 2)
        read = wintypes.DWORD()
        result = kernel32.ReadFile(pipe, buf, 960 * 2, ctypes.byref(read), None)
        print(f'[CLIENT] Read {read.value} bytes, result={result}')
        time.sleep(0.02)
    
    kernel32.CloseHandle(pipe)
    print('[CLIENT] Done')

# Run test
t1 = threading.Thread(target=server)
t2 = threading.Thread(target=client)
t1.start()
t2.start()
t1.join()
t2.join()
print('Test complete')