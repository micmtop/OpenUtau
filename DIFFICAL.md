# Diffical 分支说明

本 fork 只维护一个补丁：`OpenUtau.Core/Diffical/`（3 个文件）与
`OpenUtau.Core/Render/Renderers.cs` 里的渲染器注册。

- 后端（Diffical 引擎）：https://github.com/micmtop/diffical
- 默认后端地址：`http://127.0.0.1:8573/`，可用 `DIFFICAL_SERVER_URL` 覆盖
- 后端启动路径可用 `DIFFICAL_BACKEND_PATH` / `DIFFICAL_PYTHON` 指定

## 自动化

- `Sync upstream`（每天 03:00 UTC）：把 `openutau/OpenUtau` 的 master 拉到分支
  `upstream`，再把 `diffical` rebase 到最新上游；补丁冲突时 job 失败并提示手动处理。
- `Build (win/osx/linux)`：push 到 `diffical` 时构建三端，产物在 Actions 的
  artifact 里（`OpenUtau-win-x64` / `OpenUtau-osx-arm64` / `OpenUtau-linux-x64`）。

## 说明

- 自动音高（AutoPitch / PrettyPitch）不在本 fork 中，相关改动不会上传。
- 本 fork 的 `diffical` 分支是默认分支；`master` 仅用于跟随上游。
