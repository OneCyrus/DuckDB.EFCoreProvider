using BenchmarkDotNet.Attributes;
using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DuckDB.EFCoreProvider.Benchmarks;

/// <summary>
///     Compares the same plain LINQ single-field projection with strict and relaxed STRUCT
///     nullability mapping enabled independently.
/// </summary>
[MemoryDiagnoser]
public class StructNullabilityBenchmarks
{
    private const int RowCount = 500_000;
    private const long ExpectedField01Sum = (long)RowCount * (RowCount - 1) / 2;
    private string _parquetPath = "";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _parquetPath = Path.Combine(Path.GetTempPath(), $"struct_nullability_{Guid.NewGuid():N}.parquet");
        var setupDbPath = Path.Combine(Path.GetTempPath(), $"struct_nullability_setup_{Guid.NewGuid():N}.db");
        using var context = new OptionalStructContext(setupDbPath, _parquetPath);
        context.Database.OpenConnection();
    #pragma warning disable EF1002
        context.Database.ExecuteSqlRaw($"""
            COPY (
            SELECT
                i AS id,
                struct_pack(
                    field01 := i,
                    field02 := i + 1,
                    field03 := i + 2,
                    field04 := i + 3,
                    field05 := i + 4,
                    field06 := i + 5,
                    field07 := i + 6,
                    field08 := i + 7,
                    field09 := i + 8,
                    field10 := i + 9,
                    field11 := i + 10,
                    field12 := i + 11,
                    field13 := i + 12,
                    field14 := i + 13,
                    field15 := i + 14,
                    field16 := i + 15
                ) AS payload
            FROM range({RowCount}) AS rows(i))
            TO '{_parquetPath.Replace("\\", "/").Replace("'", "''")}' (FORMAT PARQUET)
            """);
#pragma warning restore EF1002
        context.Database.CloseConnection();
        File.Delete(setupDbPath);

        using var strictContext = new OptionalStructContext(":memory:", _parquetPath, false);
        using var relaxedContext = new OptionalStructContext(":memory:", _parquetPath, true);
        Console.WriteLine("--- Strict STRUCT mapping ---");
        Console.WriteLine(BuildProjection(strictContext).ToQueryString());
        Console.WriteLine("--- Relaxed STRUCT mapping ---");
        Console.WriteLine(BuildProjection(relaxedContext).ToQueryString());
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var file in new[] { _parquetPath })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Benchmark]
    public long Strict()
        => ReadSelectedField(new OptionalStructContext(":memory:", _parquetPath, false));

    [Benchmark(Baseline = true)]
    public long Relaxed()
        => ReadSelectedField(new OptionalStructContext(":memory:", _parquetPath, true));

    private static long ReadSelectedField(DbContext context)
    {
        using (context)
        {
            var values = context.Set<StructRow>()
                .AsNoTracking()
                .Select(row => (long?)row.Payload!.Field01)
                .ToList();
            var sum = values.Sum(value => value ?? 0);
            if (sum != ExpectedField01Sum)
            {
                throw new InvalidOperationException(
                    $"Unexpected STRUCT projection result. Expected {ExpectedField01Sum}, got {sum}.");
            }

            return sum;
        }
    }

    private static IQueryable<long?> BuildProjection(OptionalStructContext context)
        => context.Rows.Select(row => (long?)row.Payload!.Field01);

    private class OptionalStructContext(
        string dbPath,
        string parquetPath,
        bool relaxedNullabilityChecks = false) : DbContext
    {
        public DbSet<StructRow> Rows => Set<StructRow>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseDuckDB("DataSource=" + dbPath);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StructRow>().FromParquet(parquetPath);
            modelBuilder.Entity<StructRow>().Property(row => row.Id).HasColumnName("id");
            modelBuilder.Entity<StructRow>().ComplexProperty(row => row.Payload, payload =>
            {
                payload.IsRequired(false);
                payload.UseStructMapping("payload", relaxedNullabilityChecks);
            });
        }
    }

    private sealed class StructRow
    {
        public long Id { get; set; }
        public LargeStruct? Payload { get; set; }
    }

    private sealed class LargeStruct
    {
        public long Field01 { get; set; }
        public long Field02 { get; set; }
        public long Field03 { get; set; }
        public long Field04 { get; set; }
        public long Field05 { get; set; }
        public long Field06 { get; set; }
        public long Field07 { get; set; }
        public long Field08 { get; set; }
        public long Field09 { get; set; }
        public long Field10 { get; set; }
        public long Field11 { get; set; }
        public long Field12 { get; set; }
        public long Field13 { get; set; }
        public long Field14 { get; set; }
        public long Field15 { get; set; }
        public long Field16 { get; set; }
    }
}
