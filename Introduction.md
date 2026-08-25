# NEUTRINO v3 (CPU)

This extension runs NEUTRINO v3 voicebanks natively in TuneLab 2.0. It uses the original `t.bin`, `p.bin`, `s.bin`, and `v.bin` inference chain and does not call `NEUTRINO.exe`.

The `SHFC` automation changes the style pitch seen by `p.bin` while compensating the generated F0, so it shifts voice color without transposing the rendered melody.
Editing it resynthesizes the affected range and refreshes TuneLab's synthesized-pitch overlay. It deliberately does not rewrite the user's editable pitch curve, and the compensated overlay will not move by the entered cent value.

Open **Settings > Extensions**, then add one or more **Voicebank directories**. A path may be the complete NEUTRINO directory, its `model` directory, or an individual voicebank directory such as `model\ZUNDAMON`. Use `+` to add another path. Leaving the list blank keeps automatic discovery enabled.

Phoneme conversion always uses the dictionary included in this plugin. Files under a configured NEUTRINO `settings\dic` directory are not read.

Voicebank model files are not included. Install and use official NEUTRINO voicebanks separately.
