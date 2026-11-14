using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NasPos.Api.Security;
using NasPos.Application.Common;
using NasPos.Application.Masters;
using NasPos.Domain;

namespace NasPos.Api.Controllers;

[ApiController, Authorize]
[Route("api/lookups/{kind}")]
public class LookupsController(LookupService lookups) : ControllerBase
{
    [HttpGet]
    public Task<List<LookupDto>> List(string kind, [FromQuery] bool activeOnly, CancellationToken ct) => lookups.ListAsync(kind, activeOnly, ct);

    [HttpPost, Permission(Screens.Masters, Right.Add)]
    public async Task<object> Create(string kind, LookupDto dto, CancellationToken ct) => new { id = await lookups.SaveAsync(kind, dto with { Id = 0 }, ct) };

    [HttpPut("{id:long}"), Permission(Screens.Masters, Right.Edit)]
    public async Task<object> Update(string kind, long id, LookupDto dto, CancellationToken ct) => new { id = await lookups.SaveAsync(kind, dto with { Id = id }, ct) };

    [HttpDelete("{id:long}"), Permission(Screens.Masters, Right.Delete)]
    public async Task<IActionResult> Delete(string kind, long id, CancellationToken ct) { await lookups.DeleteAsync(kind, id, ct); return NoContent(); }
}

[ApiController, Authorize]
[Route("api/account-groups")]
public class AccountGroupsController(AccountGroupService groups) : ControllerBase
{
    [HttpGet]
    public Task<List<AccountGroupDto>> List(CancellationToken ct) => groups.ListAsync(ct);

    [HttpPost, Permission(Screens.AccountGroup, Right.Add)]
    public async Task<object> Create(AccountGroupDto dto, CancellationToken ct) => new { id = await groups.SaveAsync(dto with { Id = 0 }, ct) };

    [HttpPut("{id:long}"), Permission(Screens.AccountGroup, Right.Edit)]
    public async Task<object> Update(long id, AccountGroupDto dto, CancellationToken ct) => new { id = await groups.SaveAsync(dto with { Id = id }, ct) };

    [HttpDelete("{id:long}"), Permission(Screens.AccountGroup, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await groups.DeleteAsync(id, ct); return NoContent(); }
}

[ApiController, Authorize]
[Route("api/ledgers")]
public class LedgersController(LedgerService ledgers, LoyaltyService loyalty, ICurrentUser user) : ControllerBase
{
    private static string ScreenFor(PartyType? t) => t switch
    {
        PartyType.Customer => Screens.Customer,
        PartyType.Supplier => Screens.Supplier,
        PartyType.Employee => Screens.Employee,
        _ => Screens.Ledger,
    };

    [HttpGet]
    public Task<PagedResult<LedgerListItem>> List([FromQuery] PartyType? partyType, [FromQuery] long? groupId, [FromQuery] string? search,
        [FromQuery] bool? active, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        user.Require(ScreenFor(partyType), Right.Read);
        return ledgers.ListAsync(partyType, groupId, search, active, page, pageSize, ct);
    }

    /// <summary>Party lookup for billing screens (any billing user may search parties).</summary>
    [HttpGet("parties")]
    public Task<List<PartyLookup>> Parties([FromQuery] PartyType type, [FromQuery] string? search, CancellationToken ct, [FromQuery] int take = 20) =>
        ledgers.PartiesAsync(type, search, take, ct);

    [HttpGet("{id:long}")]
    public async Task<LedgerDto> Get(long id, CancellationToken ct)
    {
        var dto = await ledgers.GetAsync(id, ct);
        user.Require(ScreenFor(dto.PartyType), Right.Read);
        return dto;
    }

    [HttpGet("next-code")]
    public async Task<object> NextCode([FromQuery] PartyType type, CancellationToken ct) => new { code = await ledgers.NextCodeAsync(type, ct) };

    [HttpPost]
    public async Task<object> Create(LedgerDto dto, CancellationToken ct)
    {
        user.Require(ScreenFor(dto.PartyType), Right.Add);
        dto.Id = 0;
        return new { id = await ledgers.SaveAsync(dto, ct) };
    }

    [HttpPut("{id:long}")]
    public async Task<object> Update(long id, LedgerDto dto, CancellationToken ct)
    {
        user.Require(ScreenFor(dto.PartyType), Right.Edit);
        dto.Id = id;
        return new { id = await ledgers.SaveAsync(dto, ct) };
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var dto = await ledgers.GetAsync(id, ct);
        user.Require(ScreenFor(dto.PartyType), Right.Delete);
        await ledgers.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("opening-balances"), Permission(Screens.OpeningBalance, Right.Read)]
    public Task<List<OpeningBalanceRow>> OpeningBalances(CancellationToken ct) => ledgers.OpeningBalancesAsync(ct);

