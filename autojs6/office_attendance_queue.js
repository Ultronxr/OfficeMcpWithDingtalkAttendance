/* 本地执行事实与核验结果队列。模块只保存和上报事实，永远不执行手机动作。 */
var source = files.path(String(engines.myEngine().getSource()));
var folder = String(new java.io.File(source).getParent());
var directory = files.join(folder, ".office-mcp", "attendance");
var actions = require(files.join(folder, "office_device_actions.js"));

/** 创建队列目录；实际 I/O 失败由调用者记录，不能改变随机动作窗口。 */
function ensureDirectory() { new java.io.File(directory).mkdirs(); }

/** 获取 Android 原子文件对象；备份恢复由 AtomicFile 完成，读写需持有对应执行锁。 */
function atomic(id) { return new android.util.AtomicFile(new java.io.File(files.join(directory, id + ".json"))); }

/** 原子保存整个执行条目，完成写入前异常时保留旧版本。 */
function save(entry) {
    var file = atomic(entry.report.local_run_id);
    var output = file.startWrite();
    try {
        output.write(new java.lang.String(JSON.stringify(entry)).getBytes("UTF-8"));
        file.finishWrite(output);
    } catch (error) { file.failWrite(output); throw error; }
}

/** 回读原子文件，可恢复中断写入之前的执行事实。 */
function read(id) { return JSON.parse(String(new java.lang.String(atomic(id).readFully(), "UTF-8"))); }

/** 写回入口日志和控制台；日志只取本地标识、固定状态及必要考勤时间。 */
function record(entry, message) {
    var line = "[" + new Date().toISOString() + "] " + message + "，local_run_id=" + entry.report.local_run_id;
    log(line);
    // 只向同目录原入口的日志追加，服务端不能指定写入路径。
    if (entry.log_name && /^[^/\\]+\.js\.log$/.test(entry.log_name)) files.append(files.join(folder, entry.log_name), line + "\n");
}

/**
 * 动作前保存稳定执行 ID 和不确定回执，持锁直到动作结束。
 * @param {string} id 已保存到本地计划的 UUID（无连字符）。
 * @param {string} workDate 北京时间工作日。
 * @param {number} executedAt 子任务实际进入执行流程的 Unix 毫秒。
 * @param {string|null} logName 同目录入口日志文件名。
 * @returns {Object} 含执行条目与释放句柄；中断后只补报不重做动作。
 */
function begin(id, workDate, executedAt, logName) {
    if (!/^[a-f0-9]{32}$/.test(id)) throw new Error("本地执行 ID 无效");
    ensureDirectory();
    var guard = actions.acquire("attendance-" + id, 0, null);
    if (guard == null) throw new Error("该执行仍在进行");
    try {
        var entry = { version: 1, log_name: logName, state: "pending", next_attempt_at: 0,
            report: { local_run_id: id, work_date: workDate, check_type: "OnDuty", executed_at_unix_ms: executedAt,
                outcome: "uncertain", error_code: "execution_interrupted" } };
        save(entry);
        return { entry: entry, release: function () { guard.release(); } };
    } catch (error) { guard.release(); throw error; }
}

/** 保存阶段时间；只在持有执行锁的定时进程调用，不抢占接收器的回执。 */
function stage(handle, name, time) {
    if (name === "screen_on") handle.entry.report.screen_on_at_unix_ms = time;
    if (name === "app_requested") handle.entry.report.app_requested_at_unix_ms = time;
    save(handle.entry);
}

/** 保存最终动作结果并释放锁；无论是否保存成功，均禁止自动重做动作。 */
function complete(handle, result) {
    try {
        handle.entry.report.outcome = result.outcome;
        handle.entry.report.error_code = result.error_code || null;
        handle.entry.report.completed_at_unix_ms = Date.now();
        save(handle.entry);
    } finally { handle.release(); }
}

/** 列出队列，包括 AtomicFile 中断写入的备份名，忽略临时新文件。 */
function ids() {
    ensureDirectory();
    var unique = {};
    (files.listDir(directory) || []).forEach(function (name) {
        var match = /^([a-f0-9]{32})\.json(?:\.bak)?$/.exec(String(name));
        if (match) unique[match[1]] = true;
    });
    return Object.keys(unique).sort();
}

