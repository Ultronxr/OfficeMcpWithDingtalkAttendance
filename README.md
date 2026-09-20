# Office HTTP API

通用办公 HTTP 服务，目标框架 `net8.0`，提供钉钉考勤查询及手机远程打卡。可在 Linux 上由 systemd 托管，也可在 Windows 本地运行。[架构与接口约定](docs/architecture.md)说明了模块边界。

AutoJs6 远程打卡与上午本地定时打卡共用持久化 Task 和钉钉 API 核验。定时动作后由常驻接收器上报执行事实、取回结果并写入手机日志，断网只补传事实，不重做动作。手机部署见 [定时与远程打卡说明](autojs6/README.md)，当前部署和运维见 [部署说明](docs/deployment.md)。

## 部署方式

- Linux 示例服务目录为 `/opt/office-mcp`，服务名为 `office-mcp.service`，按实际环境调整。
- gateway 与后端同机时，通过 `127.0.0.1:18101` 访问后端。
- 手机通过 ZeroTier 等私有网络访问服务器；文中的 `192.0.2.10`、`mcp.example.com` 均为占位地址，使用前必须替换。
- 使用 `scripts/publish-linux.ps1` 发布 Linux 自包含程序；升级保留 `appsettings.Local.json` 和 `data/device-commands`。
- 下方 NSSM 操作适用于选择 Windows 部署的环境。

## 本地启动

在项目根目录执行：

```powershell
dotnet run --project src/OfficeMcp.Api
```

默认仅监听 `http://127.0.0.1:18101`。首次启动前，将 `deploy/appsettings.Local.example.json` 复制为 `src/OfficeMcp.Api/appsettings.Local.json`，填写自己的钉钉凭据、API Key 和固定用户 ID。该文件被 Git 忽略，构建时复制到程序目录，发布时不复制。

新环境可从 `deploy/appsettings.Local.example.json` 复制到程序目录并填写凭据。配置按下列顺序覆盖：基础配置、环境配置、本机配置、环境变量、命令行。修改本机配置后重启生效。也可以使用环境变量：

| 配置 | 环境变量 | 用途 |
|---|---|---|
| `Authentication:ApiKey` | `Authentication__ApiKey` | 后端独立认证密钥，32 至 512 字符 |
| `DingTalk:ClientId` | `DingTalk__ClientId` | 钉钉 Client ID / AppKey |
| `DingTalk:ClientSecret` | `DingTalk__ClientSecret` | 钉钉 Client Secret / AppSecret |
| `DingTalk:TimeoutSeconds` | `DingTalk__TimeoutSeconds` | 单次钉钉 HTTP 超时，默认 15 秒 |
| `Attendance:UserId` | `Attendance__UserId` | 默认查询员工及主动打卡固定员工，必须由部署环境填写 |
| `Attendance:MaxQueryDays` | `Attendance__MaxQueryDays` | 单次范围上限，默认 31 天，可配置为 1 至 366 天 |
| `Attendance:MaxParallelDays` | `Attendance__MaxParallelDays` | 同时查询的七天分段数，默认 3，可配置为 1 至 8；保留原配置名称 |
| `Attendance:QueryTimeoutSeconds` | `Attendance__QueryTimeoutSeconds` | 整次查询超时，默认 120 秒，可配置为 1 至 210 秒 |
| `Urls` | `Urls` | Kestrel 监听地址，多个地址用分号分隔 |

本接口无需 App ID、AgentId。钉钉应用需具备考勤读取权限，且所查员工在可访问范围内。员工列表与姓名查询还需要部门和员工通讯录读取权限。

## 查询接口

```http
GET /api/attendance?start_date=2026-09-10&end_date=2026-09-11
X-Api-Key: <本服务的 API Key>
```

可选入参：`user_id` 与 `user_name` 二选一；都不填时使用默认员工。姓名去除首尾空白后精确匹配；不存在返回 404，重名返回 409，并在 `details.candidates` 提供 ID、姓名和部门，选定 ID 后重试。

`detail` 默认 `simple`，返回打卡摘要。设为 `full` 时，每条 `records` 增加 `details`，保留 `/attendance/listRecord` 返回的完整字段和值，包括实际返回的地址、经纬度、设备、Wi-Fi、记录 ID 等；原始明细中的员工 ID 也保持完整。未返回的字段不会补造，摘要时间仍统一转换为北京时间。

```http
GET /api/attendance?start_date=2026-09-01&end_date=2026-09-14&user_id=test-user-000001&detail=full
X-Api-Key: <本服务的 API Key>

GET /api/employees?user_name=张
X-Api-Key: <本服务的 API Key>
```

