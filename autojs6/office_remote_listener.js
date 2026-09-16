/*
 * AutoJs6 常驻远程接收器。普通脚本，不需要 ui、无障碍选择器或模拟点击。
 * 将本文件、office_device_actions.js 和 remote-config.local.json 放在同一目录。
 * 只执行服务端固定的 wake_dingtalk 动作，不执行远程传入的脚本或路径。
 */
(function () {
    var selfPath = files.path(String(engines.myEngine().getSource()));
    // AutoJs6 的 files 未提供 dirname；通过 Android 自带的 Java File 取得脚本目录。
    var folder = String(new java.io.File(selfPath).getParent());
    var actions = require(files.join(folder, "office_device_actions.js"));
    var listenerLock = null;
    var config;
    var store;

    /** 记录阶段和任务 ID；不记录 URL、请求头、响应原文或凭据。 */
    function report(message) { log("[Office MCP] " + message); }

    /**
     * 发送有超时、无重定向的 JSON 请求；只连接本地配置的服务地址。
     * @param {string} path 本地代码生成的 API 路径。
     * @param {Object} body 要发送的协议数据。
     * @returns {Object|null} JSON 响应；204 返回 null。
     */
    function post(path, body) {
        var connection = new java.net.URL(config.base_url + path).openConnection();
        var input = null;
        try {
            connection.setRequestMethod("POST");
            connection.setInstanceFollowRedirects(false);
            connection.setConnectTimeout(8000);
            connection.setReadTimeout(35000);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setRequestProperty("X-Device-Id", config.device_id);
            connection.setRequestProperty("X-Device-Key", config.device_key);
            var bytes = new java.lang.String(JSON.stringify(body)).getBytes("UTF-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            var output = connection.getOutputStream();
            try { output.write(bytes); output.flush(); } finally { output.close(); }
            var status = connection.getResponseCode();
            if (status === 204) return null;
            if (status < 200 || status >= 300) {
                var failure = new Error("HTTP 请求失败，状态=" + status);
                failure.httpStatus = status;
                throw failure;
            }
            input = new java.io.BufferedReader(new java.io.InputStreamReader(connection.getInputStream(), "UTF-8"));
            var text = "";
            var line;
            while ((line = input.readLine()) != null) {
                text += String(line);
                if (text.length > 65536) throw new Error("响应超过协议限制");
            }
            return JSON.parse(text);
        } finally {
            if (input != null) input.close();
            connection.disconnect();
        }
    }

    /** 上报持久化回执；失败保留待发送记录，之后只重传回执，不再执行动作。 */
    function sendReceipt(entry) {
        try {
            post("/api/devices/" + config.device_id + "/commands/" + entry.id + "/report", entry.receipt);
            store.remove("pending_receipt");
            report("回执已接收，任务=" + entry.id + "；最终考勤由服务端核验。");
            return true;
        } catch (error) {
            // 任务已被管理员移除或凭据失效时不能永久占住接收循环。
            if (error.httpStatus === 404 || error.httpStatus === 403) {
                store.remove("pending_receipt");
                report("回执无法归档，任务=" + entry.id + "，HTTP=" + error.httpStatus);
                return true;
            }
            report("回执暂未送达，5 秒后仅重传回执。");
            return false;
        }
    }

    /**
     * 执行一个命令并在执行前保存标记，脚本中断后不盲目重做。
     * @param {Object} command 服务端刚刚领取的命令。
     */
    function execute(command) {
        if (!/^[a-f0-9]{32}$/.test(String(command.task_id)) || !/^[a-f0-9]{32}$/.test(String(command.lease_token))) {
            throw new Error("任务领取协议无效");
        }
        var id = String(command.task_id);
        var previous = store.get("done_" + id, null);
        if (previous != null) { store.put("pending_receipt", previous); return; }
        var entry = { id: id, receipt: { lease_token: String(command.lease_token), outcome: "uncertain", error_code: "execution_interrupted" } };
        // 标记先于手机动作落盘。中断后只报告不确定并由服务端查询真实考勤。
        store.put("done_" + id, entry);
        store.put("pending_receipt", entry);
        if (command.action !== "wake_dingtalk") {
            entry.receipt = { lease_token: entry.receipt.lease_token, outcome: "failed", error_code: "unsupported_action" };
        } else {
            var remaining = Number(command.expires_at_unix_ms) - Number(command.server_time_unix_ms);
            // 服务器时间差用于处理手机时钟偏差，并预留两秒传输余量。
            var deadline = Date.now() + remaining - 2000;
            if (!isFinite(remaining) || remaining <= 2000 || remaining > 600000) {
                entry.receipt = { lease_token: entry.receipt.lease_token, outcome: "expired", error_code: "command_expired" };
            } else {
                try {
                    var result = actions.wakeAndLaunch({
                        openAppAfterWake: true,
                        targetAppPackage: "com.alibaba.android.rimet",
                        targetAppName: "",
                        openAppDelayMs: config.open_app_delay_ms
                    }, config.keep_screen_on_seconds, deadline, report);
                    entry.receipt = { lease_token: entry.receipt.lease_token, outcome: result.outcome, error_code: result.error_code };
                } catch (error) {
                    // 中断或异常可能发生在启动请求之后，保留不确定结果，禁止自动再开一次。
                    entry.receipt = { lease_token: entry.receipt.lease_token, outcome: "uncertain", error_code: "action_interrupted" };
                }
            }
        }
        store.put("done_" + id, entry);
        store.put("pending_receipt", entry);
        report("动作结束，任务=" + id + "，结果=" + entry.receipt.outcome);
    }

    try {
        config = JSON.parse(files.read(files.join(folder, "remote-config.local.json")).replace(/^\uFEFF/, ""));
        config.base_url = String(config.base_url || "").replace(/\/+$/, "");
        if (!/^https?:\/\/[^\s/?#]+(?::\d+)?$/.test(config.base_url) || config.base_url.indexOf("@") >= 0
            || !/^[A-Za-z0-9_-]{1,64}$/.test(String(config.device_id))
            || typeof config.device_key !== "string" || config.device_key.length < 32 || config.device_key.length > 512) {
            throw new Error("远程连接配置无效");
        }
        config.keep_screen_on_seconds = Number(config.keep_screen_on_seconds == null ? 15 : config.keep_screen_on_seconds);
        config.open_app_delay_ms = Number(config.open_app_delay_ms == null ? 800 : config.open_app_delay_ms);
        if (!isFinite(config.keep_screen_on_seconds) || config.keep_screen_on_seconds < 0 || config.keep_screen_on_seconds > 30
            || Math.floor(config.keep_screen_on_seconds) !== config.keep_screen_on_seconds
            || !isFinite(config.open_app_delay_ms) || config.open_app_delay_ms < 0 || config.open_app_delay_ms > 10000
            || Math.floor(config.open_app_delay_ms) !== config.open_app_delay_ms) throw new Error("手机动作配置无效");
        listenerLock = actions.acquire("remote-listener", 0, null);
        if (listenerLock == null) { report("已有接收脚本运行，本实例退出。"); return; }
        store = storages.create("office-mcp.remote.v1." + config.device_id);
        report("远程接收器已启动，正在连接服务端；保留原来的定时任务。");
        var failures = 0;
        var hasConnected = false;
        while (true) {
            try {
                var pending = store.get("pending_receipt", null);
                if (pending != null && !sendReceipt(pending)) { sleep(5000); continue; }
                var command = post("/api/devices/" + config.device_id + "/commands/lease", { wait_seconds: 25 });
                // 仅首次连接和失败后恢复时记录成功，正常的连续长轮询不刷屏。
                if (!hasConnected) {
                    report("连接成功，正在等待远程命令。");
                    hasConnected = true;
                } else if (failures > 0) {
                    report("连接已恢复，正在等待远程命令。");
                }
                failures = 0;
                if (command != null) execute(command);
            } catch (error) {
                failures++;
                // 日志与实际 sleep 使用同一个值，保持原有 2、4、8、16、30 秒退避规则。
                var retryDelayMs = Math.min(30000, 1000 * Math.pow(2, Math.min(failures, 5)));
                var reason = error.httpStatus === 401 ? "设备认证失败，请检查本机配置" : "连接暂不可用";
                report(reason + "；连续失败 " + failures + " 次，" + retryDelayMs / 1000 + " 秒后重试。");
                sleep(retryDelayMs);
            }
        }
    } catch (error) {
        // 不输出原始异常，防止包含连接配置或响应正文。
        report("接收器停止：请检查配置文件、存储权限和 AutoJs6 日志。");
    } finally {
        if (listenerLock != null) listenerLock.release();
    }
})();
