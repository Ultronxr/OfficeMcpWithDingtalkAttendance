/* 打卡收尾：持久化最近一次动作归属，取得终态或打开钉钉三分钟后请求桌面，不改变考勤结果。 */
var source = files.path(String(engines.myEngine().getSource()));
var folder = String(new java.io.File(source).getParent());
var statePath = files.join(folder, ".office-mcp", "attendance-cleanup.json");
var actions = require(files.join(folder, "office_device_actions.js"));
var terminalStates = ["succeeded", "already_completed", "failed", "expired", "unconfirmed"];

/** 所有文件操作都受 screen-action 锁保护，与亮屏／启动动作互斥。 */
function atomic() { return new android.util.AtomicFile(new java.io.File(statePath)); }

/** 读取最近一次动作；不扫描历史任务，不为升级前的打卡补做桌面动作。 */
function read() {
    if (!files.exists(statePath) && !files.exists(statePath + ".bak")) return null;
    var value = JSON.parse(String(new java.lang.String(atomic().readFully(), "UTF-8")));
    if (!value || value.version !== 1 || !/^[a-f0-9]{32}$/.test(String(value.owner_id)) ||
        ["armed", "launched", "ignored"].indexOf(value.phase) < 0 ||
        ["pending", "attempting", "requested", "failed"].indexOf(value.home_state) < 0 ||
        typeof value.verification_done !== "boolean" ||
        (value.phase !== "ignored" && (["local_schedule", "remote_command"].indexOf(value.source) < 0 ||
            !/^[a-f0-9]{32}$/.test(String(value.run_id)))) ||
        (value.task_id != null && !/^[a-f0-9]{32}$/.test(String(value.task_id))) ||
        (value.source === "remote_command" && value.task_id !== value.run_id) ||
        (value.terminal_state != null && terminalStates.indexOf(value.terminal_state) < 0))
        throw new Error("打卡收尾状态无效");
    return value;
}

/** 原子保存收尾状态；必须在发送桌面 Intent 前保存尝试标记，避免重启后重复发送。 */
function save(value) {
    var file = atomic();
    var output = file.startWrite();
    try {
        output.write(new java.lang.String(JSON.stringify(value)).getBytes("UTF-8"));
        file.finishWrite(output);
    } catch (error) { file.failWrite(output); throw error; }
}

/** 把必要标识及动作阶段写入固定日志；日志故障不能改变已持久化的收尾事实。 */
function record(value, message) {
    var line = "[" + new Date().toISOString() + "] " + message + "，source=" + (value.source || "manual") +
        "，run_id=" + (value.run_id || value.owner_id) + "，task_id=" + (value.task_id || "未关联");
    log(line);
    var name = value.source === "remote_command" ? "office_remote_listener.js.log" : "autojs6_autowake.js.log";
    try { files.append(files.join(folder, name), line + "\n"); }
    catch (error) { log("[Office MCP] HOME_LOG_ERROR：收尾文件日志写入失败。"); }
}

/**
 * 新动作在设备锁内登记归属，立即使旧任务收尾失效；无 Task 的 direct 只失效旧记录。
 * @param {Object|null} run 仅包含 source 和 id，由固定入口生成，不接受服务端脚本或路径。
 * @returns {string} 此次动作的唯一归属标识。调用者必须持有 screen-action 锁。
 */
function begin(run) {
    if (run && (["remote_command", "local_schedule"].indexOf(run.source) < 0 || !/^[a-f0-9]{32}$/.test(String(run.id))))
        throw new Error("打卡收尾归属无效");
    var value = { version: 1, owner_id: String(java.util.UUID.randomUUID()).replace(/-/g, ""),
        phase: run ? "armed" : "ignored", home_state: "pending", source: run ? run.source : null,
        run_id: run ? run.id : null, task_id: run && run.source === "remote_command" ? run.id : null,
        started_at: Date.now(), next_query_at: 0, verification_done: false };
    save(value);
    return value.owner_id;
}

/**
 * 仅在 app.launchPackage 明确返回成功后激活收尾；调用者仍持有设备锁。
 * @param {string} ownerId begin 返回的归属标识。
 * @param {number} at 实际请求打开钉钉的 Unix 毫秒。
 */
function launched(ownerId, at) {
    var value = read();
    if (!value || value.owner_id !== ownerId || value.phase !== "armed") return;
    value.phase = "launched";
    value.launched_at = at;
    value.home_deadline = at + 180000;
    save(value);
    record(value, "HOME_PENDING：已启动钉钉，等待任务终态，或打开后 180 秒返回桌面。");
}

/** 校验只读 Task 返回，防止不匹配的旧任务或未知状态被解释为本次核验终结。 */
function validResult(result, sourceName, taskId) {
    return result && /^[a-f0-9]{32}$/.test(String(result.task_id)) && (!taskId || result.task_id === taskId) &&
        result.source === sourceName && typeof result.is_terminal === "boolean" &&
        typeof result.attendance_confirmed === "boolean" &&
        (!result.is_terminal || terminalStates.indexOf(result.state) >= 0);
}

