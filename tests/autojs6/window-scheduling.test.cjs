/* 黑盒运行真实 AutoJs6 入口及共用动作模块，隔离手机、定时器和网络。 */
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');

process.env.TZ = 'Asia/Shanghai';
const scriptPath = '/storage/emulated/0/脚本/自动打卡/autojs6_autowake.js';
const scriptFolder = path.posix.dirname(scriptPath);
const entrySource = fs.readFileSync(path.resolve(__dirname, '../../autojs6/autojs6_autowake.js'), 'utf8');
const actionsSource = fs.readFileSync(path.resolve(__dirname, '../../autojs6/office_device_actions.js'), 'utf8');
const queueSource = fs.readFileSync(path.resolve(__dirname, '../../autojs6/office_attendance_queue.js'), 'utf8');
const controlSource = fs.readFileSync(path.resolve(__dirname, '../../autojs6/office_automation_control.js'), 'utf8');
const bodyOffset = entrySource.indexOf('(function () {');

/** 将测试用的本地时间转换为固定日期时间戳，避免运行日期影响结果。 */
function at(time) { return Date.parse('2026-09-16T' + time + '+08:00'); }

/** 创建一条窗口策略计划；各用例仅覆盖本身关注的字段。 */
function plan(overrides = {}) {
    return { id: 16, day: '2026-09-16', policyVersion: 2, startAt: at('08:35:00'),
        at: at('08:40:00'), endAt: at('08:50:00'), delayBufferSeconds: 300,
        status: 'pending', keepSeconds: 0, ...overrides };
}

/**
 * 在虚拟时钟和内存存储中运行原始脚本；所有设备调用只记录时间。
 * @param {Object} options 用例的启动时间、任务、配置及模拟设备状态。
 * @returns {Object} 持久化状态、日志、创建的任务和设备调用记录。
 */
