# 高帧率

「窗口/控制」底部勾选开启。支持版本切换中的全部 10 个版本，包含 1.24e。
启用和关闭前需退出游戏。无需下载：32 位模块随助手一起编译并嵌入 EXE。

每次通过助手启动游戏或录像，FPS 上限跟随主屏当前刷新率：例如系统为 240Hz，目标为 240 FPS。
这是帧率上限，复杂地图、后台运行或显卡设置仍可能使实际帧率低于目标。
开关保存在游戏目录；切换版本、更换助手程序后保持。切换到另一游戏目录时显示该目录自己的状态。
从其他启动器直接启动游戏，也会加载已安装的模块，但使用上次同步的目标值。

## 兼容版本

War3.exe 与 Game.dll 必须具有一致的完整版本号。助手检查磁盘上的限帧指令，模块加载后再次检查内存中的指令；未知构建或指令不匹配时不打补丁。

| 切换版本 | 完整构建号 | 补丁所在模块 / RVA |
|---|---|---|
| 1.20e | 1.20.4.6074 | War3.exe / 0x445FC |
| 1.21 | 1.21.0.6263 | War3.exe / 0x445FC |
| 1.22 | 1.22.0.6328 | Game.dll / 0x61D879 |
| 1.23 | 1.23.0.6352 | Game.dll / 0x61F4F9 |
| 1.24b | 1.24.1.6374 | Game.dll / 0x62DEE9 |
| 1.24e | 1.24.4.6387 | Game.dll / 0x62DF99 |
| 1.25b | 1.25.1.6397 | Game.dll / 0x62D5C9 |
| 1.26 | 1.26.0.6401 | Game.dll / 0x62D7F9 |
| 1.27a | 1.27.0.52240 | Game.dll / 0x5FCF9 |
| 1.27b | 1.27.1.7085 | Game.dll / 0x7B7A9 |

支持默认 DirectX 渲染和 OpenGL；建议使用助手默认的无边框全屏。
“全部版本”指上表中的 10 个内置版本，不包括未经适配的新版或自定义修改构建。
MIX 由游戏的 Miles 系统加载，需要游戏的音频组件完整；禁用音频加载的启动方式可能不加载模块。

## 实现与来源

[native/HighFps.cpp](../native/HighFps.cpp) 是本项目的小型模块，只负责解锁和限帧。
在游戏渲染空闲路径保留连续渲染标志，以 QPC 和高精度等待定时器控制帧间隔。
跳板保存整数寄存器、标志位和 x87/SSE 状态，执行被替换的完整指令后返回原路径。
不改游戏磁盘文件，不调整游戏逻辑时间，不附带宽屏、血条、录像或其他游戏补丁。

调查时参考了 [LoveBeforT/WarcraftHelper 的解锁位置](https://github.com/LoveBeforT/WarcraftHelper/blob/master/WarcraftHelper/plugin/unlockfps.cpp)
和[限帧支持范围](https://github.com/LoveBeforT/WarcraftHelper/blob/master/WarcraftHelper/plugin/fpslimiter.cpp)。
此前接入的完整第三方模块只有 1.27a/b 可指定 FPS 上限，因此改为此处的独立实现，并逐个核对上述构建的指令。
当前发行程序不包含该第三方 DLL，也不再下载它。

## 文件管理与迁移

仅添加以下三个文件到所选游戏目录：

| 文件 | 用途 |
|---|---|
| `War3HelperFps.mix` | 32 位加载模块，运行时选择当前版本 |
| `War3HelperFps.ini` | `[Options]` 下的 `TargetFps` |
| `.war3helper-high-fps` | 模块版本、上次目标值和安装时的 SHA-256 |

War3.exe、Game.dll、地图、存档、录像不会被高帧率开关修改。
同名文件、已知 FPSUnlocker、旧 WarcraftHelper 或 d3d9.dll 冲突时，拒绝覆盖。
关闭前完整校验归属及内容；用户修改的文件不会被覆盖或删除。
缺失文件可以关闭再开启修复。写入异常会回滚；回滚也失败时明确列出未恢复的文件。

旧版助手安装的 `v1.18-20260124-fix` 模块会在开启、启动或切换版本前自动迁移。
仅接受旧管理记录和匹配的固定 SHA-256、INI 内容；先移除旧加载器，再替换其余文件。
迁移失败时恢复整组旧文件。未受管的第三方模块不会自动删除。
原第三方模块的[发布页及 GPL-3.0 源码](https://github.com/LoveBeforT/WarcraftHelper/releases/tag/v1.18-20260124)保留供旧安装追溯。

版本切换前更新为统一模块，保留开关及目标值；下次启动按实际构建选择补丁位置。
高帧率开启时，助手会阻止未适配的构建启动；可退出游戏后关闭该功能。

## 构建与测试

运行助手无需额外运行库。源码构建需 Visual C++ 的 x86/x64 工具和 Windows 10/11 SDK，脚本用 vswhere 自动查找。
模块使用静态 CRT，以资源形式嵌入单个 War3Helper.exe。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-test.ps1
```

常规测试不联网，覆盖模块安装、关闭、插件冲突、失败回滚、用户修改保护、刷新率变化和版本切换后保持开关。
可用本机合法游戏文件验证每个构建的版本号和 PE 指令；每个版本放在单独子目录内，包含 War3.exe 和 Game.dll：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-test.ps1 -FpsVersionsRoot C:\fixtures\war3
```

迁移测试可另外传入旧官方 ZIP：`-HighFpsPackage C:\fixtures\v1.18-20260124-fix.zip`。
游戏文件和第三方二进制不提交到仓库。

### 本机实测（2026-09-13）

RTX 5090 D、3840×2160 / 240Hz、无边框全屏。用 PresentMon 2.5.1 按游戏 PID 采样，
每次 8–10 秒，排除前 2 秒，以剩余呈现间隔的平均值计算 FPS。
以下为本项目模块的结果；1.24b 首次采样有中途掉帧，重新采样后记录如下。

| 版本 / 场景 | 平均 FPS |
|---|---:|
| 1.20e DirectX 主菜单 | 240.04 |
| 1.21 DirectX 主菜单 | 239.20 |
| 1.22 DirectX 主菜单 | 240.00 |
| 1.23 DirectX 主菜单 | 240.03 |
| 1.24b DirectX 主菜单（复测） | 238.84 |
| 1.24e DirectX 主菜单 | 240.00 |
| 1.25b DirectX 主菜单 | 240.00 |
| 1.26 DirectX 主菜单 | 240.01 |
| 1.27a DirectX 主菜单 | 240.01 |
| 1.27b DirectX 主菜单 | 239.85 |
| 1.24e DirectX，Turtle Rock 开局 | 239.58 |
| 1.24e OpenGL，Turtle Rock 开局 | 238.96 |
| 1.24e → 1.27b → 1.24e 往返切换后，DirectX 对局 | 240.00 |

这些是短时间启动和渲染验证；未覆盖每个版本的所有地图、长时间联机和第三方平台组合。
