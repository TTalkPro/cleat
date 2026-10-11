using Cleat.Core.Hosting;
using Cleat.Core.Hosting;
using Cleat.Core.Runtime;

namespace Cleat.Examples;

/// <summary>
/// 嵌入示例（cleat-034/035）：Kestrel 的路由在 C# 一侧，处理函数是 Clojure（handlers.clj，经 compile-libs 的
/// .clj.dll 或源码加载）。Build 供测试调用（随机端口）；<c>dotnet run</c> 用 --urls 指定端口，如
/// <c>dotnet run --project examples/WebServer -- --urls http://127.0.0.1:8080</c>。
/// 并发请求由 Cleat.Hosting 的工作线程串行化（展开器与编译器不并发，纯函数调用也因此顺序执行）。
/// </summary>
public static class WebServer
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // 缺省随机端口（测试）；命令行 --urls 优先级更高，部署用它
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        CleatRuntime.Init();
        Loader.LoadFile(Path.Combine(AppContext.BaseDirectory, "handlers.clj"));

        app.MapGet("/hello", (string? name) =>
            Results.Text((string)CleatRuntime.Var("web.handlers", "greet").invoke(name ?? "world")));

        app.MapPost("/sum", async (HttpRequest request) =>
        {
            var xs = await request.ReadFromJsonAsync<long[]>();
            return Results.Text((string)CleatRuntime.Var("web.handlers", "sum").invoke(xs ?? Array.Empty<long>()));
        });

        app.MapGet("/stats", () =>
            Results.Text((string)CleatRuntime.Var("web.handlers", "stats").invoke()));

        return app;
    }

    public static void Main(string[] args) => Build(args).Run();
}
