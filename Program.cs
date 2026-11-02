using Dapper;
using Npgsql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("YanSen");
builder.Services.AddSingleton<NpgsqlDataSource>(sp => NpgsqlDataSource.Create(connectionString!));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(1);
        options.LoginPath = "/login.html"; 
    });
builder.Services.AddAuthorization();

// Spam protection
builder.Services.AddRateLimiter(options => {
    options.AddFixedWindowLimiter("orderLimit", opt => {
        opt.PermitLimit = 3;
        opt.Window = TimeSpan.FromMinutes(1);
    });
});

builder.Services.AddAntiforgery(); 

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAntiforgery(); 
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── PUBLIC ROUTES ──────────────────────────────────────────────────

app.MapGet("/api/product-status", async (NpgsqlDataSource db) =>
{
    using var conn = await db.OpenConnectionAsync();
    var stock = await conn.ExecuteScalarAsync<int>("SELECT stock FROM products WHERE id = 1");
    
    if (stock <= 0)
        return Results.Content("<div style='text-align:center; padding: 20px;'><h2 style='color:#ff3b30; margin:0;'>Sold Out</h2><p style='color:#666;'>Check back later!</p></div>", "text/html");
    
    return Results.Content(@"
        <form hx-post='/api/order' hx-target='#checkout-form' hx-swap='innerHTML'>
            <input type='text' name='fullName' placeholder='Full Name' required minlength='2'>
            <input type='tel' name='phone' placeholder='Phone Number' required minlength='8'>
            <input type='text' name='city' placeholder='City' required>
            <input type='text' name='street' placeholder='Street Address' required>
            <input type='text' name='addressExtra' placeholder='Extra location info (Optional)'>
            <button type='submit'>Order Now</button>
            <div class='loader'>Processing...</div>
        </form>", "text/html");
});

app.MapPost("/api/order", async ([FromForm] OrderRequest req, NpgsqlDataSource db) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length < 2 ||
        string.IsNullOrWhiteSpace(req.Phone) || req.Phone.Length < 8 ||
        string.IsNullOrWhiteSpace(req.City) || string.IsNullOrWhiteSpace(req.Street))
    {
        // Changed to Results.Ok so HTMX displays it!
        return Results.Ok("<p style='color:red; font-weight:bold;'>Please fill all required fields correctly.</p>");
    }

    using var conn = await db.OpenConnectionAsync();
    
    var sql = @"
        WITH stock_update AS (
            UPDATE products SET stock = stock - 1 WHERE id = 1 AND stock >= 1 RETURNING price
        )
        INSERT INTO orders (full_name, phone, city, street, address_extra, quantity, unit_price)
        SELECT @FullName, @Phone, @City, @Street, COALESCE(@AddressExtra, ''), 1, price FROM stock_update
        RETURNING id;";

    var orderId = await conn.QuerySingleOrDefaultAsync<long?>(sql, new { 
        req.FullName, req.Phone, req.City, req.Street, req.AddressExtra 
    });

    if (orderId == null) 
        return Results.Ok("<div style='text-align:center; padding: 20px;'><h2 style='color:#ff3b30; margin:0;'>Sold Out</h2><p style='color:#666;'>Someone else just bought the last one!</p></div>");

    return Results.Ok("<div style='text-align:center;'><h2>Order Confirmed!</h2><p>We will contact you soon for delivery.</p></div>");
}).DisableAntiforgery().RequireRateLimiting("orderLimit"); 

app.MapPost("/api/login", async (HttpContext context, [FromForm] string password) =>
{
    // YOUR CUSTOM PASSWORD HASH
    string adminHash = "$2a$12$Zn.KhbWyLQM0vhxrVKbb5e/7BGROYibmBhfWLSRSiiviQF9TE6Vq.";
    
    if (BCrypt.Net.BCrypt.Verify(password, adminHash)) 
    {
        var claims = new List<Claim> { new Claim(ClaimTypes.Name, "Admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        
        context.Response.Headers["HX-Redirect"] = "/admin.html";
        return Results.Ok();
    }
    return Results.Content("<p style='color:red;'>Wrong password</p>", "text/html");
}).DisableAntiforgery(); 

app.MapGet("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login.html");
});

// ── ADMIN ROUTES (Protected) ───────────────────────────────────────

app.MapGet("/admin.html", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Redirect("/login.html");
        
    return Results.File(Path.Combine(app.Environment.ContentRootPath, "PrivatePages", "admin.html"), "text/html");
});

app.MapGet("/api/admin/orders", async (NpgsqlDataSource db, bool hidePackaged = false) =>
{
    using var conn = await db.OpenConnectionAsync();
    var sql = hidePackaged 
        ? "SELECT id, full_name, phone, city, street, total, created_at FROM orders WHERE packaged = false ORDER BY created_at ASC"
        : "SELECT id, full_name, phone, city, street, total, created_at FROM orders ORDER BY created_at ASC";

    var orders = await conn.QueryAsync(sql);
    
    var html = "";
    foreach (var o in orders)
    {
        html += $@"
            <tr>
                <td>#{o.id} <br><small>{o.created_at:MMM dd, HH:mm}</small></td>
                <td><b>{o.full_name}</b><br>{o.phone}</td>
                <td>{o.city}, {o.street}</td>
                <td>{o.total} MAD</td>
                <td><button hx-post='/api/admin/package/{o.id}' hx-target='#orders-table' hx-swap='innerHTML'>Package</button></td>
            </tr>";
    }
    if (string.IsNullOrEmpty(html)) html = "<tr><td colspan='5' style='text-align:center;'>No orders found.</td></tr>";
    return Results.Content(html, "text/html");
}).RequireAuthorization();

app.MapPost("/api/admin/package/{id}", async (long id, NpgsqlDataSource db, HttpContext context) =>
{
    using var conn = await db.OpenConnectionAsync();
    await conn.ExecuteAsync("UPDATE orders SET packaged = true, packaged_at = now() WHERE id = @id", new { id });
    
    context.Response.Headers["HX-Trigger"] = "refreshStats, refreshTable";
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/api/admin/stats", async (NpgsqlDataSource db) =>
{
    using var conn = await db.OpenConnectionAsync();
    var stats = await conn.QuerySingleAsync(@"
        SELECT count(*) as orders, 
               count(*) FILTER (WHERE packaged) as packaged,
               count(*) FILTER (WHERE NOT packaged) as waiting,
               coalesce(sum(total) FILTER (WHERE packaged), 0) as revenue
        FROM orders");
    return Results.Ok(stats);
}).RequireAuthorization();

app.MapPost("/api/admin/restock", async (NpgsqlDataSource db, [FromForm] int amount) =>
{
    using var conn = await db.OpenConnectionAsync();
    await conn.ExecuteAsync("UPDATE products SET stock = stock + @amount WHERE id = 1", new { amount });
    return Results.Ok();
}).RequireAuthorization().DisableAntiforgery(); 

app.Run();

public record OrderRequest(string FullName, string Phone, string City, string Street, string? AddressExtra);
