using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
var app = builder.Build();

// ===== 排班查詢 =====
app.MapGet("/api/staff/on-duty", async (AppDbContext db, HttpContext ctx) =>
{
    var shopId = Guid.Parse(ctx.User.FindFirst("shop_id")!.Value);
    var today = DateTime.Now;
    var dayOfWeek = today.DayOfWeek.ToString();

    var staff = await db.Staff
        .Where(s => s.ShopId == shopId && s.IsActive)
        .ToListAsync();

    var onDuty = new List<object>();
    foreach (var s in staff)
    {
        // 排班邏輯：用逗號分隔的字串存星期幾
        if (s.WorkDays != null && s.WorkDays.Contains(dayOfWeek))
        {
            // 檢查是否在營業時間內
            var hour = DateTime.Now.Hour;
            if (hour >= s.StartHour && hour < s.EndHour)
            {
                // 檢查是否在休假中
                var onLeave = await db.Leaves
                    .AnyAsync(l => l.StaffId == s.Id
                        && l.StartDate <= today && l.EndDate >= today);

                if (!onLeave)
                {
                    onDuty.Add(new
                    {
                        s.Id,
                        s.Name,
                        s.Title,
                        s.LineNumber,
                        IsAccepting = s.MaxDailyBookings > (
                            db.Bookings.Count(b => b.StaffId == s.Id
                                && b.BookingDate.Date == today.Date
                                && b.Status != "cancelled"))
                    });
                }
            }
        }
    }
    return Results.Ok(onDuty);
});

// ===== 建立預約 =====
app.MapPost("/api/bookings", async (CreateBookingRequest req, AppDbContext db, HttpContext ctx) =>
{
    var shopId = Guid.Parse(ctx.User.FindFirst("shop_id")!.Value);

    // 驗證
    if (string.IsNullOrEmpty(req.CustomerName))
        return Results.BadRequest("客戶姓名必填");
    if (string.IsNullOrEmpty(req.CustomerPhone))
        return Results.BadRequest("客戶電話必填");
    if (req.StaffId == Guid.Empty)
        return Results.BadRequest("請選擇服務人員");
    if (req.DurationMinutes <= 0)
        return Results.BadRequest("服務時長必須大於 0");
    if (req.DurationMinutes % 30 != 0)
        return Results.BadRequest("服務時長必須是 30 的倍數");
    if (req.BookingDate < DateTime.Now)
        return Results.BadRequest("預約時間不能是過去");

    // 檢查服務人員是否存在且屬於該店
    var staff = await db.Staff.FirstOrDefaultAsync(
        s => s.Id == req.StaffId && s.ShopId == shopId);
    if (staff == null)
        return Results.BadRequest("找不到該服務人員");

    // 檢查時段衝突
    var endTime = req.BookingDate.AddMinutes(req.DurationMinutes);
    var conflict = await db.Bookings.AnyAsync(b =>
        b.StaffId == req.StaffId
        && b.Status != "cancelled"
        && b.BookingDate < endTime
        && b.BookingDate.AddMinutes(b.DurationMinutes) > req.BookingDate);
    if (conflict)
        return Results.Conflict("該時段已有預約");

    // 計算價格
    decimal price;
    var priceRecord = await db.PriceRecords
        .Where(p => p.ShopId == shopId && p.IsActive)
        .FirstOrDefaultAsync();

    if (priceRecord == null)
        return Results.BadRequest("店家尚未設定價格");

    var blocks = req.DurationMinutes / 30;
    price = priceRecord.BasePrice * blocks;

    // 檢查是否有 VIP 折扣
    var customer = await db.Customers
        .FirstOrDefaultAsync(c => c.Phone == req.CustomerPhone && c.ShopId == shopId);
    if (customer != null && customer.IsVip)
    {
        price = price * 0.9m; // VIP 九折
    }

    // 尖峰時段加價
    if (req.BookingDate.DayOfWeek == DayOfWeek.Saturday
        || req.BookingDate.DayOfWeek == DayOfWeek.Sunday
        || (req.BookingDate.Hour >= 18 && req.BookingDate.Hour < 22))
    {
        price = price * 1.2m; // 尖峰加兩成
    }

    // 建立預約
    var booking = new Booking
    {
        Id = Guid.NewGuid(),
        ShopId = shopId,
        StaffId = req.StaffId,
        CustomerName = req.CustomerName,
        CustomerPhone = req.CustomerPhone,
        BookingDate = req.BookingDate,
        DurationMinutes = req.DurationMinutes,
        Price = price,
        Status = "confirmed",
        CreatedAt = DateTime.Now,
        Note = req.Note ?? ""
    };

    db.Bookings.Add(booking);

    // 更新或建立客戶記錄
    if (customer == null)
    {
        customer = new Customer
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            Name = req.CustomerName,
            Phone = req.CustomerPhone,
            VisitCount = 1,
            IsVip = false,
            CreatedAt = DateTime.Now
        };
        db.Customers.Add(customer);
    }
    else
    {
        customer.VisitCount++;
        // 累計 10 次自動升 VIP
        if (customer.VisitCount >= 10 && !customer.IsVip)
        {
            customer.IsVip = true;
        }
    }

    await db.SaveChangesAsync();

    // 發送確認通知（直接在這裡呼叫）
    try
    {
        var message = $"預約確認：{req.CustomerName} 已預約 {staff.Name}，"
            + $"時間：{req.BookingDate:yyyy/MM/dd HH:mm}，"
            + $"時長：{req.DurationMinutes} 分鐘，"
            + $"金額：{price:N0} 元";

        // TODO: 接 Telegram Bot API
        Console.WriteLine($"[通知] {message}");
    }
    catch (Exception ex)
    {
        // 通知失敗不影響預約
        Console.WriteLine($"[通知失敗] {ex.Message}");
    }

    return Results.Ok(new { booking.Id, booking.Price, booking.Status });
});

