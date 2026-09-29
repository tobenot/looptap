import wave, struct, math, winsound, time, sys, os

rate = 48000
dur = 90  # play 90s
path = r'D:\hermes-workspace\looptap\test\tone.wav'
os.makedirs(os.path.dirname(path), exist_ok=True)

frames = bytearray()
for i in range(rate * 2):  # 2s of sine, looped
    v = int(32767 * 0.5 * math.sin(2 * math.pi * 440 * i / rate))
    frames += struct.pack('<h', v)
with wave.open(path, 'wb') as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(rate)
    w.writeframes(bytes(frames))

winsound.PlaySound(path, winsound.SND_FILENAME | winsound.SND_ASYNC | winsound.SND_LOOP)
print('PLAYING', flush=True)
time.sleep(dur)
print('DONE', flush=True)
