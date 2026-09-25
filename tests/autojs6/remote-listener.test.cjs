/* 运行真实常驻入口，以内存 HTTP 和动作替身验证优先级及 GET/POST 协议，不连接手机。 */
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');
const source = fs.readFileSync(path.resolve(__dirname, '../../autojs6/office_remote_listener.js'), 'utf8');

/** 为无限循环提供有界内存场景，请求完成后模拟脚本停止并检查锁已释放。 */
function run(queueFailure = false, cooperativeStop = false, controlFailure = false) {
    const requests = [];
    const events = [];
    const stored = new Map([['pending_receipt', { id: 'c'.repeat(32), receipt: { lease_token: 'd'.repeat(32), outcome: 'uncertain' } }]]);
    let released = false;
    let stop = false;
    let pumps = 0;
    let leases = 0;
    let actions = 0;
    let cleanupStarted = false;
    let cleanupInterrupted = false;
    let tracked = null;
    const now = Date.now();
    const config = { base_url: 'http://192.0.2.10:18101', device_id: 'office-phone', device_key: 'synthetic-key-'.repeat(4) };

    /** 将脚本的 Java 网络操作转换为可观察的内存请求。 */
    function Url(url) {
        this.openConnection = () => {
            if (leases >= 2) { stop = true; throw new Error('模拟结束'); }
            const request = { path: new URL(url).pathname, body: null };
            let response;
            return {
                setRequestMethod(value) { request.method = value; },
                setInstanceFollowRedirects(value) { assert.equal(value, false); },
                setConnectTimeout(value) { request.connectTimeout = value; },
                setReadTimeout(value) { request.readTimeout = value; },
                setDoOutput(value) { request.doOutput = value; },
                setRequestProperty() {}, setFixedLengthStreamingMode() {},
                getOutputStream: () => ({ write(value) { request.body = JSON.parse(String(value)); }, flush() {}, close() {} }),
                getResponseCode() {
                    requests.push(request);
                    events.push(request.path);
                    if (request.path.endsWith('/lease')) {
                        leases++;
                        response = leases === 1 ? { task_id: 'a'.repeat(32), lease_token: 'b'.repeat(32), action: 'wake_dingtalk',
                            expires_at_unix_ms: now + 60000, server_time_unix_ms: now } : null;
                    } else if (request.path.endsWith('/report')) response = { accepted: true };
                    else response = { task_id: 'e'.repeat(32), is_terminal: false };
                    return response === null ? 204 : 200;
                },
                getInputStream: () => JSON.stringify(response), disconnect() {}
            };
        };
    }

    /** 最小 Java I/O 替身，仅模拟实际入口所用方法。 */
    function JavaFile() { this.getParent = () => '/scripts'; }
    function JavaString(value) { this.getBytes = () => String(value); }
    function InputStreamReader(value) { this.text = value; }
    function BufferedReader(reader) { let read = false; this.readLine = () => read ? null : (read = true, reader.text); this.close = () => {}; }
    const api = {
        log() {}, sleep() { if (stop) throw new Error('模拟脚本停止'); },
        files: { path: String, join: path.posix.join, read: () => JSON.stringify(config) },
        engines: { myEngine: () => ({ getSource: () => '/scripts/office_remote_listener.js' }) },
        java: { net: { URL: Url }, io: { File: JavaFile, InputStreamReader, BufferedReader },
            lang: { String: JavaString, Thread: { currentThread: () => ({ isInterrupted: () => false }) } } },
        runtime: { getProperty: key => { assert.equal(key, 'office_mcp.listener.stop'); return cooperativeStop && pumps > 0; } },
        threads: { start(callback) {
            assert.equal(typeof callback, 'function');
            cleanupStarted = true;
            return { interrupt() { cleanupInterrupted = true; }, join(value) { assert.equal(value, 1500); } };
        } },
        storages: { create: () => ({ get: (key, fallback) => stored.get(key) ?? fallback,
            put(key, value) { stored.set(key, structuredClone(value)); }, remove(key) { stored.delete(key); } }) },
        require(name) {
            if (name.endsWith("/office_time.js")) return require("../../autojs6/office_time.js");
            if (name.endsWith('/office_attendance_cleanup.js')) return {
                tick() { events.push('home-tick'); },
                pump(request, deviceId) {
                    if (!tracked) return false;
                    request('GET', '/api/devices/' + deviceId + '/attendance/tasks/' + tracked.id, null);
                    tracked = null;
                    return true;
                }
            };
            if (name.endsWith('/office_automation_control.js')) return { sync(request, deviceId) {
                events.push('control-sync');
                assert.equal(deviceId, 'office-phone');
                if (controlFailure) throw new Error('模拟控制同步故障');
            } };
            if (name.endsWith('/office_device_actions.js')) return {
                acquire: () => ({ release() { released = true; } }),
                wakeAndLaunch(config, keepSeconds, deadline, report, stage, run) {
                    assert.equal(run.source, 'remote_command');
                    assert.equal(run.id, 'a'.repeat(32));
                    tracked = run;
                    actions++; events.push('action'); return { outcome: 'launch_requested', error_code: null };
                }
            };
            assert.ok(name.endsWith('/office_attendance_queue.js'));
            return { pump(request, deviceId) {
                assert.equal(deviceId, 'office-phone');
                pumps++;
                if (queueFailure) throw new Error('模拟队列读取故障');
                request(pumps === 1 ? 'POST' : 'GET', pumps === 1 ? '/api/devices/office-phone/attendance/executions'
                    : '/api/devices/office-phone/attendance/tasks/' + 'e'.repeat(32), pumps === 1 ? { local_run_id: 'f'.repeat(32) } : null);
                return true;
            } };
        }
    };
    vm.runInNewContext(source, api, { timeout: 1000 });
    return { requests, events, actions, released, stored, cleanupStarted, cleanupInterrupted };
}