function run(options = {}) {
    let clock = options.now ?? at('08:30:00');
    let state = { plans: structuredClone(options.plans || []) };
    let screenOn = options.screenOn ?? false;
    const locks = new Set();
    const memoryFiles = new Map(options.files || []);
    let loadedActions;
    let loadedQueue;
    let loadedControl;
    let nextUuid = 1;
    const logs = [];
    const created = [];
    const calls = { wake: [], launch: [], keep: [], cancelKeep: [] };
    const removed = [];
    const taskMap = new Map();
    let nextTaskId = 100;

    /** 构造定时任务只读外观，配合真实入口的回读与取消逻辑。 */
    function taskRecord(id, time = 0, disposable = true, target = scriptPath) {
        return { getId: () => id, getScriptPath: () => target, getMillis: () => time, isDisposable: () => disposable };
    }
    for (const value of state.plans) {
        if (value.status === 'pending') taskMap.set(value.id, taskRecord(value.id, value.at));
    }
    for (const id of options.extraTaskIds || []) taskMap.set(id, taskRecord(id, 0, false));
    for (const task of options.reusedTasks || []) taskMap.set(task.id, taskRecord(task.id, task.at, task.disposable, task.path));

    class ClockDate extends Date {
        /** 默认构造与 Date.now 共用虚拟时钟；显式日期仍使用原生解析。 */
        constructor(...args) { super(...(args.length ? args : [clock])); }
        static now() { return clock; }
    }

    /** 模拟入口目录解析，仅提供生产脚本实际调用的 Java File 方法。 */
    function JavaFile(name) {
        this.path = String(name);
        this.getParent = () => path.posix.dirname(String(name));
        this.getName = () => path.posix.basename(String(name));
        this.mkdirs = () => true;
    }

    /** 模拟 Java UTF-8 字符串转换，不依赖 Android 或真实文件系统。 */
    function JavaString(value) {
        this.toString = () => String(value);
        this.getBytes = () => String(value);
    }

    /** 模拟 Android AtomicFile 的成功提交和失败回滚，用于持久化恢复测试。 */
    function AtomicFile(file) {
        this.startWrite = () => {
            if (options.failQueueWrites || (options.failControlWrites && file.path.endsWith('/automation.json')))
                throw new Error('模拟存储故障');
            return { value: '', write(value) { this.value = String(value); } };
        };
        this.finishWrite = output => { memoryFiles.set(file.path, output.value); };
        this.failWrite = () => {};
        this.readFully = () => {
            if (!memoryFiles.has(file.path)) throw new Error('文件不存在');
            return memoryFiles.get(file.path);
        };
    }

    /** 用内存锁与虚拟时钟模拟多个入口争用同一设备锁的情况。 */
    function RandomAccessFile(name) {
        const key = String(name);
        this.close = () => {};
        this.getChannel = () => ({
            close() {},
            tryLock() {
                if (locks.has(key) || (key.endsWith('/screen-action.lock') && clock < (options.lockBusyUntil ?? 0)) ||
                    (key.endsWith('/automation-control.lock') && options.controlBusy)) return null;
                locks.add(key);
                return { release() { locks.delete(key); } };
            }
        });
    }

    const math = Object.create(Math);
    math.random = () => options.random ?? 0.5;
    const api = {
        Date: ClockDate, Math: math, isFinite, log: text => logs.push(String(text)),
        // 只推进虚拟时间，测试不会真实休眠或等待定时任务。
        sleep: milliseconds => { clock += milliseconds; },
        events: { on() {} },
        files: { path: value => value, join: (...values) => path.posix.join(...values),
            exists: name => name === scriptPath || memoryFiles.has(name), append(name, text) { memoryFiles.set(name, (memoryFiles.get(name) || '') + text); },
            listDir: directory => [...memoryFiles.keys()].filter(name => path.posix.dirname(name) === directory).map(name => path.posix.basename(name)) },
        java: { io: { File: JavaFile, RandomAccessFile }, lang: { String: JavaString },
            util: { UUID: { randomUUID: () => (nextUuid++).toString(16).padStart(32, '0') } } },
        android: { util: { AtomicFile } },
        engines: { myEngine: () => ({ getSource: () => scriptPath,
            execArgv: { intent: { getLongExtra: () => options.taskId ?? -1 } } }) },
        device: {
            isScreenOn: () => screenOn,
            wakeUp() { calls.wake.push(clock); screenOn = true; },
            keepScreenOn() { calls.keep.push(clock); },
            cancelKeepingAwake() { calls.cancelKeep.push(clock); }
        },
        app: { getPackageName: () => 'com.alibaba.android.rimet',
            launchPackage() { calls.launch.push(clock); if (options.onLaunch) options.onLaunch(loadedControl); return true; } },
        storages: { create(name) {
            assert.equal(name, 'autojs6.screen_wake.v1');
            return {
                get(key) { assert.equal(key, scriptPath); return structuredClone(state); },
                put(key, value) {
                    assert.equal(key, scriptPath);
                    if (options.failPlanWrites) throw new Error('模拟计划保存失败');
                    state = structuredClone(value);
                }
            };
        } },
        tasks: {
            addDisposableTask(request) {
                created.push({ ...request });
                const record = taskRecord(nextTaskId++, request.date);
                taskMap.set(record.getId(), record);
                return record;
            },
            getTimedTask: id => taskMap.get(id),
            removeTimedTask(id) { removed.push(id); return options.failTaskRemoval ? false : taskMap.delete(id); }
        }
    };
    const context = vm.createContext({ ...api });
    context.require = requested => {
        if (requested === path.posix.join(scriptFolder, 'office_automation_control.js')) {
            if (options.missingControl) throw new Error('模拟控制模块未部署');
            if (!loadedControl) {
                const moduleContext = { ...api, require: context.require, module: { exports: {} } };
                vm.runInNewContext(controlSource, moduleContext, { timeout: 1000 });
                loadedControl = moduleContext.module.exports;
            }
            return loadedControl;
        }
        if (requested === path.posix.join(scriptFolder, 'office_attendance_queue.js')) {
            if (!loadedQueue) {
                const moduleContext = { ...api, require: context.require, module: { exports: {} } };
                vm.runInNewContext(queueSource, moduleContext, { timeout: 1000 });
                loadedQueue = moduleContext.module.exports;
            }
            return loadedQueue;
        }
        assert.equal(requested, path.posix.join(scriptFolder, 'office_device_actions.js'));
        if (!loadedActions) {
            const moduleContext = { ...api, module: { exports: {} } };
            vm.runInNewContext(actionsSource, moduleContext, { timeout: 1000 });
            loadedActions = moduleContext.module.exports;
        }
        return loadedActions;
    };
    // 仅替换用例配置，规划和执行函数均来自生产文件，不复制其算法。
    vm.runInContext(entrySource.slice(0, bodyOffset), context, { timeout: 1000 });
    Object.assign(context.CONFIG, { randomStart: '08:35:00', randomEnd: '08:50:00',
        keepScreenOnSeconds: 0, delayBufferSeconds: 300, ...options.config });
    vm.runInContext(entrySource.slice(bodyOffset), context, { timeout: 1000 });
    return { state, logs, created, calls, removed, taskMap, clock, fileLocked: locks.size > 0,
        files: memoryFiles, queue: loadedQueue, advance(milliseconds) { clock += milliseconds; },
        getQueue: () => context.require(path.posix.join(scriptFolder, 'office_attendance_queue.js')),
        getControl: () => context.require(path.posix.join(scriptFolder, 'office_automation_control.js')),
        getState: () => structuredClone(state) };
}

