# Third-party notices for the standalone Rime engine

The binary in this directory is the official `librime 1.17.0` Windows MSVC
x64 build (`33e7814`). Rime is BSD-3-Clause. Its Windows release links the
following static dependencies into `rime.dll`; the corresponding license texts
are kept beside this file:

| Component | Release source revision | License file |
| --- | --- | --- |
| Boost | 1.89.0 | `Boost-Software-License-1.0.txt` |
| google-glog | `7b134a5c82c0c0b5698bb6bf7a835b230c5638e` | `google-glog-BSD-3-Clause.txt` |
| LevelDB | `99b3c03b3284f5886f9ef9a4ef703d57373e61be` | `LevelDB-BSD-3-Clause.txt` |
| marisa-trie | `3e87d53b78e15f2f43783d5e376561a8c9722051` | `marisa-trie-BSD-2-Clause-LGPL-2.1.txt` |
| OpenCC | `556ed22496d650bd0b13b6c163be9814637970ae` | `OpenCC-Apache-2.0.txt` |
| yaml-cpp | `2f86d13775d119edbb69af52e5f566fd65c6953b` | `yaml-cpp-MIT.txt` |
| darts-clone headers from the official dependency bundle | release bundle | `darts-clone-license.txt` |

The pinyin dictionary is `rime-pinyin-simp` commit
`0c6861ef7420ee780270ca6d993d18d4101049d0`, under Apache-2.0; its license is
`rime-pinyin-simp-Apache-2.0.txt`. The schema in `data/` is a modified
ClaudeZhInput configuration and carries its modification note in the file.

No Weasel frontend, GPL frontend code, rime-prelude, rime-essay or
rime-luna-pinyin data is included.

Official sources:

- https://github.com/rime/librime/releases/tag/1.17.0
- https://github.com/rime/librime/tree/33e7814
- https://github.com/rime/rime-pinyin-simp/tree/0c6861ef7420ee780270ca6d993d18d4101049d0

