# 部署说明

仓库提供通用配置，真实钉钉用户、应用凭据、API Key、设备密钥和网络地址由部署环境填写。`appsettings.Local.json`、`remote-config.local.json`、HTTP 客户端私密环境和实际部署记录不提交到 Git。

## Linux 后端

1. 在开发环境执行 `scripts/publish-linux.ps1`，生成 `artifacts/publish-linux-x64` 下的自包含程序。脚本只发布，不上传或重启服务。
2. 将产物放入服务目录，示例使用 `/opt/office-mcp`。复制 `deploy/linux/appsettings.Local.example.json` 为该目录中的 `appsettings.Local.json`，填写 API Key、钉钉 ClientId / ClientSecret、固定 Attendance.UserId 和设备凭据。
3. 按实际网络配置 `Urls`：同机网关可用 `http://127.0.0.1:18101`；手机通过 ZeroTier 等私有网络访问时，还需监听服务器实际的私有网络地址，并限制访问来源。
4. 创建独立服务账号 `office-mcp`，确保其可以读取程序和配置、写入 `data`。配置文件仅允许必要账号读取，任务数据不要放在公开目录。
5. 按实际安装目录修改 `deploy/linux/office-mcp.service` 的 `WorkingDirectory`、`ExecStart` 和 `ReadWritePaths`，再交由 systemd 管理。

升级前停止服务，替换程序时保留已有 `appsettings.Local.json` 和 `data/device-commands`，再启动服务。同一状态目录只允许一个后端实例使用。`/healthz` 仅验证进程存活，不证明钉钉接口或手机在线。

远程开关状态及请求去重记录位于 `data/device-commands/automation/state.json`（自定义 `RemoteDevices:StateDirectory` 时随其移动），必须连同原任务一起保留。开关首版部署需更新后端、网关两个新 operationId，以及手机控制模块／定时入口／常驻接收器。新版接收器确认后，`attendance_automation_status` 才会显示 `sync_state=applied`；旧接收器在线不能代替控制版本确认。手机端须先部署模块、再更新入口，确认旧定时引擎已退出并重启接收器；默认初始化开启，不用真实关闭／开启测试代替只读验收。

升级前确认默认员工 `Attendance:UserId` 已填写在 `appsettings.Local.json` 中。旧部署如将真实 ID 放在 `appsettings.json`，应原样迁入私密配置后再替换程序；发布包的基础配置只保留空占位值。

## 手机与网关

手机部署和任务逻辑见 [AutoJs6 说明](../autojs6/README.md)。将 `remote-config.example.json` 复制为同目录的 `remote-config.local.json`，使用手机可访问的真实服务地址和已配对的设备密钥。

网关配置参考 [OpenAPI 示例](../deploy/gateway-office.openapi.yaml) 或 [HTTP 示例](../deploy/gateway-office.http.yaml)，二选一合并到已有服务。网关与后端同机时使用回环地址；凭据从网关自己的私密环境中读取。保留已有认证设置，不用示例整体覆盖线上配置。

## 手机公网 HTTPS 设备入口

手机的公网 `base_url` 使用 `https://mcp.example.com/office_mcp_device_api`。手机仅需普通互联网；本方案不部署办公电脑领取端，不依赖手机 ZeroTier。Nginx 复用现有公网 HTTPS 入口和证书，直接代理设备接口到同机 `office-mcp`，保留现有 MCP 路由及认证。

1. 将 [Nginx 设备路由片段](../deploy/nginx/office_mcp_device_api.locations.conf) 放入服务器 snippets 目录。在当前 HTTPS `server {}` 内增加以下 include，不要将此片段当成独立 server 配置，也不要覆盖既有站点：

   ```nginx
   include /etc/nginx/snippets/office_mcp_device_api.locations.conf;
   ```

2. 核对片段中的 `127.0.0.1:18101` 是实际内部后端地址。默认片段只代理六类设备路径和指定方法：领取、回执、探测应答、执行补报、自动开关同步、任务查询；未知路径返回 404，错误方法返回 405。不代理办公 API、员工查询、OpenAPI 或 healthz。父级不能用 `^~` 前缀遮蔽片段中的正则白名单。
3. 设备继续由 `office-mcp` 校验 `X-Device-Id` 和 `X-Device-Key`，Nginx 不注入身份，并清除不属于设备通道的 `Authorization`／`X-Api-Key` 上游头。不要把网关 OAuth、网关静态密钥或办公 API Key 填入手机，不把密钥加入 URL／访问日志。更换设备密钥时同步更新私密设备配置并重启后端，再更新手机配置；不要改动其他设备登记。
4. 先执行 `nginx -t`，确认现有配置通过后再 reload。长轮询等待原上限 25 秒，片段读取超时 40 秒，关闭缓冲、缓存及代理自动重试。HTTPS 证书必须被手机系统信任；不关闭证书或主机名校验，不依赖 HTTP 重定向升级。
5. 备份手机原 `remote-config.local.json` 和接收器。没有活动打卡时合作停止唯一接收器，更新 `office_remote_listener.js`，只修改配置中的 `base_url`，保留设备密钥、动作参数、定时任务、Task 去重、自动开关及收尾状态，再启动唯一接收器。已有后端无需更改业务代码或设备协议。
6. 上线后只读调用 `attendance_device_status` 验证真实往返，查询原开关确认状态未变。检查不带凭据的设备请求被拒绝、其他业务路径不被此入口代理。不通过启动钉钉、设置开关或切断用户网络验收。