test('随机登记包含 start 和 end−5分钟，实际窗口完整保存', () => {
    for (const [random, expected] of [[0, '08:35:00'], [0.9999999, '08:45:00']]) {
        const result = run({ random });
        assert.equal(result.created.length, 1);
        assert.equal(result.created[0].date, at(expected));
        assert.equal(typeof result.created[0].date, 'number');
        assert.equal(result.state.plans[0].startAt, at('08:35:00'));
        assert.equal(result.state.plans[0].endAt, at('08:50:00'));
        assert.equal(result.state.plans[0].policyVersion, 2);
        assert.equal(result.state.plans[0].maxLateSeconds, undefined);
        assert.equal(result.calls.wake.length, 0);
        assert.equal(result.calls.launch.length, 0);
    }
});

test('下午实测迟到 209.719 秒在新窗口策略下可以请求打开应用', () => {
    const item = plan({ startAt: at('15:10:00'), at: at('15:10:11'), endAt: at('15:20:00') });
    const result = run({ plans: [item], taskId: 16, now: at('15:13:40.719') });
    assert.equal(result.calls.launch.length, 1);
    assert.ok(result.calls.launch[0] < item.endAt);
    assert.equal(result.state.plans[0].delayMs, 209719);
    assert.equal(result.state.plans[0].executionDeadlineAt, item.endAt);
});

test('5 分钟是尾部余量，实际迟到超过 5 分钟但仍在窗口内也允许执行', () => {
    const result = run({ plans: [plan({ at: at('08:35:00') })], taskId: 16, now: at('08:49:00') });
    assert.equal(result.calls.launch.length, 1);
    assert.equal(result.state.plans[0].delayMs, 14 * 60000);
});

test('窗口结束后 1 毫秒触发必须跳过，不能亮屏或打开应用', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:50:00.001') });
    assert.equal(result.state.plans[0].status, 'skipped');
    assert.equal(result.state.plans[0].skipReason, 'window_expired');
    assert.equal(result.calls.wake.length, 0);
    assert.equal(result.calls.launch.length, 0);
});

test('不在窗口前执行，也不因提前收到回调而早于抽中时刻执行', () => {
    for (const now of [at('08:34:59.999'), at('08:39:59.999')]) {
        const result = run({ plans: [plan()], taskId: 16, now });
        assert.equal(result.state.plans[0].skipReason, 'not_due');
        assert.equal(result.calls.wake.length, 0);
        assert.equal(result.calls.launch.length, 0);
    }
});

test('请求打开应用恰好在 end 时允许执行', () => {
    const result = run({ plans: [plan()], taskId: 16, screenOn: true, now: at('08:49:59.200') });
    assert.deepEqual(result.calls.launch, [at('08:50:00')]);
});

test('窗口内启动但 800 毫秒等待跨过 end 时，不打开应用并记录 expired', () => {
    const result = run({ plans: [plan()], taskId: 16, screenOn: true, now: at('08:49:59.201') });
    assert.equal(result.calls.launch.length, 0);
    assert.equal(result.state.plans[0].status, 'expired');
    assert.equal(result.fileLocked, false);
    assert.ok(result.logs.some(line => line.includes('command_expired')));
});

test('设备锁一直占用至窗口结束时，不再亮屏或打开应用', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:49:59'), lockBusyUntil: at('08:51:00') });
    assert.equal(result.state.plans[0].status, 'skipped');
    assert.equal(result.calls.wake.length, 0);
    assert.equal(result.calls.launch.length, 0);
});

