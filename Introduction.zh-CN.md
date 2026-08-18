# NEUTRINO v3（CPU）

这个扩展让 TuneLab 2.0 直接推理 NEUTRINO v3 声库。它按原版顺序运行 `t.bin`、`p.bin`、`s.bin` 和 `v.bin`，不会调用 `NEUTRINO.exe`，也没有加入 HNSEP 或其它音频后处理。

请在 **设置 > 扩展** 中填写 **NEUTRINO 目录**。这里应当是同时包含 `model` 和 `settings` 文件夹的目录，例如 `D:\NEUTRINO`。

安装包不包含声库模型，请另外安装并遵守官方 NEUTRINO 声库的许可协议。
