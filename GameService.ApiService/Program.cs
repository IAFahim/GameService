using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();


// using (var scope = app.Services.CreateScope())
// {
//     try
//     {
//         var db = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
//         using var conn = db.CreateConnection();
//         await conn.OpenAsync();
//         using var cmd = conn.CreateCommand();
//         cmd.CommandText = @"
// CREATE TABLE IF NOT EXISTS VisitCounts (
//         Id INT PRIMARY KEY,
//         Count INT NOT NULL DEFAULT 0
//     );
//     ";
//         await cmd.ExecuteNonQueryAsync();
//     }
//     catch (Exception ex)
//     {
//         Console.WriteLine($"DB Init Error: {ex.Message}");
//     }
// }

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


// force the SSL redirect
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/health"),
    applicationBuilder => applicationBuilder.UseHttpsRedirection()
);

app.MapDefaultEndpoints();

app.Run();