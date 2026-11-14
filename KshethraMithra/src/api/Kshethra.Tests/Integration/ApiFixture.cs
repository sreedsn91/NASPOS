using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;

namespace Kshethra.Tests.Integration;

/// <summary>
/// Hosts the API against a throwaway SQL Server database (created by migrations, dropped afterwards).
/// Server: env KSHETHRA_TEST_SQL (default ".\MSSQLSERVER2025" with Windows auth).
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    /// <summary>
    /// Base connection without a database. CI sets KSHETHRA_TEST_SQL_CONNECTION (e.g. a SQL Server container);
    /// locally it defaults to Windows auth on KSHETHRA_TEST_SQL or .\MSSQLSERVER2025.
    /// </summary>
    private static readonly string BaseConnection = Environment.GetEnvironmentVariable("KSHETHRA_TEST_SQL_CONNECTION")
        ?? $"Server={Environment.GetEnvironmentVariable("KSHETHRA_TEST_SQL") ?? @".\MSSQLSERVER2025"};Trusted_Connection=True;TrustServerCertificate=True";
    public string DatabaseName { get; } = $"Kshethra_Test_{Guid.NewGuid():N}"[..28];
    private string ConnectionString => new SqlConnectionStringBuilder(BaseConnection) { InitialCatalog = DatabaseName }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        // Direct receipt printing records the job instead of sending it to a real printer.
        builder.ConfigureTestServices(s => s.AddSingleton<Kshethra.Application.Common.IRawPrinter>(Printer));
    }

    public RecordingPrinter Printer { get; } = new();

    public sealed class RecordingPrinter : Kshethra.Application.Common.IRawPrinter
    {
        public List<(string Printer, string Document, byte[] Data)> Jobs { get; } = [];
        public void Send(string printerName, string documentName, byte[] data) { lock (Jobs) Jobs.Add((printerName, documentName, data)); }
    }

    public Task InitializeAsync()
    {
        _ = Server; // start host => migrate
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var cn = new SqlConnection(new SqlConnectionStringBuilder(BaseConnection) { InitialCatalog = "master" }.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"IF DB_ID(@db) IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END";
        cmd.Parameters.AddWithValue("@db", DatabaseName);
        await cmd.ExecuteNonQueryAsync();
    }

    public HttpClient NewClient(string counter = "TEST-1")
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Counter", counter);
        return c;
    }
}

public static class HttpExt
{
    public static async Task<T> Ok<T>(this Task<HttpResponseMessage> call)
    {
        var resp = await call;
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{(int)resp.StatusCode} {resp.RequestMessage?.Method} {resp.RequestMessage?.RequestUri}: {body}");
        return JsonSerializer.Deserialize<T>(body, ApiFixture.Json)!;
    }

    public static async Task Ok(this Task<HttpResponseMessage> call)
    {
        var resp = await call;
        if (!resp.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"{(int)resp.StatusCode} {resp.RequestMessage?.Method} {resp.RequestMessage?.RequestUri}: {await resp.Content.ReadAsStringAsync()}");
    }

    public static Task<HttpResponseMessage> Post(this HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, ApiFixture.Json);
    public static Task<HttpResponseMessage> Put(this HttpClient c, string url, object body) => c.PutAsJsonAsync(url, body, ApiFixture.Json);

    public static void Bearer(this HttpClient c, string token) => c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public static JsonElement J(this JsonElement e, string name) => e.GetProperty(name);
}
