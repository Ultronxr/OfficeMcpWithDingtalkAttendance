/* AutoJs6 共用动作：定时和远程入口使用同一把设备文件锁，不改变钉钉自身打卡规则。 */
var heldLocks = [];

/** 释放持有的文件锁；重复释放安全，适用于脚本退出和正常结束。 */
function releaseAll() {
    heldLocks.slice().forEach(function (item) { item.release(); });
}
events.on("exit", releaseAll);

/**
 * 获取跨脚本引擎的设备文件锁；等待受超时和动作截止时间双重约束。
 * @param {string} name 固定锁名，由本地代码指定。
 * @param {number} timeoutMs 最长等待毫秒数。
 * @param {number|null} deadline 动作最晚开始时间，手机本地时间戳。
 * @returns {Object|null} 可释放锁；超时返回 null。
 */
function acquire(name, timeoutMs, deadline) {
    // 所有入口放在原定时脚本目录内，复用已有可写目录，不要求写入存储根目录。
    var source = files.path(String(engines.myEngine().getSource()));
    // 兼容 AutoJs6：目录解析使用 Java File，不依赖不存在的 files.dirname。
    var folder = files.join(String(new java.io.File(source).getParent()), ".office-mcp");
    new java.io.File(folder).mkdirs();
    var handle = new java.io.RandomAccessFile(files.join(folder, name + ".lock"), "rw");
    var channel = handle.getChannel();
    var until = Date.now() + timeoutMs;
    var lock = null;
    try {
        do {
            if (deadline != null && Date.now() > deadline) break;
            try { lock = channel.tryLock(); }
            catch (error) {
                if (String(error).indexOf("OverlappingFileLockException") < 0) throw error;
            }
            if (lock != null) break;
            if (Date.now() >= until) break;
            sleep(200);
        } while (true);
        if (lock == null) { channel.close(); handle.close(); return null; }
        var released = false;
        var holder = {
            release: function () {
                if (released) return;
                released = true;
                try { lock.release(); } finally {
                    try { channel.close(); } finally { handle.close(); }
                    heldLocks = heldLocks.filter(function (value) { return value !== holder; });
                }
            }
        };
        heldLocks.push(holder);
        return holder;
    } catch (error) {
        try { channel.close(); } finally { handle.close(); }
        throw error;
    }
}

/** 校验执行配置，避免无效延迟或保亮参数产生失控等待。 */
function integer(value, min, max, name) {
    if (typeof value !== "number" || !isFinite(value) || Math.floor(value) !== value || value < min || value > max) {
        throw new Error(name + " 参数无效");
    }
    return value;
}

/**
 * 在互斥区内亮屏和拉起应用，返回动作回执；不把启动请求视为打卡成功。
 * @param {Object} config 原定时脚本的动作配置。
 * @param {number} keepSeconds 打开应用后继续保亮的秒数。
 * @param {number|null} deadline 最迟允许请求打开应用的时间戳。
 * @param {Function} report 本地日志回调，不包含任何设备密钥。
 * @returns {Object} 亮屏结果、启动请求结果及固定错误码。
 */
function wakeAndLaunch(config, keepSeconds, deadline, report) {
    integer(keepSeconds, 0, 600, "keepScreenOnSeconds");
    var guard = acquire("screen-action", 30000, deadline);
    if (guard == null) {
        return { outcome: deadline != null && Date.now() > deadline ? "expired" : "busy",
            wake_status: "skipped", error_code: "action_busy", launch_requested: false };
    }
    var keeping = false;
    try {
        var wasOn = device.isScreenOn();
        var isOn = wasOn;
        for (var attempt = 0; !isOn && attempt < 3; attempt++) {
            if (deadline != null && Date.now() > deadline) {
                return { outcome: "expired", wake_status: "expired", error_code: "command_expired", launch_requested: false };
            }
            device.wakeUp();
            sleep(400);
            isOn = device.isScreenOn();
        }
        if (!isOn) return { outcome: "failed", wake_status: "failed", error_code: "wake_failed", launch_requested: false };
        report(wasOn ? "ALREADY_ON：屏幕原本已亮。" : "SCREEN_ON：已确认屏幕点亮。");
        var result = { outcome: "failed", wake_status: wasOn ? "already_on" : "screen_on",
            screen_on: true, launch_requested: false, error_code: null };
        var launchEnabled = config.openAppAfterWake === true;
        var delay = 0;
        if (launchEnabled) {
            try { delay = integer(config.openAppDelayMs, 0, 10000, "openAppDelayMs"); }
            catch (error) { result.error_code = "invalid_action_config"; launchEnabled = false; }
        }
        if (keepSeconds > 0) {
            device.keepScreenOn(keepSeconds * 1000 + (launchEnabled ? delay + 5000 : 2000));
            keeping = true;
        }
        if (launchEnabled) {
            try {
                var pkg = String(config.targetAppPackage || "").trim();
                if (!pkg) pkg = String(app.getPackageName(String(config.targetAppName || "")) || "");
                if (!pkg) throw new Error("未找到目标应用");
                if (delay > 0) sleep(delay);
                if (deadline != null && Date.now() > deadline) {
                    result.outcome = "expired";
                    result.error_code = "command_expired";
                } else if (!device.isScreenOn()) {
                    result.error_code = "screen_off";
                } else {
                    // 沿用现有动作：不按 Home、不杀应用、不模拟点击，仅发送启动请求。
                    result.launch_requested = !!app.launchPackage(pkg);
                    result.outcome = result.launch_requested ? "launch_requested" : "failed";
                    if (!result.launch_requested) result.error_code = "launch_failed";
                    report(result.launch_requested ? "APP_REQUESTED：已请求打开 " + pkg + "，等待服务端核验考勤。" : "APP_ERROR：启动请求失败。");
                }
            } catch (error) {
                result.outcome = "failed";
                result.error_code = "launch_failed";
                report("APP_ERROR：启动应用失败。");
            }
        } else if (!result.error_code) result.error_code = "launch_disabled";
        // 保持旧脚本的保亮语义：启动请求处理后继续等待配置时长，不主动熄屏。
        if (keepSeconds > 0) sleep(keepSeconds * 1000);
        return result;
    } finally {
        try { if (keeping) device.cancelKeepingAwake(); }
        finally { guard.release(); }
    }
}

// require 加载时导出共用动作；误点独立运行时只提示用途，不操作手机。
if (typeof module !== "undefined" && module.exports != null) {
    module.exports = { acquire: acquire, wakeAndLaunch: wakeAndLaunch, releaseAll: releaseAll };
} else {
    log("[Office MCP] 这是共用动作模块，无需单独运行。远程接收请运行 office_remote_listener.js；定时打卡请配置 autojs6_autowake.js。");
}
