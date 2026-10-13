using WebCore;

// The host is plain ASP.NET. The handlers are Clojure: Web.Handlers is a gen-class type (src/web/handlers.clj)
// implementing the C# interface WebCore.IHandlers. Creating it starts the cleat runtime on first use -- there is
// no initialization call.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IHandlers>(_ => new Web.Handlers());
var app = builder.Build();

app.MapGet("/hello", (string? name, IHandlers h) => h.Greet(name ?? ""));
app.MapPost("/sum", (long[] numbers, IHandlers h) => new { sum = h.Sum(numbers) });
app.MapGet("/slug", (string title, IHandlers h) => new { slug = h.Slugify(title) });
app.MapGet("/stats", (IHandlers h) => new { requests = h.Requests() });

// WEB_NREPL_PORT=7888: an nREPL server inside this process (Cleat.Nrepl), for neorepl to connect to
// and redefine handlers while the host serves (README, "Developing the running host").
if (int.TryParse(Environment.GetEnvironmentVariable("WEB_NREPL_PORT"), out int nreplPort))
    Web.Handlers.StartNrepl(nreplPort);

app.Run();