原无路径 ZeroTier／内部 HTTP 配置继续兼容；恢复时仅还原手机连接地址或接收器，保留当前状态，不恢复历史 Task／计划快照。服务入口和网关工具名、参数未改变，无需为本功能新增 MCP 工具或调整网关白名单。完整约定见 [开发目标](device-public-https-prd.md)。

隔离代理检查可在有 Nginx 的 Linux 环境执行 `python3 tests/nginx/device-api-proxy.test.py`；测试只启动回环临时端口上的独立进程和合成上游，不加载或 reload 线上 Nginx。该测试验证代理路由和转发，真实手机 TLS 联通留在上线验收。

## 设备实时在线探测升级

更新后端并在网关加入 `attendance_device_status`，刷新 OpenAPI；手机仅需更新 `office_remote_listener.js` 并安全停止旧实例、启动唯一新版接收器。保留配置、定时任务、全部 Task、自动开关和手机持久化状态。正在执行打卡时先等待完成，不通过手动运行定时入口验收。

新版接收器在领取请求声明 `supports_probe=true`，纯通信挑战通过同一链路下发，再由独立认证路由回传。后端保留旧手机领取协议，新手机也能接收旧后端命令；但旧接收器不支持挑战时，新探测只能超时，不能把旧心跳在线当作升级完成。

上线后只调用新只读工具确认 `online=true/status=online`，检查北京时间字段及原接口兼容；不触发打卡或切换自动开关。断网、应答丢失及重放由合成设备测试覆盖，不为验收强制断开用户手机网络。回滚后端或接收器不需要迁移数据，因为探测仅存于内存。

## 自动开关响应移除旧在线字段

`attendance_automation_status` 和 `attendance_automation_set` 不再返回 `device_online`，调用方需停止依赖这个字段；实时在线改用 `attendance_device_status`。`sync_state` 与 `applied_*` 保留最后一次版本确认的含义，不表示设备此刻在线。

部署本次响应调整只需更新后端、刷新网关 OpenAPI／工具说明。已支持实时探测的手机无需更新或重启；不迁移开关状态、不修改版本、定时计划或历史 Task。上线验收读取状态 JSON 和 OpenAPI，确认两种开关响应模型均无旧字段，专用探测仍可用；不得通过真实开关设置或打卡来验收字段删除。

## 非工作日加班查询升级

本次更新后端并刷新网关 OpenAPI／工具说明，工具名及五个参数保持不变。部署前确认原生 `getattcolumns` 和 `getcolumnval` 在当前应用下可读；部署后只读验证历史工作日、休息日白天完整卡及缺卡日期。确认 `day_type`、`anomalies`、nullable 时长和 `summary.duration_complete` 已在工具定义与真实响应中生效。仅检查 healthz 不足以确认该功能可用。保留配置、自动开关和所有 Task 数据，手机脚本不参与此次查询能力升级。

## Windows 可选部署

`scripts/publish.ps1` 生成 Windows 发布程序，`scripts/install-nssm.ps1` 可注册服务；需要目标主机具备相应 ASP.NET Core 运行时。NSSM 路径、发布目录和服务名可通过脚本参数指定，具体示例见 [项目 README](../README.md)。

不同主机的真实地址、账号路径和运维记录留在本地配置或私有运维文档中，不写入公开示例。

## 时间规范升级

参见 [全项目时间规范](time-conventions.md)。本次需要更新后端、刷新网关 OpenAPI 工具定义，并把包括 `office_time.js` 的七个手机脚本放在同一目录；先更新共用模块再更新入口并重启唯一接收器。保留原配置、状态和定时任务，不运行定时入口做连通性验证。

后端日志正文自带固定 +08:00 时间，不依赖宿主时区；systemd／日志查看工具的外层时间属于系统格式，可用 `TZ=Asia/Shanghai journalctl -u office-mcp` 查看北京时间。AutoJs6 手工创建的周期主任务仍由系统调度，应保持手机系统时区为北京时间；脚本内的随机窗口和一次性任务使用显式北京时间换算，不自动重排已有周期主任务。旧日志和状态文件不批量重写。
