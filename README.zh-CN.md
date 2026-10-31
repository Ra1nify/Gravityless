<div align="center">

<img src="src/Gravityless.App/Assets/brand.png" width="120" alt="Gravityless">

# Gravityless

Yet Another Spicy Companion Of Antigravity

</div>

[English](README.md) | **简体中文**


Gravityless 在 Windows 上并行运行多个相互隔离的 Antigravity 实例，管理一个 Google 账号池，并为应用注入原生窗口材质。


## 前置条件

- Windows 10 1809 或更高
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## 功能

| 方面 | 说明 |
| --- | --- |
| 实例 | 每个实例拥有独立的 `--user-data-dir`，并重定向 `USERPROFILE` / `APPDATA` / `LOCALAPPDATA` |
| 号池 | 通过一次性沙盒捕获 OAuth 凭据，并约束一个账号同时只被一个运行中实例占用 |
| 项目记忆库 | 每个项目独立的 `brain/` 与会话数据库，按需挂载，可按步骤分叉 |
| 共享扩展 | `<安装目录>\shared\` 统一存放技能、插件与 MCP，用 Junction 挂载进每个实例 |
| 网络 | 直连、跟随系统、或手动 SOCKS5/HTTP/HTTPS，并提供连通性与授权探针 |
| 核心管理 | 探测本机已装版本，或按官方更新清单下载，SHA512 校验后部署 |
| 材质 | 向 Antigravity 注入 Mica、Tabbed、Acrylic 背板，支持实时调整 |
| 托盘与语言 | 关闭窗口后驻留托盘；自身界面与注入的 Antigravity 界面同步切换中英文 |

---

## 文档

完整文档位于 [Wiki](https://github.com/Ra1nify/Gravityless/wiki)。

| 页面 | 内容 |
| --- | --- |
| [概览](https://github.com/Ra1nify/Gravityless/wiki/Overview) | 版本、更新检查、仓库入口 |
| [实例](https://github.com/Ra1nify/Gravityless/wiki/Instances) | 创建、绑定、启动与停止实例 |
| [项目](https://github.com/Ra1nify/Gravityless/wiki/Projects) | 导入、分叉、重命名、记忆库、会话 |
| [工具](https://github.com/Ra1nify/Gravityless/wiki/Tools) | 技能、插件与 MCP 的共享池 |
| [代理](https://github.com/Ra1nify/Gravityless/wiki/Proxy) | 直连、跟随系统、手动代理 |
| [号池](https://github.com/Ra1nify/Gravityless/wiki/Accounts) | 捕获、绑定、互斥锁、额度刷新 |
| [外观](https://github.com/Ra1nify/Gravityless/wiki/Appearance) | 材质、透明度、弹窗噪点、皮肤、主题、语言 |
| [多开](https://github.com/Ra1nify/Gravityless/wiki/Multi-Instance) | 为什么并发不同账号已不可行 |
| [数据布局](https://github.com/Ra1nify/Gravityless/wiki/Data-Layout) | 运行时目录结构与文件含义 |
| [故障排查](https://github.com/Ra1nify/Gravityless/wiki/Troubleshooting) | 常见故障与处理方式 |

---

## 说明

- Gravityless 修改本地 Antigravity 安装，不重新分发 Google 的二进制文件。
- Antigravity 2.28 把 OAuth 凭据迁移到 Windows 凭据管理器的单一全局条目，不同账号无法再同时运行，详见 [多开](https://github.com/Ra1nify/Gravityless/wiki/Multi-Instance)。
- `data\` 目录存放明文凭据，应视为敏感数据，移动或重装前请备份。

---

## 构建

需要 .NET 8 SDK。

```powershell
dotnet build Gravityless.sln
dotnet run --project src\Gravityless.App\Gravityless.App.csproj
```

---

## 许可证

GNU General Public License v3.0，见 [LICENSE](LICENSE)。

---

## 问题反馈

欢迎在 [Issues](../../issues) 提交问题与功能请求。

反馈时请附上概览页中的 Antigravity 版本、Gravityless 版本，以及故障排查页中的相关输出。

---

s∞n
