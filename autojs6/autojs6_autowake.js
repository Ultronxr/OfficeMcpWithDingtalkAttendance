/*
 * AutoJs6 定时亮屏脚本 v1.2
 * 普通脚本，无需添加 "ui"、auto.waitFor() 或 console.show()。
 *
 * direct：运行后立即亮屏，可选打开 APP。
 * random：先生成当天随机窗口内的一次性子任务，到点亮屏，可选打开 APP。
 * cancel：取消本文件登记的待执行随机子任务，不删除手动创建的主任务。
 *
 * 日期、星期、循环规则由 AutoJs6 自带的定时任务编排。
 * random 模式不长时间 sleep；主脚本登记子任务后退出。
 * 在 [randomStart, randomEnd - delayBufferSeconds] 中随机登记，延迟后仍只在原窗口内发起动作。
 * 所有时间采用手机本地时间；随机窗口不能跨午夜。
 * 同一文件每天只生成一个有效计划；不同窗口请使用不同文件。
 * 不要同时运行同一文件的多个规划实例。
 *
 * 已保留日期兼容性修复：date 直接传毫秒时间戳，不使用 new Date(chosen)。
 * 沿用原存储名称和文件路径作为记录键，不自动清空旧计划。
 * 远程联动扩展：动作复用同目录 office_device_actions.js；原计划编排保持独立。
 */

var CONFIG = {
    enabled: true,

    // direct / random / cancel
    mode: "random",

    // 0：只亮屏；15：额外保亮约 15 秒。
    // 启用打开 APP 时，在启动请求处理完毕后继续保亮这些秒数。
    keepScreenOnSeconds: 15,

    // 正式上午执行窗口，不支持跨午夜；随机登记上界为 randomEnd 减去配置的延迟余量。
    // 系统延迟时，发起设备动作仍不得超过 randomEnd 的窗口截止时间。
    randomStart: "08:45:00",
    randomEnd: "08:58:00",

    // 实际开始规划时，距离窗口开始至少还剩多少秒。
    // 允许 10～3600 秒，不是必须提前 20 分钟。
    minPlanningLeadSeconds: 10,

    // 在窗口尾部预留的调度延迟余量，单位为秒，允许 1～3600 的整数。
    // 随机登记范围为 [randomStart, randomEnd - 此余量]，窗口长度不能小于余量。
    // 被系统延迟后允许执行到 randomEnd，不再使用抽中时刻后的 30 秒限制。
    delayBufferSeconds: 180,

    // true：亮屏后请求打开 APP；false：只亮屏。
    openAppAfterWake: true,

    // 包名优先。包名和名称至少填写一项，关闭打开 APP 时可都留空。
    targetAppPackage: "com.alibaba.android.rimet",
    targetAppName: "",

    // 确认亮屏后，请求打开 APP 前等待的毫秒数；允许 0～10000。
    openAppDelayMs: 800,

    // 仅上午随机子任务接入服务端上班核验；direct、下午计划不生成核验任务。
    verifyScheduledAttendance: true,

    // 日志追加到当前脚本完整路径后加 .log 的文件。
    writeLogFile: true
};