    [HttpPut("{id:long}/opening"), Permission(Screens.OpeningBalance, Right.Edit)]
    public async Task<IActionResult> SetOpening(long id, OpeningBalanceRow row, CancellationToken ct)
    { await ledgers.SetOpeningAsync(id, row.Amount, row.Side, ct); return NoContent(); }

    [HttpGet("{id:long}/loyalty")]
    public Task<List<LoyaltyHistoryRow>> LoyaltyHistory(long id, CancellationToken ct) => loyalty.HistoryAsync(id, ct);
}

[ApiController, Authorize]
[Route("api/employees")]
public class EmployeesController(EmployeeService employees) : ControllerBase
{
    [HttpGet]
    public Task<List<EmployeeDto>> List([FromQuery] bool activeOnly, CancellationToken ct) => employees.ListAsync(activeOnly, ct);

    [HttpPost, Permission(Screens.Employee, Right.Add)]
    public async Task<object> Create(EmployeeDto dto, CancellationToken ct) { dto.Id = 0; return new { id = await employees.SaveAsync(dto, ct) }; }

    [HttpPut("{id:long}"), Permission(Screens.Employee, Right.Edit)]
    public async Task<object> Update(long id, EmployeeDto dto, CancellationToken ct) { dto.Id = id; return new { id = await employees.SaveAsync(dto, ct) }; }

    [HttpDelete("{id:long}"), Permission(Screens.Employee, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await employees.DeleteAsync(id, ct); return NoContent(); }
}

[ApiController, Authorize]
[Route("api/loyalty-schemes")]
public class LoyaltyController(LoyaltyService loyalty) : ControllerBase
{
    [HttpGet]
    public Task<List<LoyaltySchemeDto>> List(CancellationToken ct) => loyalty.ListAsync(ct);

    [HttpPost, Permission(Screens.Loyalty, Right.Add)]
    public async Task<object> Create(LoyaltySchemeDto dto, CancellationToken ct) => new { id = await loyalty.SaveAsync(dto with { Id = 0 }, ct) };

    [HttpPut("{id:long}"), Permission(Screens.Loyalty, Right.Edit)]
    public async Task<object> Update(long id, LoyaltySchemeDto dto, CancellationToken ct) => new { id = await loyalty.SaveAsync(dto with { Id = id }, ct) };

    [HttpDelete("{id:long}"), Permission(Screens.Loyalty, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await loyalty.DeleteAsync(id, ct); return NoContent(); }
}

[ApiController, Authorize]
[Route("api/products")]
public class ProductsController(ProductService products) : ControllerBase
{
    [HttpGet, Permission(Screens.Product, Right.Read)]
    public Task<PagedResult<ProductListItem>> List([FromQuery] string? search, [FromQuery] long? categoryId, [FromQuery] bool? active,
        [FromQuery] bool? belowReorder, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 100) =>
        products.ListAsync(search, categoryId, active, belowReorder, page, pageSize, ct);

    /// <summary>Billing lookup by barcode / code / PLU / scale label / name.</summary>
    [HttpGet("lookup")]
    public Task<List<ProductLookup>> Lookup([FromQuery] string? term, [FromQuery] long? priceListId, [FromQuery] bool purchase,
        [FromQuery] bool exact, CancellationToken ct, [FromQuery] int take = 30) =>
        products.LookupAsync(term, priceListId, purchase, exact, take, ct);

    [HttpGet("pos-tiles")]
    public Task<List<PosTile>> PosTiles([FromQuery] long? categoryId, [FromQuery] long? priceListId, CancellationToken ct) =>
        products.PosTilesAsync(categoryId, priceListId, ct);

    [HttpGet("{id:long}/party-rates")]
    public Task<List<object>> PartyRates(long id, [FromQuery] long? partyId, [FromQuery] bool purchase, CancellationToken ct) =>
        products.PartyRatesAsync(id, partyId, purchase, ct);

    [HttpGet("{id:long}"), Permission(Screens.Product, Right.Read)]
    public Task<ProductDto> Get(long id, CancellationToken ct) => products.GetAsync(id, ct);

    [HttpGet("next-code")]
    public async Task<object> NextCode(CancellationToken ct) => new { code = await products.NextCodeAsync(ct) };

    /// <summary>Proposes the next free in-store barcode; it is linked when the item is saved.</summary>
    [HttpPost("barcode/generate"), Permission(Screens.Product, Right.Read)]
    public async Task<object> GenerateBarcode(CancellationToken ct) => new { barcode = await products.GenerateBarcodeAsync(ct) };

    /// <summary>Generates and links barcodes for active goods that have none (all of them, or only the given ids).</summary>
    [HttpPost("barcode/generate-missing"), Permission(Screens.Product, Right.Edit)]
    public async Task<object> GenerateMissing(GenerateBarcodesRequest r, CancellationToken ct) => new { count = await products.GenerateMissingBarcodesAsync(r.ProductIds, ct) };

