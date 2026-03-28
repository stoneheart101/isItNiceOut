using IsItNiceOut.Data;
using IsItNiceOut.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db"));

builder.Services.AddHttpClient<WeatherService>();

var app = builder.Build();

// Ensure database is created and apply any schema upgrades
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // TODO: Add ALTER TABLE schema upgrade guards here as columns are added over time
    // Example:
    // var conn = db.Database.GetDbConnection();
    // conn.Open();
    // using var cmd = conn.CreateCommand();
    // cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TableName') WHERE name='NewColumn'";
    // if ((long)cmd.ExecuteScalar()! == 0)
    // {
    //     cmd.CommandText = "ALTER TABLE TableName ADD COLUMN NewColumn TEXT";
    //     cmd.ExecuteNonQuery();
    // }
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<IsItNiceOut.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
