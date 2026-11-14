using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NasPos.Api.Infrastructure;
using NasPos.Api.Security;
using NasPos.Application.Accounting;
using NasPos.Application.Common;
using NasPos.Application.Documents;
using NasPos.Application.Messaging;
using NasPos.Application.Printing;
using NasPos.Application.Reports;
using NasPos.Domain;

namespace NasPos.Api.Controllers;

[ApiController, Authorize]
[Route("api/documents")]
public class DocumentsController(DocumentService documents, NumberingService numbering, ICurrentUser user, PrintService print,
    DocumentMessaging messaging, IAppDbContext db) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<DocumentListItem>> List([FromQuery] VoucherType? type, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] long? partyId, [FromQuery] string? search, [FromQuery] bool? cancelled, [FromQuery] bool? openOrders, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (type is not null) user.Require(Screens.ForVoucher(type.Value), Right.Read);
        else if (!user.IsAdmin) throw new ForbiddenException("Choose a document type.");
        return documents.ListAsync(new DocumentQuery(type, from, to, partyId, search, cancelled, openOrders, page, pageSize), ct);
    }

    [HttpGet("{id:long}")]
    public async Task<TradeDocumentDto> Get(long id, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Read);
        return dto;
    }

    [HttpGet("next-number")]
    public async Task<object> NextNumber([FromQuery] VoucherType type, CancellationToken ct) =>
        new { number = await numbering.PeekAsync(type, user.FinancialYearId, ct) };

    [HttpPost("preview")]
    public Task<TradeDocumentDto> Preview(TradeDocumentDto dto, CancellationToken ct)
    {
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Read);
        return documents.PreviewAsync(dto, ct);
    }

    [HttpPost]
    public Task<TradeDocumentDto> Create(TradeDocumentDto dto, CancellationToken ct)
    {
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Add);
        return documents.SaveAsync(dto, null, ct);
    }

    [HttpPut("{id:long}")]
    public Task<TradeDocumentDto> Update(long id, TradeDocumentDto dto, CancellationToken ct)
    {
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Edit);
        return documents.SaveAsync(dto, id, ct);
    }

    [HttpPost("{id:long}/cancel"), RequireStepUp]
    public async Task<IActionResult> Cancel(long id, CancelRequest r, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Cancel);
        await documents.CancelAsync(id, r.Reason, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}"), RequireStepUp]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Delete);
        await documents.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Applies extra money paid with this bill to the customer's old outstanding (a receipt settling their oldest bills).
    /// Allowed for whoever can make this kind of bill, so a cashier can collect old dues at the counter.
    /// </summary>
    [HttpPost("{id:long}/old-balance")]
    public async Task<VoucherDto> ReceiveOldBalance(long id, OldBalanceRequest r, [FromServices] VoucherService vouchers, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Add);
        return await vouchers.ReceiveOnOldBalanceAsync(id, r, ct);
    }

    [HttpPost("void-items")]
    public async Task<IActionResult> VoidItem(VoidItemDto v, CancellationToken ct) { await documents.LogVoidItemAsync(v, ct); return NoContent(); }

    /// <summary>Bill PDF. <paramref name="copies"/> = comma list, e.g. "original,duplicate", prints each copy marked; omit for one unmarked copy.</summary>
    [HttpGet("{id:long}/pdf")]
    public async Task<IActionResult> Pdf(long id, [FromQuery] long? scheme, [FromQuery] string? copies, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Print);
        var list = string.IsNullOrWhiteSpace(copies) ? null : copies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return this.Pdf(await print.DocumentPdfAsync(id, scheme, ct, list), $"{dto.DocumentNo}.pdf");
    }

    [HttpGet("{id:long}/text")]
    public async Task<IActionResult> Text(long id, CancellationToken ct, [FromQuery] int width = 42)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Print);
        return Content(await print.DocumentTextAsync(id, width, ct), "text/plain; charset=utf-8");
    }

    [HttpPost("{id:long}/email")]
    public async Task<IActionResult> Email(long id, EmailDocumentRequest r, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Email);
        var pdf = await print.DocumentPdfAsync(id, null, ct);
        var company = db.Companies.First(c => c.Id == user.CompanyId);
        await messaging.QueueAsync(MessageChannel.Email, r.To, r.Subject ?? $"{dto.VoucherType.DisplayName()} {dto.DocumentNo} from {company.Name}",
            r.Body ?? $"Dear {dto.PartyName},\n\nPlease find attached {dto.VoucherType.DisplayName()} {dto.DocumentNo} for Rs. {dto.BillAmount:0.00}.\n\nRegards,\n{company.Name}",
            $"{dto.VoucherType} {dto.DocumentNo}", $"{dto.DocumentNo}.pdf", pdf, ct);
        return Accepted();
    }

    [HttpPost("{id:long}/sms")]
    public async Task<IActionResult> Sms(long id, SmsDocumentRequest r, CancellationToken ct)
    {
        var dto = await documents.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Sms);
        await messaging.QueueAsync(MessageChannel.Sms, r.Mobile, null,
            r.Text ?? $"Bill {dto.DocumentNo} dated {dto.Date:dd-MM-yyyy} for Rs.{dto.BillAmount:0.00}. Thank you.", $"{dto.VoucherType} {dto.DocumentNo}", ct: ct);
        return Accepted();
    }
}

