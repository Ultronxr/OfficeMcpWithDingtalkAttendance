/* 真实共用时间模块的跨时区测试；不连接手机，不创建系统定时任务。 */
const assert = require('node:assert/strict');
const test = require('node:test');
const time = require('../../autojs6/office_time.js');

test('偏移、UTC、无偏移都统一为北京时间，显示到秒', () => {
    for (const value of ['2026-09-25T01:00:00.1234567Z', '2026-09-25T01:00:00.1234567+00:00',
        '2026-09-24T21:00:00.1234567-04:00', '2026-09-25T09:00:00.1234567+08:00', '2026-09-25 09:00:00.1234567']) {
        assert.equal(time.format(value), '2026-09-25T09:00:00+08:00');
    }
});

test('展示省略毫秒不修改原始时间戳，跨日及日志格式正确', () => {
    const at = Date.parse('2026-09-24T16:00:00.123Z');
    assert.equal(time.format(at), '2026-09-25T00:00:00+08:00');
    assert.equal(at, Date.parse('2026-09-24T16:00:00.123Z'));
    assert.equal(time.format('2026-09-25T23:59:59.9999999+08:00'), '2026-09-25T23:59:59+08:00');
    assert.match(time.line('合成日志'), /^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+08:00\] 合成日志$/);
});

test('UTC和美洲宿主都按北京时间今天生成目标且正确判断上午', () => {
    const previous = process.env.TZ;
    try {
        for (const zone of ['UTC', 'America/New_York', 'Asia/Shanghai']) {
            process.env.TZ = zone;
            const now = Date.parse('2026-09-24T16:00:01Z');
            assert.equal(time.todayAt('08:45', now), Date.parse('2026-09-25T08:45:00+08:00'));
            assert.equal(time.hour(Date.parse('2026-09-25T01:00:00Z')), 9);
        }
    } finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous; }
});

test('非法日期和时刻拒绝，不进位成其他日期', () => {
    for (const value of ['2026-02-30 09:00:00', '2026-09-25T09:00:00+25:00', 'not-time']) assert.throws(() => time.format(value));
    assert.throws(() => time.todayAt('25:00', Date.now()));
});
