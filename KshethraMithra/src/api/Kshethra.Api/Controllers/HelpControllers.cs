using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kshethra.Api.Security;
using Kshethra.Application.Common;
using Kshethra.Application.Help;
using Kshethra.Domain;

namespace Kshethra.Api.Controllers;

public sealed record FaqAskRequest(string? Message);
public sealed record FaqFeedbackRequest(string? Question, bool Helpful);

/// <summary>FAQ › Chatbot. Every signed-in user may use it; answers are filtered by the user's own role rights.</summary>
[ApiController, Authorize]
[Route("api/faq")]
public class FaqController(FaqService faq) : ControllerBase
{
    [HttpGet("start")]
    public Task<FaqStart> Start(CancellationToken ct) => faq.StartAsync(ct);

    [HttpPost("ask")]
    public Task<FaqReply> Ask(FaqAskRequest r, CancellationToken ct) => faq.AskAsync(r.Message ?? "", ct);

    [HttpGet("answer/{id}")]
    public Task<FaqReply> Answer(string id, CancellationToken ct) => faq.AnswerAsync(id, ct);

    [HttpPost("feedback")]
    public async Task<IActionResult> Feedback(FaqFeedbackRequest r, CancellationToken ct) { await faq.FeedbackAsync(r.Question ?? "", r.Helpful, ct); return NoContent(); }

    // ---- manage (Faq screen rights) ----

    [HttpGet("items"), Permission(Screens.Faq, Right.Read)]
    public Task<List<FaqItemDto>> Items(CancellationToken ct) => faq.ListAsync(ct);

    [HttpGet("entries/{id:long}"), Permission(Screens.Faq, Right.Read)]
    public Task<FaqEntryDto> Get(long id, CancellationToken ct) => faq.GetAsync(id, ct);

    [HttpPost("entries"), Permission(Screens.Faq, Right.Add)]
    public async Task<object> Create(FaqEntryDto dto, CancellationToken ct) => new { id = await faq.SaveAsync(dto with { Id = 0 }, ct) };

    [HttpPut("entries/{id:long}"), Permission(Screens.Faq, Right.Edit)]
    public async Task<object> Update(long id, FaqEntryDto dto, CancellationToken ct) => new { id = await faq.SaveAsync(dto with { Id = id }, ct) };

    [HttpDelete("entries/{id:long}"), Permission(Screens.Faq, Right.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) { await faq.DeleteAsync(id, ct); return NoContent(); }

    [HttpGet("unanswered"), Permission(Screens.Faq, Right.Read)]
    public Task<List<FaqLogDto>> Unanswered(CancellationToken ct) => faq.LogsAsync(ct);

    [HttpDelete("unanswered/{id:long}"), Permission(Screens.Faq, Right.Edit)]
    public async Task<IActionResult> Dismiss(long id, CancellationToken ct) { await faq.DismissLogAsync(id, ct); return NoContent(); }

    [HttpGet("test"), Permission(Screens.Faq, Right.Read)]
    public Task<FaqTestResult> Test([FromQuery] string? q, CancellationToken ct) => faq.TestAsync(q ?? "", ct);

    [HttpGet("screens"), Permission(Screens.Faq, Right.Read)]
    public object ScreenList() => Screens.All.Select(s => new { key = s, name = FaqResponder.ScreenNames.GetValueOrDefault(s, s) });
}

/// <summary>FAQ › Email Us. Any signed-in user can write to support; messages go out through the e-mail outbox.</summary>
[ApiController, Authorize]
[Route("api/support")]
public class SupportController(SupportService support) : ControllerBase
{
    [HttpGet]
    public Task<SupportInfo> Info(CancellationToken ct) => support.InfoAsync(ct);

    [HttpGet("topics")]
    public string[] Topics() => SupportService.Topics;

    [HttpPost]
    public async Task<object> Send(SupportRequest r, CancellationToken ct) => new { id = await support.SendAsync(r, ct) };

    [HttpGet("mine")]
    public Task<List<SupportMessageDto>> Mine(CancellationToken ct) => support.MineAsync(ct);

    [HttpPost("{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, CancellationToken ct) { await support.RetryAsync(id, ct); return NoContent(); }
}
