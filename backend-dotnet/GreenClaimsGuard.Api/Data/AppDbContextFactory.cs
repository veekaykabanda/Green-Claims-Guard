using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GreenClaimsGuard.Api.Data;

// only used by `dotnet ef`, so migrations can run without starting the whole app
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // NoClobber so an explicit env connection string wins over .env
        DotNetEnv.Env.NoClobber().Load();

        var connectionString = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION")
            ?? "Server=(localdb)\\mssqllocaldb;Database=GreenClaimsGuard_Design;Trusted_Connection=True;TrustServerCertificate=True;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