`employee_list` 对应 `GET /api/employees`，返回员工数组，字段为 `user_id`（完整 ID）、`name`、`departments`（`dept_id`、`name`）。内部通过 `/topapi/v2/user/list` 分页，并以 `/topapi/v2/department/listsub` 遍历应用可见的部门和子部门；跨部门员工按 ID 去重并合并部门。可选 `user_name` 为姓名包含筛选，省略时返回完整可见目录。完整目录缓存五分钟；任何分页或部门读取失败均返回错误，不使用半份目录判断姓名唯一性。

从本机配置读取密钥并请求，不在命令或输出中直接展示密钥：

```powershell
$officeConfig = Get-Content ./src/OfficeMcp.Api/appsettings.Local.json -Raw | ConvertFrom-Json
$officeHeaders = @{ 'X-Api-Key' = $officeConfig.Authentication.ApiKey }
$officeResult = Invoke-RestMethod 'http://127.0.0.1:18101/api/attendance?start_date=2026-09-10&end_date=2026-09-11' -Headers $officeHeaders -TimeoutSec 150
ConvertTo-Json -InputObject $officeResult -Depth 6
```

考勤响应是按日期升序排列的数组，每天恰好一个结果。以下片段展示成功和失败日期的结构（同一失败分段中的日期均失败，示例时间和错误为虚构值）：

```json
[
  {
    "work_date": "2026-09-10",
    "user_id": "tes**********001",
    "time_zone": "Asia/Shanghai",
    "records": [
      {
        "check_type": "OnDuty",
        "check_type_name": "上班",
        "planned_check_time": "2026-09-10T09:00:00+08:00",
        "actual_check_time": "2026-09-10T08:58:00+08:00",
        "status_code": "Normal",
        "status": "正常"
      }
    ],
    "success": true
  },
  {
    "work_date": "2026-09-11",
    "user_id": "tes**********001",
    "time_zone": "Asia/Shanghai",
    "records": [],
    "success": false,
    "error": {
      "code": "dingtalk_api_error",
      "message": "钉钉拒绝了业务请求，请检查应用权限、用户可见范围和上游错误码。",
      "provider_code": "60011"
    }
  }
]
```

起止日期必须是有效的 `yyyy-MM-dd`，包含首尾日期，开始日期不能晚于结束日期，默认最多 31 天。单日查询将两个日期传成相同值，仍返回长度为 1 的数组。原单天入参 `work_date` 已替换为 `start_date`、`end_date`；响应中每个日期的 `work_date` 字段保留。

考勤摘要的 `user_id` 只展示首尾各 3 位，中间逐位替换为 `*`；长度不超过 6 位时全部隐藏。按姓名查询时还返回所选 `user_name`。接口按上游 `workDate` 归属工作日，保留同一时段的不同流水及跨午夜记录，不计算工时；相同记录 ID 仅保留较新版本。缺少打卡时间为 `null`，不自行补造缺卡记录。`success: true` 且 `records: []` 表示当天没有明细；`success: false` 表示查询失败，应读取 `error`，不能据此判断休息或旷工。

服务调用 `/attendance/listRecord`，按最多七个自然日分段，默认同时查询三段；一段失败时，该段日期均标记失败，其他段继续查询。整次查询默认 120 秒超时（含姓名解析），保留已完成段，并将尚未完成或尚未请求的日期标记为 `attendance_query_timeout`。姓名尚未解析完成即超时则返回 HTTP 504。客户端断开连接时取消后续工作。调用方可只对失败日期重试。

| HTTP 状态 | 含义 |
|---|---|
| 200 | 范围查询结果数组；逐日检查 `success`，可能部分或全部失败 |
| 400 | 日期或选择参数无效、重复参数、范围超限、ID 与姓名同时填写、未知 detail |
| 401 | API Key 缺失或不正确 |
| 404 / 409 | 姓名不存在 / 姓名重复，需要选择候选 ID |
| 502 / 504 | 员工目录读取失败 / 目录或姓名解析超时 |
| 500 | 服务内部错误 |

参数校验、认证、员工解析及内部错误采用 `application/problem+json`。考勤分段的业务错误、网络失败、损坏响应、用户不匹配和超时放入对应日期的 `error`，含 `code`、`message` 及可用的 `provider_code`，不会丢弃其他分段的成功结果。单次 HTTP 超时使用 `dingtalk_timeout`，整次范围查询超时使用 `attendance_query_timeout`。

## 发布与 NSSM