test('不足 5 分钟的窗口直接报错，不登记或保存新计划', () => {
    const result = run({ config: { randomEnd: '08:39:59' } });
    assert.equal(result.created.length, 0);
    assert.equal(result.state.plans.length, 0);
    assert.ok(result.logs.some(line => line.includes('窗口长度不能小于')));
});

test('恰好 5 分钟的窗口仅在 start 登记，不移动执行边界', () => {
    const result = run({ config: { randomEnd: '08:40:00' }, random: 0.9999999 });
    assert.equal(result.created[0].date, at('08:35:00'));
    assert.equal(result.state.plans[0].endAt, at('08:40:00'));
});

test('拒绝无效的调度余量参数', () => {
    for (const delayBufferSeconds of [0, -1, 3601, 1.5, NaN, Infinity, '300']) {
        const result = run({ config: { delayBufferSeconds } });
        assert.equal(result.created.length, 0);
        assert.ok(result.logs.some(line => line.includes('delayBufferSeconds 必须')));
    }
});

test('原有跨午夜限制和提前规划限制仍然生效', () => {
    const overnight = run({ config: { randomStart: '23:50:00', randomEnd: '00:10:00' } });
    assert.equal(overnight.created.length, 0);
    assert.ok(overnight.logs.some(line => line.includes('不能跨午夜')));
    const tooLate = run({ now: at('08:34:51') });
    assert.equal(tooLate.created.length, 0);
    assert.ok(tooLate.logs.some(line => line.includes('规划太晚')));
});

test('修改 CONFIG 不改变已经登记的新计划边界', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:49:00'),
        config: { randomStart: '15:10:00', randomEnd: '15:20:00', delayBufferSeconds: 60 } });
    assert.equal(result.calls.launch.length, 1);
    assert.equal(result.state.plans[0].executionDeadlineAt, at('08:50:00'));
});

test('旧版已登记计划继续使用旧容差，不自动扩大截止时间', () => {
    const item = { id: 16, day: '2026-09-16', at: at('08:40:00'), endAt: at('08:50:00'),
        maxLateSeconds: 30, status: 'pending', keepSeconds: 0 };
    const result = run({ plans: [item], taskId: 16, now: at('08:40:31') });
    assert.equal(result.calls.launch.length, 0);
    assert.equal(result.state.plans[0].executionDeadlineAt, at('08:40:30'));
    assert.equal(result.state.plans[0].status, 'skipped');
});

test('损坏或未知版本的计划失败关闭，不绕过时间判断', () => {
    for (const overrides of [{ startAt: undefined }, { endAt: undefined },
        { policyVersion: 99 }, { at: at('08:49:00') }, { startAt: at('08:41:00') }]) {
        const result = run({ plans: [plan(overrides)], taskId: 16, now: at('08:44:00') });
        assert.equal(result.state.plans[0].status, 'failed');
        assert.equal(result.calls.wake.length, 0);
        assert.equal(result.calls.launch.length, 0);
    }
});

test('同一天不重新随机，已执行的同一任务不重复发起动作', () => {
    const first = run();
    assert.equal(run({ plans: first.state.plans }).created.length, 0);
    const fired = run({ plans: [plan()], taskId: 16, now: at('08:44:00') });
    const duplicate = run({ plans: fired.state.plans, taskId: 16, now: at('08:45:00') });
    assert.equal(duplicate.calls.launch.length, 0);
    assert.equal(duplicate.created.length, 0);
});

test('历史 skipped 记录在升级后仍保留，不补执行或重新随机', () => {
    const item = { id: 14, day: '2026-09-16', at: at('08:41:42'), endAt: at('08:50:00'),
        status: 'skipped', actualAt: at('08:44:34.549'), maxLateSeconds: 30, keepSeconds: 15 };
    const result = run({ plans: [item], now: at('14:30:00'), config: { randomStart: '15:10:00', randomEnd: '15:20:00' } });
    assert.deepEqual(result.state.plans, [item]);
    assert.equal(result.created.length, 0);
    assert.equal(result.calls.launch.length, 0);
});