/** 保存终态供独立本地收尾循环处理；考勤结果只用于日志，不能由 Home 成败反向修改。 */
function finish(value, result) {
    value.verification_done = true;
    value.terminal_state = result.state;
    value.attendance_confirmed = result.attendance_confirmed;
    save(value);
    if (value.source === "remote_command") {
        var actual = result.record && result.record.actual_check_time;
        record(value, "VERIFY_RESULT：state=" + result.state + "，attendance_confirmed=" + result.attendance_confirmed +
            "，实际打卡=" + (actual || "无") + "，核验次数=" + (result.verification_attempts || 0));
    }
}

/**
 * 被动任务复用原队列的核验结果，不重复查询 API；锁忙则保留 final_pending 等待下一轮。
 * @param {string} runId 本地队列的 local_run_id，必须与最近启动归属一致。
 * @param {Object} result 原队列已确认的服务端终态摘要。
 * @returns {boolean} 是否已处理或无需处理；false 只表示设备动作仍占锁。
 */
function localTerminal(runId, result) {
    var guard = actions.acquire("screen-action", 0, null);
    if (guard == null) return false;
    try {
        var value = read();
        if (!value || value.phase !== "launched" || value.source !== "local_schedule" || value.run_id !== runId) return true;
        if (!validResult(result, "local_schedule", value.task_id) || !result.is_terminal) throw new Error("本地核验终态无效");
        if (!value.verification_done) {
            value.task_id = result.task_id;
            finish(value, result);
        }
        return true;
    } finally { guard.release(); }
}

/**
 * 主动打卡补齐手机端终态查询；一次最多一个短 GET，网络请求期间释放设备锁。
 * @param {Function} request 常驻接收器的认证短请求 (method, path, body)。
 * @param {string} deviceId 本地已配对设备身份。
 * @returns {boolean} 最近任务是否仍需取回服务端状态，用于缩短领取轮询间隔。
 */
function pump(request, deviceId) {
    var guard = actions.acquire("screen-action", 0, null);
    if (guard == null) return true;
    var current;
    try {
        current = read();
        if (!current || current.phase !== "launched" || current.source !== "remote_command" || current.verification_done) return false;
        if (current.next_query_at > Date.now()) return true;
        current.next_query_at = Date.now() + 5000;
        save(current);
    } finally { guard.release(); }
    var result = null;
    var errorStatus = null;
    try {
        result = request("GET", "/api/devices/" + deviceId + "/attendance/tasks/" + current.task_id, null);
        if (!validResult(result, "remote_command", current.task_id)) throw new Error("主动核验返回格式无效");
    } catch (error) { errorStatus = error.httpStatus || 0; }
    guard = actions.acquire("screen-action", 0, null);
    if (guard == null) return true;
    try {
        var latest = read();
        // 在等待网络时可能已经执行了新任务；旧响应不能更新新归属或触发新桌面动作。
        if (!latest || latest.owner_id !== current.owner_id) return true;
        if (errorStatus != null) {
            latest.next_query_at = Date.now() + 30000;
            if (!latest.query_error_logged) record(latest, "VERIFY_PENDING：主动任务终态暂未取回；本地收尾仍独立计时。");
            latest.query_error_logged = true;
            // 任务不存在或认证归属被拒绝时停止查询，但仍保留三分钟桌面收尾。
            if (errorStatus === 403 || errorStatus === 404) latest.verification_done = true;
            save(latest);
        } else if (result.is_terminal) finish(latest, result);
        return !latest.verification_done;
    } finally { guard.release(); }
}

/**
 * 只进行本地收尾，不联网；由独立线程每秒检查，网络阻塞不延迟三分钟截止检查。
 * 发出 Intent 前持久化 attempting，发送成功后标记 requested；中断后不盲目重复 Home。
 */
function tick() {
    var guard = actions.acquire("screen-action", 0, null);
    if (guard == null) return;
    try {
        var value = read();
        if (!value || value.phase !== "launched" || value.home_state !== "pending") return;
        if (typeof value.home_deadline !== "number" || !isFinite(value.home_deadline) ||
            value.home_deadline !== value.launched_at + 180000) throw new Error("收尾截止时间无效");
        if (!value.terminal_state && Date.now() < value.home_deadline) return;
        value.home_state = "attempting";
        value.home_reason = value.terminal_state ? "task_terminal" : "verification_wait_timeout";
        value.home_attempted_at = Date.now();
        save(value);
        try {
            // 系统桌面请求不需要亮屏、不依赖无障碍，不清任务栈、不退出钉钉。
            var intent = new android.content.Intent(android.content.Intent.ACTION_MAIN);
            intent.addCategory(android.content.Intent.CATEGORY_HOME);
            intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(intent);
            value.home_state = "requested";
        } catch (error) { value.home_state = "failed"; }
        save(value);
        record(value, (value.home_state === "requested" ? "HOME_REQUESTED：已请求返回桌面" : "HOME_FAILED：桌面请求发送失败") +
            "，reason=" + value.home_reason + "；不改变考勤核验结果。");
    } finally { guard.release(); }
}

module.exports = { begin: begin, launched: launched, localTerminal: localTerminal, pump: pump, tick: tick };
