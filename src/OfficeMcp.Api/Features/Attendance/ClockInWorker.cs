using Microsoft.Extensions.Options;
using OfficeMcp.Api.Features.DeviceCommands;

namespace OfficeMcp.Api.Features.Attendance;

/// <summary>后台恢复和核验远程考勤任务，HTTP 调用结束不影响任务继续处理。</summary>
public sealed class ClockInWorker(DeviceCommandStore store, ClockInService service,
    IOptions<DeviceCommandOptions> devices, IOptions<ClockInOptions> options, ILogger<ClockInWorker> logger) : BackgroundService
{
    /// <summary>周期处理活跃任务；取消时停止网络读取，不主动重新下发已领取动作。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!devices.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in await store.ActiveAsync(ClockInService.Kind, stoppingToken))
                {
                    try { await service.ProcessAsync(job, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        // 单个任务的异常不阻塞其他设备任务，日志只记录安全标识。
                        logger.LogError("远程任务处理异常，任务：{TaskId}，类型：{ErrorType}", job.Id, exception.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("远程任务后台处理异常，类型：{ErrorType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.VerificationIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