test('direct 模式仍立即执行，不使用随机窗口或余量校验', () => {
    const result = run({ now: at('18:30:00'), config: { mode: 'direct', delayBufferSeconds: 0 } });
    assert.equal(result.calls.launch.length, 1);
    assert.equal(result.created.length, 0);
});

test('cancel 仅取消已登记子任务，保留手动主任务及历史记录', () => {
    const old = plan({ id: 14, status: 'skipped' });
    const result = run({ plans: [old, plan()], config: { mode: 'cancel' }, extraTaskIds: [15] });
    assert.deepEqual(result.removed, [16]);
    assert.equal(result.taskMap.has(15), true);
    assert.equal(result.state.plans[0].status, 'skipped');
    assert.equal(result.state.plans[1].status, 'cancelled');
    assert.equal(result.calls.launch.length, 0);
});

/** 从内存文件读取唯一执行队列条目，验证真实模块的持久化结果。 */
function queueEntries(result) {
    return [...result.files].filter(([name]) => name.includes('/attendance/') && name.endsWith('.json')).map(([, text]) => JSON.parse(text));
}

/** 合成服务端安全摘要，不使用真实 Task、员工或设备资料。 */
function taskResult(terminal = false) {
    return { task_id: 'a'.repeat(32), source: 'local_schedule', is_terminal: terminal,
        state: terminal ? 'succeeded' : 'verifying', attendance_confirmed: terminal,
        verification_relation: terminal ? 'within_execution_window' : null,
        record: terminal ? { actual_check_time: '2026-09-16T08:40:04+08:00', status_code: 'Normal' } : null };
}

test('上午子任务动作前持久化执行标识，保存阶段时间且不产生网络依赖', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:40:00') });
    const [entry] = queueEntries(result);
    assert.match(entry.report.local_run_id, /^[a-f0-9]{32}$/);
    assert.equal(entry.report.local_run_id, result.state.plans[0].localRunId);
    assert.equal(entry.report.executed_at_unix_ms, at('08:40:00'));
    assert.equal(entry.report.screen_on_at_unix_ms, at('08:40:00.400'));
    assert.equal(entry.report.app_requested_at_unix_ms, at('08:40:01.200'));
    assert.equal(entry.report.outcome, 'launch_requested');
    assert.equal(entry.state, 'pending');
    assert.equal(result.calls.launch.length, 1);
    assert.equal(result.fileLocked, false);
});

test('存储故障不阻止原定时动作，也不会宣称已进入服务端核验', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:40:00'), failQueueWrites: true });
    assert.equal(result.calls.launch.length, 1);
    assert.equal(result.fileLocked, false);
    assert.equal(queueEntries(result).length, 0);
    assert.ok(result.logs.some(line => line.includes('VERIFY_STORAGE_ERROR')));
});

test('断网和响应丢失后重启只补传相同执行事实，最终结果写回入口日志', () => {
    const first = run({ plans: [plan()], taskId: 16, now: at('08:40:00') });
    let firstBody;
    first.queue.pump((method, url, body) => {
        assert.equal(method, 'POST');
        firstBody = JSON.parse(JSON.stringify(body));
        throw new Error('模拟服务端已接收但响应丢失');
    }, 'office-phone');
    assert.equal(queueEntries(first)[0].state, 'pending');
    const restored = run({ plans: first.state.plans, files: first.files, now: at('08:41:00') });
    const queue = restored.getQueue();
    queue.pump((method, url, body) => {
        assert.equal(method, 'POST');
        assert.deepEqual(JSON.parse(JSON.stringify(body)), firstBody);
        return taskResult();
    }, 'office-phone');
    assert.equal(queueEntries(restored)[0].task_id, 'a'.repeat(32));
    restored.advance(5000);
    queue.pump((method, url, body) => {
        assert.equal(method, 'GET');
        assert.equal(body, null);
        assert.ok(url.endsWith('/tasks/' + 'a'.repeat(32)));
        return taskResult(true);
    }, 'office-phone');
    assert.equal(queueEntries(restored)[0].state, 'done');
    assert.ok(restored.files.get(scriptPath + '.log').includes('VERIFY_RESULT'));
    assert.ok(restored.files.get(scriptPath + '.log').includes('2026-09-16T08:40:04+08:00'));
    queue.pump(() => assert.fail('终态不再请求服务器'), 'office-phone');
    assert.equal(restored.calls.launch.length, 0);
    assert.equal(restored.calls.wake.length, 0);
});

