# Test fixtures

## `titanet-chirp.json`

Reference values for `tests/Nytka.Audio.Tests/Voice`: a synthetic 2 s signal (a 150 to 3,500 Hz chirp
with its second harmonic, a 4 Hz envelope and uniform noise from a seeded linear congruential
generator; no voice, recorded or synthetic), the log-mel features `kaldi-native-fbank` computes for it
with the options sherpa-onnx uses for NeMo speaker models, and the TitaNet-small fingerprint sherpa-onnx
computes for it. The test rebuilds the signal in C# (`Chirp.Signal`, which must stay identical to
`signal()` below), then checks its own features (max abs error < 1e-3) and fingerprint (cosine >= 0.999)
against these.

The build never runs the generator. To make the file again, with the model fetched by
`scripts/fetch-speaker-model.sh`, save the script below as `generate.py` and run:

```bash
uv run --python 3.12 --with sherpa-onnx==1.13.8 --with kaldi-native-fbank==1.22.3 --with numpy==2.5.3 \
  python3 generate.py src/Nytka.Audio/Models/nemo_en_titanet_small.onnx tests/fixtures/titanet-chirp.json
```

Versions used for the committed file: Python 3.12.13, sherpa-onnx 1.13.8 (with sherpa-onnx-core 1.13.8),
kaldi-native-fbank 1.22.3 (the version sherpa-onnx 1.13.8 builds against, `cmake/kaldi-native-fbank.cmake`),
numpy 2.5.3, uv 0.11.18, macOS arm64.

```python
# Writes titanet-chirp.json: a synthetic 2 s chirp with seeded noise, its log-mel features from
# kaldi-native-fbank and its TitaNet-small fingerprint from sherpa-onnx.
import json, math, sys
import numpy as np
import kaldi_native_fbank as knf
import sherpa_onnx

model, out = sys.argv[1], sys.argv[2]
RATE, SECONDS = 16000, 2.0
F0, F1 = 150.0, 3500.0


def signal():
    n = int(RATE * SECONDS)
    state = 12345
    samples = np.empty(n, dtype=np.float32)
    for i in range(n):
        t = i / RATE
        phase = 2 * math.pi * (F0 * t + (F1 - F0) * t * t / (2 * SECONDS))
        envelope = 0.6 + 0.4 * math.sin(2 * math.pi * 4 * t)
        state = (state * 1664525 + 1013904223) % 2**32
        noise = (state / 2**32 - 0.5) * 0.04
        samples[i] = 0.4 * envelope * math.sin(phase) + 0.15 * math.sin(2 * phase) + noise
    return samples


samples = signal()

opts = knf.FbankOptions()
opts.frame_opts.samp_freq = RATE
opts.frame_opts.frame_length_ms = 25
opts.frame_opts.frame_shift_ms = 10
opts.frame_opts.dither = 0
opts.frame_opts.snip_edges = True
opts.frame_opts.remove_dc_offset = False
opts.frame_opts.preemph_coeff = 0.97
opts.frame_opts.window_type = "hann"
opts.mel_opts.num_bins = 80
opts.mel_opts.low_freq = 0
opts.mel_opts.high_freq = -400
opts.mel_opts.is_librosa = True
fbank = knf.OnlineFbank(opts)
fbank.accept_waveform(RATE, samples.tolist())
fbank.input_finished()
features = [[round(float(v), 6) for v in fbank.get_frame(i)] for i in range(fbank.num_frames_ready)]

config = sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=model, num_threads=1)
extractor = sherpa_onnx.SpeakerEmbeddingExtractor(config)
stream = extractor.create_stream()
stream.accept_waveform(sample_rate=RATE, waveform=samples)
stream.input_finished()
fingerprint = [float(v) for v in extractor.compute(stream)]

with open(out, "w") as f:
    json.dump({"sampleRate": RATE, "seconds": SECONDS, "features": features, "fingerprint": fingerprint},
              f, separators=(",", ":"))
    f.write("\n")
print(len(features), len(features[0]), len(fingerprint))
```
