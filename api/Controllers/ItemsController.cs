using BdoMarketTracker.Data;
using BdoMarketTracker.Dtos;
using BdoMarketTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BdoMarketTracker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ItemsController(AppDbContext db, IVelocityCalculator velocity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetItems(CancellationToken ct)
    {
        var items = await db.TrackedItems
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.Grade,
                LatestSnapshot = i.Snapshots
                    .OrderByDescending(s => s.RecordedAt)
                    .Select(s => new
                    {
                        s.RecordedAt,
                        s.TotalTrades,
                        s.CurrentStock,
                        s.BasePrice,
                        s.LastSoldPrice,
                        s.TotalPreorders
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id}/velocity")]
    public async Task<IActionResult> GetVelocity(int id, CancellationToken ct)
    {
        var item = await db.TrackedItems.FindAsync([id], ct);
        if (item == null) return NotFound();

        var result = await velocity.GetVelocityAsync(id, ct);
        return Ok(result);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard([FromQuery] string window = "24h", CancellationToken ct = default)
    {
        if (!VelocityCalculator.IsValidWindow(window))
            return BadRequest(new { message = $"Invalid window '{window}'. Valid values: {string.Join(", ", VelocityCalculator.WindowDefinitions.Keys)}" });

        var result = await velocity.GetDashboardAsync(window, ct);
        return Ok(result);
    }
}
