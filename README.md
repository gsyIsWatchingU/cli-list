# CLI List

CLI List 是一个面向 Windows 的轻量命令面板。它会在资源管理器右键菜单中添加 `CLI List`，让你从当前目录快速执行常用脚本、打开 PowerShell，或进入带文件预览的目录选择器。

![CLI List 极简像素界面](assets/screenshot.png)

## 核心能力

- 在桌面、目录、文件和磁盘的右键菜单中打开命令面板。
- 使用 `Ctrl + Alt + Space` 在任意位置显示或隐藏命令面板。
- 常驻系统托盘，并将重复启动转交给同一个进程。
- 通过 `commands.json` 管理可执行命令，无需重新编译。
- 支持关键词搜索、标签筛选，并可按使用次数或最近使用排序。
- 在本地 `usage.json` 中记录每个 CLI 的执行次数和最近使用时间。
- 在当前目录直接启动 PowerShell 或其他 CLI。
- 浏览目录并预览文本、代码与常见图片。
- 提供桌面快捷方式和 `cli-list` 终端命令。
- 首次安装后，执行 `git pull` 自动构建、校验并更新已安装程序。
- 使用极简像素风界面与多尺寸 Windows 图标。

## 技术栈

- C# / Windows Forms
- PowerShell
- .NET Framework 4.x 自带编译器
- Windows 注册表 Shell 菜单

## 环境要求

- Windows 10 或 Windows 11
- Windows PowerShell 5.1 或 PowerShell 7
- .NET Framework 4.x
- Microsoft Edge（仅构建图标时使用）

## 快速开始

以管理员身份打开 PowerShell，进入项目目录：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

安装脚本会自动构建和验证，无需提前执行其他命令。

安装完成后，可以通过以下入口启动：

- 在桌面、文件夹或文件上右键，选择 `CLI List`。
- 按下 `Ctrl + Alt + Space` 显示或隐藏命令面板。
- 双击桌面的 `CLI List` 快捷方式。
- 在终端中执行 `cli-list`；如命令不可用，请将 `%USERPROFILE%\bin` 加入 `PATH`。

安装脚本会创建开机启动项。关闭命令面板后程序仍驻留在系统托盘；需要完全退出时，右键托盘图标并选择“退出”。右键菜单或终端再次启动时，当前目录会传递给驻留进程。

程序安装到 `%USERPROFILE%\.cli-list`。首次安装后，其他设备更新到 GitHub 最新版本只需在各自仓库执行：

```powershell
git pull --ff-only
```

版本库中的 Git Hook 会自动执行构建、测试、安装目录同步和驻留进程重启。Git Hook 仅在通过本仓库运行过一次 `install.ps1` 后启用；需要手动同步时可执行 `.\sync-installed.ps1`。

重复安装默认保留本机配置；需要清空本机覆盖并恢复共享配置时执行：

```powershell
.\install.ps1 -ReplaceConfig
```

## 配置命令

仓库中的 `commands.json` 是多端共享配置，推送后可随代码同步。本机专用命令写入 `%USERPROFILE%\.cli-list\commands.local.json`；同一 `Id` 会覆盖共享命令，新 `Id` 会追加命令，也可用 `Disabled` 隐藏共享命令。

普通命令示例：

```json
{
  "Name": "打开 PowerShell",
  "Description": "在当前目录启动 PowerShell",
  "Id": "open-powershell",
  "Tags": ["终端", "本地"],
  "Executable": "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
  "Arguments": "-NoExit",
  "WorkingDirectory": "{context}",
  "CloseAfterLaunch": true
}
```

隐藏某个共享命令：

```json
{
  "Id": "open-vscode",
  "Disabled": true
}
```

`{context}` 表示打开 CLI List 时所在的目录。内置动作 `BrowsePowerShell` 用于打开目录浏览与文件预览窗口。
`Id` 用于关联使用统计，应保持唯一且不要随意修改；`Tags` 用于搜索和标签筛选。`commands.local.json`、迁移备份和统计数据只保存在本机，不会上传。

## 卸载

以管理员身份运行：

```powershell
.\uninstall.ps1
```

卸载会退出驻留进程，并移除右键菜单、桌面快捷方式、开机启动项和终端命令；配置目录仍会保留。

## 当前状态

项目已经可以构建、安装和日常使用，当前重点是完善配置体验与发布流程。功能状态和后续计划见：

- [功能清单](docs/feature-list.md)
- [开发记录](docs/work-roadmap.md)
- [后续计划](docs/plan-list.md)
- [文档维护规范](docs/doc-maintenance.md)

## 许可证

[MIT](LICENSE)