test('旧远程回执优先，本地短请求和最终 GET 查询共存且不重复生成远程动作', () => {
    const result = run();
    assert.ok(result.requests[0].path.endsWith('/commands/' + 'c'.repeat(32) + '/report'));
    assert.equal(result.actions, 1);
    const local = result.requests.filter(request => request.path.includes('/attendance/') && !request.path.endsWith('a'.repeat(32)));
    assert.deepEqual(local.map(request => request.method), ['POST', 'GET']);
    assert.equal(local[1].body, null);
    assert.equal(local[1].doOutput, undefined);
    for (const request of local) {
        assert.equal(request.connectTimeout, 3000);
        assert.equal(request.readTimeout, 5000);
    }
    assert.deepEqual(result.requests.filter(request => request.path.endsWith('/lease')).map(request => request.body.wait_seconds), [5, 5]);
    assert.equal(result.stored.get('pending_receipt'), undefined);
    assert.equal(result.released, true);
});

test('本地队列故障不会阻止原远程任务领取与回执', () => {
    const result = run(true);
    assert.equal(result.actions, 1);
    assert.deepEqual(result.requests.filter(request => request.path.endsWith('/lease')).map(request => request.body.wait_seconds), [25, 5]);
    assert.equal(result.stored.get('pending_receipt'), undefined);
    assert.equal(result.released, true);
});

test('停止请求在长轮询后生效，已领取命令只保存不确定回执，不执行动作', () => {
    const result = run(false, true);
    assert.equal(result.actions, 0);
    assert.equal(result.stored.get('pending_receipt').receipt.outcome, 'uncertain');
    assert.equal(result.stored.get('pending_receipt').receipt.error_code, 'listener_stopping');
    assert.equal(result.released, true);
});

test('开关同步先于待补回执，失败不阻断主动打卡和本地核验', () => {
    const result = run(false, false, true);
    assert.equal(result.events[0], 'control-sync');
    assert.equal(result.actions, 1);
    assert.equal(result.stored.get('pending_receipt'), undefined);
    assert.equal(result.requests.filter(request => request.path.includes('/attendance/')).length, 3);
    assert.equal(result.released, true);
});

test('主动任务关联终态 GET，独立收尾线程随常驻接收器启动和停止', () => {
    const result = run();
    const remote = result.requests.filter(request => request.path.endsWith('/attendance/tasks/' + 'a'.repeat(32)));
    assert.equal(remote.length, 1);
    assert.equal(remote[0].method, 'GET');
    assert.equal(remote[0].connectTimeout, 3000);
    assert.equal(remote[0].readTimeout, 5000);
    assert.equal(result.cleanupStarted, true);
    assert.equal(result.cleanupInterrupted, true);
});
