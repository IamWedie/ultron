import ntgcalls

# Test SHELL source with a simple command
ad = ntgcalls.AudioDescription(
    ntgcalls.MediaSource.SHELL,
    48000, 1,
    'cmd /c echo hello'
)
print('SHELL AudioDescription created successfully')