// ===== 取消預約 =====
app.MapPost("/api/bookings/{id}/cancel", async (
    Guid id, CancelRequest req, AppDbContext db, HttpContext ctx) =>
{
    var shopId = Guid.Parse(ctx.User.FindFirst("shop_id")!.Value);
    var booking = await db.Bookings.FirstOrDefaultAsync(
        b => b.Id == id && b.ShopId == shopId);

    if (booking == null)
        return Results.NotFound();

    if (booking.Status == "cancelled")
        return Results.BadRequest("此預約已取消");

    if (booking.Status == "completed")
        return Results.BadRequest("已完成的預約無法取消");

    // 取消時間限制：預約前 2 小時才能取消
    if (booking.BookingDate.Subtract(DateTime.Now).TotalHours < 2)
        return Results.BadRequest("預約前 2 小時內無法取消");

    booking.Status = "cancelled";
    booking.CancelledAt = DateTime.Now;
    booking.CancelReason = req.Reason ?? "未提供原因";

    await db.SaveChangesAsync();

    return Results.Ok();
});

// ===== 查詢預約列表 =====
app.MapGet("/api/bookings", async (
    AppDbContext db, HttpContext ctx,
    string? status, string? date, int page = 1, int size = 20) =>
{
    var shopId = Guid.Parse(ctx.User.FindFirst("shop_id")!.Value);

    var query = db.Bookings.Where(b => b.ShopId == shopId);

    if (!string.IsNullOrEmpty(status))
        query = query.Where(b => b.Status == status);

    if (!string.IsNullOrEmpty(date))
    {
        var d = DateTime.Parse(date);
        query = query.Where(b => b.BookingDate.Date == d.Date);
    }

    var total = await query.CountAsync();
    var items = await query
        .OrderByDescending(b => b.BookingDate)
        .Skip((page - 1) * size)
        .Take(size)
        .Select(b => new
        {
            b.Id,
            b.CustomerName,
            b.CustomerPhone,
            StaffName = db.Staff.Where(s => s.Id == b.StaffId)
                .Select(s => s.Name).FirstOrDefault(),
            b.BookingDate,
            b.DurationMinutes,
            b.Price,
            b.Status,
            b.CancelReason
        })
        .ToListAsync();

    return Results.Ok(new { total, page, size, items });
});

// ===== 每日營收統計 =====
app.MapGet("/api/reports/daily-revenue", async (
    AppDbContext db, HttpContext ctx, string? date) =>
{
    var shopId = Guid.Parse(ctx.User.FindFirst("shop_id")!.Value);
    var d = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date);

    var bookings = await db.Bookings
        .Where(b => b.ShopId == shopId
            && b.BookingDate.Date == d.Date
            && b.Status != "cancelled")
        .ToListAsync();

    var totalRevenue = bookings.Sum(b => b.Price);
    var totalBookings = bookings.Count;
    var completedBookings = bookings.Count(b => b.Status == "completed");
    var avgPrice = totalBookings > 0 ? totalRevenue / totalBookings : 0;

    // 各服務人員業績
    var staffRevenue = bookings.GroupBy(b => b.StaffId).Select(g =>
    {
        var staffName = db.Staff.Where(s => s.Id == g.Key)
            .Select(s => s.Name).FirstOrDefault() ?? "未知";
        return new
        {
            StaffName = staffName,
            BookingCount = g.Count(),
            Revenue = g.Sum(b => b.Price)
        };
    }).OrderByDescending(x => x.Revenue).ToList();

    return Results.Ok(new
    {
        Date = d.ToString("yyyy-MM-dd"),
        TotalRevenue = totalRevenue,
        TotalBookings = totalBookings,
        CompletedBookings = completedBookings,
        AveragePrice = avgPrice,
        StaffRevenue = staffRevenue
    });
});

app.Run();

// ===== Models =====
public class Booking
{
    public Guid Id { get; set; }
    public Guid ShopId { get; set; }
    public Guid StaffId { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public DateTime BookingDate { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "confirmed";  // confirmed, completed, cancelled
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}

public class Staff
{
    public Guid Id { get; set; }
    public Guid ShopId { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string LineNumber { get; set; } = "";   // 編號，例如 "63"
    public string? WorkDays { get; set; }           // "Monday,Tuesday,Wednesday"
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public int MaxDailyBookings { get; set; } = 8;
    public bool IsActive { get; set; } = true;
}

public class Customer
{
    public Guid Id { get; set; }
    public Guid ShopId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public int VisitCount { get; set; }
    public bool IsVip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Leave
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string Reason { get; set; } = "";
}

[Table("price_records")]
public class PriceRecord
{
    public Guid Id { get; set; }
    public Guid ShopId { get; set; }
    public decimal BasePrice { get; set; }      // 每 30 分鐘的基本價
    public bool IsActive { get; set; } = true;
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Leave> Leaves => Set<Leave>();
    public DbSet<PriceRecord> PriceRecords => Set<PriceRecord>();
}

// ===== Request DTOs =====
public record CreateBookingRequest(
    string CustomerName,
    string CustomerPhone,
    Guid StaffId,
    DateTime BookingDate,
    int DurationMinutes,
    string? Note);

public record CancelRequest(string? Reason);
