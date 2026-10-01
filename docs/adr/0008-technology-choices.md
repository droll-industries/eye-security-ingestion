# 0008 – Technology choices

## Decision
* **C# / .NET 10 (LTS):** current long-term support release.
* **ASP.NET Core minimal APIs:** one endpoint doesn't need MVC controllers. Typed `Results`
  and ProblemDetails (RFC 9457) for errors.
* **`BackgroundService` + `System.Threading.Channels`** for the worker/queue (ADR 0002).
* **Microsoft.Extensions.Http.Resilience (Polly v8)** with `IHttpClientFactory`: the
  standard resilience pipeline, configured per dependency (ADR 0006, 0007). Typed clients,
  so connection pooling and DNS rotation are handled for us.
* **System.CommandLine 2.0** for the CLI: parsing, `--help` and option validation (e.g.
  unknown `--category` is rejected before reading the file).
* **No CSV library:** the export is simple (no quoting); see the assumptions in
  `CsvActivityReader`. We'd switch to CsvHelper if quoted fields appear.
* **xUnit + WebApplicationFactory + FakeTimeProvider:** fast tests that don't depend on
  timing. The rate limiter and back-off are tested on virtual time.
* **OpenAPI 3.1 (YAML) for the proposed downstream contracts** (ADR 0003): the format other
  teams can review. Tests read the specs with **YamlDotNet** (test-only dependency).
* **Strict build:** nullable enabled, `TreatWarningsAsErrors`, `latest-recommended`
  analyzers, with a few documented exceptions in `Directory.Build.props`.
