using DuckDB.EFCoreProvider.Extensions;
using DuckDB.EFCoreProvider.Metadata;
using DuckDB.NET.Data;
using HotChocolate;
using HotChocolate.AspNetCore;
using HotChocolate.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Path = System.IO.Path;

// GraphQL over DuckDB Parquet files demo — struct field projection.
//
// Run with:  dotnet run --project samples/GraphQLParquet
// Then open the Hot Chocolate Nitro GraphQL IDE in a browser:
//   http://localhost:5181/graphql/ui
// The GraphQL API itself is served at http://localhost:5181/graphql.
//
// The demo builds two Parquet files that contain native DuckDB STRUCT columns,
// then maps them directly via [FromParquet]. STRUCT sub-fields are mapped as EF
// Core complex properties marked with [UseStructMapping]. A model convention
// (DuckDBStructFieldConvention) auto-infers the struct column name and field
// paths from the complex property hierarchy, so no manual HasColumnName or
// HasStructField calls are needed. The provider's VisitColumn override reads
// that annotation and generates DuckDB struct field access syntax
// (t."Location".city), so individual struct sub-fields are projected, filtered,
// and sorted at the SQL level — not in memory. Navigation properties cross the
// two parquet sets, so a single GraphQL query can expand a customer and its
// orders.

const string dbPath = "graphql_demo.duckdb";
var dataDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "data"));
Directory.CreateDirectory(dataDir);

var customersParquet = Path.Combine(dataDir, "customers.parquet");
var ordersParquet = Path.Combine(dataDir, "orders.parquet");

// Fresh slate each run.
if (File.Exists(dbPath)) File.Delete(dbPath);
if (Directory.Exists(dataDir))
{
    foreach (var f in Directory.EnumerateFiles(dataDir)) File.Delete(f);
}
Directory.CreateDirectory(dataDir);

// 1) Seed the parquet files with DuckDB STRUCT columns, then COPY out to parquet.
SeedParquet();

// 2) Create the DuckDB database file that EF connects to. [FromParquet]
//    generates read_parquet(...) directly in SQL — no views or tables needed.
//    The struct columns stay intact; HasStructField metadata on each sub-property
//    enables SQL-level struct field access via the provider's VisitColumn override.
EnsureDatabase();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<DemoDbContext>(options =>
    options.UseDuckDB($"Data Source={dbPath}"));

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddFiltering()
    .AddSorting()
    .AddProjections();

var app = builder.Build();
app.MapGraphQL();
// MapNitroApp serves the Hot Chocolate Nitro GraphQL IDE (successor to Banana Cake Pop
// in v16) on a dedicated URL.
app.MapNitroApp();

var url = "http://localhost:5181";
app.Urls.Add(url);
app.MapGet("/", () => Results.Redirect("/graphql/ui", true));

app.Run();

return;

void EnsureDatabase()
{
    // Just opening a connection creates the DuckDB file.
    using var conn = new DuckDBConnection($"Data Source={dbPath}");
    conn.Open();
}

void SeedParquet()
{
    using var conn = new DuckDBConnection("DataSource=:memory:");
    conn.Open();

    Exec(conn, """
        CREATE TABLE customers (
            Id INTEGER,
            Name VARCHAR,
            Tier VARCHAR,
            Location STRUCT(city VARCHAR, country VARCHAR, lat DOUBLE)
        )
        """);
    Exec(conn, """
        INSERT INTO customers VALUES
        (1, 'Acme Corp', 'gold',   {'city': 'Seattle', 'country': 'US', 'lat': 47.6062}),
        (2, 'Globex',     'silver', {'city': 'Portland', 'country': 'US', 'lat': 45.5152}),
        (3, 'Initech',    'bronze', {'city': 'Austin', 'country': 'US', 'lat': 30.2672}),
        (4, 'Umbrella',   'gold',   {'city': 'Toronto', 'country': 'CA', 'lat': 43.6532})
        """);
    Exec(conn, $"COPY customers TO '{Escape(customersParquet)}' (FORMAT PARQUET)");

    Exec(conn, """
        CREATE TABLE orders (
            Id INTEGER,
            CustomerId INTEGER,
            Amount DECIMAL(18,2),
            OrderedAt TIMESTAMP,
            Shipping STRUCT(method VARCHAR, cost DECIMAL(18,2),
                           address STRUCT(street VARCHAR, city VARCHAR, zip VARCHAR))
        )
        """);
    Exec(conn, """
        INSERT INTO orders VALUES
        (1001, 1, 1250.00, '2024-01-15 09:30:00',
            {'method': 'air', 'cost': 29.90, 'address': {'street': '1 Pike St', 'city': 'Seattle', 'zip': '98101'}}),
        (1002, 1, 320.50,  '2024-02-03 14:00:00',
            {'method': 'ground', 'cost': 9.50, 'address': {'street': '99 Alaskan Way', 'city': 'Seattle', 'zip': '98104'}}),
        (1003, 2, 89.99,   '2024-02-20 11:15:00',
            {'method': 'ground', 'cost': 4.99, 'address': {'street': '5 NW Glisan', 'city': 'Portland', 'zip': '97209'}}),
        (1004, 3, 4100.00, '2024-03-10 08:45:00',
            {'method': 'next-day', 'cost': 49.00, 'address': {'street': '200 Congress', 'city': 'Austin', 'zip': '78701'}}),
        (1005, 4, 760.00,  '2024-03-22 16:20:00',
            {'method': 'air', 'cost': 22.00, 'address': {'street': '100 Front St', 'city': 'Toronto', 'zip': 'M5J1E3'}}),
        (1006, 1, 14.99,   '2024-04-01 10:00:00',
            {'method': 'mail', 'cost': 0.00, 'address': {'street': '1 Pike St', 'city': 'Seattle', 'zip': '98101'}})
        """);
    Exec(conn, $"COPY orders TO '{Escape(ordersParquet)}' (FORMAT PARQUET)");
}

