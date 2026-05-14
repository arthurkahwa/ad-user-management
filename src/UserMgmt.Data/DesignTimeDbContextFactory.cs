using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UserMgmt.Data;

/// <summary>
/// Hand-off point for <c>dotnet ef</c> tooling so migrations can be scaffolded
/// without the runtime host. Used only at design time; never registered into a
/// runtime container.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<UserMgmtDbContext>
{
    /// <summary>Create a context configured for the SQL Server provider for migration scaffolding.</summary>
    public UserMgmtDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UserMgmtDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=UserMgmt;Trusted_Connection=True;")
            .Options;

        return new UserMgmtDbContext(options);
    }
}
