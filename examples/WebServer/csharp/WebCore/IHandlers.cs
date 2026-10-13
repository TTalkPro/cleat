namespace WebCore;

/// <summary>
/// What the web host needs from the application. Implemented in Clojure: the gen-class type
/// <c>Web.Handlers</c> (src/web/handlers.clj), which C# code creates with <c>new Web.Handlers()</c>.
/// </summary>
public interface IHandlers
{
    /// <summary>A greeting for <paramref name="name"/>.</summary>
    Greeting Greet(string name);

    /// <summary>The sum of <paramref name="numbers"/>.</summary>
    long Sum(long[] numbers);

    /// <summary>A URL slug for <paramref name="title"/>.</summary>
    string Slugify(string title);

    /// <summary>Counts this call and returns the count so far.</summary>
    long Requests();
}

/// <summary>A C# record the Clojure code constructs and returns.</summary>
public sealed record Greeting(string Name, string Message);
