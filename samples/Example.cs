using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Rivet;

namespace CaseBridge.Application.Submissions;

public enum MessageVisibility { Internal, Public }

public enum SubmissionStatus { Draft, Submitted, UnderReview, Approved, Rejected }

public sealed record Address(string Line1, string? Line2, string City, string Postcode);

[RivetType]
public sealed record CreateMessageCommand(
    Guid SubmissionId,
    string Body,
    MessageVisibility Visibility);

[RivetType]
public sealed record MessageDto(
    Guid Id,
    string Body,
    string AuthorName,
    DateTime CreatedAt);

[RivetType]
public sealed record SubmissionDetailDto(
    Guid Id,
    string Reference,
    SubmissionStatus Status,
    Address CorrespondenceAddress,
    List<MessageDto> Messages,
    Dictionary<string, string> Metadata,
    DateTime? ClosedAt);

public static class SubmissionEndpoints
{
    [RivetEndpoint]
    [HttpGet("/api/submissions/{id}")]
    public static Task<SubmissionDetailDto> GetSubmission(
        [FromRoute] Guid id)
        => throw new NotImplementedException();

    [RivetEndpoint]
    [HttpPost("/api/submissions/{id}/messages")]
    public static Task<MessageDto> CreateMessage(
        [FromRoute] Guid id,
        [FromBody] CreateMessageCommand body)
        => throw new NotImplementedException();

    [RivetEndpoint]
    [HttpGet("/api/submissions")]
    public static Task<List<SubmissionDetailDto>> ListSubmissions(
        [FromQuery] string status,
        [FromQuery] int page)
        => throw new NotImplementedException();

    [RivetEndpoint]
    [HttpDelete("/api/submissions/{id}")]
    public static Task DeleteSubmission(
        [FromRoute] Guid id)
        => throw new NotImplementedException();
}
