import wave, struct, math, winsound, time, os
rate = 48000
path = r'D:\hermes-workspace\looptap\test\tone880.wav'
frames = bytearray()
for i in range(rate * 2):
    v = int(32767 * 0.5 * math.sin(2 * math.pi * 880 * i / rate))
    frames += struct.pack('<h', v)
with wave.open(path, 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
    w.writeframes(bytes(frames))
winsound.PlaySound(path, winsound.SND_FILENAME | winsound.SND_ASYNC | winsound.SND_LOOP)
print('PLAYING 880Hz', flush=True)
time.sleep(60)
