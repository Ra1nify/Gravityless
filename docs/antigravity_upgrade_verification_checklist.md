# Antigravity 版本升级适配验证检查清单

本文档为 Gravityless 对 Antigravity 本体进行版本迭代适配时的标准检查流程规范（SOP）。每次官方发布新版本（例如 2.29.0 -> 2.30.0）并更新 core 核心后，必须按本清单逐项验证与自测。

---

## 一、版本与运行时基础环境验证

1. **版本元数据读取**
   - 检查 `core\Antigravity.exe` 的文件版本与产品版本：
     ```powershell
     (Get-Item "$coreDir\Antigravity.exe").VersionInfo | Select-Object ProductVersion, FileVersion
     ```
   - 检查 `core\resources\app\package.json` 中的 `version` 字段与依赖项变动。
2. **底层运行时内核检测**
   - 查看 Electron、Chromium、Node.js 版本：
     ```powershell
     $env:ELECTRON_RUN_AS_NODE="1"; & "$coreDir\Antigravity.exe" -e "console.log(process.versions)" | Out-String
     ```
   - 若 Electron 大版本升级（如 3x -> 4x），重点关注窗口及视图 API 变更。

---

## 二、主进程窗口架构与材质（Mica/Tabbed/Acrylic）验证

1. **窗口宿主类检测**
   - 检查 `dist/utils.js`、`dist/main.js` 中主窗口实例化类：
     - 是 `new electron.BrowserWindow` 还是 `new electron.BaseWindow`？
   - 验证 `PatchService.cs` 中的 Hook 覆盖：
     - `BrowserWindow.prototype.setTitleBarOverlay` / `BaseWindow.prototype.setTitleBarOverlay`
     - `BrowserWindow.prototype.setBackgroundColor` / `BaseWindow.prototype.setBackgroundColor`
     - 构造函数参数：`backgroundMaterial`、`backgroundColor = '#00000000'`、`titleBarOverlay.color = '#00000000'`。
2. **底层遮挡幕布（curtainView / underlay）排查**
   - 搜索 `curtainView` 或其它置于前景 `hubWcv` 底层的 `WebContentsView`：
     - 检查 `addChildView(curtainView)` 逻辑。
     - 检查 `curtainView.webContents.loadURL` 或 `setHubCurtainColor` 是否注入不透明纯色 HTML/CSS。
     - 检查 `syncHubViewBounds` 是否将幕布铺满全屏窗口。
   - **拦截规则**：在 `addChildView` 中将 `curtainView` 尺寸锁定为 `0, 0, 0, 0`，同时设置 `setVisible(false)` 与 `setBackgroundColor('#00000000')`，防止物理黑底遮盖 DWM 背板。
3. **动态材质切换测试（Daemon SSE）**
   - 在 Gravityless 外观设置中切换 `Mica`、`Tabbed`、`Acrylic`、`关闭`：
     - 验证 `applyMaterialToWindows` 是否向 `BaseWindow.getAllWindows()` 与 `BrowserWindow.getAllWindows()` 同步推送。
     - 验证关闭材质时是否恢复原色实底，不残留透明异常。

---

## 三、前端渲染层与样式注入（CSS / DOM）验证

1. **根容器与画布透明度**
   - 验证注入样式中的选择器是否完全覆盖当前版本的根节点：
     - `html, body, #root, .antigravity-layout, .antigravity-shell` 强制透明。
     - `--background: rgba(surface, alpha) !important` 驱动的透明度滑块生效。
2. **Monaco Editor 与终端代码块隔离**
   - 验证代码编辑框（`.monaco-editor`）、Diff 对比区与终端（`.xterm`）未被错误透明化，保持深色/浅色高对比度可读性。
3. **汉化与 i18n 注入验证**
   - 检查 `injectI18n` 在 `dom-ready` 下的注入情况。
   - 验证界面元素、菜单项与托盘文本是否正确按所选语言呈现。

---

## 四、号池捕获与配额探针协议验证

1. **OAuth 捕获链路验证**
   - 触发一次登录，验证一次性沙盒重定向 `antigravity://` 协议是否被宿主拦截捕获并写入 `accounts.json`。
2. **配额探针端点验证**
   - 验证 `https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary` 接口连通性。
   - 确认响应包含 `5h`（5小时平滑滚动窗口）与 `weekly`（7天周套餐窗口）分组。
   - 验证 Access Token 过期时（>1h）能够自动通过 Refresh Token 完成换票，无 401 报错。

---

## 五、项目记忆库与会话存储隔离验证

1. **存储路径与数据库架构**
   - 检查 Antigravity 是否变更了会话数据库或缓存路径（如 `state.vscdb`、`db.sqlite`）。
   - 验证 `ProjectMemoryService` 的 Junction / 符号链接挂载是否正常。
2. **多开互斥锁与隔离验证**
   - 启动不同实例，验证各实例的 `USERPROFILE` / `APPDATA` / `LOCALAPPDATA` 完全隔离。
   - 验证 Windows Credential Manager 的凭据互斥锁（一个账号同一时间仅被一个运行中实例占用）。

---

## 六、新模块与非预期行为排查

1. **安装向导或弹窗探测**
   - 检查 `dist/` 下是否有新增模块（例如 2.30.0 新增的 `ideInstall` IDE 安装向导）。
   - 确保新增模块不会在多开沙盒中触发非预期的弹窗或后台网络下载。
2. **自动更新静默屏蔽**
   - 确认 `updater.js` / `electron-updater` 无法在未受控情况下自动覆盖 core 目录。

---

## 七、验证通过标准

- `dotnet build Gravityless.sln` 零警告、零错误。
- 启动 Antigravity 实例，Mica / Tabbed 材质立即可见，无黑底遮挡。
- 外观滑块实时调节半透明比例平滑生效。
- 账号切换、会话查看、项目记忆挂载均无报错。