public sealed record EmailDocumentRequest(string To, string? Subject, string? Body);
public sealed record SmsDocumentRequest(string Mobile, string? Text);

[ApiController, Authorize]
[Route("api/vouchers")]
public class VouchersController(VoucherService vouchers, ICurrentUser user, PrintService print) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<VoucherListItem>> List([FromQuery] VoucherType? type, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] long? ledgerId, [FromQuery] string? search, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        user.Require(Screens.Voucher, Right.Read);
        return vouchers.ListAsync(type, from, to, ledgerId, search, page, pageSize, ct);
    }

    [HttpGet("{id:long}")]
    public async Task<VoucherDto> Get(long id, CancellationToken ct)
    {
        var v = await vouchers.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(v.VoucherType), Right.Read);
        return v;
    }

    [HttpPost]
    public Task<VoucherDto> Create(VoucherDto dto, CancellationToken ct)
    {
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Add);
        return vouchers.SaveAsync(dto, null, ct);
    }

    [HttpPut("{id:long}")]
    public Task<VoucherDto> Update(long id, VoucherDto dto, CancellationToken ct)
    {
        user.Require(Screens.ForVoucher(dto.VoucherType), Right.Edit);
        return vouchers.SaveAsync(dto, id, ct);
    }

    [HttpPost("{id:long}/cancel"), RequireStepUp]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var v = await vouchers.GetAsync(id, ct);
        user.Require(Screens.ForVoucher(v.VoucherType), Right.Cancel);
        await vouchers.CancelAsync(id, ct);
        return NoContent();
    }

    [HttpGet("open-bills/{ledgerId:long}")]
    public Task<List<OpenBillDto>> OpenBills(long ledgerId, CancellationToken ct) => vouchers.OpenBillsAsync(ledgerId, ct);

    [HttpPost("reconcile"), Permission(Screens.Voucher, Right.Edit)]
    public async Task<IActionResult> Reconcile(ReconcileRequest r, CancellationToken ct) { await vouchers.ReconcileAsync(r, ct); return NoContent(); }

    [HttpGet("{id:long}/pdf"), Permission(Screens.Voucher, Right.Print)]
    public async Task<IActionResult> Pdf(long id, CancellationToken ct) => this.Pdf(await print.VoucherPdfAsync(id, ct), $"voucher-{id}.pdf");
}

[ApiController, Authorize]
[Route("api/reports")]
public class ReportsController(ReportService reports, DocumentMessaging messaging, ICurrentUser user) : ControllerBase
{
    [HttpGet]
    public IEnumerable<ReportDefinition> Catalog() => reports.Catalog();

    [HttpPost("{key}")]
    public Task<ReportResult> Run(string key, ReportFilter filter, CancellationToken ct) => reports.RunAsync(key, filter, ct);

    [HttpPost("{key}/export")]
    public async Task<IActionResult> Export(string key, ReportFilter filter, CancellationToken ct, [FromQuery] string format = "xlsx")
    {
        var (content, type, name) = await reports.ExportAsync(key, filter, format, ct);
        return File(content, type, name);
    }

    [HttpPost("{key}/email")]
    public async Task<IActionResult> Email(string key, [FromQuery] string to, ReportFilter filter, CancellationToken ct, [FromQuery] string format = "pdf")
    {
        var (content, _, name) = await reports.ExportAsync(key, filter, format, ct);
        var def = reports.Catalog().First(d => d.Key == key);
        user.Require(def.Screen, Right.Email);
        await messaging.QueueAsync(MessageChannel.Email, to, def.Title, $"Please find attached: {def.Title}.", $"Report {key}", name, content, ct);
        return Accepted();
    }

    [HttpPost("gstr1/workbook")]
    public async Task<IActionResult> Gstr1(ReportFilter filter, CancellationToken ct)
    {
        var (content, name) = await reports.Gstr1WorkbookAsync(filter, ct);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }
}

