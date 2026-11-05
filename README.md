# Owlpath

**中文** · [English](README.en.md)

**给科研人员的极简代理客户端。** 粘贴订阅，点一下连接，整台电脑或手机即可访问 PubMed 全文、Google Scholar、期刊官网、学术会议和各类科研工具。

Owlpath（原名 World Science）是 [v2rayN](https://github.com/2dust/v2rayN) 的免费开源分支（GPL-3.0），面向第一次使用代理的医生、医学生和科研人员：没有账号、没有复杂设置，界面只保留真正需要的几件事。

<p align="center"><img src="docs/menu-bar-owl.gif" width="360" alt="Mac 菜单栏：没连接时猫头鹰闭眼睡觉，连上后睁眼，有流量时扇动翅膀，流量越大扇得越快"></p>

## 为什么做这个工具

现在很多 AI 服务对 IP 很敏感。用 Clash 负载均衡这类方案时，出口节点经常跳来跳去，对账号来说其实很不稳定：频繁换 IP 容易触发风控，甚至被封号；就算没被封，AI 服务识别到频繁换 IP 之后，后续使用也会很不友好，比如频繁弹出各种人机验证。

## 我们的方案

v2rayN、Clash 这类常见客户端的思路是节点越多越好、自动切换。我们推荐的做法不一样：**订阅里只放一个固定 IP 的节点**。你只需要填一个订阅链接就能直接用，出口 IP 始终不变，使用过程会非常稳定。在 AI 时代，这是一个很有意思、也更稳妥的方案。

## 适合谁

Owlpath 基于 [v2rayN](https://github.com/2dust/v2rayN) 开发，非常适合第一次用代理的新手直接上手：填一个订阅链接，点一下连接就能用。已经熟悉 v2rayN、Clash 的用户就不需要用它了，原版更灵活。

## 功能

- **通用订阅**：支持任何服务商的标准订阅链接（vmess / vless / trojan / shadowsocks / hysteria2 / tuic 分享链接及其 Base64 订阅），也可以直接粘贴节点链接。
- **一键全局连接**：默认整机流量走代理（Mac / Windows 使用 TUN，Android 使用系统 VPN），浏览器和桌面软件无需单独设置。
- **自动选线**：后台测量延迟和下载速度，自动选择可用节点，故障时自动切换；也可以按国家 / 地区手动选择。手动选定的线路连不上时，会临时改用最快的可用线路，恢复后自动切回（电脑版可在设置里关闭）。订阅里只有一个固定 IP 节点时，出口 IP 始终不变。
- **猫头鹰**：没连接时是白天，猫头鹰闭眼睡觉；连接中天色变成黄昏，它半睁着眼；连上后变成黑夜，月亮升起，猫头鹰的眼睛亮起来。
- **Mac 菜单栏**：猫头鹰没连接时睡觉，连上后睁眼，有流量时扇动翅膀，流量越大扇得越快，没有流量时静止；旁边显示实时上传 / 下载速度。系统开启“减少动态效果”时不播放动画。
- **省心**：开机启动、自动连接、检测冲突的其他代理软件、一键导出诊断信息（自动隐藏订阅地址和 IP）、应用内更新（SHA-256 校验）。
- **中英双语**：跟随系统语言，可在设置中切换。

## 下载

| 平台 | 安装包 |
| --- | --- |
| Mac（Apple Silicon / Intel，macOS 13.6+） | `World-Science-<版本>-macos-arm64.dmg` / `-macos-x64.dmg` |
| Windows 10/11（x64 / ARM64） | `World-Science-<版本>-windows-x64.zip` / `-windows-arm64.zip` |
| Android 8+（ARM64 / x86_64） | `World-Science-<版本>-android.apk` |

安装包文件名暂时沿用旧名 `World-Science-`，这样已安装的旧版本仍能自动更新。

- GitHub：[Releases](https://github.com/robintheai/world-science/releases)
- 国内镜像：<https://ai.z5a.com/clients/world-science/>（GitHub 打不开时使用，文件和校验值完全相同）

第一次安装请看 [安装教程](INSTALL.md)。iOS 版暂未提供（需要 Apple 开发者签名）。

## 三步开始

1. 打开 Owlpath，在「订阅」里粘贴服务商给你的订阅链接，点「添加订阅」。
2. 回到首页，保持「自动」或在「选择线路」中选一个地区。
3. 点中间的圆形按钮连接；再点一次断开。

Owlpath 本身不提供节点。节点是否可用、速度和流量由你的订阅服务商决定。

## 隐私

- 不收集任何使用数据，没有账号系统，没有统计或广告 SDK。
- 订阅地址只保存在本机（Android 使用系统加密存储），日志和诊断信息不包含订阅地址、节点密码或访问记录。
- 联网请求只有：你的订阅地址、你选择的节点、测速 / 连通性检测地址，以及检查更新（先查国内镜像，失败时查 GitHub）。

## 从源码构建

需要 .NET 10 SDK、Python 3；Mac 包需要 Xcode 命令行工具。

```bash
python3 scripts/build.py --rid osx-arm64 --version 1.0.0 --output dist
```

`--rid` 可选 `osx-arm64`、`osx-x64`、`win-x64`、`win-arm64`。内核来自 [v2rayN-core-bin](https://github.com/2dust/v2rayN-core-bin) 的固定提交，构建时校验 SHA-256（见 `scripts/core-checksums.json`）。

测试：

```bash
dotnet run --project tests/Simple.Smoke -c Release
```

```bash
WORLD_SCIENCE_DATA_DIR="$(mktemp -d)" ./build/osx-arm64/WorldScience --integration-test=test-results
```

Android 和 Go 内核见 [mobile/README.md](mobile/README.md)。猫头鹰图标和菜单栏动画的矢量源码在 `assets/brand/owl/`。

## 许可与致谢

GPL-3.0，见 [LICENSE](LICENSE)。基于 [2dust/v2rayN](https://github.com/2dust/v2rayN)，内核为 [Xray-core](https://github.com/XTLS/Xray-core)、[sing-box](https://github.com/SagerNet/sing-box)、[mihomo](https://github.com/MetaCubeX/mihomo)，完整清单见 [THIRD-PARTY.md](THIRD-PARTY.md)。

本项目不是 v2rayN 官方版本，与任何订阅服务商无关。请遵守你所在地的法律法规。

---

English: see [README.en.md](README.en.md).
