# 部署说明

仓库提供通用配置，真实钉钉用户、应用凭据、API Key、设备密钥和网络地址由部署环境填写。`appsettings.Local.json`、`remote-config.local.json`、HTTP 客户端私密环境和实际部署记录不提交到 Git。

## Linux 后端

1. 在开发环境执行 `scripts/publish-linux.ps1`，生成 `artifacts/publish-linux-x64` 下的自包含程序。脚本只发布，不上传或重启服务。
2. 将产物放入服务目录，示例使用 `/opt/office-mcp`。复制 `deploy/linux/appsettings.Local.example.json` 为该目录中的 `appsettings.Local.json`，填写 API Key、钉钉 ClientId / ClientSecret、固定 Attendance.UserId 和设备凭据。
3. 按实际网络配置 `Urls`：同机网关可用 `http://127.0.0.1:18101`；手机通过 ZeroTier 等私有网络访问时，还需监听服务器实际的私有网络地址，并限制访问来源。
4. 创建独立服务账号 `office-mcp`，确保其可以读取程序和配置、写入 `data`。配置文件仅允许必要账号读取，任务数据不要放在公开目录。
5. 按实际安装目录修改 `deploy/linux/office-mcp.service` 的 `WorkingDirectory`、`ExecStart` 和 `ReadWritePaths`，再交由 systemd 管理。

升级前停止服务，替换程序时保留已有 `appsettings.Local.json` 和 `data/device-commands`，再启动服务。同一状态目录只允许一个后端实例使用。`/healthz` 仅验证进程存活，不证明钉钉接口或手机在线。

升级前确认默认员工 `Attendance:UserId` 已填写在 `appsettings.Local.json` 中。旧部署如将真实 ID 放在 `appsettings.json`，应原样迁入私密配置后再替换程序；发布包的基础配置只保留空占位值。

## 手机与网关

手机部署和任务逻辑见 [AutoJs6 说明](../autojs6/README.md)。将 `remote-config.example.json` 复制为同目录的 `remote-config.local.json`，使用手机可访问的真实服务地址和已配对的设备密钥。

网关配置参考 [OpenAPI 示例](../deploy/gateway-office.openapi.yaml) 或 [HTTP 示例](../deploy/gateway-office.http.yaml)，二选一合并到已有服务。网关与后端同机时使用回环地址；凭据从网关自己的私密环境中读取。保留已有认证设置，不用示例整体覆盖线上配置。

## Windows 可选部署

`scripts/publish.ps1` 生成 Windows 发布程序，`scripts/install-nssm.ps1` 可注册服务；需要目标主机具备相应 ASP.NET Core 运行时。NSSM 路径、发布目录和服务名可通过脚本参数指定，具体示例见 [项目 README](../README.md)。

不同主机的真实地址、账号路径和运维记录留在本地配置或私有运维文档中，不写入公开示例。
