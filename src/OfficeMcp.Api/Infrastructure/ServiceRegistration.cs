using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using OfficeMcp.Api.Infrastructure.DingTalk;
using OfficeMcp.Api.Infrastructure.Errors;
using OfficeMcp.Api.Infrastructure.Security;
using OfficeMcp.Api.Infrastructure.Time;
using Swashbuckle.AspNetCore.Swagger;

namespace OfficeMcp.Api.Infrastructure;

/// <summary>所有办公功能共享的认证、错误处理、钉钉连接和接口文档注册。</summary>
public static class ServiceRegistration
{
    /// <summary>为 HTTP 宿主注册通用基础设施，并在启动时校验必要配置。</summary>
    public static IServiceCollection AddOfficeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>().Bind(configuration.GetSection("Authentication"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.ApiKey) && x.ApiKey.Length is >= 32 and <= 512,
                "Authentication:ApiKey 必须配置为 32 至 512 个字符。")
            .ValidateOnStart();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options => OfficeTime.ConfigureJson(options.SerializerOptions));
        // Swashbuckle 的模型生成器使用 MVC JSON 选项，需与 Minimal API 的实际输出一致。
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => OfficeTime.ConfigureJson(options.JsonSerializerOptions));
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddHealthChecks();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<DingTalkOptions>().Bind(configuration.GetSection("DingTalk"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.ClientId) && !string.IsNullOrWhiteSpace(x.ClientSecret),
                "请配置 DingTalk:ClientId 和 DingTalk:ClientSecret。")
            .Validate(x => x.TimeoutSeconds is >= 1 and <= 60, "DingTalk:TimeoutSeconds 必须在 1 至 60 秒之间。")
            .ValidateOnStart();
        services.AddHttpClient(DingTalkTransport.ClientName)
            // 旧版钉钉 API 把令牌放在 query 中，禁用默认 HTTP 请求日志和自动跳转。
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton<DingTalkTransport>();
        services.AddSingleton<DingTalkTokenProvider>();
        services.AddSingleton<DingTalkClient>();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Office HTTP API", Version = "v1", Description = OfficeTime.Contract });
            options.SchemaFilter<TimeSchemaFilter>();
            options.OperationFilter<TimeSchemaFilter>();
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory,
                $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header,
                Name = ApiKeyAuthenticationHandler.HeaderName, Description = "独立的网关到办公服务 API Key。"
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference
                    { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>()
            });
        });
        return services;
    }

    /// <summary>发布受认证保护的 OpenAPI 3.0 文档，供网关按 operationId 导入。</summary>
    public static IEndpointRouteBuilder MapOfficeOpenApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/openapi/v1.json", (ISwaggerProvider provider) =>
        {
            using var text = new StringWriter();
            provider.GetSwagger("v1").SerializeAsV3(new OpenApiJsonWriter(text));
            return Results.Text(text.ToString(), "application/json");
        }).RequireAuthorization().ExcludeFromDescription();
        return endpoints;
    }
}