test('未完成动作受执行锁保护，中断恢复只上报不确定事实及已保存阶段', () => {
    const result = run();
    const queue = result.getQueue();
    const handle = queue.begin('b'.repeat(32), '2026-09-16', at('08:30:00'), 'autojs6_autowake.js.log');
    queue.stage(handle, 'screen_on', at('08:30:00'));
    queue.pump(() => assert.fail('正在动作时不可提前上报半成品'), 'office-phone');
    // 模拟引擎退出释放 OS 文件锁，未执行 complete。
    handle.release();
    queue.pump((method, url, body) => {
        assert.equal(body.outcome, 'uncertain');
        assert.equal(body.error_code, 'execution_interrupted');
        assert.equal(body.screen_on_at_unix_ms, at('08:30:00'));
        assert.equal(body.completed_at_unix_ms, undefined);
        return taskResult(true);
    }, 'office-phone');
    assert.equal(result.calls.launch.length, 0);
    assert.equal(queueEntries(result)[0].state, 'done');
});

test('过期子任务仍登记执行事实，跳过设备动作', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:51:00') });
    assert.equal(result.calls.launch.length, 0);
    const [entry] = queueEntries(result);
    assert.equal(entry.report.outcome, 'expired');
    assert.equal(entry.report.error_code, 'window_expired');
    assert.equal(entry.report.app_requested_at_unix_ms, undefined);
});

test('超出当天首次补报范围时归档拒绝信息，已接受任务跨日仍只读查询', () => {
    const result = run({ plans: [plan()], taskId: 16, now: at('08:40:00') });
    result.advance(86400000);
    result.queue.pump(() => { const error = new Error('日期已过'); error.httpStatus = 400; throw error; }, 'office-phone');
    assert.equal(queueEntries(result)[0].state, 'rejected');
    result.queue.pump(() => assert.fail('拒绝的首次补报不无限重试'), 'office-phone');
    assert.ok(result.logs.some(line => line.includes('VERIFY_REJECTED')));

    const accepted = run({ plans: [plan()], taskId: 16, now: at('08:40:00') });
    accepted.queue.pump(() => taskResult(), 'office-phone');
    accepted.advance(86400000);
    accepted.queue.pump(method => { assert.equal(method, 'GET'); return taskResult(true); }, 'office-phone');
    assert.equal(queueEntries(accepted)[0].state, 'done');
});

test('每轮只发送一个本地请求，坏记录与未升级接口不会饿死其他执行', () => {
    const result = run();
    const queue = result.getQueue();
    for (const letter of ['c', 'd']) {
        const handle = queue.begin(letter.repeat(32), '2026-09-16', at('08:30:00'), null);
        queue.complete(handle, { outcome: 'uncertain', error_code: 'action_interrupted' });
    }
    let calls = 0;
    queue.pump(() => { calls++; const error = new Error('接口未升级'); error.httpStatus = 404; throw error; }, 'office-phone');
    assert.equal(calls, 1);
    queue.pump(() => { calls++; return taskResult(true); }, 'office-phone');
    assert.equal(calls, 2);
    assert.deepEqual(queueEntries(result).map(entry => entry.state), ['pending', 'done']);
});

test('下午、direct 与显式关闭核验时不生成本地 Task 事实', () => {
    const afternoon = run({ plans: [plan({ at: at('15:10:00'), startAt: at('15:00:00'), endAt: at('15:20:00') })],
        taskId: 16, now: at('15:10:00') });
    const direct = run({ config: { mode: 'direct' } });
    const disabled = run({ plans: [plan()], taskId: 16, now: at('08:40:00'), config: { verifyScheduledAttendance: false } });
    for (const result of [afternoon, direct, disabled]) {
        assert.equal(result.calls.launch.length, 1);
        assert.equal(queueEntries(result).length, 0);
    }
});

/** 构造远程开关协议与原子文件，用合成设备身份运行真实控制模块。 */
function policy(enabled, revision, lastDisabled = enabled ? 0 : revision) {
    return { device_id: 'office-phone', enabled, revision, last_disabled_revision: lastDisabled };
}
/** 创建固定入口的本地开关文件；文件内容与生产模块实际读取格式一致。 */
function controlFiles(value) {
    return [[path.posix.join(scriptFolder, '.office-mcp/automation.json'), JSON.stringify({ version: 1, ...value })]];
}

