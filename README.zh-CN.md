# Planify Windows Community

基于 [Planify](https://github.com/alainm23/planify) 的设计与 CalDAV 架构思路重新实现的非官方 Windows 任务客户端。核心功能是与 Nextcloud CalDAV 双向同步。本项目独立维护，不代表上游官方。

[下载安装包](https://github.com/vannear/planify-win/releases/latest) · [English / 构建说明](README.md)

## 安装与使用

下载 Release 中的 x64 MSI，支持 Windows 10 1809 及以上和 Windows 11。包含运行组件，无需单独安装 .NET。当前安装包未进行代码签名。

安装到当前用户的 `%LOCALAPPDATA%\Programs\Planify Windows Community`，并创建开始菜单快捷方式。安装前退出旧版；已有本地数据会继续使用。

1. 打开“账户与连接”，输入 Nextcloud 地址、用户名及应用专用密码。
2. 勾选“记住密码，下次启动自动连接”，认证成功后保存到 Windows 凭据管理器。
3. 点击“同步所有列表”上传和拉取全部列表；也可仅同步当前列表。自动连接不等于自动同步。
4. 编辑任务后先保存，再同步。离线修改会保存在本地待上传队列中。

Apple 提醒事项和本应用连接同一个 Nextcloud CalDAV 账户后，可经服务器交换任务。本应用不直连 iCloud；建议先用独立列表验证所需任务类型。

## 功能与边界

- 全列表一键同步；单个列表失败不阻断其他列表。
- 普通任务新建、标题与备注编辑、完成、重新打开、删除。
- 列表上方可直接输入标题并按 Enter 快速添加；搜索框移至左侧栏。
- 可设置或清除截止日期，并设置 CalDAV 提醒日期与时间。
- SQLite 缓存、持久化离线队列、ETag 并发保护和冲突提示。
- 保留未修改的 Apple 扩展、截止日期、时区、提醒等 iCalendar 字段。
- Windows 凭据管理器保存、替换和忘记密码。
- 中文界面、侧边栏视图、列表计数与应用图标。
- 点击窗口关闭按钮后缩到系统通知区域并继续运行；右键托盘图标可重新打开或退出。

重复任务、例外实例及带组织者或参与者的任务只读。提醒作为任务的 CalDAV / Apple 提醒事项闹钟同步；应用暂不提供 Windows 后台弹窗提醒、后台同步、子任务、自动更新、列表创建或多账号界面。当前为早期社区版本，未覆盖全部 Apple 任务类型。

## 数据与密码

本地任务正文以普通 SQLite 保存于 `%LOCALAPPDATA%\PlanifyWindowsCommunity\tasks.db`，应用不加密任务数据库。备份前退出应用并复制整个目录。

密码单独保存在当前 Windows 用户、本机范围的 Windows 凭据管理器中。换电脑或 Windows 用户需要重新输入。卸载会保留任务数据与凭据；需要移除凭据时，请先在应用中点击“忘记已保存的密码”。这不会撤销 Nextcloud 端的应用专用密码。

## 开发与许可证

构建和安装包生成步骤见英文说明。自动化检查覆盖同步协议、离线队列、批量同步、密码保存策略及截止日期／提醒字段保留；原生凭据和真实服务器兼容性需另行交互测试。

按 GPL-3.0-or-later 开源，见 LICENSE。图标来自上游 Planify，原作者权益保留。本实现由 AI 辅助开发，未获上游背书。第三方组件见 THIRD-PARTY-NOTICES.md。
