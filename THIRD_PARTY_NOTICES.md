# Third-Party Notices

Deadlock MVM vendors Dear ImGui 1.91.9b for its in-game interface and libgmavi
for first-party uncompressed AVI recording.

Dear ImGui is distributed under the MIT License. The vendored DX11 backend is
adapted to use stable default buffers and `UpdateSubresource` uploads for
Deadlock's bounded render-resource registry. The complete vendored license is
available at [`native/third_party/imgui/LICENSE.txt`](native/third_party/imgui/LICENSE.txt).

libgmavi is Copyright (c) 2022 Gijs Oosterling and is distributed under the MIT
License. DeadLockMVM vendors the reviewed source revision
`b194ea163230aced846ba0703ad41dbdd5582d02` with narrow correctness and cleanup
fixes. The complete vendored license is available at
[`native/third_party/libgmavi/LICENSE`](native/third_party/libgmavi/LICENSE).

This notice does not grant a license for Deadlock MVM itself; the repository currently has no root project license file.
