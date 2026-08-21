# NEUTRINO v3 (CPU)

This extension runs NEUTRINO v3 voicebanks natively in TuneLab 2.0. It uses the original `t.bin`, `p.bin`, `s.bin`, and `v.bin` inference chain and does not call `NEUTRINO.exe`.

The `SHFC` automation changes the style pitch seen by `p.bin` while compensating the generated F0, so it shifts voice color without transposing the rendered melody.

Open **Settings > Extensions**, then set **NEUTRINO directory** to the directory that contains `model` and `settings`, for example `D:\NEUTRINO`.

Voicebank model files are not included. Install and use official NEUTRINO voicebanks separately.