(function () {
    var selfPath = files.path(String(engines.myEngine().getSource()));
    var store;
    var state;
    var automationGuard = null;

    function pad(n) {
        return n < 10 ? "0" + n : String(n);
    }

    function stamp(value) {
        var d = new Date(value);
        return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" +
            pad(d.getDate()) + " " + pad(d.getHours()) + ":" +
            pad(d.getMinutes()) + ":" + pad(d.getSeconds());
    }

    function report(message) {
        var line = "[" + stamp(Date.now()) + "] " + message;
        log(line);

        if (CONFIG.writeLogFile) {
            try {
                files.append(selfPath + ".log", line + "\n");
            } catch (e) {
                log("文件日志写入失败，不影响主流程：" + String(e));
            }
        }
    }

    function reportException(prefix, e) {
        // 同时输出异常描述和调用栈，避免只看到行号。
        report(prefix + "：" + String(e));

        if (e && e.message) {
            report(prefix + "_MESSAGE：" + String(e.message));
        }

        if (e && e.javaException) {
            report(prefix + "_JAVA：" + String(e.javaException));
        }

        if (e && e.stack) {
            report(prefix + "_STACK：\n" + String(e.stack));
        }
    }

    function integer(value, min, max, name) {
        if (typeof value !== "number" || !isFinite(value) ||
            Math.floor(value) !== value || value < min || value > max) {
            throw new Error(
                name + " 必须是 " + min + "～" + max + " 的整数"
            );
        }

        return value;
    }

    function todayAt(text, now) {
        var m = /^(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(text);

        if (!m) {
            throw new Error(
                "时间格式必须是 HH:mm 或 HH:mm:ss：" + text
            );
        }

        var h = integer(Number(m[1]), 0, 23, "小时");
        var mi = integer(Number(m[2]), 0, 59, "分钟");
        var s = integer(Number(m[3] || 0), 0, 59, "秒");

        var d = new Date(now);
        d.setHours(h, mi, s, 0);

        // 拒绝夏令时跳变造成的不存在的本地时刻。
        if (d.getHours() !== h ||
            d.getMinutes() !== mi ||
            d.getSeconds() !== s) {
            throw new Error("当天不存在这个本地时刻：" + text);
        }

        return d.getTime();
    }

    function taskId() {
        var args = engines.myEngine().execArgv;
        var intent = args && args.intent;

        return intent ?
            Number(intent.getLongExtra("task_id", -1)) :
            -1;
    }

    function save() {
        store.put(selfPath, state);
    }

    /**
     * 复用设备动作，记录亮屏结果并保留执行过期状态。
     * @param {number} keepSeconds 继续保亮的秒数。
     * @param {number|null} deadline 本次计划的执行截止时间。
     * @param {Object|null} verification 本地执行事实句柄，direct 模式不传入。
     * @returns {string} 亮屏状态，或在应用启动前到达截止时间时的 expired 状态。
     */
    function wake(keepSeconds, deadline, verification) {
        // AutoJs6 的 files 未提供 dirname，使用 Java File 解析当前入口的目录。
        var folder = String(new java.io.File(selfPath).getParent());
        var actions = require(files.join(folder, "office_device_actions.js"));
        var result = actions.wakeAndLaunch(CONFIG, keepSeconds, deadline, report, verification ? function (name, time) {
            // 核验落盘故障只记录错误，不阻断既有设备动作。
            try { verification.queue.stage(verification.handle, name, time); }
            catch (error) { report("VERIFY_STORAGE_ERROR：阶段保存失败，后续仅核验已保存事实。"); }
        } : null);
        if (verification) verification.result = result;
        if (result.error_code === "wake_failed") {
            throw new Error("请求亮屏 3 次后屏幕仍未点亮");
        }
        if (result.error_code) report("ACTION_RESULT：" + result.error_code);
        // 已亮屏但尚未拉起应用时也可能跨过截止时间，不能只记为 screen_on。
        if (result.error_code === "command_expired") return "expired";
        // 计划继续保存原亮屏状态；考勤结果通过独立的执行标识与服务端 Task 关联。
        return result.wake_status;
    }

    /** 动作前登记本地事实；只处理上午随机计划，不在规划阶段联系服务端。 */
    function beginVerification(p) {
        if (CONFIG.verifyScheduledAttendance !== true || new Date(p.at).getHours() >= 12) return null;
        try {
            var folder = String(new java.io.File(selfPath).getParent());
            var queue = require(files.join(folder, "office_attendance_queue.js"));
            p.localRunId = p.localRunId || String(java.util.UUID.randomUUID()).replace(/-/g, "");
            save();
            var name = CONFIG.writeLogFile ? String(new java.io.File(selfPath).getName()) + ".log" : null;
            var handle = queue.begin(p.localRunId, p.day, p.actualAt, name);
            report("VERIFY_LOCAL：执行事实已保存，local_run_id=" + p.localRunId + "；由常驻接收器补报。");
            return { queue: queue, handle: handle, result: { outcome: "uncertain", error_code: "action_interrupted" } };
        } catch (error) {
            report("VERIFY_STORAGE_ERROR：本地核验队列未能登记，请检查模块和存储权限；定时动作继续。");
            return null;
        }
    }

    /** 结束本次执行事实并允许接收器上报；无网络请求，不延长动作窗口。 */
    function finishVerification(verification) {
        if (!verification) return;
        try {
            verification.queue.complete(verification.handle, verification.result);
            report("VERIFY_QUEUED：等待上报，local_run_id=" + verification.handle.entry.report.local_run_id);
        } catch (error) { report("VERIFY_STORAGE_ERROR：最终动作结果保存失败，保留已有事实用于核验。"); }
    }
    function cancelPending() {
        var n = 0;

        for (var i = 0; i < state.plans.length; i++) {
            var p = state.plans[i];

            if (p.status !== "pending" &&
                p.status !== "creating") {
                continue;
            }

            if (p.id >= 0) {
                var t = tasks.getTimedTask(p.id);

                if (t && String(t.getScriptPath()) === selfPath) {
                    tasks.removeTimedTask(p.id);
                }
            }

            p.status = "cancelled";
            n++;
        }

        save();

        report(
            "已取消 " + n +
            " 个待执行随机计划；用户创建的周期任务未改动。"
        );
    }

    /**
     * 从持久化计划解析执行边界；配置后续修改不改变已经登记的计划。
     * @param {Object} p 待执行的计划快照，新计划使用 policyVersion=2。
     * @returns {Object} 窗口起点、不得早于的计划时刻及动作截止时间。
     */
    function executionWindow(p) {
        var at = integer(p.at, 0, 8640000000000000, "计划时刻");
        var end = integer(p.endAt, at, 8640000000000000, "计划截止时间");

        if (typeof p.policyVersion === "undefined") {
            // 旧计划没有完整窗口起点，继续使用其创建时保存的容差，不猜测或扩大旧边界。
            var legacyLate = integer(p.maxLateSeconds, 0, 3600, "旧计划 maxLateSeconds");
            return { startAt: at, notBefore: at, deadline: Math.min(end, at + legacyLate * 1000) };
        }
        if (p.policyVersion !== 2) throw new Error("无法识别计划的时间策略版本");

        var start = integer(p.startAt, 0, at, "计划窗口起点");
        var buffer = integer(p.delayBufferSeconds, 1, 3600, "计划 delayBufferSeconds");
        if (at > end - buffer * 1000) throw new Error("计划时刻未保留指定的窗口末尾余量");
        return { startAt: start, notBefore: at, deadline: end };
    }

    /**
     * 领取并执行一次随机计划，拒绝提前触发、越过窗口或重复执行。
     * @param {Object} p 当前任务 ID 对应的持久化计划。
     * @returns {void}
     */
    function executePlan(p) {
        if (p.status !== "pending") {
            report(
                "SKIP：本次计划状态为 " + p.status +
                "，不重复执行。"
            );
            return;
        }

        var window;
        try {
            window = executionWindow(p);
        } catch (e) {
            // 损坏或无法识别的边界必须停止执行，避免 NaN 比较绕过截止时间。
            p.status = "failed";
            p.error = String(e);
            save();
            throw e;
        }
        var now = Date.now();
        var deadline = window.deadline;

        // 先标记已领取，脚本中断后不自动补执行。
        // 这里优先避免重复触发。
        p.status = "claimed";
        p.actualAt = now;
        p.delayMs = now - p.at;
        p.executionDeadlineAt = deadline;
        save();

        report(
            "RUN：计划 ID=" + p.id + "，目标=" + stamp(p.at) +
            "，实际=" + stamp(now) + "，调度偏差毫秒=" + p.delayMs +
            "，允许窗口=" + stamp(window.startAt) + "～" + stamp(deadline)
        );

        var verification = beginVerification(p);
        if (now < window.notBefore || now > deadline) {
            p.status = "skipped";
            p.skipReason = now < window.notBefore ? "not_due" : "window_expired";
            save();

            if (verification) verification.result = { outcome: "expired", error_code: p.skipReason };
            finishVerification(verification);

            report(
                "SKIP：" + (p.skipReason === "not_due" ? "尚未到计划时刻" : "已超过执行截止时间") +
                "。抽中=" + stamp(p.at) + "，最晚=" + stamp(deadline)
            );
            return;
        }

        try {
            p.status = wake(p.keepSeconds, deadline, verification);
            save();
        } catch (e) {
            p.status = "failed";
            p.error = String(e);
            save();
            throw e;
        } finally { finishVerification(verification); }
    }

    /**
     * 为当天登记一个随机子任务，在原窗口尾部预留调度延迟余量。
     * @returns {void} 登记完成后退出，不在规划进程中等待到点。
     */
    function planRandom() {
        integer(
            CONFIG.minPlanningLeadSeconds,
            10,
            3600,
            "minPlanningLeadSeconds"
        );

        integer(
            CONFIG.delayBufferSeconds,
            1,
            3600,
            "delayBufferSeconds"
        );

        if (typeof tasks.addDisposableTask !== "function") {
            throw new Error(
                "当前 AutoJs6 没有 tasks.addDisposableTask，请检查版本"
            );
        }

        if (!files.exists(selfPath)) {
            throw new Error("请先将脚本保存为本地 .js 文件");
        }

        var now = Date.now();
        var day = stamp(now).slice(0, 10);

        // 同一文件同一天只生成一个有效计划。
        // 取消或创建失败的计划，不阻止重新规划。
        for (var i = 0; i < state.plans.length; i++) {
            var old = state.plans[i];

            if (old.day === day &&
                old.status !== "cancelled" &&
                old.status !== "schedule_failed") {
                report(
                    "SKIP：今天已有计划 " + stamp(old.at) +
                    "，状态=" + old.status +
                    "。不重新随机。"
                );
                return;
            }
        }

        var start = todayAt(CONFIG.randomStart, now);
        var end = todayAt(CONFIG.randomEnd, now);

        if (end < start) {
            throw new Error(
                "随机窗口不能跨午夜；请拆分为不同日期的任务"
            );
        }

        var randomUntil = end - CONFIG.delayBufferSeconds * 1000;
        if (randomUntil < start) {
            throw new Error(
                "随机窗口长度不能小于 delayBufferSeconds=" + CONFIG.delayBufferSeconds +
                " 秒；请扩大窗口或减小余量，不会提前越过 randomStart。"
            );
        }

        if (start - now < CONFIG.minPlanningLeadSeconds * 1000) {
            report(
                "SKIP：规划太晚。需在 " +
                stamp(start - CONFIG.minPlanningLeadSeconds * 1000) +
                " 或更早运行；不会挪到明天或缩小随机窗口。"
            );
            return;
        }

        // 在缩短后的整数秒候选中均匀随机，不对原随机结果做截断，避免偏向窗口起点。
        // 恰好等于余量的窗口只有 randomStart 一个候选时刻，仍然合法。
        var seconds = (randomUntil - start) / 1000;

        var chosen = start +
            Math.floor(Math.random() * (seconds + 1)) * 1000;

        var p = {
            id: -1,
            day: day,
            at: chosen,
            policyVersion: 2,
            startAt: start,
            endAt: end,
            status: "creating",
            keepSeconds: CONFIG.keepScreenOnSeconds,
            delayBufferSeconds: CONFIG.delayBufferSeconds
        };

        // 保留近 62 天记录，以及尚未到期的记录。
        state.plans = state.plans.filter(function (x) {
            return x.endAt >= now - 62 * 86400000;
        });

        state.plans.push(p);
        save();

        try {
            report(
                "SCHEDULE_REQUEST：目标=" + stamp(chosen) +
                "，毫秒时间戳=" + chosen +
                "，参数类型=" + typeof chosen
            );

            // 关键兼容性修复：
            // date 直接传数值毫秒时间戳，不使用 new Date(chosen)。
            var t = tasks.addDisposableTask({
                path: selfPath,
                date: chosen,
                delay: 0,
                loopTimes: 1,
                interval: 0,
                isAsync: false
            });

            if (!t || typeof t.getId !== "function") {
                throw new Error(
                    "创建一次性任务未返回有效任务对象"
                );
            }

            p.id = Number(t.getId());

            if (!(p.id >= 0) || !tasks.getTimedTask(p.id)) {
                throw new Error("无法回读新建的一次性任务");
            }

            p.status = "pending";
            save();
        } catch (e) {
            p.status = "schedule_failed";

            if (p.id >= 0) {
                try {
                    tasks.removeTimedTask(p.id);
                } catch (cleanupError) {
                    reportException("CLEANUP_ERROR", cleanupError);
                }
            }

            save();
            throw e;
        }

        report(
            "PLANNED：" + stamp(chosen) +
            "，一次性任务 ID=" + p.id +
            "，随机登记范围=" + stamp(start) + "～" + stamp(randomUntil) +
            "，执行窗口截止=" + stamp(end) +
            "，尾部预留秒数=" + CONFIG.delayBufferSeconds +
            "。本次不亮屏，登记完毕即退出。"
        );
    }

    try {
        if (!CONFIG.enabled) {
            report("DISABLED：脚本已禁用。");
            return;
        }

        if (["direct", "random", "cancel"].indexOf(CONFIG.mode) < 0) {
            throw new Error(
                "mode 只能是 direct / random / cancel"
            );
        }

        integer(
            CONFIG.keepScreenOnSeconds,
            0,
            600,
            "keepScreenOnSeconds"
        );

        // 规划、随机子任务与远程开关应用共用门禁；手动 direct 不受远程开关影响。
        // 对已登记子任务即使后来改为 direct 也保留检查，避免绕过关闭状态。
        var id = taskId();
        if (CONFIG.mode !== "direct" || id >= 0) {
            var folder = String(new java.io.File(selfPath).getParent());
            var automation = require(files.join(folder, "office_automation_control.js"));
            automationGuard = automation.enter();
            if (!automationGuard) {
                report("AUTOMATION_BUSY：控制状态正在同步，本次自动任务停止。");
                return;
            }
        }

        // 保留原来的存储名称，兼容之前已经登记的计划；必须在控制锁内读取，防止覆盖刚取消的计划。
        store = storages.create("autojs6.screen_wake.v1");
        state = store.get(selfPath, { plans: [] });

        if (!state || !Array.isArray(state.plans)) {
            throw new Error("本地计划记录格式异常");
        }

        if (CONFIG.mode === "cancel") {
            cancelPending();
            return;
        }

        if (automationGuard && automationGuard.policy && !automationGuard.policy.enabled) {
            report("AUTOMATION_DISABLED：被动自动打卡已关闭，revision=" + automationGuard.policy.revision +
                "；不规划、不亮屏、不打开钉钉。主动打卡仍可使用。");
            return;
        }

        // 优先识别已登记的随机子任务。
        // 避免把子任务再次当作规划任务。
        var liveTask = id >= 0 ? tasks.getTimedTask(id) : null;
        for (var i = state.plans.length - 1; i >= 0; i--) {
            // 已移除的一次性任务可能仍收到回调；有现存任务时同时核对类型和时刻，避免旧 ID 遮蔽周期主任务。
            if (id >= 0 && state.plans[i].id === id && (!liveTask ||
                (liveTask.isDisposable() && Number(liveTask.getMillis()) === state.plans[i].at))) {
                executePlan(state.plans[i]);
                return;
            }
        }

        if (CONFIG.mode === "random") {
            planRandom();
        } else {
            wake(CONFIG.keepScreenOnSeconds, null);
        }
    } catch (e) {
        // 记录错误，不主动弹出错误界面干扰目标 APP。
        reportException("ERROR", e);
    } finally {
        if (automationGuard) automationGuard.release();
    }
})();
