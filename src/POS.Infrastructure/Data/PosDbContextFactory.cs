using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace POS.Infrastructure.Data;

public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(currentDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--provider"] = "Database:Provider",
                ["--connection"] = "ConnectionStrings:Default",
                ["--base-path"] = "Database:BasePath"
            })
            .Build();

        var provider = configuration["Database:Provider"]?.Trim();
        var connectionString = configuration.GetConnectionString("Default");
        var basePath = configuration["Database:BasePath"];

        var optionsBuilder = new DbContextOptionsBuilder<PosDbContext>();

        DependencyInjection.ConfigurePosDbContext(
            optionsBuilder,
            provider,
            string.IsNullOrWhiteSpace(connectionString) ? "Data Source=pos_design.db" : connectionString,
            string.IsNullOrWhiteSpace(basePath) ? currentDirectory : basePath);

        return new PosDbContext(optionsBuilder.Options);
    }
}