test('关闭后主任务不规划、既有子任务不动作，也不生成未发生动作的核验事实', () => {
    for (const options of [{}, { plans: [plan()], taskId: 16, now: at('08:40:00') }]) {
        const result = run({ ...options, files: controlFiles(policy(false, 1)) });
        assert.equal(result.created.length, 0);
        assert.equal(result.calls.launch.length, 0);
        assert.equal(queueEntries(result).length, 0);
        assert.ok(result.logs.some(line => line.includes('AUTOMATION_DISABLED')));
        assert.equal(result.fileLocked, false);
    }
});

test('同步关闭取消待执行子任务，保留主任务和历史记录；确认晚于取消和落盘', () => {
    const result = run({ plans: [plan(), plan({ id: 14, status: 'screen_on' })], extraTaskIds: [20] });
    const bodies = [];
    result.getControl().sync((method, url, body) => {
        bodies.push(structuredClone(body));
        if (bodies.length === 2) {
            assert.equal(result.getState().plans[0].status, 'cancelled');
            assert.equal(result.taskMap.has(16), false);
            assert.equal(JSON.parse(result.files.get(controlFiles({})[0][0])).enabled, false);
        }
        return policy(false, 1);
    }, 'office-phone');
    assert.deepEqual(bodies, [{ applied_revision: null, applied_enabled: null }, { applied_revision: 1, applied_enabled: false }]);
    assert.deepEqual(result.removed, [16]);
    assert.equal(result.taskMap.has(20), true);
    assert.equal(result.getState().plans[1].status, 'screen_on');
    assert.equal(result.getState().plans[0].cancelReason, 'automation_disabled');
    assert.equal(result.calls.launch.length, 0);
});

test('离线期间关闭又开启，最新策略仍取消旧计划；恢复开关不主动补建', () => {
    const result = run({ plans: [plan()], files: controlFiles(policy(true, 0)), extraTaskIds: [20] });
    result.getControl().sync(() => policy(true, 2, 1), 'office-phone');
    assert.equal(result.getState().plans[0].status, 'cancelled');
    assert.equal(result.created.length, 0);
    assert.equal(result.calls.launch.length, 0);
    const callback = run({ plans: result.getState().plans, files: result.files, taskId: 16, now: at('08:40:00') });
    assert.equal(callback.calls.launch.length, 0);
    assert.equal(callback.created.length, 0);
    // 次日原定主任务再次进入时才恢复原随机登记逻辑。
    const nextDay = run({ plans: result.getState().plans, files: result.files, now: at('08:30:00') + 86400000 });
    assert.equal(nextDay.created.length, 1);
    assert.equal(nextDay.created[0].date, at('08:40:00') + 86400000);
});

test('网络故障沿用最后开关，关闭状态跨引擎恢复，不阻止手动 direct', () => {
    const result = run({ files: controlFiles(policy(false, 1)) });
    assert.throws(() => result.getControl().sync(() => { throw new Error('离线'); }, 'office-phone'));
    const restored = run({ files: result.files });
    assert.equal(restored.created.length, 0);
    const manual = run({ files: result.files, config: { mode: 'direct' } });
    assert.equal(manual.calls.launch.length, 1);
    assert.equal(queueEntries(manual).length, 0);
});

test('正在执行的子任务持有门禁，关闭不得提前确认，动作完成后可同步关闭', () => {
    let during = 0;
    const result = run({ plans: [plan()], taskId: 16, now: at('08:40:00'), onLaunch(control) {
        control.sync(() => { during++; return policy(false, 1); }, 'office-phone');
    } });
    assert.equal(during, 0);
    assert.equal(result.calls.launch.length, 1);
    const acknowledgements = [];
    result.getControl().sync((method, url, body) => { acknowledgements.push(body); return policy(false, 1); }, 'office-phone');
    assert.equal(acknowledgements[1].applied_revision, 1);
    assert.equal(queueEntries(result).length, 1);
});