/** 把终态摘要写回日志；写入失败时保留 final_pending，重启后可重试记录。 */
function writeFinal(entry) {
    var result = entry.result;
    // 共用收尾只接受本次已启动的归属；旧结果或升级前任务不会补做桌面动作。
    var terminal = { task_id: result.task_id, source: "local_schedule", is_terminal: true,
        state: result.state, attendance_confirmed: result.attendance_confirmed };
    try {
        var cleanup = require(files.join(folder, "office_attendance_cleanup.js"));
        if (!cleanup.localTerminal(entry.report.local_run_id, terminal)) return;
    } catch (error) {
        // 收尾状态损坏不能卡住整条事实队列；其独立截止检查仍会在状态可用时继续。
        log("[Office MCP] HOME_STORAGE_ERROR：核验结果已取得，但收尾通知失败；考勤结果继续归档。");
    }
    var attendance = result.record || {};
    record(entry, "VERIFY_RESULT：task_id=" + result.task_id + "，state=" + result.state +
        "，attendance_confirmed=" + result.attendance_confirmed + "，relation=" + (result.verification_relation || "none") +
        "，实际打卡=" + (attendance.actual_check_time || "无") + "，考勤状态=" + (attendance.status_code || "无") +
        "，核验次数=" + (result.verification_attempts || 0) +
        "，核验错误=" + String(result.verification_error || "无").replace(/[\r\n]/g, " ").slice(0, 256));
    entry.state = "done";
    save(entry);
}

/**
 * 每轮至多处理一条到期事实或状态查询，给原有远程回执和领取流程保留运行机会。
 * @param {Function} request 由接收器提供的认证短请求，签名为 (method, path, body)。
 * @param {string} deviceId 本地已配对设备身份。
 * @returns {boolean} 是否还有待处理条目，用于缩短下一次领取等待（不执行动作）。
 */
function pump(request, deviceId) {
    var pending = false;
    var processed = false;
    ids().forEach(function (id) {
        var guard = actions.acquire("attendance-" + id, 0, null);
        if (guard == null) { pending = true; return; }
        try {
            var entry = read(id);
            if (entry.version !== 1 || entry.report.local_run_id !== id) throw new Error("队列格式无效");
            if (entry.state === "done" || entry.state === "rejected") return;
            pending = true;
            if (processed || entry.next_attempt_at > Date.now()) return;
            processed = true;
            if (entry.state === "final_pending") { writeFinal(entry); return; }
            try {
                var root = "/api/devices/" + deviceId + "/attendance";
                var result = entry.task_id
                    ? request("GET", root + "/tasks/" + entry.task_id, null)
                    : request("POST", root + "/executions", entry.report);
                if (!result || !/^[a-f0-9]{32}$/.test(String(result.task_id)) ||
                    result.source !== "local_schedule" || typeof result.is_terminal !== "boolean" ||
                    (entry.task_id && result.task_id !== entry.task_id)) throw new Error("核验响应无效");
                var accepted = !entry.task_id;
                entry.task_id = String(result.task_id);
                entry.next_attempt_at = Date.now() + 5000;
                entry.state = result.is_terminal ? "final_pending" : "verifying";
                // 只保留可展示摘要，不把用户 ID 等服务端字段复制进手机日志。
                if (result.is_terminal) entry.result = { task_id: result.task_id, state: result.state,
                    attendance_confirmed: result.attendance_confirmed, verification_relation: result.verification_relation,
                    verification_attempts: result.verification_attempts, verification_error: result.verification_error,
                    record: result.record };
                save(entry);
                if (accepted) record(entry, "VERIFY_ACCEPTED：task_id=" + entry.task_id + "，已进入服务端核验");
                if (result.is_terminal) writeFinal(entry);
            } catch (error) {
                // 当天补报被拒绝或任务已删除，保存终止原因；不能无限占用队列，也不能重做手机动作。
                if (error.httpStatus === 400 || error.httpStatus === 403 || error.httpStatus === 409 ||
                    (entry.task_id && error.httpStatus === 404)) {
                    record(entry, "VERIFY_REJECTED：HTTP=" + error.httpStatus + "，task_id=" + (entry.task_id || "未创建") + "，未能取得核验结果");
                    entry.state = "rejected";
                    entry.http_status = error.httpStatus;
                } else {
                    // 网络、认证、未升级服务端等错误保留事实；所有重试使用同一个执行标识。
                    entry.next_attempt_at = Date.now() + 30000;
                    if (!entry.retry_logged) record(entry, "VERIFY_PENDING：上报或查询暂未完成，保留事实后重试");
                    entry.retry_logged = true;
                }
                save(entry);
            }
        } catch (error) {
            log("[Office MCP] VERIFY_STORAGE_ERROR：执行记录=" + id + "，请检查本地队列文件；不会重做动作。");
        } finally { guard.release(); }
    });
    return pending;
}

module.exports = { begin: begin, stage: stage, complete: complete, pump: pump };
