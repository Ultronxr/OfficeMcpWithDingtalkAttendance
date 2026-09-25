using OfficeMcp.Api.Features.Attendance;
using OfficeMcp.Api.Infrastructure;
using OfficeMcp.Api.Features.DeviceCommands;
using OfficeMcp.Api.Features.Employees;
using OfficeMcp.Api.Infrastructure.Time;
using Microsoft.Extensions.Logging.Console;

/// <summary>通用办公 HTTP 服务入口；NSSM 可直接托管发布后的可执行文件。</summary>
public partial class Program
{
    /// <summary>加载配置、注册共享基础设施和业务模块，并运行 HTTP 宿主。</summary>
    /// <param name="args">命令行配置参数，例如 --urls。</param>
    public static void Main(string[] args)
    {
        // 服务管理器的工作目录可能是 System32，配置文件始终从程序目录读取。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables().AddCommandLine(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = BeijingJsonConsoleFormatter.FormatterName)
            .AddConsoleFormatter<BeijingJsonConsoleFormatter, ConsoleFormatterOptions>();
        builder.Services.AddOfficeInfrastructure(builder.Configuration);
        builder.Services.AddAttendanceFeature(builder.Configuration);
        builder.Services.AddEmployeeFeature();
        builder.Services.AddRemoteCommands(builder.Configuration);
        builder.Services.AddAutomationControl();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            // 考勤属于个人数据，避免浏览器或中间代理缓存业务响应。
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.MapHealthChecks("/healthz").AllowAnonymous();
        app.MapOfficeOpenApi();
        app.MapGroup("/api").RequireAuthorization().MapAttendanceEndpoints().MapClockInEndpoints().MapAutomationEndpoints().MapEmployeeEndpoints();
        app.MapDeviceCommands();
        app.Run();
    }
}
