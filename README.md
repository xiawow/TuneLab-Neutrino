# TuneLab NEUTRINO v3

TuneLab 2.0 voice synthesis plugin for native NEUTRINO v3 CPU inference.

The plugin runs the original `t.bin`, `p.bin`, `s.bin`, and `v.bin` model
chain directly through ONNX Runtime. It does not call `NEUTRINO.exe` and does
not apply HNSEP or other audio post-processing.

## Requirements

- Windows x64
- TuneLab `release/2.0.0`
- A NEUTRINO v3 installation containing at least one complete voice model
- .NET 8 SDK for building

Place the TuneLab source and this repository next to each other:

```text
workspace/
|-- TuneLab/
\-- TuneLab-Neutrino/
```

Clone the matching TuneLab source with:

```powershell
git clone --branch release/2.0.0 https://github.com/LiuYunPlayer/TuneLab.git
```

## Build

```powershell
.\build.ps1
```

The installable package is written to:

```text
artifacts/TuneLab.NeutrinoV3-win-x64.tlx
```

## Voicebanks

Open TuneLab's extension settings and add one or more voicebank directories.
The first row is blank by default, and the `+` button adds another directory.
Each entry may point to a complete NEUTRINO installation, its `model` directory,
or one individual voicebank directory such as `model/ZUNDAMON`.

A normal installation looks like:

```text
NEUTRINO/
|-- model/
|   \-- MERROW/
|       |-- t.bin
|       |-- p.bin
|       |-- s.bin
|       |-- v.bin
|       \-- info.toml
```

Leaving the list blank keeps automatic discovery enabled. Overlapping entries
are safe: each physical model directory is loaded only once. Phoneme conversion
always uses the dictionary bundled with this plugin, so no dictionary is read
from any configured voicebank directory.

Voice models are not included in this repository. Follow the licenses of
NEUTRINO and each voice library.

## Current Scope

- NEUTRINO v3 only
- CPU inference only
- Japanese kana, supported direct phonemes, and common romaji input
- TuneLab phoneme layout, pinned phonemes, pitch editing, rests, and
  continuation lyrics such as `-`, `+`, and `+~`
- `SHFC` style shift automation from -1200 to +1200 cents with compensated F0

## License

The plugin source code is available under the [MIT License](LICENSE). NEUTRINO,
TuneLab, ONNX Runtime, and voicebank files remain subject to their own licenses.
