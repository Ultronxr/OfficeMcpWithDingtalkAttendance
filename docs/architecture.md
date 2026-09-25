# 架构与已确认约定

本项目是 `mcp-gateway` 的考勤业务后端，生产环境运行在与 gateway 相同的 Linux 服务器上，以 ASP.NET Core 8 普通 HTTP API 对接网关。项目名保留 `office-mcp`，程序不实现 MCP 协议。源代码在办公电脑维护；后续必须访问办公机环境的能力应独立部署本地后端。

```mermaid
flowchart LR
    A[外部 AI 客户端] -->|MCP / OAuth| G[mcp-gateway]
    G -->|本机 HTTP / X-Api-Key| H[服务器 OfficeMcp.Api]
    P[手机 AutoJs6] -->|ZeroTier 长轮询和回执| H
    H --> C[考勤功能]
    H -.后续添加.-> F[其他办公功能]
    C --> D[共享钉钉客户端与令牌缓存]
    F -.按需复用.-> D
    D -->|HTTPS| O[钉钉官方 API]
```

## 功能和基础设施边界

- `Program.cs`：HTTP 宿主与功能装配。
- `Features/Attendance`：考勤接口、业务整理、配置及响应契约。未来功能放在 `Features` 下的并列目录中，独立注册。
- `Features/Employees`：可见部门递归、员工分页与去重、五分钟完整目录缓存及姓名解析。
- `Features/DeviceCommands`：通用设备认证、命令持久化、领取和回执；`Attendance` 下的 `ClockIn` 流程负责考勤基线比较与后台核验。
- `Infrastructure/DingTalk`：跨功能共享的企业令牌、HTTP 客户端、业务错误处理和钉钉时间格式转换。
- `Infrastructure/Security`：网关到后端的 API Key 认证，独立于公网 OAuth 和钉钉应用密钥。
- `Infrastructure/Errors`：跨功能共享的 ProblemDetails 错误响应。

日期范围查询实时访问钉钉并缓存 accessToken。远程打卡通过 `BackgroundService` 核验结果，任务保存在 `data/device-commands`，使用原子文件替换和独占锁；无需数据库服务或 MCP SDK。详细流程见 [远程打卡方案](remote-clock-in-design.md)。

## 已确认接口行为

- `GET /api/attendance?start_date=yyyy-MM-dd&end_date=yyyy-MM-dd`，两个日期必填且各只能传一个值，包含首尾日期，默认最多 31 天；单日查询传相同日期。
- `user_id` 与 `user_name` 二选一；省略时使用 `Attendance:UserId`。姓名精确匹配，重名返回候选 ID 和部门，不自动选择。主动打卡及其旧接口核验继续使用配置员工。
- 考勤摘要的 `user_id` 保留首尾各三位，中间逐位替换为 `*`；长度不超过六位时全部隐藏。员工列表和完整原始明细保留完整 ID；按姓名查询的摘要附上 `user_name`。
- 顶层返回按工作日升序排列的数组，每天恰好一个结果。每个工作日保留所有上下班时段，按计划时间排序，保留跨天记录。
- 每个日期包含 `success`；某段上游调用失败时，该段所有日期返回 `success: false` 和 `error`，不丢弃其他段结果。`error` 包含安全错误码、说明和可用的钉钉错误码。
- 每条记录包含上下班类型、计划时间、实际时间、中文状态和原始状态码。
- 使用 `/attendance/listRecord` 顶层 `recordresult` 打卡明细，以 `workDate` 归属日期。保留同一时段不同流水，同一记录 ID 保留较新版本；不重新判断迟到、计算工时或补造缺卡记录。
- `detail=simple`（默认）输出摘要；`detail=full` 附上每条记录的完整字段；已知时间与摘要共用工具类输出北京时间，其他原始值不变。
- `GET /api/employees` 对应 `employee_list`，通过 `/topapi/v2/user/list` 分页和 `/topapi/v2/department/listsub` 递归，返回应用可见员工的完整 ID、姓名和部门；可选 `user_name` 按包含关系筛选。
- 目录完整读取后缓存五分钟，任何页或部门失败均不发布部分目录，避免错误判断姓名唯一性。按 ID 查询无需访问通讯录。
- 未打卡时间返回 `null`；仅当 `success: true` 时，空记录表示钉钉没有返回结果，不能推断成旷工或休息。
- 未识别状态返回“未知状态”，同时保留原始码。
- 全项目必须遵守 [时间规范](time-conventions.md)：HTTP、完整明细、日志和新产生的状态日期时间共用 `OfficeTime`，手机使用 `office_time.js`；对外显示到秒并带 `+08:00`，内部状态保留原精度，不输出 `time_zone`。钉钉无偏移时间按北京时间解析；设备 Unix 毫秒协议与时长不平移。
- `GET /openapi/v1.json` 提供文档，查询 operationId 为 `attendance_query`、`employee_list`。文档和业务均要求 `X-Api-Key`。
- `GET /healthz` 匿名，仅表示宿主进程存活，不调用钉钉，也不证明外部 API 可用。

## 运行行为

令牌默认有效期由钉钉返回；提前最多 300 秒刷新，并发请求复用一次刷新。业务请求仅在明确的令牌无效/过期错误下刷新并重试一次。其他业务错误、限流、网络失败不自动重试。

单次上游 HTTP 超时默认 15 秒。范围查询按最多七个自然日分段，并发分段数由原配置 `Attendance:MaxParallelDays` 控制，默认 3；日期上限由 `Attendance:MaxQueryDays` 控制，默认 31 天。分段结果按日期索引写入响应，完成顺序不会改变输出顺序。

整次查询超时由 `Attendance:QueryTimeoutSeconds` 控制，默认 120 秒，包含姓名解析；姓名解析超时返回 504。分段查询超时保留已完成结果，其余日期返回 `attendance_query_timeout`。独立员工列表读取上限为 120 秒。网关示例超时为 150 秒，以便服务返回部分结果。客户端取消仍取消整次工作，不继续访问钉钉。

参数和员工解析成功后，考勤查询以 HTTP 200 返回结果数组，调用者必须逐日检查 `success`。分段的钉钉业务、网络、格式错误和超时都放入该段日期的 `error`，不转发上游原始错误正文；员工目录失败以 502/504 ProblemDetails 返回，姓名不存在或重名以 404/409 返回。内部编程错误仍走统一 500 ProblemDetails。客户端日志禁用 URL 记录，业务结果也不写日志。

## 参考依据

- [钉钉获取用户考勤数据](https://open.dingtalk.com/document/orgapp/obtain-the-attendance-update-data)
- [阿里官方 API 字段与示例](https://developer.alibaba.com/docs/api.htm?apiId=37307)
- [钉钉企业内部应用令牌](https://open.dingtalk.com/document/orgapp/obtain-orgapp-token)
- [ASP.NET Core HTTP 请求](https://learn.microsoft.com/aspnet/core/fundamentals/http-requests?view=aspnetcore-8.0)
- [NSSM 使用说明](https://www.nssm.cc/usage)