static void Exec(DuckDBConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static string Escape(string path) => path.Replace("\\", "\\\\").Replace("'", "''");

// --- EF Core model -------------------------------------------------------
// Two entities map directly to parquet files via [FromParquet]. The struct columns
// are mapped as complex properties. A model convention (DuckDBStructFieldConvention)
// auto-infers the struct column name (from the complex property name) and the field
// names (camelCase of the property names), so no manual HasColumnName/HasStructField
// is needed. The provider's VisitColumn override reads that annotation and generates
// DuckDB struct field access syntax (t."Location".city) in SQL.

[FromParquet("data/customers.parquet")]
public sealed class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Tier { get; set; } = "bronze";

    /// <summary>Mapped as a complex property to the <c>Location STRUCT(city VARCHAR, country VARCHAR, lat DOUBLE)</c> column.</summary>
    [UseStructMapping]
    public required Location Location { get; set; }

    public List<Order> Orders { get; set; } = [];
}

[FromParquet("data/orders.parquet")]
public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime OrderedAt { get; set; }

    /// <summary>Mapped as a complex property to the <c>Shipping STRUCT(method VARCHAR, cost DECIMAL, address STRUCT(...))</c> column.</summary>
    [UseStructMapping]
    public required Shipping Shipping { get; set; }

    public Customer? Customer { get; set; }
}

public sealed class Location
{
    public required string City { get; set; }
    public required string Country { get; set; }
    public double Lat { get; set; }
}

public sealed class Shipping
{
    public required string Method { get; set; }
    public decimal Cost { get; set; }
    public required Address Address { get; set; }
}

public sealed class Address
{
    public required string Street { get; set; }
    public required string City { get; set; }
    public required string Zip { get; set; }
}

public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Navigation property: Customer 1—* Orders, joined on CustomerId.
            modelBuilder.Entity<Customer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);

            // Location struct: the convention auto-infers HasStructField("Location") on all
            // scalar sub-properties and HasColumnName("city"/"country"/"lat") from the
            // camelCase property names. No manual configuration needed here.
            //
            // Fluent alternative (instead of [UseStructMapping] on the property):
            //   modelBuilder.Entity<Customer>()
            //       .ComplexProperty(c => c.Location)
            //       .UseStructMapping();
            modelBuilder.Entity<Customer>()
                .ComplexProperty(c => c.Location);

            // Shipping struct with nested Address struct. The convention infers:
            //   method/cost → HasStructField("Shipping")
            //   street/city/zip → HasStructField("Shipping", "address")
            modelBuilder.Entity<Order>()
                .ComplexProperty(o => o.Shipping);
        }
    }

    // --- Hot Chocolate query root --------------------------------------------
    // The queryables are returned as fields. Hot Chocolate applies GraphQL filters,
    // sorts and projections to the underlying IQueryable, which the DuckDB provider
    // translates to SQL over read_parquet(...). Struct sub-fields are individually
    // projected thanks to the auto-inferred struct field annotations + provider
    // VisitColumn override.

    public sealed class Query
    {
        [UseProjection]
        [UseFiltering]
        [UseSorting]
        public IQueryable<Customer> Customers([Service] DemoDbContext db) => db.Customers;

        [UseProjection]
        [UseFiltering]
        [UseSorting]
        public IQueryable<Order> Orders([Service] DemoDbContext db) => db.Orders;
    }