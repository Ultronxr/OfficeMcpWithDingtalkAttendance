/* 被动自动打卡开关。只同步持久化策略和取消旧子任务，不亮屏、不打开应用、不补建计划。 */
var source = files.path(String(engines.myEngine().getSource()));
var folder = String(new java.io.File(source).getParent());
var time = require(files.join(folder, "office_time.js"));
var statePath = files.join(folder, ".office-mcp", "automation.json");
var entryPath = files.join(folder, "autojs6_autowake.js");
var actions = require(files.join(folder, "office_device_actions.js"));

/** 校验协议版本号，保证 Rhino／JavaScript 数字比较不会丢失整数精度。 */
function validRevision(value) {
    return typeof value === "number" && isFinite(value) && Math.floor(value) === value && value >= 0 && value <= 9007199254740991;
}

/** 校验持久化策略和下发协议；禁止未知字段缺失时默认打开自动任务。 */
function validate(value) {
    if (!value || !/^[A-Za-z0-9_-]{1,64}$/.test(String(value.device_id)) || typeof value.enabled !== "boolean" ||
        !validRevision(value.revision) || !validRevision(value.last_disabled_revision) ||
        value.last_disabled_revision > value.revision || (!value.enabled && value.last_disabled_revision !== value.revision) ||
        (value.revision === 0 && !value.enabled)) throw new Error("自动打卡策略格式无效");
    return value;
}

/** 返回 Android 原子文件；所有调用必须已经持有自动打卡控制锁。 */
function atomic() { return new android.util.AtomicFile(new java.io.File(statePath)); }

/** 读取已确认的本地策略；只有确实没有文件及备份时才沿用安装前的默认开启行为。 */
function read() {
    if (!files.exists(statePath) && !files.exists(statePath + ".bak")) return null;
    var value = JSON.parse(String(new java.lang.String(atomic().readFully(), "UTF-8")));
    if (value.version !== 1) throw new Error("自动打卡状态版本无效");
    return validate(value);
}

/** 原子保存策略，失败恢复旧文件；调用者在成功后才能向服务端确认。 */
function save(policy) {
    var value = { version: 1, device_id: policy.device_id, revision: policy.revision,
        enabled: policy.enabled, last_disabled_revision: policy.last_disabled_revision };
    var file = atomic();
    var output = file.startWrite();
    try {
        output.write(new java.lang.String(JSON.stringify(value)).getBytes("UTF-8"));
        file.finishWrite(output);
    } catch (error) { file.failWrite(output); throw error; }
    return value;
}

/**
 * 随机入口读取开关并持锁运行；锁覆盖规划或整次设备动作，关闭确认不能越过正在执行的动作。
 * @returns {Object|null} 包含本地策略和 release；短等待失败返回 null，由入口停止自动执行。
 */
function enter() {
    var guard = actions.acquire("automation-control", 2000, null);
    if (guard == null) return null;
    try { return { policy: read(), release: function () { guard.release(); } }; }
    catch (error) { guard.release(); throw error; }
}

/** 同步和取消操作写入固定入口日志，服务端不能指定文件路径。 */
function record(message) {
    var line = "[" + time.format(Date.now()) + "] " + message;
    log(line);
    try { files.append(entryPath + ".log", line + "\n"); }
    catch (error) { log(time.line("AUTOMATION_LOG_ERROR：开关已处理，文件日志写入失败。")); }
}

/**
 * 取消固定入口登记的待执行子任务，保留周期主任务和历史记录；在控制锁内调用。
 * @param {number} revision 导致旧计划失效的关闭版本。
 * @returns {number} 本次标记取消的计划数。
 */