[ApiController, Authorize]
[Route("api/print")]
public class PrintController(PrintService print) : ControllerBase
{
    [HttpGet("schemes")]
    public Task<List<PrintSchemeDto>> Schemes([FromQuery] VoucherType? type, CancellationToken ct) => print.SchemesAsync(type, ct);

    [HttpPost("schemes"), Permission(Screens.Printing, Right.Add)]
    public async Task<object> CreateScheme(PrintSchemeDto dto, CancellationToken ct) => new { id = await print.SaveSchemeAsync(dto with { Id = 0 }, ct) };

    [HttpPut("schemes/{id:long}"), Permission(Screens.Printing, Right.Edit)]
    public async Task<object> UpdateScheme(long id, PrintSchemeDto dto, CancellationToken ct) => new { id = await print.SaveSchemeAsync(dto with { Id = id }, ct) };

    [HttpDelete("schemes/{id:long}"), Permission(Screens.Printing, Right.Delete)]
    public async Task<IActionResult> DeleteScheme(long id, CancellationToken ct) { await print.DeleteSchemeAsync(id, ct); return NoContent(); }

    [HttpGet("label-templates")]
    public Task<List<LabelTemplateDto>> LabelTemplates(CancellationToken ct) => print.LabelTemplatesAsync(ct);

    [HttpPost("label-templates"), Permission(Screens.Printing, Right.Edit)]
    public async Task<object> SaveLabelTemplate(LabelTemplateDto dto, CancellationToken ct) => new { id = await print.SaveLabelTemplateAsync(dto, ct) };

    [HttpPost("labels"), Permission(Screens.Printing, Right.Print)]
    public async Task<IActionResult> Labels(LabelRequest r, CancellationToken ct) => this.Pdf(await print.LabelsPdfAsync(r, ct), "labels.pdf");

    [HttpGet("barcode.svg")]
    public IActionResult Barcode([FromQuery] string text, [FromQuery] bool showText = true)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 60) return BadRequest();
        return Content(Code128.Svg(text, 50, 1.5, showText), "image/svg+xml");
    }
}

[ApiController, Authorize]
[Route("api/messaging")]
public class MessagingController(MessagingService messaging) : ControllerBase
{
    [HttpGet("templates"), Permission(Screens.Messaging, Right.Read)]
    public Task<List<SmsTemplateDto>> Templates(CancellationToken ct) => messaging.TemplatesAsync(ct);

    [HttpGet("tags")]
    public string[] Tags() => TemplateRenderer.Tags;

    [HttpPost("templates"), Permission(Screens.Messaging, Right.Add)]
    public async Task<object> CreateTemplate(SmsTemplateDto dto, CancellationToken ct) => new { id = await messaging.SaveTemplateAsync(dto with { Id = 0 }, ct) };

    [HttpPut("templates/{id:long}"), Permission(Screens.Messaging, Right.Edit)]
    public async Task<object> UpdateTemplate(long id, SmsTemplateDto dto, CancellationToken ct) => new { id = await messaging.SaveTemplateAsync(dto with { Id = id }, ct) };

    [HttpDelete("templates/{id:long}"), Permission(Screens.Messaging, Right.Delete)]
    public async Task<IActionResult> DeleteTemplate(long id, CancellationToken ct) { await messaging.DeleteTemplateAsync(id, ct); return NoContent(); }

    [HttpGet("schedules"), Permission(Screens.Messaging, Right.Read)]
    public Task<List<SmsScheduleDto>> Schedules(CancellationToken ct) => messaging.SchedulesAsync(ct);

    [HttpPost("bulk"), Permission(Screens.Messaging, Right.Sms)]
    public async Task<object> Bulk(BulkSmsRequest r, CancellationToken ct) => new { count = await messaging.BulkAsync(r, ct) };

    [HttpGet("outbox"), Permission(Screens.Messaging, Right.Read)]
    public Task<List<OutboundMessageDto>> Outbox([FromQuery] MessageStatus? status, CancellationToken ct) => messaging.OutboxAsync(status, ct);

    [HttpPost("outbox/{id:long}/retry"), Permission(Screens.Messaging, Right.Sms)]
    public async Task<IActionResult> Retry(long id, CancellationToken ct) { await messaging.RetryAsync(id, ct); return NoContent(); }

    [HttpPost("send"), Permission(Screens.Messaging, Right.Sms)]
    public async Task<IActionResult> Send(SendMessageRequest r, CancellationToken ct) { await messaging.SendAsync(r, ct); return Accepted(); }
}
