using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Levee;
using Levee.EntityFrameworkCore;
using Levee.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Adapters;
using Sample.Contracts;
using Testcontainers.PostgreSql;

BenchmarkRunner.Run<Levee.Benchmarks.PipelineBenchmarks>(args: args);

namespace Levee.Benchmarks
{
    /// <summary>
    /// What the pipeline costs over the same work done by hand: each pair runs one transaction, as the app's least-privilege role,
    /// with row-level security scoped to one tenant. The dispatched side adds authorization, the read-only declaration, the
    /// scope settings, admissions, the outcome and, for a write, the audit.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 10, iterationCount: 40)]
    public class PipelineBenchmarks
    {
        private const string Tenant = "bench";
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        private IHost _host = null!;
        private Dispatcher _dispatcher = null!;
        private readonly BenchMember _caller = new("ada@bench", Tenant);

        [GlobalSetup]
        public async Task SetUp()
        {
            await _postgres.StartAsync();
            var owner = _postgres.GetConnectionString();
            var sql = new StringBuilder()
                .AppendLine("create role app login password 'app' nosuperuser nobypassrls nocreaterole nocreatedb;")
                .AppendLine("revoke create on schema public from public;")
                .AppendLine(LeveeSchema.Sql)
                .AppendLine(SampleDbContext.Sql)
                .AppendLine(LeveeSchema.Grants("app"));
            await using (var connection = new NpgsqlConnection(owner))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(sql.ToString(), connection);
                await command.ExecuteNonQueryAsync();
            }
            var app = new NpgsqlConnectionStringBuilder(owner) { Username = "app", Password = "app" }.ConnectionString;

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["Sample:BannedWords"] = "none";
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(app));
            builder.Services.AddDbContext<SampleDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
            builder.Services.AddLeveeOperations(builder.Configuration);
            builder.Services.AddLeveePostgres<SampleDbContext>();
            builder.Services.AddSingleton<IAccessPolicy, BenchAccess>();
            _host = builder.Build();
            _dispatcher = _host.Services.GetRequiredService<Dispatcher>();
            for (var i = 0; i < 20; i++)
            {
                await _dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(TimeProvider.System.GetUtcNow()), Tenant, $"note {i}"), _caller, CancellationToken.None);
            }
        }

        [GlobalCleanup]
        public async Task TearDown()
        {
            _host.Dispose();
            await _postgres.DisposeAsync();
        }

        [Benchmark(Baseline = true, Description = "read: EF in a transaction, by hand")]
        public async Task<int> ReadByHand()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("set transaction read only");
            await db.Database.ExecuteSqlAsync($"select set_config('levee.scope', {Tenant}, true)");
            var notes = await db.Database.SqlQueryRaw<BenchNote>("select id, tenant, text from notes where tenant = {0} order by created_at", Tenant).ToListAsync();
            await tx.CommitAsync();
            return notes.Count;
        }

        [Benchmark(Description = "read: notes.list, dispatched")]
        public async Task<int> ReadDispatched() =>
            (await _dispatcher.SendAsync(new ListNotes(Tenant), _caller, CancellationToken.None)).Value.Count;

        [Benchmark(Description = "write: EF in a transaction, by hand")]
        public async Task WriteByHand()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"select set_config('levee.scope', {Tenant}, true)");
            await db.Database.ExecuteSqlAsync($"insert into notes (id, tenant, text, created_at) values ({Guid.CreateVersion7(TimeProvider.System.GetUtcNow())}, {Tenant}, {"by hand"}, now())");
            await tx.CommitAsync();
        }

        [Benchmark(Description = "write: notes.create, dispatched and audited")]
        public async Task WriteDispatched() =>
            await _dispatcher.SendAsync(new CreateNote(Guid.CreateVersion7(TimeProvider.System.GetUtcNow()), Tenant, "dispatched"), _caller, CancellationToken.None);
    }

    public sealed record BenchMember(string Subject, string Tenant) : Principal(Subject);

    public sealed record BenchNote(Guid Id, string Tenant, string Text);

    [Refuses("bench.tenant")]
    internal sealed class BenchAccess : IAccessPolicy
    {
        public ValueTask<Error?> AuthorizeAsync(AccessRequest request, CancellationToken ct) => ValueTask.FromResult(
            request.Principal is BenchMember m && request.Scope.Key == m.Tenant ? null : Error.Forbidden("bench.tenant", "Not this tenant."));
    }
}
