#!/usr/bin/env python3
"""
DAMbv InfraDrone — Voice Profile Enrollment
Records the operator's voice and saves a reference "voiceprint" that
future voice commands are checked against, so someone else's voice
nearby can't trigger flight commands.

Usage: python3 enroll_voice.py
"""
import sys
import numpy as np
import sounddevice as sd
from resemblyzer import VoiceEncoder, preprocess_wav

SAMPLE_RATE = 16000
PROFILE_PATH = "voice_profile.npy"

def record(seconds, prompt):
    input(f"\n{prompt}\nPress Enter, then speak for {seconds:.0f} seconds...")
    print("Recording...")
    audio = sd.rec(int(seconds * SAMPLE_RATE), samplerate=SAMPLE_RATE, channels=1, dtype='float32')
    sd.wait()
    print("Done.")
    return audio.flatten()

def main():
    print("=== DAMbv InfraDrone Voice Enrollment ===")
    print("We'll record your voice 3 times to build a reliable profile.")
    print("Speak naturally, as you would when giving a flight command.\n")

    encoder = VoiceEncoder()
    embeddings = []
    prompts = [
        "Recording 1 of 3: say 'InfraDrone, this is my voice.'",
        "Recording 2 of 3: say 'Take off and hold position.'",
        "Recording 3 of 3: say 'Mark this location for inspection.'",
    ]
    for prompt in prompts:
        audio = record(4, prompt)
        wav = preprocess_wav(audio, source_sr=SAMPLE_RATE)
        embeddings.append(encoder.embed_utterance(wav))

    profile = np.mean(embeddings, axis=0)
    profile = profile / np.linalg.norm(profile)
    np.save(PROFILE_PATH, profile)
    print(f"\nVoice profile saved to {PROFILE_PATH}")
    print("Voice commands will now only work for this voice.")

if __name__ == "__main__":
    main()
