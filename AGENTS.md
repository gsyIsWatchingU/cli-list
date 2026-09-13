# 项目维护要求

- 所有说明文档和用户可见文案使用简体中文。
- 每次完成代码、配置或文档更新后，必须执行 `./test.ps1`。
- 测试通过后，只提交本次任务相关文件，并推送到 GitHub `origin/main`。
- 推送后必须确认本地 `HEAD` 与 `origin/main` 一致，并检查 GitHub Actions 构建结果。
- 不得提交本机 `commands.json`、`usage.json`、密钥、临时截图或构建产物。
- 安装更新默认保留 `%USERPROFILE%\.cli-list\commands.json`，除非用户明确要求覆盖。
