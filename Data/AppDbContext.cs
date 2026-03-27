using Microsoft.EntityFrameworkCore;

namespace IsItNiceOut.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // TODO: Add DbSet properties here
    // public DbSet<MyModel> MyModels => Set<MyModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // TODO: Configure entity relationships here if needed
    }
}