构建需要支持 net8.0 的 .NET SDK，发布程序需要 Windows x64 上安装 ASP.NET Core 8 运行时。

```powershell
# 生成 artifacts/publish 下的可执行文件及依赖。
./scripts/publish.ps1

# 首次安装时复制本机配置；升级时保留发布目录已有的配置。
Copy-Item ./src/OfficeMcp.Api/appsettings.Local.json ./artifacts/publish/appsettings.Local.json
```

网关通过 ZeroTier 接入前，在发布目录的 `appsettings.Local.json` 增加监听配置：

```json
"Urls": "http://127.0.0.1:18101;http://192.0.2.10:18101"
```

`192.0.2.10` 仅为文档示例，必须替换为本机实际的 ZeroTier 地址。确保监听地址存在，并按需配置防火墙，仅允许网关和已配对手机的私有网络地址访问该端口。

使用管理员 PowerShell 注册并启动：

```powershell
./scripts/install-nssm.ps1 -StartNow
```

脚本默认使用相邻的 `nssm-2.24/win64/nssm.exe`，也支持 `-NssmPath`、`-PublishDirectory`、`-ServiceName`。设置开机自启、异常重启和 10 MiB 日志轮转。日志位于发布目录的 `logs`。默认仅注册，传 `-StartNow` 才立即启动。

也可通过 NSSM GUI 配置：

| 字段 | 值 |
|---|---|
| Path | 发布目录下 `OfficeMcp.Api.exe` 的绝对路径 |
| Startup directory | 发布目录的绝对路径 |
| Arguments | 留空 |
| Environment | `ASPNETCORE_ENVIRONMENT=Production` |

升级前先停止 `OfficeMcp` 服务，发布完成后再启动。不要在服务占用文件时变更构建目录。服务账号须能读取发布目录中的配置、写入 `logs`；按需要在 NSSM 的 Log on 页选择运行账号。

```powershell
Stop-Service OfficeMcp
./scripts/publish.ps1
Start-Service OfficeMcp
Invoke-RestMethod http://127.0.0.1:18101/healthz
```

## 后续接入 mcp-gateway

准备了两种匹配现有网关实现的配置，二选一：

- `deploy/gateway-office.openapi.yaml`：从受保护的 `/openapi/v1.json` 导入考勤查询、员工列表、远程打卡和任务状态四个操作。
- `deploy/gateway-office.http.yaml`：手工描述 HTTP 工具，网关启动时无需读取本服务文档。

将示例服务项合并到自己的 mcp-gateway 配置中，例如 `/opt/mcp-gateway/config/gateway.yaml`，并在网关私密环境配置 `OFFICE_API_KEY`，值与本服务的 `Authentication:ApiKey` 相同。随后重启网关并刷新客户端工具列表。MCP 地址示例为 `https://mcp.example.com/office_common_tools/mcp`，需替换为实际域名；工具名保留后端原名，`attendance_query` 的必填参数为 `start_date` 和 `end_date`，返回按日组织的数组。

升级已有接入时，OpenAPI 方式需要让网关重新读取在线服务文档，并将 `employee_list` 加入白名单；手工 HTTP 方式需同步新参数及员工列表工具。两份网关示例的 HTTP 超时为 150 秒，高于服务默认的 120 秒整次查询限制。如调整服务超时或日期上限，请同步更新网关配置和工具说明。此处配置示例不会自动修改线上网关。

远程打卡为 `attendance_clock_in`（POST）与 `attendance_clock_in_status`（GET）；仍使用配置的固定员工，通过 `/topapi/attendance/getupdatedata` 核验，不随动态查询员工改变。其启用、设备配对和调用语义见远程打卡说明。网关使用 `methods: [GET, POST]`，`include_operations` 包含这两个 operationId、`attendance_query` 和 `employee_list`。仅发布办公服务或刷新客户端不会修改旧白名单。设备领取／回执接口使用独立认证，不作为 Agent 工具导入。

## 验证

```powershell
dotnet test OfficeMcp.sln -c Release -m:1
```

测试使用合成用户和内存 HTTP 替身覆盖认证、日期范围边界、七天分段、OpenAPI 契约、完整明细、部分失败、整体超时、部门递归与员工分页、重名选择、目录缓存、令牌缓存和失效重试，不访问真实钉钉接口。

## 私密配置

真实用户清单、密钥、设备命令数据和实际部署记录仅保留在本地，不纳入 Git。HTTP 请求文件从 `http-client.private.env.json` 的 `local` 环境读取 `officeApiKey`，该文件同样被忽略且不会进入构建或发布产物。公开配置和示例不得填写真实凭据。
