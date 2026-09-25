/* 全部手机业务时间共用此模块；固定 UTC+8，数值时间戳始终代表原来的瞬间。 */
var offsetMs = 8 * 60 * 60 * 1000;

/** 左侧补零，统一日期和时分秒，不依赖系统区域格式。 */
function pad(value, width) { var text = String(value); while (text.length < width) text = "0" + text; return text; }

/** 读取毫秒时间的北京时间日历分量，仅用 UTC 方法避免手机本地时区影响。 */
function parts(value) {
    if (typeof value !== "number" || !isFinite(value)) throw new Error("时间戳无效");
    var date = new Date(value + offsetMs);
    if (!isFinite(date.getTime())) throw new Error("时间戳超出范围");
    return date;
}

/**
 * 输出与服务端一致的 yyyy-MM-ddTHH:mm:ss+08:00；只在展示时省略小数，不四舍五入。
 * @param {number|string} value Unix 毫秒，或带偏移／明确的无偏移北京时间字符串。
 * @returns {string} 完整的北京时间表示。
 */
function format(value) {
    if (typeof value === "string") {
        var match = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})?$/.exec(value);
        if (!match) throw new Error("日期时间格式无效");
        var milliseconds = Number(((match[7] || "") + "000").slice(0, 3));
        var local = new Date(0);
        local.setUTCFullYear(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
        local.setUTCHours(Number(match[4]), Number(match[5]), Number(match[6]), milliseconds);
        if (local.getUTCFullYear() !== Number(match[1]) || local.getUTCMonth() + 1 !== Number(match[2]) ||
            local.getUTCDate() !== Number(match[3]) || local.getUTCHours() !== Number(match[4]) ||
            local.getUTCMinutes() !== Number(match[5]) || local.getUTCSeconds() !== Number(match[6])) throw new Error("日期时间不存在");
        var zone = match[8] || "+08:00";
        var minutes = zone === "Z" ? 0 : Number(zone.slice(1, 3)) * 60 + Number(zone.slice(4, 6));
        if (minutes > 840 || (zone !== "Z" && Number(zone.slice(4, 6)) > 59)) throw new Error("时区偏移无效");
        if (zone.charAt(0) === "-") minutes = -minutes;
        value = local.getTime() - minutes * 60000;
    }
    var date = parts(value);
    return pad(date.getUTCFullYear(), 4) + "-" + pad(date.getUTCMonth() + 1, 2) + "-" + pad(date.getUTCDate(), 2) +
        "T" + pad(date.getUTCHours(), 2) + ":" + pad(date.getUTCMinutes(), 2) + ":" + pad(date.getUTCSeconds(), 2) + "+08:00";
}

/** 将配置时刻与北京时间今天组合成绝对毫秒；只创建目标时刻，不更改已有定时任务。 */
function todayAt(text, now) {
    var match = /^(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(text);
    if (!match || Number(match[1]) > 23 || Number(match[2]) > 59 || Number(match[3] || 0) > 59)
        throw new Error("时间格式必须是有效的 HH:mm 或 HH:mm:ss");
    var date = parts(now);
    date.setUTCHours(Number(match[1]), Number(match[2]), Number(match[3] || 0), 0);
    return date.getTime() - offsetMs;
}

/** 判断北京时间的上午／下午，不借用手机设置。 */
function hour(value) { return parts(value).getUTCHours(); }

/** 为控制台或文件日志添加统一的北京时间正文前缀。 */
function line(message) { return "[" + format(Date.now()) + "] " + message; }

module.exports = { format: format, todayAt: todayAt, hour: hour, line: line };
