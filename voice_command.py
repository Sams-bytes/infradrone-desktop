#!/usr/bin/env python3
"""
DAMbv InfraDrone — Voice Command Recognition (with speaker verification
and spoken audio feedback at every stage).

Usage: python3 voice_command.py [seconds_to_record]

Prints stage markers to stdout, flushed immediately, so a caller (C#)
can show live progress instead of waiting silently for the whole
process to finish:
  STAGE:GET_READY
  STAGE:LISTENING
  STAGE:PROCESSING
  STAGE:REJECTED
  STAGE:NO_SPEECH
  STAGE:RESULT:<transcribed text>
Also speaks the key moments out loud via espeak-ng, since this is meant
to be usable without staring at the screen.
"""
import sys
import os
import subprocess
import numpy as np
import sounddevice as sd
from scipy.io.wavfile import write as wav_write
from faster_whisper import WhisperModel
from resemblyzer import VoiceEncoder, preprocess_wav
import tempfile
import time

SAMPLE_RATE = 16000
PROFILE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_profile.npy")
SIMILARITY_THRESHOLD = 0.72

def say(text):
    """Speak out loud AND flush a stage marker to stdout so C# sees it live."""
    print(text, flush=True)
    try:
        subprocess.run(["espeak-ng", text], check=False,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except FileNotFoundError:
        pass  # espeak-ng not installed -- degrade gracefully, still print

def record(seconds):
    print("STAGE:GET_READY", flush=True)
    say("Get ready")
    time.sleep(1.5)
    print("STAGE:LISTENING", flush=True)
    say("Listening now")
    time.sleep(0.8)  # let the TTS audio fully finish before recording starts,
                       # otherwise the mic picks up the announcement itself
    audio = sd.rec(int(seconds * SAMPLE_RATE), samplerate=SAMPLE_RATE, channels=1, dtype='float32')
    sd.wait()
    print("STAGE:PROCESSING", flush=True)
    return audio.flatten()

def main():
    if not os.path.exists(PROFILE_PATH):
        say("No voice profile enrolled. Run enrollment first.")
        print("STAGE:REJECTED", flush=True)
        return

    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 4.0
    audio = record(seconds)

    profile = np.load(PROFILE_PATH)
    encoder = VoiceEncoder()
    wav = preprocess_wav(audio, source_sr=SAMPLE_RATE)
    embedding = encoder.embed_utterance(wav)
    embedding = embedding / np.linalg.norm(embedding)
    similarity = float(np.dot(profile, embedding))
    print(f"SIMILARITY:{similarity:.3f}", flush=True)

    if similarity < SIMILARITY_THRESHOLD:
        say("Voice not recognized. Command rejected.")
        print("STAGE:REJECTED", flush=True)
        return

    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        wav_write(tmp.name, SAMPLE_RATE, (audio * 32767).astype(np.int16))
        wav_path = tmp.name

    model = WhisperModel("base", device="cpu", compute_type="int8")
    segments, info = model.transcribe(wav_path, language="en")
    text = " ".join(seg.text for seg in segments).strip()
    os.unlink(wav_path)

    if not text:
        say("No speech detected. Please try again.")
        print("STAGE:NO_SPEECH", flush=True)
        return

    print(f"STAGE:RESULT:{text}", flush=True)

if __name__ == "__main__":
    main()