    [HttpPost, Permission(Screens.Product, Right.Add)]
    public async Task<object> Create(ProductDto dto, CancellationToken ct) { dto.Id = 0; return new { id = await products.SaveAsync(dto, ct) }; }

    [HttpPut("{id:long}"), Permission(Screens.Product, Right.Edit)]
    public async Task<object> Update(long id, ProductDto dto, CancellationToken ct) { dto.Id = id; return new { id = await products.SaveAsync(dto, ct) }; }

    [HttpDelete("{id:long}"), Permission(Screens.Product, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await products.DeleteAsync(id, ct); return NoContent(); }

    [HttpGet("{id:long}/batches")]
    public Task<List<BatchDto>> Batches(long id, [FromQuery] bool activeOnly, CancellationToken ct) => products.BatchesAsync(id, activeOnly, ct);

    [HttpPut("batches/{batchId:long}"), Permission(Screens.Product, Right.Edit)]
    public async Task<IActionResult> UpdateBatch(long batchId, BatchDto dto, CancellationToken ct) { await products.UpdateBatchAsync(dto with { Id = batchId }, ct); return NoContent(); }

    [HttpGet("{id:long}/image")]
    public async Task<IActionResult> Image(long id, CancellationToken ct)
    {
        var img = await products.ImageAsync(id, ct);
        if (img is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(img.Value.Content, img.Value.ContentType);
    }

    [HttpPut("{id:long}/image"), Permission(Screens.Product, Right.Edit)]
    public async Task<IActionResult> SetImage(long id, IFormFile? file, CancellationToken ct)
    {
        byte[]? bytes = null;
        if (file is not null) { using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
        await products.SetImageAsync(id, bytes, file?.ContentType, ct);
        return NoContent();
    }
}

[ApiController, Authorize]
[Route("api/offers")]
public class OffersController(OfferService offers) : ControllerBase
{
    [HttpGet, Permission(Screens.Offer, Right.Read)]
    public Task<List<ItemOfferDto>> List([FromQuery] bool liveOnly, CancellationToken ct) => offers.ListAsync(liveOnly, ct);

    [HttpPut, Permission(Screens.Offer, Right.Edit)]
    public async Task<IActionResult> Save(List<ItemOfferDto> items, CancellationToken ct) { await offers.SaveAsync(items, ct); return NoContent(); }

    [HttpPost("fill"), Permission(Screens.Offer, Right.Add)]
    public async Task<object> Fill(OfferFillRequest r, CancellationToken ct) => new { count = await offers.FillAsync(r, ct) };

    [HttpDelete("{id:long}"), Permission(Screens.Offer, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await offers.DeleteAsync(id, ct); return NoContent(); }
}

[ApiController, Authorize]
[Route("api/price-lists/{id:long}")]
public class PriceListsController(PriceListService lists) : ControllerBase
{
    [HttpGet("items"), Permission(Screens.PriceList, Right.Read)]
    public Task<List<PriceListRow>> Items(long id, [FromQuery] long? categoryId, [FromQuery] string? search, CancellationToken ct) =>
        lists.ItemsAsync(id, categoryId, search, ct);

    [HttpPut("items"), Permission(Screens.PriceList, Right.Edit)]
    public async Task<IActionResult> Save(long id, List<PriceListUpdate> updates, CancellationToken ct) { await lists.SaveAsync(id, updates, ct); return NoContent(); }

    [HttpPost("bulk"), Permission(Screens.PriceList, Right.Edit)]
    public async Task<object> Bulk(long id, PriceListBulkRequest r, CancellationToken ct) => new { count = await lists.BulkAsync(id, r, ct) };
}

[ApiController, Authorize]
[Route("api/boms")]
public class BomsController(BomService boms) : ControllerBase
{
    [HttpGet]
    public Task<List<BomDto>> List(CancellationToken ct) => boms.ListAsync(ct);

    [HttpGet("{id:long}")]
    public Task<BomDto> Get(long id, CancellationToken ct) => boms.GetAsync(id, ct);

    [HttpGet("{id:long}/scale")]
    public Task<List<BomLineDto>> Scale(long id, [FromQuery] decimal qty, CancellationToken ct) => boms.ScaleAsync(id, qty, ct);

    [HttpPost, Permission(Screens.Bom, Right.Add)]
    public async Task<object> Create(BomDto dto, CancellationToken ct) { dto.Id = 0; return new { id = await boms.SaveAsync(dto, ct) }; }

    [HttpPut("{id:long}"), Permission(Screens.Bom, Right.Edit)]
    public async Task<object> Update(long id, BomDto dto, CancellationToken ct) { dto.Id = id; return new { id = await boms.SaveAsync(dto, ct) }; }

    [HttpDelete("{id:long}"), Permission(Screens.Bom, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await boms.DeleteAsync(id, ct); return NoContent(); }
}