function cancelPending(revision) {
    var store = storages.create("autojs6.screen_wake.v1");
    var state = store.get(entryPath, { plans: [] });
    if (!state || !Array.isArray(state.plans)) throw new Error("本地计划记录格式异常");
    var count = 0;
    state.plans.forEach(function (plan) {
        if (plan.status !== "pending" && plan.status !== "creating") return;
        if (plan.id >= 0) {
            var task = tasks.getTimedTask(plan.id);
            // ID 可能被复用；必须同时匹配一次性类型及原计划时间，不能误删周期主任务。
            // AutoJs6 TimedTask 提供 isDisposable() 与 getMillis()。
            if (task && String(task.getScriptPath()) === entryPath && task.isDisposable() && Number(task.getMillis()) === plan.at) {
                tasks.removeTimedTask(plan.id);
                var remaining = tasks.getTimedTask(plan.id);
                if (remaining && String(remaining.getScriptPath()) === entryPath && remaining.isDisposable() &&
                    Number(remaining.getMillis()) === plan.at) throw new Error("子任务尚未移除");
            }
        }
        plan.status = "cancelled";
        plan.cancelReason = "automation_disabled";
        plan.automationDisabledRevision = revision;
        count++;
    });
    if (count > 0) store.put(entryPath, state);
    return count;
}

/**
 * 在短事务中应用新策略；动作执行期间不抢锁，保持服务端为待同步。
 * @param {Object} policy 已通过设备认证取得的策略。
 * @param {string} deviceId 本机配对身份。
 * @returns {Object|null} 已落盘策略；正在执行或过时响应时返回 null，下一轮重试。
 */
function apply(policy, deviceId) {
    validate(policy);
    if (policy.device_id !== deviceId) throw new Error("自动打卡策略设备不匹配");
    var guard = actions.acquire("automation-control", 0, null);
    if (guard == null) return null;
    try {
        var current = read();
        if (current && current.device_id !== deviceId) throw new Error("本地开关绑定了其他设备");
        if (current && policy.revision < current.revision) throw new Error("服务端开关版本落后，请检查状态恢复");
        if (current && policy.revision === current.revision) {
            if (policy.enabled !== current.enabled || policy.last_disabled_revision !== current.last_disabled_revision)
                throw new Error("同一开关版本内容冲突");
            return current;
        }
        if (current && policy.last_disabled_revision < current.last_disabled_revision)
            throw new Error("服务端关闭版本发生回退");
        // 离线期间即使先关闭再开启，关闭前留下的计划仍须失效，不能随开启复活。
        var cancelled = 0;
        if (!policy.enabled || policy.last_disabled_revision > (current ? current.last_disabled_revision : 0))
            cancelled = cancelPending(policy.last_disabled_revision);
        var value = save(policy);
        record("AUTOMATION_APPLIED：revision=" + value.revision + "，enabled=" + value.enabled +
            "，last_disabled_revision=" + value.last_disabled_revision + "，取消待执行子任务=" + cancelled +
            "；周期主任务保留，开启不补建计划。");
        return value;
    } finally { guard.release(); }
}

/** 构造手机确认，只上报本地已落盘版本；没有状态时不伪造默认开启的确认。 */
function receipt(value) {
    return { applied_revision: value ? value.revision : null, applied_enabled: value ? value.enabled : null };
}

/**
 * 每轮同步一次；新版本落盘后立即补一次确认，响应丢失时下轮以同版本重传。
 * @param {Function} request 接收器提供的认证短请求 (method, path, body)。
 * @param {string} deviceId 手机固定设备 ID。
 * @returns {void} 网络或持久化失败交给接收器记录，不执行设备动作。
 */
function sync(request, deviceId) {
    var guard = actions.acquire("automation-control", 0, null);
    if (guard == null) return;
    var current;
    try {
        current = read();
        if (current && current.device_id !== deviceId) throw new Error("本地开关绑定了其他设备");
    } finally { guard.release(); }
    var path = "/api/devices/" + deviceId + "/attendance/automation/sync";
    var policy = request("POST", path, receipt(current));
    var applied = apply(policy, deviceId);
    if (applied && (!current || current.revision !== applied.revision)) {
        // 第二次响应可能已有更新设置；应用它后下一轮再确认，避免无限同步饿死主动任务。
        var latest = request("POST", path, receipt(applied));
        apply(latest, deviceId);
    }
}

// 独立误运行模块不会进行同步或打卡，必须由入口显式调用。
if (typeof module !== "undefined" && module.exports != null) {
    module.exports = { enter: enter, sync: sync };
}
