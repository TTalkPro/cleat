using System.Net.Http.Json;
using NUnit.Framework;

namespace Cleat.Examples.Tests;

/// <summary>
/// cleat-035 的验收：嵌入示例可运行。进程内构建 WebServer（随机端口），HTTP 驱动 Clojure 处理函数：
/// 纯函数、C# → Clojure 的数组参数、有状态的 atom、并发请求下的串行化。整跑一次约 5 s（首次加载两个镜像）。
/// </summary>
[NonParallelizable]
public class WebServerTests
{
    [Test]
    public async Task ServesClojureHandlers()
    {
        var app = WebServer.Build([]);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            Assert.That(await client.GetStringAsync("/hello?name=clojure"), Is.EqualTo("hello, clojure!"));
            Assert.That(await client.GetStringAsync("/hello"), Is.EqualTo("hello, world!"));

            using var sum = await client.PostAsJsonAsync("/sum", new long[] { 1, 2, 3, 4 });
            Assert.That(await sum.Content.ReadAsStringAsync(), Is.EqualTo("10"));

            // 并发请求：Cleat.Hosting 串行执行，atom 的计数不重不漏
            var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetStringAsync("/stats")));
            Assert.That(burst.OrderBy(x => x), Is.EqualTo(Enumerable.Range(1, 8).Select(i => $"requests: {i}")),
                string.Join(",", burst));
        }
        finally
        {
            await app.StopAsync();
            app.DisposeAsync().AsTask().Wait();
        }
    }
}