test('损坏、缺失模块或门禁忙时自动任务停止，不能默认开启；安装首次无状态保持原行为', () => {
    for (const options of [
        { files: [[controlFiles({})[0][0], '{broken']] },
        { files: controlFiles({ revision: 1, enabled: false }) },
        { missingControl: true }, { controlBusy: true }
    ]) {
        const result = run(options);
        assert.equal(result.calls.launch.length, 0);
        assert.equal(result.created.length, 0);
        assert.equal(result.fileLocked, false);
    }
    assert.equal(run().created.length, 1);
    assert.equal(run({ missingControl: true, config: { mode: 'direct' } }).calls.launch.length, 1);
});

test('确认响应丢失后只重传已保存版本，拒绝服务器回退或同版本冲突', () => {
    const result = run({ plans: [plan()] });
    let calls = 0;
    assert.throws(() => result.getControl().sync(() => {
        if (++calls === 2) throw new Error('确认响应丢失');
        return policy(false, 1);
    }, 'office-phone'));
    const restored = run({ plans: result.getState().plans, files: result.files });
    calls = 0;
    restored.getControl().sync((method, url, body) => {
        calls++;
        assert.equal(body.applied_revision, 1);
        return policy(false, 1);
    }, 'office-phone');
    assert.equal(calls, 1);
    assert.equal(restored.removed.length, 0);
    for (const invalid of [policy(true, 0), policy(true, 1, 1), policy(false, 2, 1), policy('false', 2)]) {
        assert.throws(() => restored.getControl().sync(() => invalid, 'office-phone'));
    }
    assert.equal(JSON.parse(restored.files.get(controlFiles({})[0][0])).enabled, false);
});

test('历史 ID 被周期主任务或其他计划复用时不误删，开启后周期主任务仍可规划', () => {
    for (const reused of [
        { id: 16, at: 0, disposable: false },
        { id: 16, at: at('09:00:00'), disposable: true },
        { id: 16, at: at('08:40:00'), disposable: true, path: '/scripts/other.js' }
    ]) {
        const result = run({ plans: [plan()], reusedTasks: [reused] });
        result.getControl().sync(() => policy(false, 1), 'office-phone');
        assert.equal(result.taskMap.has(16), true);
        assert.equal(result.removed.length, 0);
        assert.equal(result.getState().plans[0].status, 'cancelled');
    }
    const nextMain = run({ plans: [plan({ status: 'cancelled' })], taskId: 16,
        files: controlFiles(policy(true, 2, 1)), extraTaskIds: [16] });
    assert.equal(nextMain.created.length, 1);
    assert.equal(nextMain.calls.launch.length, 0);
});

test('子任务删除、计划保存或开关落盘失败时不确认生效，恢复后继续取消并确认', () => {
    for (const failure of [{ failTaskRemoval: true }, { failPlanWrites: true }, { failControlWrites: true }]) {
        const result = run({ ...failure, plans: [plan()] });
        let calls = 0;
        assert.throws(() => result.getControl().sync(() => { calls++; return policy(false, 1); }, 'office-phone'));
        assert.equal(calls, 1); // 失败期间只取策略，不发送新的版本确认。
        assert.equal(result.files.has(controlFiles({})[0][0]), false);
        const restored = run({ plans: result.getState().plans, files: result.files, now: at('08:36:00') });
        const acknowledgements = [];
        restored.getControl().sync((method, url, body) => { acknowledgements.push(body); return policy(false, 1); }, 'office-phone');
        assert.equal(acknowledgements[1].applied_revision, 1);
        assert.equal(restored.getState().plans[0].status, 'cancelled');
        assert.equal(restored.calls.launch.length, 0);
    }
});

test('确认请求期间服务端再次切换，仅应用新版本，下一轮确认且不重新规划', () => {
    const result = run({ plans: [plan()] });
    const acknowledgements = [];
    result.getControl().sync((method, url, body) => {
        acknowledgements.push(structuredClone(body));
        return acknowledgements.length === 1 ? policy(false, 1) : policy(true, 2, 1);
    }, 'office-phone');
    assert.equal(acknowledgements.length, 2);
    assert.equal(acknowledgements[1].applied_revision, 1);
    assert.equal(JSON.parse(result.files.get(controlFiles({})[0][0])).revision, 2);
    result.getControl().sync((method, url, body) => {
        assert.equal(body.applied_revision, 2);
        assert.equal(body.applied_enabled, true);
        return policy(true, 2, 1);
    }, 'office-phone');
    assert.equal(result.created.length, 0);
    assert.equal(result.getState().plans[0].status, 'cancelled');
});
